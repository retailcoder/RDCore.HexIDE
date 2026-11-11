using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using RDCore.PlatformInstaller;
using RDCore.SDK;
using RDCore.SDK.Platform;

namespace RDCore.HexIDE;

/// <summary>
/// The add-in's own settings, kept in RDCore's local application data folder: HexIDE gives an add-in no settings store.
/// </summary>
internal sealed record AddinSettings
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// A platform root to run instead of an installed platform. It is either of two kinds:
    /// <list type="bullet">
    /// <item>a development build, such as a build's <c>artifacts/rdcore-dev</c>, which has no <c>release.json</c>: for working on
    /// RDCore itself, and before any release is published. It runs as it is.</item>
    /// <item>an RDCore the user chose, already on the machine (<see cref="SavePlatformRoot"/>): a release's platform, which has a
    /// <c>release.json</c>. It runs while it was built with an RDCore.SDK compatible with the add-in's own.</item>
    /// </list>
    /// Nothing is downloaded while it is set and runs.
    /// </summary>
    public string? PlatformRoot { get; init; }

    /// <summary>
    /// A folder to download releases from instead of GitHub, as <see cref="Platform.DevelopmentFeed"/> lays one out: for trying
    /// the question, the download and the installation before any release is published. <see cref="PlatformRoot"/>, when it is
    /// set too, wins, and nothing is downloaded.
    /// </summary>
    public string? DevelopmentFeed { get; init; }

    /// <summary>Which releases are installed. Absent means the add-in's own kind: preview for a prerelease add-in.</summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<ReleaseChannel>))]
    public ReleaseChannel? Channel { get; init; }

    /// <summary>The channel in effect.</summary>
    public ReleaseChannel EffectiveChannel => Channel ?? (AddinIsPrerelease ? ReleaseChannel.Preview : ReleaseChannel.Stable);

    /// <summary>Where the settings are read from.</summary>
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RDCore", "HexIDE", "settings.json");

    /// <summary>Whether this build of the add-in is a prerelease, such as <c>0.1.0-alpha</c>.</summary>
    public static bool AddinIsPrerelease =>
        typeof(AddinSettings).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0].Contains('-') ?? false;

    /// <summary>
    /// The version of the RDCore.SDK package the add-in runs with, as the SDK says it (<see cref="RDCoreSdk.Version"/>): the
    /// installer offers only a platform built with a compatible one.
    /// </summary>
    public static PlatformVersion SdkVersion => RDCoreSdk.Version;

    /// <summary>
    /// Writes <paramref name="platformRoot"/> as the <c>platformRoot</c> of the settings at <paramref name="path"/>, or takes it out
    /// for null, and keeps everything else the file holds. A file that can't be read as settings is written afresh.
    /// </summary>
    /// <param name="path">The settings file, as <see cref="DefaultPath"/>.</param>
    /// <param name="platformRoot">The platform root the user chose, or null for none.</param>
    /// <exception cref="IOException">The file can't be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The file can't be written.</exception>
    public static void SavePlatformRoot(string path, string? platformRoot)
    {
        JsonObject settings;
        try
        {
            settings = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? [] : [];
        }
        catch (JsonException)
        {
            settings = [];
        }

        // Whatever the spelling it was read with, it is written once.
        foreach (var name in settings.Select(property => property.Key)
            .Where(key => string.Equals(key, "platformRoot", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            settings.Remove(name);
        }

        if (platformRoot is not null)
        {
            settings["platformRoot"] = platformRoot;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>The settings at <paramref name="path"/>; the defaults when there are none, or they can't be read.</summary>
    public static AddinSettings Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<AddinSettings>(File.ReadAllText(path), Json) ?? new() : new();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new();
        }
    }
}
