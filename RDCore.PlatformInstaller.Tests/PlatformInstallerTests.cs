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

    private PlatformInstaller Installer(FakeGitHub github, ReleaseChannel channel = ReleaseChannel.Stable,
        Func<PlatformInstallerOptions, PlatformInstallerOptions>? adjust = null) =>
        new((adjust ?? (options => options))(new PlatformInstallerOptions { Root = root, Channel = channel }), new HttpClient(github));

    private async Task<InstalledPlatform> InstallLatest(PlatformInstaller installer, IProgress<InstallProgress>? progress = null)
    {
        var release = await installer.Feed.FindLatestAsync(TestContext.CancellationToken);
        Assert.IsNotNull(release);
        return await installer.InstallAsync(release, progress, TestContext.CancellationToken);
    }

    private async Task Refused(PlatformInstaller installer, InstallFailure failure)
    {
        var refusal = await Assert.ThrowsExactlyAsync<PlatformInstallException>(() => InstallLatest(installer));
        Assert.AreEqual(failure, refusal.Failure, refusal.Message);
        Assert.IsNull(installer.Current);
        Assert.IsTrue(NothingLeftOver());
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
        public TaskCompletionSource SomeBytes { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Report(InstallProgress value)
        {
            lock (Reports)
            {
                Reports.Add(value);
            }

            if (value is { Stage: InstallStage.Downloading, Bytes: > 0 })
            {
                SomeBytes.TrySetResult();
            }
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
    public async Task A_checksum_marked_as_read_in_binary_mode_is_read_as_well()
    {
        var installed = await InstallLatest(Installer(new FakeGitHub().Release("v0.1.0", binaryMarker: true)));

        Assert.AreEqual("0.1.0", installed.Version.Text);
    }

    [TestMethod]
    public async Task A_download_that_doesn_t_match_its_checksum_is_refused_and_nothing_is_deployed()
    {
        await Refused(Installer(new FakeGitHub().Release("v0.1.0", digest: new string('0', 64))), InstallFailure.ChecksumMismatch);

        Assert.IsFalse(Directory.Exists(Path.Combine(root, "versions", "0.1.0")));
    }

    [TestMethod]
    public async Task An_archive_that_isn_t_a_platform_is_refused_and_nothing_is_deployed()
    {
        var noManifest = new Dictionary<string, string> { ["readme.txt"] = "not a platform" };
        var noServer = new Dictionary<string, string> { ["rdcore.json"] = """{ "langService": "RDCore.LanguageServer/RDCore.LanguageServer.exe" }""" };
        var notAName = new Dictionary<string, string> { ["rdcore.json"] = """{ "langService": 42 }""" };

        foreach (var entries in new[] { noManifest, noServer, notAName })
        {
            await Refused(Installer(new FakeGitHub().Release("v0.1.0", entries: entries)), InstallFailure.NotAPlatform);
        }
    }

    [TestMethod]
    [DataRow("../evil.exe")]
    [DataRow("RDCore.LanguageServer/../../evil.exe")]
    [DataRow("C:/Windows/System32/cmd.exe")]
    [DataRow("/bin/sh")]
    public async Task A_manifest_naming_a_language_server_outside_the_platform_is_refused(string languageServer)
    {
        var entries = new Dictionary<string, string>
        {
            ["rdcore.json"] = $$"""{ "langService": "{{languageServer}}" }""",
            ["RDCore.LanguageServer/RDCore.LanguageServer.exe"] = "",
        };
        // There is a file where the relative ones lead, so only their leading outside refuses them.
        Directory.CreateDirectory(Path.Combine(root, "versions"));
        File.WriteAllText(Path.Combine(root, "versions", "evil.exe"), "");

        await Refused(Installer(new FakeGitHub().Release("v0.1.0", entries: entries)), InstallFailure.NotAPlatform);
    }

    [TestMethod]
    public async Task An_archive_with_an_entry_that_would_land_outside_the_platform_is_refused_and_writes_nothing_there()
    {
        var entries = new Dictionary<string, string>(FakeGitHub.Platform("0.1.0")) { ["../escaped.txt"] = "outside" };

        await Refused(Installer(new FakeGitHub().Release("v0.1.0", entries: entries)), InstallFailure.NotAPlatform);

        Assert.IsFalse(File.Exists(Path.Combine(root, "versions", "escaped.txt")));
    }

    [TestMethod]
    public async Task A_download_bigger_than_its_release_says_is_refused()
    {
        await Refused(Installer(new FakeGitHub().Release("v0.1.0", listedSize: 10)), InstallFailure.TooLarge);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task A_SHA256SUMS_bigger_than_one_should_be_is_refused_before_the_platform_is_downloaded(bool unsized)
    {
        var github = new FakeGitHub { Unsized = unsized }.Release("v0.1.0");

        await Refused(Installer(github, adjust: options => options with { MaxChecksumsBytes = 16 }), InstallFailure.TooLarge);

        Assert.IsFalse(github.Requests.Any(request => request.AbsolutePath.EndsWith(".zip")));
    }

    [TestMethod]
    public async Task A_release_whose_files_don_t_say_their_size_installs()
    {
        var installed = await InstallLatest(Installer(new FakeGitHub { Unsized = true }.Release("v0.1.0")));

        Assert.AreEqual("0.1.0", installed.Version.Text);
    }

    [TestMethod]
    public async Task An_archive_with_too_many_entries_or_too_much_to_unpack_is_refused_before_it_is_unpacked()
    {
        await Refused(Installer(new FakeGitHub().Release("v0.1.0"), adjust: options => options with { MaxEntries = 2 }),
            InstallFailure.TooLarge);
        await Refused(Installer(new FakeGitHub().Release("v0.1.0"), adjust: options => options with { MaxExtractedBytes = 16 }),
            InstallFailure.TooLarge);
        Assert.IsFalse(Directory.Exists(Path.Combine(root, "versions", "0.1.0.partial")));
    }

    [TestMethod]
    public async Task A_failed_update_leaves_the_platform_in_use_as_it_was()
    {
        await InstallLatest(Installer(new FakeGitHub().Release("v0.1.0")));
        var installer = Installer(new FakeGitHub().Release("v0.1.0").Release("v0.2.0", digest: new string('0', 64)));

        await Assert.ThrowsExactlyAsync<PlatformInstallException>(() => InstallLatest(installer));

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
    public async Task An_older_version_installed_again_is_kept_beside_the_one_in_use_which_stays_in_use()
    {
        await InstallLatest(Installer(new FakeGitHub().Release("v0.1.0")));
        await InstallLatest(Installer(new FakeGitHub().Release("v0.2.0")));
        await InstallLatest(Installer(new FakeGitHub().Release("v0.3.0")));
        var installer = Installer(new FakeGitHub().Release("v0.1.0").Release("v0.3.0"));
        var older = await installer.Feed.FindAsync(PlatformVersion.Parse("0.1.0"), TestContext.CancellationToken);
        Assert.IsNotNull(older);

        // As a server whose launch names 0.1.0 has it deployed again.
        var installed = await installer.InstallAsync(older, null, TestContext.CancellationToken);

        Assert.AreEqual("0.3.0", installer.Current?.Version.Text);
        Assert.IsTrue(File.Exists(installed.Manifest));
        CollectionAssert.AreEqual(new[] { "0.1.0", "0.2.0", "0.3.0" }, Versions());

        await InstallLatest(Installer(new FakeGitHub().Release("v0.4.0")));
        CollectionAssert.AreEqual(new[] { "0.3.0", "0.4.0" }, Versions(), "removed once a later installation finds it unused");
    }

    // Only Windows refuses to rename a folder with a file open in it, which is what keeps a held version whole.
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public async Task A_version_a_server_still_runs_from_is_kept_whole_and_removed_once_it_is_released()
    {
        var held = await InstallLatest(Installer(new FakeGitHub().Release("v0.1.0")));
        var lease = PlatformInstaller.Hold(held);
        await InstallLatest(Installer(new FakeGitHub().Release("v0.2.0")));

        await InstallLatest(Installer(new FakeGitHub().Release("v0.3.0")));

        CollectionAssert.AreEqual(new[] { "0.1.0", "0.2.0", "0.3.0" }, Versions());
        Assert.IsTrue(File.Exists(held.Manifest));
        Assert.IsTrue(File.Exists(Path.Combine(held.Root, "RDCore.LanguageServer", "RDCore.LanguageServer.exe")));

        lease.Dispose();
        await InstallLatest(Installer(new FakeGitHub().Release("v0.4.0")));
        CollectionAssert.AreEqual(new[] { "0.3.0", "0.4.0" }, Versions());
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

    [TestMethod]
    public async Task A_download_cancelled_halfway_leaves_nothing_behind()
    {
        var github = new FakeGitHub().Release("v0.1.0");
        github.MidDownloadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var installer = Installer(github);
        var release = await installer.Feed.FindLatestAsync(TestContext.CancellationToken);
        Assert.IsNotNull(release);
        using var cancellation = new CancellationTokenSource();
        var progress = new Recorded();

        var install = installer.InstallAsync(release, progress, cancellation.Token);
        await progress.SomeBytes.Task.WaitAsync(TestContext.CancellationToken);
        Assert.IsTrue(File.Exists(Path.Combine(root, "downloads", release.ArchiveName + ".partial")), "some of it was written");
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => install);
        Assert.IsNull(installer.Current);
        Assert.IsTrue(NothingLeftOver());
    }
}
