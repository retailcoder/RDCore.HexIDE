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

    [TestMethod]
    public async Task The_stable_channel_never_offers_a_prerelease_s_version_even_one_released_as_a_release()
    {
        var github = new FakeGitHub().Release("v0.1.0").Release("v0.2.0-rc.1", prerelease: false);

        Assert.AreEqual("0.1.0", (await Feed(github).FindLatestAsync(TestContext.CancellationToken))?.Version.Text);
    }

    [TestMethod]
    public async Task A_release_whose_assets_are_anywhere_but_GitHub_is_not_offered()
    {
        var github = new FakeGitHub().Release("v0.1.0").Release("v0.2.0", host: "downloads.example.com");

        Assert.AreEqual("0.1.0", (await Feed(github).FindLatestAsync(TestContext.CancellationToken))?.Version.Text);
    }

    [TestMethod]
    public async Task A_version_is_found_whatever_the_channel_and_a_version_taken_down_is_not()
    {
        var github = new FakeGitHub().Release("v0.1.0").Release("v0.2.0-alpha.1", prerelease: true);

        Assert.AreEqual("0.2.0-alpha.1",
            (await Feed(github).FindAsync(PlatformVersion.Parse("0.2.0-alpha.1"), TestContext.CancellationToken))?.Version.Text);
        Assert.IsNull(await Feed(github).FindAsync(PlatformVersion.Parse("0.1.1"), TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task An_answer_that_isn_t_a_list_of_releases_is_refused_as_unreadable()
    {
        var feed = new ReleaseFeed(new HttpClient(new Answering("""{ "message": "API rate limit exceeded" }""")),
            new PlatformInstallerOptions { Root = Path.GetTempPath() });

        var refusal = await Assert.ThrowsExactlyAsync<PlatformInstallException>(() => feed.FindLatestAsync(TestContext.CancellationToken));

        Assert.AreEqual(InstallFailure.UnreadableReleases, refusal.Failure);
    }

    private sealed class Answering(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json) });
    }

    public TestContext TestContext { get; set; } = null!;
}
