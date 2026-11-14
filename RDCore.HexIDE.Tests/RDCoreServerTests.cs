using System.Globalization;
using System.Text.Json;
using HexIDE.Addins;
using NSubstitute;
using RDCore.HexIDE.Platform;
using RDCore.HexIDE.Resources;
using RDCore.HexIDE.Server;
using RDCore.PlatformInstaller;
using RDCore.PlatformInstaller.Tests;
using RDCore.SDK.Client;
using RDCore.SDK.Platform;

namespace RDCore.HexIDE.Tests;

[TestClass]
public sealed class RDCoreServerTests : IDisposable
{
    private readonly string folder = TempFolders.New("rdcore-addin-");
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
            RuntimeIdentifier = "win-x64",
        }, new HttpClient(github ?? new FakeGitHub()));
        return new RDCoreServer(host, new PlatformSource(new AddinSettings { PlatformRoot = platformRoot }, Path.Combine(folder, "settings.json"),
            installer, prompts, new FakeMachine()), prompts);
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
        Assert.IsTrue(entry.PipeName!.StartsWith("rdc-", StringComparison.Ordinal) && entry.PipeName.Length <= PlatformPipeName.MaxLength,
            $"a pipe name the platform makes, short enough for a macOS socket path: {entry.PipeName}");
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

    [TestMethod]
    [DataRow("no folder")]
    [DataRow("no manifest")]
    [DataRow("no language server named")]
    [DataRow("no language server there")]
    public async Task Why_a_platform_root_can_t_be_used_is_told_in_the_user_s_language_and_not_in_the_exception_s(string flaw)
    {
        var root = BuiltPlatform();
        switch (flaw)
        {
            case "no folder":
                Directory.Delete(root, recursive: true);
                break;
            case "no manifest":
                File.Delete(Path.Combine(root, "rdcore.json"));
                break;
            case "no language server named":
                File.WriteAllText(Path.Combine(root, "rdcore.json"), "{}");
                break;
            case "no language server there":
                Directory.Delete(Path.Combine(root, "RDCore.LanguageServer"), recursive: true);
                break;
        }

        var before = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo("fr-CA");
        try
        {
            using var server = Server(root);
            await server.ProjectLoadedAsync(ProjectFile());

            var reason = flaw switch
            {
                "no folder" => Strings.FolderMissing,
                "no manifest" => Strings.NoManifest,
                "no language server named" => Strings.ManifestUnusable,
                _ => Strings.LanguageServerMissing,
            };
            Assert.AreEqual(Strings.PlatformRootUnusable(root, reason), prompts.Told.Single());
            StringAssert.StartsWith(prompts.Told[0], "La plateforme RDCore");
            host.DidNotReceiveWithAnyArgs().RegisterLanguageServer(default!);
        }
        finally
        {
            CultureInfo.CurrentUICulture = before;
        }
    }

    [TestMethod]
    public async Task A_project_reopened_while_the_user_is_asked_is_asked_nothing_more_and_gets_the_one_server()
    {
        var github = new FakeGitHub().Release("v0.1.0");
        prompts.Pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = Server(github: github);

        var first = server.ProjectLoadedAsync(ProjectFile());
        server.ProjectUnloaded(ProjectFile());
        var again = server.ProjectLoadedAsync(ProjectFile());
        prompts.Pending.SetResult(true);
        await Assert.ThrowsAsync<OperationCanceledException>(() => first);
        await again;

        Assert.AreEqual(1, prompts.Asked.Count, "one question, for both");
        host.ReceivedWithAnyArgs(1).RegisterLanguageServer(default!);
    }

    [TestMethod]
    public async Task Not_now_holds_until_HexIDE_restarts()
    {
        prompts.Answer = false;
        using var server = Server(github: new FakeGitHub().Release("v0.1.0"));

        await server.ProjectLoadedAsync(ProjectFile());
        server.ProjectUnloaded(ProjectFile());
        await server.ProjectLoadedAsync(ProjectFile("Invoices"));

        Assert.AreEqual(1, prompts.Asked.Count);
        host.DidNotReceiveWithAnyArgs().RegisterLanguageServer(default!);
    }

    [TestMethod]
    public async Task A_server_HexIDE_won_t_take_is_reported_to_the_user()
    {
        host.RegisterLanguageServer(Arg.Any<AddinLanguageServer>()).Returns(_ => throw new InvalidOperationException("the id is taken"));
        using var server = Server(BuiltPlatform());

        await server.ProjectLoadedAsync(ProjectFile());

        Assert.AreEqual(Strings.ServerNotRegistered("the id is taken"), prompts.Told.Single());
        Assert.IsNull(server.Connection);
    }

    // Only Windows keeps a file that is open from being deleted, which is how this sees the lease.
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public async Task The_installed_version_a_server_is_registered_from_is_held_until_the_server_goes()
    {
        prompts.Answer = true;
        using var server = Server(github: new FakeGitHub().Release("v0.1.0"));
        await server.ProjectLoadedAsync(ProjectFile());
        var manifest = Path.Combine(folder, "installed", "versions", "0.1.0", "rdcore.json");

        Assert.ThrowsExactly<IOException>(() => File.Delete(manifest));
        server.ProjectUnloaded(ProjectFile());
        File.Delete(manifest);
    }

    // The lease is what would keep the version's folder from being replaced, and only Windows enforces it against a plain delete.
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public async Task A_held_version_that_lost_its_language_server_is_deployed_again_before_the_server_starts_and_held_again()
    {
        prompts.Answer = true;
        using var server = Server(github: new FakeGitHub().Release("v0.1.0"));
        await server.ProjectLoadedAsync(ProjectFile());
        var version = Path.Combine(folder, "installed", "versions", "0.1.0");
        File.Delete(Path.Combine(version, "RDCore.LanguageServer", "RDCore.LanguageServer.exe"));
        Assert.IsNotNull(registered?.BeforeLaunch);

        await registered.BeforeLaunch(TestContext.CancellationToken);

        Assert.IsTrue(File.Exists(Path.Combine(version, "RDCore.LanguageServer", "RDCore.LanguageServer.exe")));
        Assert.ThrowsExactly<IOException>(() => File.Delete(Path.Combine(version, "rdcore.json")), "held again");
    }
}
