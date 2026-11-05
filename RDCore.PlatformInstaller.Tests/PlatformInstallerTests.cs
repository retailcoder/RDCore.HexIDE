namespace RDCore.PlatformInstaller.Tests;

[TestClass]
public sealed class PlatformInstallerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "rdcore-installer-" + Guid.NewGuid().ToString("N"));

    public TestContext TestContext { get; set; } = null!;

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing was installed.
        }
    }

    private PlatformInstaller Installer(FakeGitHub github, ReleaseChannel channel = ReleaseChannel.Stable) =>
        new(new PlatformInstallerOptions { Root = root, Channel = channel }, new HttpClient(github));

    private async Task<InstalledPlatform> InstallLatest(PlatformInstaller installer, IProgress<InstallProgress>? progress = null)
    {
        var release = await installer.Feed.FindLatestAsync(TestContext.CancellationToken);
        Assert.IsNotNull(release);
        return await installer.InstallAsync(release, progress, TestContext.CancellationToken);
    }

    private string[] Versions() =>
        Directory.EnumerateDirectories(Path.Combine(root, "versions")).Select(Path.GetFileName).Order().ToArray()!;

    private bool NothingLeftOver() =>
        (!Directory.Exists(Path.Combine(root, "downloads")) || !Directory.EnumerateFileSystemEntries(Path.Combine(root, "downloads")).Any())
        && (!Directory.Exists(Path.Combine(root, "versions"))
            || !Directory.EnumerateDirectories(Path.Combine(root, "versions")).Any(folder => folder.EndsWith(".partial")));

    // Progress<T> posts to the thread pool, so its reports may land after the call returns; this one reports as it is told.
    private sealed class Recorded : IProgress<InstallProgress>
    {
        public List<InstallProgress> Reports { get; } = [];
        public void Report(InstallProgress value)
        {
            lock (Reports) Reports.Add(value);
        }
    }

    [TestMethod]
    public async Task The_newest_release_is_downloaded_checked_and_deployed_as_the_platform_in_use()
    {
        var installer = Installer(new FakeGitHub().Release("v0.1.0"));
        Assert.IsNull(installer.Current);
        var progress = new Recorded();

        var installed = await InstallLatest(installer, progress);

        Assert.AreEqual(installed, installer.Current);
        Assert.AreEqual("0.1.0", installed.Version.Text);
        Assert.IsTrue(File.Exists(Path.Combine(installed.Root, "RDCore.LanguageServer", "RDCore.LanguageServer.exe")));
        Assert.IsTrue(NothingLeftOver());
        var stages = progress.Reports.Select(report => report.Stage).Distinct().ToArray();
        CollectionAssert.AreEqual(new[] { InstallStage.Downloading, InstallStage.Verifying, InstallStage.Extracting, InstallStage.Deploying }, stages);
        var last = progress.Reports.Last(report => report.Stage == InstallStage.Downloading);
        Assert.AreEqual(last.TotalBytes, last.Bytes, "the whole archive was received");
    }

    [TestMethod]
    public async Task A_download_that_doesn_t_match_its_checksum_is_refused_and_nothing_is_deployed()
    {
        var installer = Installer(new FakeGitHub().Release("v0.1.0", digest: new string('0', 64)));

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => InstallLatest(installer));

        Assert.IsNull(installer.Current);
        Assert.IsFalse(Directory.Exists(Path.Combine(root, "versions", "0.1.0")));
        Assert.IsTrue(NothingLeftOver());
    }

    [TestMethod]
    public async Task An_archive_that_isn_t_a_platform_is_refused_and_nothing_is_deployed()
    {
        var noManifest = new Dictionary<string, string> { ["readme.txt"] = "not a platform" };
        var noServer = new Dictionary<string, string> { ["rdcore.json"] = """{ "langService": "RDCore.LanguageServer/RDCore.LanguageServer.exe" }""" };

        foreach (var entries in new[] { noManifest, noServer })
        {
            var installer = Installer(new FakeGitHub().Release("v0.1.0", entries: entries));

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => InstallLatest(installer));

            Assert.IsNull(installer.Current);
            Assert.IsTrue(NothingLeftOver());
        }
    }

    [TestMethod]
    public async Task A_failed_update_leaves_the_platform_in_use_as_it_was()
    {
        await InstallLatest(Installer(new FakeGitHub().Release("v0.1.0")));
        var installer = Installer(new FakeGitHub().Release("v0.1.0").Release("v0.2.0", digest: new string('0', 64)));

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => InstallLatest(installer));

        Assert.AreEqual("0.1.0", installer.Current?.Version.Text);
    }

    [TestMethod]
    public async Task A_version_deployed_already_is_made_current_without_downloading_it_again()
    {
        await InstallLatest(Installer(new FakeGitHub().Release("v0.1.0")));
        var github = new FakeGitHub().Release("v0.1.0");

        var installed = await InstallLatest(Installer(github));

        Assert.AreEqual("0.1.0", installed.Version.Text);
        Assert.IsFalse(github.Requests.Any(request => request.AbsolutePath.EndsWith(".zip")), "nothing was downloaded");
    }

    [TestMethod]
    public async Task A_platform_in_use_that_has_lost_its_manifest_is_no_platform_and_is_deployed_again()
    {
        var installer = Installer(new FakeGitHub().Release("v0.1.0"));
        var installed = await InstallLatest(installer);
        File.Delete(installed.Manifest);

        Assert.IsNull(installer.Current);
        await InstallLatest(installer);
        Assert.AreEqual(installed, installer.Current);
    }

    [TestMethod]
    public async Task An_update_keeps_the_version_before_it_and_removes_older_ones()
    {
        await InstallLatest(Installer(new FakeGitHub().Release("v0.1.0")));
        await InstallLatest(Installer(new FakeGitHub().Release("v0.2.0")));
        var installer = Installer(new FakeGitHub().Release("v0.3.0"));

        await InstallLatest(installer);

        Assert.AreEqual("0.3.0", installer.Current?.Version.Text);
        CollectionAssert.AreEqual(new[] { "0.2.0", "0.3.0" }, Versions());
    }

    [TestMethod]
    public async Task Two_installations_at_once_take_turns_and_download_the_platform_once()
    {
        var github = new FakeGitHub().Release("v0.1.0");
        github.DownloadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Installer(github);
        var second = Installer(github);
        var release = await first.Feed.FindLatestAsync(TestContext.CancellationToken);
        Assert.IsNotNull(release);

        var installs = new[]
        {
            first.InstallAsync(release, null, TestContext.CancellationToken),
            second.InstallAsync(release, null, TestContext.CancellationToken),
        };
        github.DownloadGate.SetResult();
        var installed = await Task.WhenAll(installs);

        Assert.AreEqual(installed[0], installed[1]);
        Assert.AreEqual(1, github.Requests.Count(request => request.AbsolutePath.EndsWith(".zip")));
    }

    [TestMethod]
    public async Task A_cancelled_download_leaves_nothing_behind()
    {
        var github = new FakeGitHub().Release("v0.1.0");
        github.DownloadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var installer = Installer(github);
        var release = await installer.Feed.FindLatestAsync(TestContext.CancellationToken);
        Assert.IsNotNull(release);
        using var cancellation = new CancellationTokenSource();

        var install = installer.InstallAsync(release, null, cancellation.Token);
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => install);
        Assert.IsNull(installer.Current);
        Assert.IsTrue(NothingLeftOver());
    }
}
