using System.Text.Json;
using HexIDE.Addins;
using NSubstitute;
using RDCore.HexIDE.Platform;
using RDCore.HexIDE.Server;
using RDCore.PlatformInstaller;
using RDCore.PlatformInstaller.Tests;
using RDCore.SDK.Client;

namespace RDCore.HexIDE.Tests;

[TestClass]
public sealed class RDCoreServerTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "rdcore-addin-" + Guid.NewGuid().ToString("N"));
    private readonly IHexIdeHost host = Substitute.For<IHexIdeHost>();
    private readonly IAddinServerConnection connection = Substitute.For<IAddinServerConnection>();
    private readonly Prompts prompts = new();
    private AddinLanguageServer? registered;

    public RDCoreServerTests()
    {
        host.RegisterLanguageServer(Arg.Do<AddinLanguageServer>(server => registered = server)).Returns(connection);
    }

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

    private string ProjectFile(string project = "Ledger") => Path.Combine(folder, project, ".rdproj");

    // A built platform, as a development override names one.
    private string BuiltPlatform()
    {
        var root = Path.Combine(folder, "rdcore-dev");
        Directory.CreateDirectory(Path.Combine(root, "RDCore.LanguageServer"));
        File.WriteAllText(Path.Combine(root, "rdcore.json"), """{ "langService": "RDCore.LanguageServer/RDCore.LanguageServer.exe" }""");
        File.WriteAllText(Path.Combine(root, "RDCore.LanguageServer", "RDCore.LanguageServer.exe"), "");
        return root;
    }

    private RDCoreServer Server(string? platformRoot = null, FakeGitHub? github = null)
    {
        var installer = new PlatformInstaller.PlatformInstaller(new PlatformInstallerOptions
        {
            Root = Path.Combine(folder, "installed"),
            Channel = ReleaseChannel.Preview,
        }, new HttpClient(github ?? new FakeGitHub()));
        return new RDCoreServer(host, new PlatformSource(new AddinSettings { PlatformRoot = platformRoot }, installer, prompts), prompts);
    }

    [TestMethod]
    public async Task The_first_RDCore_project_registers_the_server_to_run_from_its_platform_with_HexIDE_filling_the_rest()
    {
        var root = BuiltPlatform();
        using var server = Server(root);

        await server.ProjectLoadedAsync(ProjectFile());

        Assert.IsNotNull(registered);
        var entry = registered.Entry;
        Assert.AreEqual(RDCoreServer.Id, entry.Id);
        Assert.AreEqual(Path.Combine(root, "RDCore.LanguageServer", "RDCore.LanguageServer.exe"), entry.Command);
        Assert.AreEqual(Path.Combine(root, "RDCore.LanguageServer"), entry.WorkingDirectory);
        Assert.AreEqual(root, entry.Environment![RDCoreLanguageServerLaunch.PlatformRootEnvironmentVariable]);
        Assert.AreEqual("pipe", entry.Transport);
        Assert.AreEqual("connect", entry.PipeRole);
        CollectionAssert.AreEqual(
            new[] { "--pipe-name", "{pipe}", "--workspace", "{workspaceUri}", "--client-process-id", "{pid}" }, entry.Arguments!.List);
        CollectionAssert.AreEquivalent(new[] { ".bas", ".cls", ".frm", ".doccls" }, entry.Extensions);
        Assert.AreEqual(RDCoreServer.BeforeLaunchTimeout, registered.BeforeLaunchTimeout);
        Assert.AreEqual("rdcore/", registered.MessagePrefix);
        CollectionAssert.AreEquivalent(
            new[]
            {
                RDCorePlatformProtocol.SessionMessageBox, RDCorePlatformProtocol.SessionInputBox,
                RDCorePlatformProtocol.RubberduckIndenterSettings, RDCorePlatformProtocol.RubberduckIndenterSettingsApply,
            },
            registered.ServerMethods.Keys.ToArray(), "every server-to-client method RDCore's protocol has");
        Assert.AreSame(connection, server.Connection);
    }

    [TestMethod]
    public async Task A_project_that_isn_t_an_RDCore_project_registers_nothing()
    {
        using var server = Server(BuiltPlatform());

        await server.ProjectLoadedAsync(Path.Combine(folder, "Ledger", "Ledger.vbp"));
        await server.ProjectLoadedAsync(Path.Combine(folder, "Ledger", "Ledger.rdproj"));

        host.DidNotReceiveWithAnyArgs().RegisterLanguageServer(default!);
    }

    [TestMethod]
    public async Task The_server_stays_while_an_RDCore_project_is_open_and_goes_with_the_last_one()
    {
        using var server = Server(BuiltPlatform());

        await server.ProjectLoadedAsync(ProjectFile("Ledger"));
        await server.ProjectLoadedAsync(ProjectFile("Invoices"));
        server.ProjectUnloaded(ProjectFile("Ledger"));
        connection.DidNotReceive().Dispose();
        server.ProjectUnloaded(ProjectFile("Invoices"));

        host.ReceivedWithAnyArgs(1).RegisterLanguageServer(default!);
        connection.Received(1).Dispose();
        Assert.IsNull(server.Connection);
    }

    [TestMethod]
    public async Task After_each_start_the_add_in_says_hello_to_the_platform_over_HexIDE_s_connection()
    {
        using var server = Server(BuiltPlatform());
        await server.ProjectLoadedAsync(ProjectFile());
        connection.RequestAsync(default!, default, default, default).ReturnsForAnyArgs(JsonDocument.Parse("{}").RootElement);

        await registered!.AfterStart!(connection, TestContext.CancellationToken);

        await connection.Received(1).RequestAsync(RDCorePlatformProtocol.PlatformInitialize, Arg.Any<JsonElement?>(),
            Timeout.InfiniteTimeSpan, Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task With_no_platform_the_newest_release_is_downloaded_once_the_user_agrees_and_the_server_runs_from_it()
    {
        var github = new FakeGitHub().Release("v0.1.0-alpha", prerelease: true);
        prompts.Answer = true;
        using var server = Server(github: github);

        await server.ProjectLoadedAsync(ProjectFile());

        Assert.AreEqual("0.1.0-alpha", prompts.Asked.Single().Version.Text);
        Assert.IsNotNull(registered);
        StringAssert.StartsWith(registered.Entry.Command, Path.Combine(folder, "installed", "versions", "0.1.0-alpha"));
        Assert.IsTrue(prompts.InstallShown);
    }

    [TestMethod]
    public async Task A_declined_download_downloads_nothing_and_registers_nothing_and_says_nothing_more()
    {
        var github = new FakeGitHub().Release("v0.1.0");
        prompts.Answer = false;
        using var server = Server(github: github);

        await server.ProjectLoadedAsync(ProjectFile());

        Assert.AreEqual(1, prompts.Asked.Count);
        Assert.IsFalse(github.Requests.Any(request => request.AbsolutePath.EndsWith(".zip")));
        host.DidNotReceiveWithAnyArgs().RegisterLanguageServer(default!);
        Assert.AreEqual(0, prompts.Told.Count);
    }

    [TestMethod]
    public async Task With_no_release_to_download_the_user_is_told_and_nothing_is_registered()
    {
        using var server = Server(github: new FakeGitHub());

        await server.ProjectLoadedAsync(ProjectFile());

        Assert.AreEqual(0, prompts.Asked.Count);
        Assert.AreEqual(1, prompts.Told.Count);
        host.DidNotReceiveWithAnyArgs().RegisterLanguageServer(default!);
    }

    [TestMethod]
    public async Task A_project_closed_while_the_user_is_asked_downloads_nothing_whatever_the_answer()
    {
        var github = new FakeGitHub().Release("v0.1.0");
        prompts.Pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = Server(github: github);

        var loading = server.ProjectLoadedAsync(ProjectFile());
        server.ProjectUnloaded(ProjectFile());
        prompts.Pending.SetResult(true);
        await Assert.ThrowsAsync<OperationCanceledException>(() => loading);

        Assert.IsFalse(github.Requests.Any(request => request.AbsolutePath.EndsWith(".zip")));
        Assert.IsFalse(Directory.Exists(Path.Combine(folder, "installed")), "nothing was installed, nor begun");
        host.DidNotReceiveWithAnyArgs().RegisterLanguageServer(default!);
    }

    [TestMethod]
    public async Task A_platform_root_that_isn_t_one_is_reported_and_registers_nothing()
    {
        Directory.CreateDirectory(Path.Combine(folder, "empty"));
        using var server = Server(Path.Combine(folder, "empty"));

        await server.ProjectLoadedAsync(ProjectFile());

        Assert.AreEqual(1, prompts.Told.Count);
        host.DidNotReceiveWithAnyArgs().RegisterLanguageServer(default!);
    }

    private sealed class Prompts : IUserPrompts
    {
        public bool Answer { get; set; }
        public TaskCompletionSource<bool>? Pending { get; set; }
        public List<PlatformRelease> Asked { get; } = [];
        public List<string> Told { get; } = [];
        public bool InstallShown { get; private set; }

        public Task<bool> ConfirmDownloadAsync(PlatformRelease release, string target)
        {
            Asked.Add(release);
            return Pending?.Task ?? Task.FromResult(Answer);
        }

        public Task TellAsync(string message)
        {
            Told.Add(message);
            return Task.CompletedTask;
        }

        public IInstallView ShowInstall(PlatformRelease release)
        {
            InstallShown = true;
            return new View();
        }

        private sealed class View : IInstallView
        {
            public void Report(InstallProgress value)
            {
            }

            public void Dispose()
            {
            }
        }
    }
}
