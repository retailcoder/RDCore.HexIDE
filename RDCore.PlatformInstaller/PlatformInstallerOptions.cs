namespace RDCore.PlatformInstaller;

/// <summary>Where the platform is installed from and to.</summary>
public sealed record PlatformInstallerOptions
{
    /// <summary>
    /// The folder the installer owns: each deployed version under <c>versions/</c>, the one in use named by
    /// <c>installer.json</c>, and downloads in progress under <c>downloads/</c>. Nothing outside it is written.
    /// </summary>
    public required string Root { get; init; }

    /// <summary>Which releases are offered.</summary>
    public ReleaseChannel Channel { get; init; } = ReleaseChannel.Stable;

    /// <summary>The GitHub repository whose releases carry the platform, as <c>owner/name</c>.</summary>
    public string Repository { get; init; } = "rubberduck-vba/RDCore";

    /// <summary>The runtime identifier of the platform to install. Releases are built for <c>win-x64</c> only, for now.</summary>
    public string RuntimeIdentifier { get; init; } = "win-x64";

    /// <summary>The GitHub REST API's base address.</summary>
    public Uri ApiBase { get; init; } = new("https://api.github.com/");

    /// <summary>
    /// The hosts a release's assets are downloaded from, over https only: an asset anywhere else makes its release one the
    /// installer doesn't offer. GitHub then redirects a download to its own storage.
    /// </summary>
    public IReadOnlyList<string> AssetHosts { get; init; } = ["github.com"];

    /// <summary>The most a platform's zip may be; a bigger download is refused. 1 GiB.</summary>
    public long MaxArchiveBytes { get; init; } = 1L << 30;

    /// <summary>The most a platform may unpack to; a bigger archive is refused before it is unpacked. 4 GiB.</summary>
    public long MaxExtractedBytes { get; init; } = 4L << 30;

    /// <summary>The most entries a platform's zip may have; one with more is refused before it is unpacked.</summary>
    public int MaxEntries { get; init; } = 50_000;

    /// <summary>
    /// The root a user's platform is installed under: <c>RDCore/Platform</c> in the local application data folder, which is
    /// RDCore's own, not any one client's.
    /// </summary>
    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RDCore", "Platform");
}
