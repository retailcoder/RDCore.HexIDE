using System.Reflection;
using System.Text.Json;
using RDCore.PlatformInstaller;

namespace RDCore.HexIDE;

/// <summary>
/// The add-in's own settings, kept in RDCore's local application data folder: HexIDE gives an add-in no settings store.
/// </summary>
internal sealed record AddinSettings
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// A platform root to run instead of an installed platform, such as a build's <c>artifacts/rdcore-dev</c>: for working on
    /// RDCore itself, and before any release is published. Nothing is downloaded while it is set.
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
