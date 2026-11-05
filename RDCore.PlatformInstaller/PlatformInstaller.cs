using System.IO.Compression;
using System.Security.Cryptography;
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
/// keeps running; the next start uses the new one. The current version and the one before it are kept, and older ones are
/// removed once nothing runs from them.
/// </para>
/// <para>
/// <b>One installation at a time</b>, across processes: two IDE windows wanting the platform at once take turns, and the
/// second finds the version the first deployed.
/// </para>
/// </remarks>
public sealed class PlatformInstaller
{
    private const string StateFile = "installer.json";
    private const string VersionsFolder = "versions";
    private const string DownloadsFolder = "downloads";
    private const string LockFile = "install.lock";
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
                return null;
            var root = VersionRoot(version);
            return File.Exists(Path.Combine(root, "rdcore.json")) ? new InstalledPlatform(version, root) : null;
        }
    }

    /// <summary>
    /// Deploys <paramref name="release"/> and makes it the platform in use. A version deployed already, by this process or
    /// another, is made current without a download.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// The download doesn't match the release's checksums, or it isn't a platform: it has no <c>rdcore.json</c>, or no language
    /// server where that names it. Nothing is deployed, and the platform in use stays as it was.
    /// </exception>
    /// <exception cref="HttpRequestException">The release couldn't be downloaded.</exception>
    public async Task<InstalledPlatform> InstallAsync(PlatformRelease release, IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.Root);
        using var gate = await AcquireLockAsync(cancellationToken);

        var root = VersionRoot(release.Version);
        if (!File.Exists(Path.Combine(root, "rdcore.json")))
            await DeployAsync(release, root, progress, cancellationToken);
        else
            progress?.Report(new InstallProgress(InstallStage.Deploying));

        MakeCurrent(release.Version);
        RemoveOldVersions();
        return new InstalledPlatform(release.Version, root);
    }

    private async Task DeployAsync(PlatformRelease release, string root, IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var sums = await http.GetStringAsync(release.Checksums, cancellationToken);
        var expected = Sha256Sums.Find(sums, release.ArchiveName)
            ?? throw new InvalidDataException($"The release's {ReleaseFeed.ChecksumsAsset} doesn't list {release.ArchiveName}.");

        var downloads = Directory.CreateDirectory(Path.Combine(options.Root, DownloadsFolder)).FullName;
        var archive = Path.Combine(downloads, release.ArchiveName + ".partial");
        var unpacked = root + ".partial";
        try
        {
            var actual = await DownloadAsync(release, archive, progress, cancellationToken);
            progress?.Report(new InstallProgress(InstallStage.Verifying));
            if (actual != expected)
                throw new InvalidDataException(
                    $"{release.ArchiveName} doesn't match the release's checksum: it may be damaged, or not the release's own file.");

            progress?.Report(new InstallProgress(InstallStage.Extracting));
            DeleteFolder(unpacked);
            // ExtractToDirectory refuses an entry that would land outside the folder.
            ZipFile.ExtractToDirectory(archive, unpacked);
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

    // Downloads to the file and answers its SHA-256, computed as it is written.
    private async Task<string> DownloadAsync(PlatformRelease release, string path, IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(release.Archive, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? (release.ArchiveSize > 0 ? release.ArchiveSize : null);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            var buffer = new byte[81920];
            long received = 0;
            progress?.Report(new InstallProgress(InstallStage.Downloading, 0, total));
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                hash.AppendData(buffer, 0, read);
                received += read;
                progress?.Report(new InstallProgress(InstallStage.Downloading, received, total));
            }
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    // A platform root holds rdcore.json at its top, and the language server where that names it.
    private static void EnsurePlatform(string root, PlatformRelease release)
    {
        var manifestPath = Path.Combine(root, "rdcore.json");
        if (!File.Exists(manifestPath))
            throw new InvalidDataException($"{release.ArchiveName} isn't an RDCore platform: it has no rdcore.json at its root.");

        string? languageServer;
        try
        {
            languageServer = JsonNode.Parse(File.ReadAllText(manifestPath))?["langService"]?.GetValue<string>();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{release.ArchiveName}'s rdcore.json can't be read: {ex.Message}", ex);
        }
        if (string.IsNullOrWhiteSpace(languageServer))
            throw new InvalidDataException($"{release.ArchiveName}'s rdcore.json names no language server.");

        var path = Path.GetFullPath(Path.Combine(root, languageServer));
        if (!File.Exists(path) && !File.Exists(path + ".exe"))
            throw new InvalidDataException($"{release.ArchiveName} has no language server at {languageServer}, which its rdcore.json names.");
    }

    private string VersionRoot(PlatformVersion version) => Path.Combine(options.Root, VersionsFolder, version.Text);

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
            state = state with { Current = version.Text, Previous = state.Current };
        var path = Path.Combine(options.Root, StateFile);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state));
        File.Move(temporary, path, overwrite: true);
    }

    // Every version but the current one and the one before it. One a process still runs from can't be deleted on Windows;
    // it is tried again after the next installation.
    private void RemoveOldVersions()
    {
        var state = ReadState();
        var versions = Path.Combine(options.Root, VersionsFolder);
        foreach (var folder in Directory.EnumerateDirectories(versions))
        {
            var name = Path.GetFileName(folder);
            if (name == state.Current || name == state.Previous || !PlatformVersion.TryParse(name, out _))
                continue;
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still in use.
            }
        }
    }

    private static void DeleteFolder(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    // An exclusive handle on a file under the root: held for the installation, released with it, and freed by the system if
    // the process dies holding it.
    private async Task<FileStream> AcquireLockAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(options.Root, LockFile);
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                await Task.Delay(LockRetry, cancellationToken);
            }
        }
    }

    private sealed record InstallerState
    {
        public string? Current { get; init; }
        public string? Previous { get; init; }
    }
}
