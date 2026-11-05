namespace RDCore.PlatformInstaller.Tests;

[TestClass]
public class ReleaseFeedTests
{
    private static ReleaseFeed Feed(FakeGitHub github, ReleaseChannel channel = ReleaseChannel.Stable) =>
        new(new HttpClient(github), new PlatformInstallerOptions { Root = Path.GetTempPath(), Channel = channel });

    private static FakeGitHub Releases() => new FakeGitHub()
        .Release("v0.1.0")
        .Release("v0.2.0-alpha.1", prerelease: true)
        .Release("v0.3.0", draft: true)
        .Release("v0.1.1");

    [TestMethod]
    public async Task The_stable_channel_offers_the_newest_release_and_never_a_prerelease_or_a_draft()
    {
        var latest = await Feed(Releases()).FindLatestAsync(TestContext.CancellationToken);

        Assert.AreEqual("0.1.1", latest?.Version.Text);
        Assert.AreEqual("rdcore-0.1.1-win-x64.zip", latest?.ArchiveName);
    }

    [TestMethod]
    public async Task The_preview_channel_offers_prereleases_too_but_still_no_draft()
    {
        var latest = await Feed(Releases(), ReleaseChannel.Preview).FindLatestAsync(TestContext.CancellationToken);

        Assert.AreEqual("0.2.0-alpha.1", latest?.Version.Text);
    }

    [TestMethod]
    public async Task A_release_with_no_platform_for_the_runtime_identifier_is_not_offered()
    {
        var github = new FakeGitHub().Release("v0.1.0").Release("v0.2.0", rid: "linux-x64");

        var latest = await Feed(github).FindLatestAsync(TestContext.CancellationToken);

        Assert.AreEqual("0.1.0", latest?.Version.Text);
    }

    [TestMethod]
    public async Task No_release_at_all_is_no_release_rather_than_a_failure()
    {
        Assert.IsNull(await Feed(new FakeGitHub()).FindLatestAsync(TestContext.CancellationToken));
    }

    public TestContext TestContext { get; set; } = null!;
}
