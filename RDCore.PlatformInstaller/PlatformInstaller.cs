using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RDCore.PlatformInstaller;

/// <summary>
/// Gets the RDCore platform onto the machine: finds the newest published release, downloads it, checks it against the
/// release's checksums, and deploys it under <see cref="PlatformInstallerOptions.Root"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>A deployed version is never changed.</b> Each one is unpacked beside the others, under <c>versions/&lt;version&gt;</c>,
/// and is complete before it is named the current one in <c>installer.json</c>. A server running from the version before
/// keeps running; the next start uses the new one. The current version and the one before it are kept. An older one is
/// removed whole or not at all: it is renamed out of the way first, which fails while any of its files is open, as a running
/// server's are on Windows, and it is left where it is until a later installation finds it unused.
/// </para>
/// <para>
/// <b>One installation at a time</b>, across processes: two IDE windows wanting the platform at once take turns, and the
/// second finds the version the first deployed.
/// </para>
/// <para>
/// <b>Bounded:</b> a download is refused once it is bigger than its release says, or than
/// <see cref="PlatformInstallerOptions.MaxArchiveBytes"/>, and an archive whose entries are too many or too big is refused
/// before it is unpacked.
/// </para>
/// </remarks>
public sealed class PlatformInstaller
{
    private const string StateFile = "installer.json";
    private const string VersionsFolder = "versions";
    private const string DownloadsFolder = "downloads";
    private const string LockFile = "install.lock";
    private const string RemovingSuffix = ".removing";
    private static readonly TimeSpan LockRetry = TimeSpan.FromMilliseconds(200);

    private readonly PlatformInstallerOptions options;
    private readonly HttpClient http;

