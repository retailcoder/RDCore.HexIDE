using System.Text.Json;

namespace RDCore.PlatformInstaller;

/// <summary>
/// A platform on the machine that no installer deployed, as an unpacked release archive leaves one: what its
/// <c>rdcore.json</c> and its <c>release.json</c> say of it.
/// </summary>
/// <param name="Root">Its platform root, the folder that holds <c>rdcore.json</c>.</param>
/// <param name="Version">The version its <c>rdcore.json</c> names, as it is written there; empty when it names none.</param>
/// <param name="RuntimeIdentifier">The runtime identifier its <c>rdcore.json</c> names, or null when it names none.</param>
/// <param name="HostService">The full path of the <c>rdc</c> executable its <c>rdcore.json</c> names, or null when it names none.</param>
/// <param name="HasReleaseInfo">Whether it has a <c>release.json</c>, as a release's platform has and a development build doesn't.</param>
/// <param name="SdkVersion">The RDCore.SDK version its <c>release.json</c> says it was built with, or null when it says none.</param>
public sealed record PlatformFolder(string Root, string Version, string? RuntimeIdentifier, string? HostService, bool HasReleaseInfo,
    PlatformVersion? SdkVersion)
{
    /// <summary>The most a platform's <c>release.json</c> may be; a bigger one says nothing. 4 MiB.</summary>
    public const long MaxReleaseInfoBytes = 4L << 20;

    /// <summary>
    /// The platform at <paramref name="root"/>, or null when the folder isn't one: it has no <c>rdcore.json</c> that can be
    /// read, or not the language server that <c>rdcore.json</c> names, inside the folder.
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

            using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(full, "rdcore.json")));
            var releaseInfo = new FileInfo(Path.Combine(full, "release.json"));
            var sdk = releaseInfo.Exists && releaseInfo.Length <= MaxReleaseInfoBytes
                ? ReleaseFeed.SdkOf(File.ReadAllBytes(releaseInfo.FullName))
                : null;
            var host = Text(manifest.RootElement, "hostService");
            return new PlatformFolder(full, Text(manifest.RootElement, "platformVersion") ?? "", Text(manifest.RootElement, "rid"),
                host is null ? null : Path.GetFullPath(Path.Combine(full, host.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar))),
                releaseInfo.Exists, sdk);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether it was built with an RDCore.SDK compatible with <paramref name="sdk"/>, the one a client was built with, by the
    /// rule a release is offered by: the same major and minor versions before 1.0, and the same major version from 1.0. One
    /// that doesn't say isn't.
    /// </summary>
    /// <param name="sdk">The RDCore.SDK version the client was built with.</param>
    public bool IsBuiltWith(PlatformVersion sdk) => SdkVersion is { } built && ReleaseFeed.IsCompatible(sdk, built);

    /// <summary>Whether it was built for <paramref name="runtimeIdentifier"/>; one whose <c>rdcore.json</c> names none is taken to be.</summary>
    /// <param name="runtimeIdentifier">The runtime identifier, as <see cref="PlatformInstallerOptions.MachineRuntimeIdentifier"/>.</param>
    public bool IsFor(string runtimeIdentifier) =>
        RuntimeIdentifier is null || string.Equals(RuntimeIdentifier, runtimeIdentifier, StringComparison.OrdinalIgnoreCase);

    private static string? Text(JsonElement manifest, string name) =>
        manifest.ValueKind == JsonValueKind.Object && manifest.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
