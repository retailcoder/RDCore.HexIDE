using RDCore.SDK.Platform;

namespace RDCore.PlatformInstaller.Tests;

[TestClass]
public class ReleaseFeedTests
{
    private static ReleaseFeed Feed(FakeGitHub github, ReleaseChannel channel = ReleaseChannel.Stable, string rid = "win-x64",
        string? sdk = null, long maxReleaseInfoBytes = 4L << 20) =>
        new(new HttpClient(github), new PlatformInstallerOptions
        {
            Root = Path.GetTempPath(),
            Channel = channel,
            RuntimeIdentifier = rid,
            SdkVersion = sdk is null ? null : PlatformVersion.Parse(sdk),
            MaxReleaseInfoBytes = maxReleaseInfoBytes,
        });

    [TestMethod]
    public async Task A_client_is_offered_the_newest_release_built_with_a_compatible_RDCore_SDK_and_never_a_newer_one_built_with_another()
    {
        var github = new FakeGitHub()
            .Release("v0.1.0", sdk: "0.1.0-alpha")
            .Release("v0.1.1", sdk: "0.1.1-alpha")
            .Release("v0.2.0", sdk: "0.2.0-alpha");

        var latest = await Feed(github, sdk: "0.1.1-alpha").FindLatestAsync(TestContext.CancellationToken);

        Assert.AreEqual("0.1.1", latest?.Version.Text);
        Assert.IsFalse(github.Requests.Any(request => request.AbsolutePath.EndsWith(".zip")), "no archive is downloaded to choose");
    }

    [TestMethod]
    public async Task No_release_is_offered_when_none_was_built_with_a_compatible_RDCore_SDK()
    {
        var github = new FakeGitHub().Release("v0.2.0", sdk: "0.2.0-alpha").Release("v1.0.0", sdk: "1.0.0");

        Assert.IsNull(await Feed(github, sdk: "0.1.1-alpha").FindLatestAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task A_release_whose_release_json_says_no_RDCore_SDK_is_not_offered()
    {
        var github = new FakeGitHub().Release("v0.1.0").Release("v0.1.1", sdk: null);

        Assert.AreEqual("0.1.0", (await Feed(github, sdk: FakeGitHub.Sdk).FindLatestAsync(TestContext.CancellationToken))?.Version.Text);
    }

    [TestMethod]
    public async Task A_release_without_a_release_json_is_not_offered()
    {
        var github = new FakeGitHub().Release("v0.1.0").Release("v0.1.1", releaseInfo: false);

        Assert.AreEqual("0.1.0", (await Feed(github, sdk: FakeGitHub.Sdk).FindLatestAsync(TestContext.CancellationToken))?.Version.Text);
    }

    [TestMethod]
    public async Task A_release_whose_release_json_doesn_t_match_its_checksum_is_not_offered()
    {
        var github = new FakeGitHub().Release("v0.1.0").Release("v0.1.1", releaseInfoDigest: new string('0', 64));

        Assert.AreEqual("0.1.0", (await Feed(github, sdk: FakeGitHub.Sdk).FindLatestAsync(TestContext.CancellationToken))?.Version.Text);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task A_release_whose_release_json_is_bigger_than_one_should_be_is_not_offered(bool unsized)
    {
        var github = new FakeGitHub { Unsized = unsized }.Release("v0.1.0");

        Assert.IsNull(await Feed(github, sdk: FakeGitHub.Sdk, maxReleaseInfoBytes: 16).FindLatestAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task A_version_is_found_whatever_RDCore_SDK_it_was_built_with()
    {
        var github = new FakeGitHub().Release("v0.2.0", sdk: "0.2.0-alpha");

        Assert.AreEqual("0.2.0",
            (await Feed(github, sdk: "0.1.1-alpha").FindAsync(PlatformVersion.Parse("0.2.0"), TestContext.CancellationToken))?.Version.Text);
    }

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
    [DataRow("linux-x64")]
    [DataRow("osx-x64")]
    [DataRow("osx-arm64")]
    public async Task Off_Windows_a_release_s_tar_gz_for_the_runtime_identifier_is_offered_and_another_s_platform_is_not(string rid)
    {
        var github = new FakeGitHub().Release("v0.1.0", rid: rid, tarGz: true).Release("v0.2.0").Release("v0.2.1", rid: "linux-arm64", tarGz: true);

        var latest = await Feed(github, rid: rid).FindLatestAsync(TestContext.CancellationToken);

        Assert.AreEqual("0.1.0", latest?.Version.Text);
        Assert.AreEqual($"rdcore-0.1.0-{rid}.tar.gz", latest?.ArchiveName);
    }

    [TestMethod]
    public void The_runtime_identifier_is_the_machine_s_own_unless_one_is_given()
    {
        var system = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

        Assert.AreEqual($"{system}-{architecture}", new PlatformInstallerOptions { Root = Path.GetTempPath() }.RuntimeIdentifier);
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

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task An_answer_bigger_than_a_list_of_releases_should_be_is_refused_as_unreadable_whether_or_not_it_says_its_size(bool unsized)
    {
        var github = new FakeGitHub { Unsized = unsized }.Release("v0.1.0");
        var feed = new ReleaseFeed(new HttpClient(github), new PlatformInstallerOptions { Root = Path.GetTempPath(), MaxReleasesBytes = 64 });

        var refusal = await Assert.ThrowsExactlyAsync<PlatformInstallException>(() => feed.FindLatestAsync(TestContext.CancellationToken));

        Assert.AreEqual(InstallFailure.UnreadableReleases, refusal.Failure);
    }

    [TestMethod]
    public async Task An_answer_that_doesn_t_say_its_size_is_read_whole()
    {
        var latest = await Feed(new FakeGitHub { Unsized = true }.Release("v0.1.0").Release("v0.1.1")).FindLatestAsync(TestContext.CancellationToken);

        Assert.AreEqual("0.1.1", latest?.Version.Text);
    }

    private sealed class Answering(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json) });
    }

    public TestContext TestContext { get; set; } = null!;
}