    /// <summary>An installer for <paramref name="options"/>, downloading with <paramref name="http"/>.</summary>
    public PlatformInstaller(PlatformInstallerOptions options, HttpClient http)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Root);
        this.options = options;
        this.http = http;
        Feed = new ReleaseFeed(http, options);
    }

    /// <summary>The published releases the installer chooses from.</summary>
    public ReleaseFeed Feed { get; }

    /// <summary>The platform in use, or null when none is deployed, or the one named is gone or incomplete.</summary>
    public InstalledPlatform? Current
    {
        get
        {
            var state = ReadState();
            if (state.Current is not { } current || !PlatformVersion.TryParse(current, out var version))
            {
                return null;
            }

            var root = VersionRoot(version);
            return File.Exists(Path.Combine(root, "rdcore.json")) ? new InstalledPlatform(version, root) : null;
        }
    }

    /// <summary>Where <paramref name="version"/> is, or would be, deployed.</summary>
    public string VersionRoot(PlatformVersion version) => Path.Combine(options.Root, VersionsFolder, version.Text);

    /// <summary>
    /// Keeps <paramref name="platform"/> deployed for as long as the lease is held, whatever any installer removes meanwhile: hold
    /// it while a server runs from it. Its manifest stays open, readable by anyone but not removable, so the version's folder
    /// can't be renamed out of the way, and a removal leaves it whole. The lease is per process, and the system frees it with the
    /// process.
    /// </summary>
    /// <remarks>
    /// <b>Windows only.</b> The guard is Windows' refusal to rename a folder that holds an open file. Elsewhere an open file
    /// doesn't stop its folder being renamed or removed, so the lease keeps nothing deployed there.
    /// </remarks>
    /// <exception cref="FileNotFoundException">The platform isn't deployed any more.</exception>
    public static IDisposable Hold(InstalledPlatform platform) =>
        new FileStream(platform.Manifest, FileMode.Open, FileAccess.Read, FileShare.Read);

    /// <summary>
    /// Deploys <paramref name="release"/> and makes it the platform in use. A version deployed already, by this process or
    /// another, is made current without a download.
    /// </summary>
    /// <remarks>
    /// A version older than the one in use is deployed beside it but not made current, so that whoever asked for it, as a server
    /// whose launch names it does, has it without taking the newer one from anyone else. It is kept, until an installation
    /// after this one finds it unused.
    /// </remarks>
    /// <exception cref="PlatformInstallException">
    /// The download doesn't match the release's checksums, is bigger than it should be, or isn't a platform. Nothing is
    /// deployed, and the platform in use stays as it was.
    /// </exception>
    /// <exception cref="HttpRequestException">The release couldn't be downloaded.</exception>
    public async Task<InstalledPlatform> InstallAsync(PlatformRelease release, IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.Root);
        using var gate = await AcquireLockAsync(cancellationToken).ConfigureAwait(false);

        var root = VersionRoot(release.Version);
        if (!File.Exists(Path.Combine(root, "rdcore.json")))
        {
            await DeployAsync(release, root, progress, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            progress?.Report(new InstallProgress(InstallStage.Deploying));
        }

        if (Current is not { } current || current.Version.CompareTo(release.Version) <= 0)
        {
            MakeCurrent(release.Version);
        }

        RemoveOldVersions(keep: release.Version);
        return new InstalledPlatform(release.Version, root);
    }

    private async Task DeployAsync(PlatformRelease release, string root, IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        string sums;
        using (var response = await http.GetAsync(release.Checksums, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            sums = Encoding.UTF8.GetString(await BoundedContent.ReadAsync(response, options.MaxChecksumsBytes,
                size => new PlatformInstallException(InstallFailure.TooLarge,
                    $"The release's {ReleaseFeed.ChecksumsAsset} is bigger than it should be: {size} bytes at least, where {options.MaxChecksumsBytes} is the most."),
                cancellationToken).ConfigureAwait(false));
        }

        var expected = Sha256Sums.Find(sums, release.ArchiveName)
            ?? throw new PlatformInstallException(InstallFailure.ChecksumMismatch,
                $"The release's {ReleaseFeed.ChecksumsAsset} doesn't list {release.ArchiveName}.");

        var downloads = Directory.CreateDirectory(Path.Combine(options.Root, DownloadsFolder)).FullName;
        var archive = Path.Combine(downloads, release.ArchiveName + ".partial");
        var unpacked = root + ".partial";
        try
        {
            var actual = await DownloadAsync(release, archive, progress, cancellationToken).ConfigureAwait(false);
            progress?.Report(new InstallProgress(InstallStage.Verifying));
            if (actual != expected)
            {
                throw new PlatformInstallException(InstallFailure.ChecksumMismatch,
                    $"{release.ArchiveName} doesn't match the release's checksum: it may be damaged, or not the release's own file.");
            }

            progress?.Report(new InstallProgress(InstallStage.Extracting));
            DeleteFolder(unpacked);
            Extract(archive, unpacked, release);
            EnsurePlatform(unpacked, release);

            progress?.Report(new InstallProgress(InstallStage.Deploying));
            // A folder of this version without its manifest is what's left of a deployment someone broke: it is replaced.
            DeleteFolder(root);
            Directory.CreateDirectory(Path.GetDirectoryName(root)!);
            Directory.Move(unpacked, root);
        }
        finally
        {
            File.Delete(archive);
            DeleteFolder(unpacked);
        }
    }

    // Downloads to the file and answers its SHA-256, computed as it is written. It stops as soon as it has more than the release
    // said it would send, or than any release may.
    private async Task<string> DownloadAsync(PlatformRelease release, string path, IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var limit = release.ArchiveSize > 0 ? Math.Min(release.ArchiveSize, options.MaxArchiveBytes) : options.MaxArchiveBytes;
        using var response = await http.GetAsync(release.Archive, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? (release.ArchiveSize > 0 ? release.ArchiveSize : null);
        if (total > limit)
        {
            throw TooLarge(release, total.Value, limit);
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            var buffer = new byte[81920];
            long received = 0;
            progress?.Report(new InstallProgress(InstallStage.Downloading, 0, total));
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                received += read;
                if (received > limit)
                {
                    throw TooLarge(release, received, limit);
                }

                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                progress?.Report(new InstallProgress(InstallStage.Downloading, received, total));
            }
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static PlatformInstallException TooLarge(PlatformRelease release, long size, long limit) =>
        new(InstallFailure.TooLarge, $"{release.ArchiveName} is bigger than it should be: {size} bytes, where {limit} is the most.");

    // Refused whole before anything is written when it has too many entries or unpacks too big. ExtractToDirectory itself
    // refuses an entry that would land outside the folder.
    private void Extract(string archive, string folder, PlatformRelease release)
    {
        using (var zip = ZipFile.OpenRead(archive))
        {
            if (zip.Entries.Count > options.MaxEntries)
            {
                throw new PlatformInstallException(InstallFailure.TooLarge,
                    $"{release.ArchiveName} has {zip.Entries.Count} entries, where {options.MaxEntries} is the most.");
            }

            var size = zip.Entries.Sum(entry => entry.Length);
            if (size > options.MaxExtractedBytes)
            {
                throw new PlatformInstallException(InstallFailure.TooLarge,
                    $"{release.ArchiveName} unpacks to {size} bytes, where {options.MaxExtractedBytes} is the most.");
            }
        }

        try
        {
            ZipFile.ExtractToDirectory(archive, folder);
        }
        catch (IOException ex) when (ex is not FileNotFoundException and not DirectoryNotFoundException)
        {
            throw new PlatformInstallException(InstallFailure.NotAPlatform, $"{release.ArchiveName} can't be unpacked: {ex.Message}", ex);
        }
    }

    // A platform root holds rdcore.json at its top, and the language server where that names it, inside the root.
    private static void EnsurePlatform(string root, PlatformRelease release)
    {
        var manifestPath = Path.Combine(root, "rdcore.json");
        if (!File.Exists(manifestPath))
        {
            throw NotAPlatform(release, "it has no rdcore.json at its root");
        }

        string? languageServer;
        try
        {
            languageServer = JsonNode.Parse(File.ReadAllText(manifestPath))?["langService"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw NotAPlatform(release, $"its rdcore.json can't be read: {ex.Message}", ex);
        }

        if (string.IsNullOrWhiteSpace(languageServer))
        {
            throw NotAPlatform(release, "its rdcore.json names no language server");
        }

        var full = Path.GetFullPath(Path.Combine(root, languageServer.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (Path.IsPathRooted(languageServer) || !full.StartsWith(prefix, comparison))
        {
            throw NotAPlatform(release, $"its rdcore.json names a language server outside the platform, {languageServer}");
        }

        if (!File.Exists(full) && !File.Exists(full + ".exe"))
        {
            throw NotAPlatform(release, $"it has no language server at {languageServer}, which its rdcore.json names");
        }
    }

    private static PlatformInstallException NotAPlatform(PlatformRelease release, string why, Exception? inner = null) =>
        new(InstallFailure.NotAPlatform, $"{release.ArchiveName} isn't an RDCore platform: {why}.", inner);

    private InstallerState ReadState()
    {
        var path = Path.Combine(options.Root, StateFile);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<InstallerState>(File.ReadAllText(path)) ?? new() : new();
        }
        catch (JsonException)
        {
            // A state file nobody can read names no platform; the next installation writes a good one.
            return new();
        }
    }

    // Written whole, then moved over the old file, so a reader never sees half of it.
    private void MakeCurrent(PlatformVersion version)
    {
        var state = ReadState();
        if (state.Current != version.Text)
        {
            state = state with { Current = version.Text, Previous = state.Current };
        }

        var path = Path.Combine(options.Root, StateFile);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state));
        File.Move(temporary, path, overwrite: true);
    }

    // Every version but the current one, the one before it and the one just installed, each whole: renamed out of the way
    // first, which fails while a process runs from it, and only then deleted. One that can't be renamed is left as it is, for a
    // later installation.
    private void RemoveOldVersions(PlatformVersion keep)
    {
        var state = ReadState();
        var versions = Path.Combine(options.Root, VersionsFolder);
        foreach (var folder in Directory.EnumerateDirectories(versions))
        {
            var name = Path.GetFileName(folder);
            if (name.EndsWith(RemovingSuffix, StringComparison.Ordinal))
            {
                // A removal an earlier installation couldn't finish: nothing runs from it, as it was renamed.
                TryDelete(folder);
                continue;
            }

            if (name == state.Current || name == state.Previous || name == keep.Text || !PlatformVersion.TryParse(name, out _))
            {
                continue;
            }

            var removing = folder + RemovingSuffix;
            try
            {
                Directory.Move(folder, removing);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still in use.
                continue;
            }

            TryDelete(removing);
        }
    }

    private static void TryDelete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Tried again after the next installation.
        }
    }

    private static void DeleteFolder(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    // An exclusive handle on a file under the root: held for the installation, released with it, and freed by the system if
    // the process dies holding it.
    private async Task<FileStream> AcquireLockAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(options.Root, LockFile);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                await Task.Delay(LockRetry, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private sealed record InstallerState
    {
        public string? Current { get; init; }
        public string? Previous { get; init; }
    }
}

/// <summary>Why an installation was refused.</summary>
public enum InstallFailure
{
    /// <summary>The download doesn't match the release's checksums, or the checksums don't list it.</summary>
    ChecksumMismatch,

    /// <summary>The download isn't an RDCore platform: no manifest, no language server, or one outside it.</summary>
    NotAPlatform,

    /// <summary>The download, or what it unpacks to, is bigger than allowed.</summary>
    TooLarge,

    /// <summary>The releases can't be read: what GitHub answered isn't a list of releases.</summary>
    UnreadableReleases,
}

/// <summary>An installation refused: nothing was deployed, and the platform in use is as it was.</summary>
public sealed class PlatformInstallException(InstallFailure failure, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    /// <summary>Why.</summary>
    public InstallFailure Failure { get; } = failure;
}
