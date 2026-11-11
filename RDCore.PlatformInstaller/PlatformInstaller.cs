using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using RDCore.SDK.Client;
using RDCore.SDK.Platform;

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
/// server's are on Windows, and it is left where it is until a later installation finds it unused. A version a server runs from is
/// held while it runs (<see cref="SharedPlatformRoot.Hold"/>), and a removal leaves it where it is. These are the rules of the
/// SDK's <see cref="SharedPlatformRoot"/>, which <c>rdc install</c> deploys by too.
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
    private const string DownloadsFolder = "downloads";

    private readonly PlatformInstallerOptions options;
    private readonly HttpClient http;
    private readonly SharedPlatformRoot shared;

    /// <summary>An installer for <paramref name="options"/>, downloading with <paramref name="http"/>.</summary>
    public PlatformInstaller(PlatformInstallerOptions options, HttpClient http)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Root);
        this.options = options;
        this.http = http;
        shared = new SharedPlatformRoot(options.Root);
        Feed = new ReleaseFeed(http, options);
    }

    /// <summary>The published releases the installer chooses from.</summary>
    public ReleaseFeed Feed { get; }

    /// <summary>The platform in use, or null when none is deployed, or the one named is gone or incomplete.</summary>
    public DeployedPlatform? Current => shared.Current;

    /// <summary>The folder the installer owns, <see cref="PlatformInstallerOptions.Root"/>.</summary>
    public string Root => options.Root;

    /// <summary>Where <paramref name="version"/> is, or would be, deployed.</summary>
    public string VersionRoot(PlatformVersion version) => shared.VersionRoot(version);


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
    public async Task<DeployedPlatform> InstallAsync(PlatformRelease release, IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var gate = await shared.LockAsync(null, cancellationToken).ConfigureAwait(false);

        var root = VersionRoot(release.Version);
        if (!SharedPlatformRoot.IsWhole(root))
        {
            await DeployAsync(release, root, progress, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            progress?.Report(new InstallProgress(InstallStage.Deploying));
        }

        shared.MakeCurrentUnlessOlder(release.Version);
        shared.RemoveOldVersions(keep: release.Version);
        return new DeployedPlatform(release.Version, root);
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
        var unpacked = shared.PartialRoot(release.Version);
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
            shared.MoveIntoPlace(release.Version);
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

    private void Extract(string archive, string folder, PlatformRelease release)
    {
        if (release.IsTarGz)
        {
            ExtractTarGz(archive, folder, release);
        }
        else
        {
            ExtractZip(archive, folder, release);
        }
    }

    // Refused whole before anything is written when it has too many entries or unpacks too big. ExtractToDirectory itself
    // refuses an entry that would land outside the folder.
    private void ExtractZip(string archive, string folder, PlatformRelease release)
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
            throw Unpackable(release, ex);
        }
    }

    // Read twice. The first reading takes the entries' headers alone, and refuses the archive whole before anything is written
    // when it has too many entries, unpacks too big, has a link, or has an entry that would land outside the folder. The second
    // unpacks it, and gives each folder and file its entry's permissions, so an executable stays one.
    private void ExtractTarGz(string archive, string folder, PlatformRelease release)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        try
        {
            using (var reader = OpenTarGz(archive, release))
            {
                long entries = 0;
                long size = 0;
                while (reader.GetNextEntry() is { } entry)
                {
                    if (++entries > options.MaxEntries)
                    {
                        throw new PlatformInstallException(InstallFailure.TooLarge,
                            $"{release.ArchiveName} has more than {options.MaxEntries} entries, the most an archive may have.");
                    }

                    TargetOf(entry, root, release);
                    size += entry.Length;
                    if (size > options.MaxExtractedBytes)
                    {
                        throw new PlatformInstallException(InstallFailure.TooLarge,
                            $"{release.ArchiveName} unpacks to more than {options.MaxExtractedBytes} bytes, the most a platform may unpack to.");
                    }
                }
            }

            Directory.CreateDirectory(root);
            List<(string Path, UnixFileMode Mode)> folders = [];
            using (var reader = OpenTarGz(archive, release))
            {
                while (reader.GetNextEntry() is { } entry)
                {
                    var target = TargetOf(entry, root, release);
                    var mode = entry.Mode & PermissionBits;
                    if (entry.EntryType == TarEntryType.Directory)
                    {
                        Directory.CreateDirectory(target);
                        folders.Add((target, mode));
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: false);
                    if (!OperatingSystem.IsWindows())
                    {
                        File.SetUnixFileMode(target, mode);
                    }
                }
            }

            // Last, and the deepest first, so a folder that isn't writable is one only once everything is in it.
            if (!OperatingSystem.IsWindows())
            {
                foreach (var (path, mode) in Enumerable.Reverse(folders))
                {
                    File.SetUnixFileMode(path, mode);
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException
            or (IOException and not FileNotFoundException and not DirectoryNotFoundException))
        {
            throw Unpackable(release, ex);
        }
    }

    // The permissions of its owner, its group and everyone else: a set-user-ID, set-group-ID or sticky bit isn't kept.
    private const UnixFileMode PermissionBits = (UnixFileMode)0x1FF;

    // What the archive unpacks to, entries and their headers, is bounded too, so that no header can make it read without end.
    // A header is 512 bytes, padding up to 511 more; a long name takes an extra header of its own.
    private TarReader OpenTarGz(string archive, PlatformRelease release)
    {
        var limit = options.MaxExtractedBytes + (options.MaxEntries + 2L) * 4096;
        var gzip = new GZipStream(File.OpenRead(archive), CompressionMode.Decompress);
        return new TarReader(new BoundedStream(gzip, limit, () => new PlatformInstallException(InstallFailure.TooLarge,
            $"{release.ArchiveName} unpacks to more than {limit} bytes, entries and headers, where {options.MaxExtractedBytes} is the most for its files.")));
    }

    // Where an entry is unpacked: a folder or a file inside the root, never a link, nor anything outside it.
    private static string TargetOf(TarEntry entry, string root, PlatformRelease release)
    {
        if (entry.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink)
        {
            throw NotAPlatform(release, $"its entry {entry.Name} is a link, which a platform doesn't have");
        }

        if (entry.EntryType is not (TarEntryType.Directory or TarEntryType.RegularFile or TarEntryType.V7RegularFile))
        {
            throw NotAPlatform(release, $"its entry {entry.Name} is neither a folder nor a file, but a {entry.EntryType}");
        }

        var target = Path.GetFullPath(Path.Combine(root, entry.Name));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var inside = target.StartsWith(root + Path.DirectorySeparatorChar, comparison)
            || (entry.EntryType == TarEntryType.Directory && Path.TrimEndingDirectorySeparator(target).Equals(root, comparison));
        if (Path.IsPathRooted(entry.Name) || !inside)
        {
            throw NotAPlatform(release, $"its entry {entry.Name} would land outside the platform");
        }

        return target;
    }

    private static PlatformInstallException Unpackable(PlatformRelease release, Exception inner) =>
        new(InstallFailure.NotAPlatform, $"{release.ArchiveName} can't be unpacked: {inner.Message}", inner);

    private static void EnsurePlatform(string root, PlatformRelease release)
    {
        if (Flaw(root) is { } flaw)
        {
            throw NotAPlatform(release, flaw.Why, flaw.Inner);
        }
    }


    // A platform root holds rdcore.json at its top, and the language server where that names it, inside the root, as the SDK
    // starts it: why the folder isn't one, or null when it is.
    internal static (string Why, Exception? Inner)? Flaw(string root)
    {
        try
        {
            _ = RDCoreLanguageServerLaunch.FindLanguageServer(root);
            return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return (ex.Message, ex);
        }
    }

    private static PlatformInstallException NotAPlatform(PlatformRelease release, string why, Exception? inner = null) =>
        new(InstallFailure.NotAPlatform, $"{release.ArchiveName} isn't an RDCore platform: {why.TrimEnd('.')}.", inner);

    private static void DeleteFolder(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
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
