using System.Reflection;
using RDCore.PlatformInstaller;
using RDCore.SDK.Client;
using RDCore.SDK.Platform;

namespace RDCore.HexIDE.Tests;

[TestClass]
public sealed class AddinSettingsTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), "rdcore-settings-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose() => File.Delete(path);

    [TestMethod]
    public void A_prerelease_add_in_installs_from_the_preview_channel_unless_told_otherwise()
    {
        Assert.IsTrue(AddinSettings.AddinIsPrerelease, "this build is 0.1.0-alpha");
        Assert.AreEqual(ReleaseChannel.Preview, new AddinSettings().EffectiveChannel);
        Assert.AreEqual(ReleaseChannel.Stable, new AddinSettings { Channel = ReleaseChannel.Stable }.EffectiveChannel);
    }

    [TestMethod]
    public void The_RDCore_SDK_version_is_the_one_the_add_in_runs_with_and_was_built_with()
    {
        var loaded = typeof(RDCorePlatformClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

        Assert.AreEqual(PlatformVersion.Parse(loaded), AddinSettings.SdkVersion);
        var built = typeof(AddinSettingsTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "RDCoreSdkVersion").Value!;
        Assert.AreEqual(PlatformVersion.Parse(built), AddinSettings.SdkVersion, "the SDK stamps the version it is packed as");
    }

    [TestMethod]
    public void Settings_are_read_by_name_and_settings_that_can_t_be_read_are_the_defaults()
    {
        File.WriteAllText(path, """{ "platformRoot": "C:/Dev/RDCore/artifacts/rdcore-dev", "channel": "Stable", "developmentFeed": "C:/feed" }""");
        var settings = AddinSettings.Load(path);
        Assert.AreEqual("C:/Dev/RDCore/artifacts/rdcore-dev", settings.PlatformRoot);
        Assert.AreEqual("C:/feed", settings.DevelopmentFeed);
        Assert.AreEqual(ReleaseChannel.Stable, settings.Channel);

        File.WriteAllText(path, "{ not json");
        Assert.AreEqual(new AddinSettings(), AddinSettings.Load(path));
        Assert.AreEqual(new AddinSettings(), AddinSettings.Load(path + ".missing"));
    }
}
