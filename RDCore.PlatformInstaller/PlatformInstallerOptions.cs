using System.Runtime.InteropServices;
using RDCore.SDK.Platform;

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

    /// <summary>The runtime identifier of the platform to install: by default <see cref="MachineRuntimeIdentifier"/>.</summary>
    public string RuntimeIdentifier { get; init; } = MachineRuntimeIdentifier;

    /// <summary>The GitHub REST API's base address.</summary>
    public Uri ApiBase { get; init; } = new("https://api.github.com/");

    /// <summary>
    /// The hosts a release's assets are downloaded from, over https only: an asset anywhere else makes its release one the
    /// installer doesn't offer. GitHub then redirects a download to its own storage.
    /// </summary>
    public IReadOnlyList<string> AssetHosts { get; init; } = ["github.com"];

    /// <summary>The most a platform's archive may be; a bigger download is refused. 1 GiB.</summary>
    public long MaxArchiveBytes { get; init; } = 1L << 30;

    /// <summary>The most a platform may unpack to; a bigger archive is refused before it is unpacked. 4 GiB.</summary>
    public long MaxExtractedBytes { get; init; } = 4L << 30;

    /// <summary>
    /// The most entries a platform's archive may have, its folders counted; one with more is refused before it is unpacked.
    /// </summary>
    public int MaxEntries { get; init; } = 50_000;

    /// <summary>The most a release's <c>SHA256SUMS</c> may be; a bigger one is refused before the platform is downloaded. 64 KiB.</summary>
    public long MaxChecksumsBytes { get; init; } = 64L << 10;

    /// <summary>The most GitHub's list of releases may be; a bigger answer is refused as not one. 16 MiB.</summary>
    public long MaxReleasesBytes { get; init; } = 16L << 20;

    /// <summary>
    /// The most a release's <c>rdcore-&lt;version&gt;-&lt;rid&gt;.release.json</c> may be; the release of a bigger one is not
    /// offered. <see cref="PlatformReleaseManifest.MaxBytes"/>, 4 MiB, which is also the most a release manifest is read to.
    /// </summary>
    public long MaxReleaseInfoBytes { get; init; } = PlatformReleaseManifest.MaxBytes;

    /// <summary>
    /// The version of the <c>RDCore.SDK</c> package the client was built with. A release is offered only when its platform was
    /// built with a compatible one, as its <c>rdcore-&lt;version&gt;-&lt;rid&gt;.release.json</c> says: the same major and minor
    /// versions before 1.0, and the same major version from 1.0. A release whose <c>release.json</c> doesn't say, or doesn't
    /// match the release's <c>SHA256SUMS</c>, is not offered. Null offers every release, whatever it was built with.
    /// </summary>
    public PlatformVersion? SdkVersion { get; init; }

    /// <summary>
    /// The root a user's platform is installed under: <c>RDCore/Platform</c> in the local application data folder, which is
    /// RDCore's own, not any one client's.
    /// </summary>
    public static string DefaultRoot => SharedPlatformRoot.ForThisUser;

    /// <summary>
    /// The runtime identifier of a platform built for this machine: its system (<c>win</c>, <c>osx</c> or <c>linux</c>) and the
    /// process's architecture, as in <c>win-x64</c> or <c>osx-arm64</c>. Releases carry a platform for <c>win-x64</c>,
    /// <c>linux-x64</c>, <c>osx-x64</c> and <c>osx-arm64</c>; on any other machine, no release is offered.
    /// </summary>
    public static string MachineRuntimeIdentifier =>
        $"{(OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux")}-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";
}
