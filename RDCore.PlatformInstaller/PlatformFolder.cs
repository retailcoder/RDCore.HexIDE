using RDCore.SDK.Client;
using RDCore.SDK.Platform;

namespace RDCore.PlatformInstaller;

/// <summary>
/// A platform on the machine that no installer deployed, as an unpacked release archive leaves one: what its
/// <c>rdcore.json</c> and its <c>release.json</c> say of it, as the SDK reads them.
/// </summary>
/// <param name="Root">Its platform root, the folder that holds <c>rdcore.json</c>.</param>
/// <param name="Version">The version its <c>rdcore.json</c> names, as it is written there; empty when it names none.</param>
/// <param name="RuntimeIdentifier">The runtime identifier its <c>rdcore.json</c> names, or null when it names none.</param>
/// <param name="HostService">The full path of the <c>rdc</c> executable its <c>rdcore.json</c> names, or null when it names none.</param>
/// <param name="HasReleaseInfo">Whether it has a <c>release.json</c>, as a release's platform has and a development build doesn't.</param>
/// <param name="SdkVersion">
/// The RDCore.SDK version its <c>release.json</c> says it was built with, or null when it says none, or isn't a release manifest.
/// </param>
public sealed record PlatformFolder(string Root, string Version, string? RuntimeIdentifier, string? HostService, bool HasReleaseInfo,
    PlatformVersion? SdkVersion)
{
    /// <summary>
    /// The platform at <paramref name="root"/>, or null when the folder isn't one: it has no <c>rdcore.json</c> that can be
    /// read, or not the language server that <c>rdcore.json</c> names, inside the folder
    /// (<see cref="RDCoreLanguageServerLaunch.FindLanguageServer"/>).
    /// </summary>
    /// <param name="root">The folder.</param>
    public static PlatformFolder? Read(string root)
    {
        try
        {
            var full = Path.GetFullPath(root);
            if (!Directory.Exists(full) || PlatformInstaller.Flaw(full) is not null)
            {
                return null;
            }

            var manifest = RDCoreLanguageServerLaunch.ReadManifest(full);
            var hasReleaseInfo = File.Exists(Path.Combine(full, PlatformReleaseManifest.FileName));
            var host = NoneIfEmpty(manifest.HostService) is { } named
                ? Path.GetFullPath(Path.Combine(full, named.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar)))
                : null;
            return new PlatformFolder(full, manifest.PlatformVersion, NoneIfEmpty(manifest.RuntimeIdentifier), host, hasReleaseInfo,
                hasReleaseInfo ? SdkOf(full) : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            return null;
        }
    }

    // a release.json that is too big, or isn't one, says nothing of the RDCore.SDK: its folder is still one that has one.
    private static PlatformVersion? SdkOf(string root)
    {
        try
        {
            return PlatformReleaseManifest.Read(root)?.SdkVersion;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether it was built with an RDCore.SDK compatible with <paramref name="sdk"/>, the one a client was built with, by the
    /// SDK's rule (<see cref="PlatformVersion.IsCompatibleWith"/>). One that doesn't say isn't.
    /// </summary>
    /// <param name="sdk">The RDCore.SDK version the client was built with.</param>
    public bool IsBuiltWith(PlatformVersion sdk) => SdkVersion is { } built && built.IsCompatibleWith(sdk);

    /// <summary>Whether it was built for <paramref name="runtimeIdentifier"/>; one whose <c>rdcore.json</c> names none is taken to be.</summary>
    /// <param name="runtimeIdentifier">The runtime identifier, as <see cref="PlatformInstallerOptions.MachineRuntimeIdentifier"/>.</param>
    public bool IsFor(string runtimeIdentifier) =>
        RuntimeIdentifier is null || string.Equals(RuntimeIdentifier, runtimeIdentifier, StringComparison.OrdinalIgnoreCase);

    private static string? NoneIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
