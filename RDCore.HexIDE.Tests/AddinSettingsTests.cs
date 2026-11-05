using RDCore.PlatformInstaller;

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
    public void Settings_are_read_by_name_and_settings_that_can_t_be_read_are_the_defaults()
    {
        File.WriteAllText(path, """{ "platformRoot": "C:/Dev/RDCore/artifacts/rdcore-dev", "channel": "Stable" }""");
        var settings = AddinSettings.Load(path);
        Assert.AreEqual("C:/Dev/RDCore/artifacts/rdcore-dev", settings.PlatformRoot);
        Assert.AreEqual(ReleaseChannel.Stable, settings.Channel);

        File.WriteAllText(path, "{ not json");
        Assert.AreEqual(new AddinSettings(), AddinSettings.Load(path));
        Assert.AreEqual(new AddinSettings(), AddinSettings.Load(path + ".missing"));
    }
}
