using System.Globalization;
using System.Text.Json;
using HexIDE.Addins;
using RDCore.HexIDE.Debugging;
using RDCore.HexIDE.Platform;
using RDCore.HexIDE.Resources;
using RDCore.PlatformInstaller;
using RDCore.PlatformInstaller.Tests;
using RDCore.SDK.Client;

namespace RDCore.HexIDE.Tests;

[TestClass]
public sealed class RDCoreDebugLaunchTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "rdcore-addin-" + Guid.NewGuid().ToString("N"));
    private readonly Prompts prompts = new();

    public TestContext TestContext { get; set; } = null!;

    public void Dispose()
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing was written.
        }
    }

    private AddinProjectInfo Project(string file = ".rdproj") => new("Ledger", Path.Combine(folder, "Ledger", file), []);

    // A built platform, as a development override names one.
    private string BuiltPlatform()
    {
        var root = Path.Combine(folder, "rdcore-dev");
        Directory.CreateDirectory(Path.Combine(root, "RDCore.LanguageServer"));
        File.WriteAllText(Path.Combine(root, "rdcore.json"), """{ "langService": "RDCore.LanguageServer/RDCore.LanguageServer.exe" }""");
        File.WriteAllText(Path.Combine(root, "RDCore.LanguageServer", "RDCore.LanguageServer.exe"), "");
        return root;
    }

    private RDCoreDebugLaunch DebugLaunch(string? platformRoot = null, FakeGitHub? github = null) =>
        new(Source(platformRoot, github));

    private PlatformSource Source(string? platformRoot = null, FakeGitHub? github = null)
    {
        var installer = new PlatformInstaller.PlatformInstaller(new PlatformInstallerOptions
        {
            Root = Path.Combine(folder, "installed"),
            Channel = ReleaseChannel.Preview,
            RuntimeIdentifier = "win-x64",
        }, new HttpClient(github ?? new FakeGitHub()));
        return new PlatformSource(new AddinSettings { PlatformRoot = platformRoot }, Path.Combine(folder, "settings.json"), installer,
            prompts, new FakeMachine());
    }

    private static T InCulture<T>(string culture, Func<T> action)
    {
        var was = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo(culture);
        try
        {
            return action();
        }
        finally
        {
            CultureInfo.CurrentUICulture = was;
        }
    }

    [TestMethod]
    public void It_is_offered_for_RDCore_projects_only()
    {
        var debugLaunch =DebugLaunch();
        var launch = debugLaunch.Launch;

        Assert.AreEqual(RDCoreDebugLaunch.Title, launch.Title);
        Assert.IsTrue(launch.Claims(Project()));
        Assert.IsFalse(launch.Claims(Project("Ledger.vbp")));
    }

    [TestMethod]
    public async Task A_run_starts_the_platform_s_debug_adapter_for_the_project_s_folder_and_leaves_what_runs_to_it()
    {
        var root = BuiltPlatform();
        var debugLaunch =DebugLaunch(root);

        var configuration = await debugLaunch.Launch.CreateConfigurationAsync(Project(), TestContext.CancellationToken);

        Assert.AreEqual(Path.Combine(root, "RDCore.LanguageServer", "RDCore.LanguageServer.exe"), configuration.Executable);
        CollectionAssert.AreEqual(
            new[] { RDCoreDebugAdapterLaunch.Switch, "--workspace", new Uri(Path.Combine(folder, "Ledger")).AbsoluteUri },
            configuration.Arguments.ToArray());
        Assert.AreEqual(Path.Combine(root, "RDCore.LanguageServer"), configuration.WorkingDirectory);
        Assert.AreEqual(root, configuration.Environment[RDCoreLanguageServerLaunch.PlatformRootEnvironmentVariable]);
        Assert.AreEqual(RDCoreDebugAdapterLaunch.AdapterId, configuration.AdapterId);
        Assert.AreEqual(JsonValueKind.Object, configuration.Launch.ValueKind);
        Assert.AreEqual(0, configuration.Launch.EnumerateObject().Count(), "no module and no entry point: the adapter finds Main");
    }

    [TestMethod]
    public async Task With_no_platform_the_newest_release_is_downloaded_once_the_user_agrees_and_the_run_starts_from_it()
    {
        prompts.Answer = true;
        var debugLaunch =DebugLaunch(github: new FakeGitHub().Release("v0.1.0-alpha", prerelease: true));

        var configuration = await debugLaunch.Launch.CreateConfigurationAsync(Project(), TestContext.CancellationToken);

        Assert.AreEqual("0.1.0-alpha", prompts.Asked.Single().Version.Text);
        StringAssert.StartsWith(configuration.Executable, Path.Combine(folder, "installed", "versions", "0.1.0-alpha"));
    }

    [TestMethod]
    public async Task A_declined_download_downloads_nothing_and_ends_the_run_saying_why()
    {
        var github = new FakeGitHub().Release("v0.1.0");
        prompts.Answer = false;
        var debugLaunch =DebugLaunch(github: github);

        var refused = await Assert.ThrowsExactlyAsync<PlatformUnavailableException>(
            () => debugLaunch.Launch.CreateConfigurationAsync(Project(), TestContext.CancellationToken));

        Assert.AreEqual(Strings.DownloadDeclined, refused.Message);
        Assert.AreEqual(0, prompts.Told.Count, "HexIDE shows the run's message");
        Assert.IsFalse(github.Requests.Any(request => request.AbsolutePath.EndsWith(".zip")));
    }

    [TestMethod]
    public async Task A_run_after_Not_now_asks_again_and_downloads_once_the_user_agrees()
    {
        var source = Source(github: new FakeGitHub().Release("v0.1.0"));
        var debugLaunch =new RDCoreDebugLaunch(source);
        prompts.Answer = false;
        Assert.IsNull(await source.GetAsync(TestContext.CancellationToken), "the question opening a project asks");
        Assert.IsNull(await source.GetAsync(TestContext.CancellationToken), "Not now holds for the next project opened");
        Assert.AreEqual(1, prompts.Asked.Count);

        prompts.Answer = true;
        var configuration = await debugLaunch.Launch.CreateConfigurationAsync(Project(), TestContext.CancellationToken);

        Assert.AreEqual(2, prompts.Asked.Count, "the run asked again");
        StringAssert.StartsWith(configuration.Executable, Path.Combine(folder, "installed", "versions", "0.1.0"));
    }

    [TestMethod]
    [DataRow("en", "RDCore's platform wasn't downloaded, so the project can't run.")]
    [DataRow("fr-CA", "La plateforme de RDCore n'a pas été téléchargée : le projet ne peut donc pas s'exécuter.")]
    public void Why_a_declined_run_can_t_start_is_said_in_the_user_s_language(string culture, string message)
    {
        Assert.AreEqual(message, InCulture(culture, () => Strings.DownloadDeclined));
    }

    [TestMethod]
    public async Task A_platform_root_that_isn_t_one_ends_the_run_saying_why_without_telling_the_user_twice()
    {
        var root = Path.Combine(folder, "empty");
        Directory.CreateDirectory(root);
        var debugLaunch =DebugLaunch(root);

        var refused = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => debugLaunch.Launch.CreateConfigurationAsync(Project(), TestContext.CancellationToken));

        Assert.AreEqual(Strings.PlatformRootUnusable(root, Strings.NoManifest), refused.Message);
        Assert.AreEqual(0, prompts.Told.Count, "HexIDE shows the run's message");
    }

    [TestMethod]
    public async Task A_run_ended_while_the_user_is_asked_downloads_nothing_whatever_the_answer()
    {
        var github = new FakeGitHub().Release("v0.1.0");
        prompts.Pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var debugLaunch =DebugLaunch(github: github);
        using var ended = new CancellationTokenSource();

        var creating = debugLaunch.Launch.CreateConfigurationAsync(Project(), ended.Token);
        await ended.CancelAsync();
        prompts.Pending.SetResult(true);
        await Assert.ThrowsAsync<OperationCanceledException>(() => creating);

        Assert.AreEqual(1, prompts.Asked.Count, "the run was ended while the user was asked");
        Assert.IsFalse(github.Requests.Any(request => request.AbsolutePath.EndsWith(".zip")));
        Assert.IsFalse(Directory.Exists(Path.Combine(folder, "installed", "versions")), "nothing was deployed");
    }

    // On Windows an open file keeps its folder from being replaced, so a version a run held couldn't be deployed again.
    [TestMethod]
    public async Task An_installed_version_that_lost_its_language_server_is_deployed_again_once_the_user_agrees_and_the_run_starts_from_it()
    {
        prompts.Answer = true;
        var debugLaunch = DebugLaunch(github: new FakeGitHub().Release("v0.1.0"));
        var first = await debugLaunch.Launch.CreateConfigurationAsync(Project(), TestContext.CancellationToken);
        File.Delete(first.Executable);

        var again = await debugLaunch.Launch.CreateConfigurationAsync(Project(), TestContext.CancellationToken);

        Assert.AreEqual(2, prompts.Asked.Count, "asked again before it was deployed again");
        Assert.AreEqual(first.Executable, again.Executable);
        Assert.IsTrue(File.Exists(again.Executable));
    }

    [TestMethod]
    public async Task An_installed_version_that_lost_its_language_server_and_isn_t_downloaded_again_ends_the_run_saying_so()
    {
        prompts.Answer = true;
        var debugLaunch = DebugLaunch(github: new FakeGitHub().Release("v0.1.0"));
        var first = await debugLaunch.Launch.CreateConfigurationAsync(Project(), TestContext.CancellationToken);
        File.Delete(first.Executable);
        prompts.Answer = false;

        var refused = await Assert.ThrowsExactlyAsync<PlatformUnavailableException>(
            () => debugLaunch.Launch.CreateConfigurationAsync(Project(), TestContext.CancellationToken));

        Assert.AreEqual(Strings.DownloadDeclined, refused.Message);
        Assert.AreEqual(0, prompts.Told.Count, "HexIDE shows the run's message");
    }
}
