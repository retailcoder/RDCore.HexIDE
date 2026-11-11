using RDCore.SDK.Platform;

namespace RDCore.PlatformInstaller;

/// <summary>Which releases are offered: releases only, or prereleases too.</summary>
public enum ReleaseChannel
{
    /// <summary>Releases only.</summary>
    Stable,

    /// <summary>Releases and prereleases.</summary>
    Preview,
}

/// <summary>A published release of the platform, for one runtime identifier, with the assets an installation needs.</summary>
/// <param name="Version">Its version, from its tag.</param>
/// <param name="Archive">
/// The platform's archive, whose root is the platform root: <c>rdcore-&lt;version&gt;-&lt;rid&gt;.zip</c> for Windows, and
/// <c>rdcore-&lt;version&gt;-&lt;rid&gt;.tar.gz</c> for Linux and macOS, which keeps each file's mode.
/// </param>
/// <param name="ArchiveSize">The archive's size in bytes, as the release lists it.</param>
/// <param name="Checksums">The release's <c>SHA256SUMS</c>, which names the archive's digest.</param>
public sealed record PlatformRelease(PlatformVersion Version, Uri Archive, long ArchiveSize, Uri Checksums)
{
    /// <summary>The archive's file name, as <c>SHA256SUMS</c> names it.</summary>
    public string ArchiveName => Path.GetFileName(Archive.AbsolutePath);

    /// <summary>Whether the archive is a tar.gz rather than a zip.</summary>
    internal bool IsTarGz => ArchiveName.EndsWith(".tar.gz", StringComparison.Ordinal);

    /// <summary>
    /// The release's <c>rdcore-&lt;version&gt;-&lt;rid&gt;.release.json</c>, which says what its platform was built with, or null
    /// when the release has none.
    /// </summary>
    public Uri? ReleaseInfo { get; init; }
}

/// <summary>A platform deployed on the machine.</summary>
/// <param name="Version">Its version.</param>
/// <param name="Root">Its platform root, the folder that holds <c>rdcore.json</c>.</param>
public sealed record InstalledPlatform(PlatformVersion Version, string Root)
{
    /// <summary>The platform manifest's path.</summary>
    public string Manifest => Path.Combine(Root, "rdcore.json");
}

/// <summary>What an installation is doing.</summary>
public enum InstallStage
{
    /// <summary>Downloading the platform.</summary>
    Downloading,

    /// <summary>Checking the download against the release's checksums.</summary>
    Verifying,

    /// <summary>Unpacking it.</summary>
    Extracting,

    /// <summary>Making it the platform in use.</summary>
    Deploying,
}

/// <summary>How far an installation is.</summary>
/// <param name="Stage">What it is doing.</param>
/// <param name="Bytes">While downloading, the bytes received so far; zero otherwise.</param>
/// <param name="TotalBytes">While downloading, the size to receive, when known.</param>
public sealed record InstallProgress(InstallStage Stage, long Bytes = 0, long? TotalBytes = null);
