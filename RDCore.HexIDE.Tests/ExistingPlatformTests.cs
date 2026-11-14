using RDCore.HexIDE.Platform;
using RDCore.HexIDE.Resources;
using RDCore.PlatformInstaller;
using RDCore.PlatformInstaller.Tests;

namespace RDCore.HexIDE.Tests;

/// <summary>How the add-in finds an RDCore already on the machine, before it installs one, and never makes a second copy of it.</summary>
[TestClass]
public sealed class ExistingPlatformTests : IDisposable
{
    private static readonly string Rdc = OperatingSystem.IsWindows() ? "rdc.exe" : "rdc";

    private readonly string folder = TempFolders.New("rdcore-existing-");
    private readonly Prompts prompts = new() { Answer = true };
    private readonly FakeMachine machine = new();

    public TestContext TestContext { get; set; } = null!;

    public void Dispose()
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private string SettingsPath => Path.Combine(folder, "settings.json");

    private string InstallerRoot => Path.Combine(folder, "installed");

    private PlatformSource Source(FakeGitHub? github = null, AddinSettings? settings = null) =>
        new(settings ?? new AddinSettings(), SettingsPath,
            new PlatformInstaller.PlatformInstaller(
                new PlatformInstallerOptions { Root = InstallerRoot, Channel = ReleaseChannel.Preview, RuntimeIdentifier = "win-x64" },
                new HttpClient(github ?? new FakeGitHub())),
            prompts, machine);

    // An RDCore as an unpacked release archive leaves one: its rdc in RDCore.CLI, and a release.json naming the RDCore.SDK it
    // was built with, unless it has none.
    private string Unpacked(string where, string? sdk = FakeGitHub.Sdk, bool releaseInfo = true, string? rid = null)
    {
        var root = Path.Combine(folder, where);
        Directory.CreateDirectory(Path.Combine(root, "RDCore.LanguageServer"));
        Directory.CreateDirectory(Path.Combine(root, "RDCore.CLI"));
        var ridProperty = rid is null ? "" : $""", "rid": "{rid}" """;
        File.WriteAllText(Path.Combine(root, "rdcore.json"),
            $$"""{ "platformVersion": "0.1.0", "langService": "RDCore.LanguageServer/RDCore.LanguageServer.exe", "hostService": "RDCore.CLI/{{Rdc}}"{{ridProperty}} }""");
        File.WriteAllText(Path.Combine(root, "RDCore.LanguageServer", "RDCore.LanguageServer.exe"), "");
        File.WriteAllText(Path.Combine(root, "RDCore.CLI", Rdc), "");
        if (releaseInfo)
        {
            File.WriteAllText(Path.Combine(root, "release.json"), FakeGitHub.ReleaseInfo(sdk));
        }
        return root;
    }

    private static string IncompatibleSdk
    {
        get
        {
            var sdk = AddinSettings.SdkVersion.Core;
            return sdk.Major == 0 ? $"0.{sdk.Minor + 1}.0" : $"{sdk.Major + 1}.0.0";
        }
    }

    private static string SdkRange
    {
        get
        {
            var sdk = AddinSettings.SdkVersion.Core;
            return sdk.Major > 0 ? $"{sdk.Major}.x" : $"{sdk.Major}.{sdk.Minor}.x";
        }
    }

    private string? SavedPlatformRoot => AddinSettings.Load(SettingsPath).PlatformRoot;

    [TestMethod]
    public async Task The_platform_in_use_in_the_installer_s_root_is_used_and_an_RDCore_on_the_PATH_is_never_offered()
    {
        var installed = await Source(new FakeGitHub().Release("v0.1.0")).GetAsync(TestContext.CancellationToken);
        Assert.IsNotNull(installed?.Installed);
        machine.Path.Add(Path.Combine(Unpacked("archive"), "RDCore.CLI"));

        var choice = await Source().GetAsync(TestContext.CancellationToken);

        Assert.AreEqual(installed.Root, choice?.Root);
        Assert.AreEqual(0, prompts.FoundAsked.Count);
        Assert.AreEqual(1, prompts.Asked.Count, "asked for the first installation only");
    }

    [TestMethod]
    public async Task A_compatible_RDCore_on_the_PATH_is_used_once_the_user_chooses_it_with_no_download_and_kept_for_the_next_start()
    {
        var unpacked = Unpacked("archive");
        machine.Path.Add(Path.Combine(folder, "elsewhere"));
        machine.Path.Add(Path.Combine(unpacked, "RDCore.CLI"));
        prompts.FoundAnswers.Enqueue(PlatformAnswer.UseFound);
        var github = new FakeGitHub().Release("v0.1.0");

        var choice = await Source(github).GetAsync(TestContext.CancellationToken);

        Assert.AreEqual(unpacked, choice?.Root);
        Assert.IsNull(choice?.Installed, "the add-in doesn't update it");
        Assert.AreEqual(unpacked, prompts.FoundAsked.Single().Root);
        Assert.AreEqual("0.1.0", prompts.FoundAsked.Single().Version);
        Assert.AreEqual(0, github.Requests.Count, "nothing is downloaded, nor looked for");
        Assert.IsFalse(Directory.Exists(Path.Combine(InstallerRoot, "versions")), "no second copy");
        Assert.AreEqual(unpacked, SavedPlatformRoot);

        machine.Path.Clear();
        var next = await Source(github, AddinSettings.Load(SettingsPath)).GetAsync(TestContext.CancellationToken);
        Assert.AreEqual(unpacked, next?.Root);
        Assert.AreEqual(1, prompts.FoundAsked.Count, "not asked again");
        Assert.AreEqual(0, prompts.Told.Count);
    }

    [TestMethod]
    [DataRow("incompatible")]
    [DataRow("no sdk")]
    [DataRow("no release.json")]
    [DataRow("another system")]
    public async Task An_RDCore_on_the_PATH_that_doesn_t_suit_the_add_in_is_not_offered_and_the_download_is(string unsuited)
    {
        var unpacked = unsuited switch
        {
            "incompatible" => Unpacked("archive", sdk: IncompatibleSdk),
            "no sdk" => Unpacked("archive", sdk: null),
            "no release.json" => Unpacked("archive", releaseInfo: false),
            _ => Unpacked("archive", rid: "plan9-x64"),
        };
        machine.Path.Add(Path.Combine(unpacked, "RDCore.CLI"));

        var choice = await Source(new FakeGitHub().Release("v0.1.0")).GetAsync(TestContext.CancellationToken);

        Assert.AreEqual(0, prompts.FoundAsked.Count);
        Assert.AreEqual(1, prompts.Asked.Count);
        Assert.IsTrue(prompts.AskedWithLocate.Single(), "the download question offers Locate too");
        Assert.IsNotNull(choice?.Installed);
    }

    [TestMethod]
    public async Task An_RDCore_on_the_PATH_inside_the_installer_s_root_is_not_offered_as_one_found_elsewhere()
    {
        var inside = Unpacked(Path.Combine("installed", "versions", "0.0.9"));
        machine.Path.Add(Path.Combine(inside, "RDCore.CLI"));

        var choice = await Source(new FakeGitHub().Release("v0.1.0")).GetAsync(TestContext.CancellationToken);

        Assert.AreEqual(0, prompts.FoundAsked.Count);
        Assert.AreEqual("0.1.0", choice?.Installed?.Version.Text);
    }

    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    public async Task An_rdc_on_the_PATH_is_followed_through_its_link_to_its_platform()
    {
        var unpacked = Unpacked("archive");
        var bin = Directory.CreateDirectory(Path.Combine(folder, "bin")).FullName;
        File.CreateSymbolicLink(Path.Combine(bin, Rdc), Path.Combine(unpacked, "RDCore.CLI", Rdc));
        machine.Path.Add(bin);
        prompts.FoundAnswers.Enqueue(PlatformAnswer.UseFound);

        var choice = await Source().GetAsync(TestContext.CancellationToken);

        Assert.AreEqual(unpacked, choice?.Root);
    }

    [TestMethod]
    public async Task Installing_instead_of_using_the_RDCore_found_asks_the_download_question_and_installs()
    {
        machine.Path.Add(Path.Combine(Unpacked("archive"), "RDCore.CLI"));
        prompts.FoundAnswers.Enqueue(PlatformAnswer.Install);

        var choice = await Source(new FakeGitHub().Release("v0.1.0")).GetAsync(TestContext.CancellationToken);

        Assert.AreEqual("0.1.0", choice?.Installed?.Version.Text);
        Assert.AreEqual(1, prompts.Asked.Count, "the download question says what is downloaded");
        Assert.IsNull(SavedPlatformRoot);
    }

    [TestMethod]
    public async Task With_nothing_found_a_located_RDCore_is_used_once_it_suits_the_add_in_and_each_folder_refused_says_why()
    {
        var notAPlatform = Directory.CreateDirectory(Path.Combine(folder, "documents")).FullName;
        var incompatible = Unpacked("old", sdk: IncompatibleSdk);
        var good = Unpacked("good");
        prompts.DownloadAnswers.Enqueue(PlatformAnswer.Locate);
        prompts.Folders.Enqueue(notAPlatform);
        prompts.Folders.Enqueue(incompatible);
        prompts.Folders.Enqueue(good);
        var github = new FakeGitHub().Release("v0.1.0");

        var choice = await Source(github).GetAsync(TestContext.CancellationToken);

        Assert.AreEqual(good, choice?.Root);
        Assert.IsNull(choice?.Installed);
        CollectionAssert.AreEqual(
            new[]
            {
                Strings.PlatformRootUnusable(notAPlatform, Strings.NotAPlatformFolder),
                Strings.PlatformRootUnusable(incompatible, Strings.SdkIncompatible(IncompatibleSdk, SdkRange)),
            },
            prompts.Told);
        Assert.AreEqual(good, SavedPlatformRoot);
        Assert.IsFalse(github.Requests.Any(request => request.AbsolutePath.EndsWith(".zip")), "nothing downloaded");
    }

    [TestMethod]
    public async Task A_located_folder_refused_with_none_picked_after_it_is_not_now()
    {
        var incompatible = Unpacked("old", sdk: IncompatibleSdk);
        prompts.DownloadAnswers.Enqueue(PlatformAnswer.Locate);
        prompts.Folders.Enqueue(incompatible);
        var source = Source(new FakeGitHub().Release("v0.1.0"));

        Assert.IsNull(await source.GetAsync(TestContext.CancellationToken));
        Assert.IsNull(await source.GetAsync(TestContext.CancellationToken), "not now holds for a project that opens");

        Assert.AreEqual(2, prompts.Picked);
        Assert.AreEqual(1, prompts.Asked.Count);
        Assert.AreEqual(1, prompts.Told.Count);
        Assert.IsNull(SavedPlatformRoot);
    }

    [TestMethod]
    public async Task A_run_locates_an_RDCore_from_the_RDCore_found_question()
    {
        machine.Path.Add(Path.Combine(Unpacked("archive"), "RDCore.CLI"));
        var good = Unpacked("good");
        prompts.FoundAnswers.Enqueue(PlatformAnswer.Locate);
        prompts.Folders.Enqueue(good);

        var choice = await Source().GetToRunAsync(TestContext.CancellationToken);

        Assert.AreEqual(good, choice.Root);
    }

    [TestMethod]
    public async Task A_chosen_RDCore_that_doesn_t_suit_this_add_in_says_so_once_and_the_installed_one_replaces_it()
    {
        var chosen = Unpacked("archive", sdk: IncompatibleSdk);
        AddinSettings.SavePlatformRoot(SettingsPath, chosen);
        var source = Source(new FakeGitHub().Release("v0.1.0"), AddinSettings.Load(SettingsPath));

        var choice = await source.GetAsync(TestContext.CancellationToken);

        Assert.AreEqual(Strings.ChosenPlatformUnsuitable("0.1.0", chosen, Strings.SdkIncompatible(IncompatibleSdk, SdkRange)),
            prompts.Told.Single());
        Assert.AreEqual("0.1.0", choice?.Installed?.Version.Text);
        Assert.IsNull(SavedPlatformRoot);
        Assert.AreEqual(choice?.Root, (await source.GetAsync(TestContext.CancellationToken))?.Root);
        Assert.AreEqual(1, prompts.Told.Count, "said once");
    }

    [TestMethod]
    public async Task A_development_build_in_the_settings_runs_as_it_is_without_a_release_json()
    {
        var build = Unpacked("rdcore-dev", releaseInfo: false);

        var choice = await Source(settings: new AddinSettings { PlatformRoot = build }).GetAsync(TestContext.CancellationToken);

        Assert.AreEqual(build, choice?.Root);
        Assert.AreEqual(0, prompts.Told.Count + prompts.Asked.Count + prompts.FoundAsked.Count);
    }

    // A platform whose rdcore.json names its rdc, as RDCore's does.
    private static FakeGitHub WithRdc(string tag)
    {
        var version = tag.TrimStart('v');
        var entries = FakeGitHub.Platform(version);
        entries["rdcore.json"] =
            $$"""{ "platformVersion": "{{version}}", "langService": "RDCore.LanguageServer/RDCore.LanguageServer.exe", "hostService": "RDCore.CLI/rdc.exe" }""";
        entries["RDCore.CLI/rdc.exe"] = "";
        return new FakeGitHub().Release(tag, entries: entries);
    }

    [TestMethod]
    public async Task Once_a_deployed_version_is_the_one_in_use_its_rdc_puts_itself_on_the_PATH()
    {
        var choice = await Source(WithRdc("v0.1.0")).GetAsync(TestContext.CancellationToken);

        var (executable, arguments) = machine.Runs.Single();
        Assert.AreEqual(Path.Combine(choice!.Root, "RDCore.CLI", "rdc.exe"), executable);
        CollectionAssert.AreEqual(new[] { "install", "--path-only" }, arguments);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task An_rdc_that_can_t_put_itself_on_the_PATH_leaves_the_platform_as_it_is_and_says_nothing(bool throws)
    {
        machine.ExitCode = 2;
        machine.Failure = throws ? new System.ComponentModel.Win32Exception(2, "The system cannot find the file specified.") : null;

        var choice = await Source(WithRdc("v0.1.0")).GetAsync(TestContext.CancellationToken);

        Assert.AreEqual("0.1.0", choice?.Installed?.Version.Text);
        Assert.AreEqual(1, machine.Runs.Count);
        Assert.AreEqual(0, prompts.Told.Count);
    }

    [TestMethod]
    public void A_saved_platform_root_keeps_every_other_setting()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(SettingsPath, """{ "PlatformRoot": "C:/old", "channel": "Stable", "developmentFeed": "C:/feed", "future": 1 }""");

        AddinSettings.SavePlatformRoot(SettingsPath, "C:/new");
        var saved = AddinSettings.Load(SettingsPath);

        Assert.AreEqual("C:/new", saved.PlatformRoot);
        Assert.AreEqual(ReleaseChannel.Stable, saved.Channel);
        Assert.AreEqual("C:/feed", saved.DevelopmentFeed);
        StringAssert.Contains(File.ReadAllText(SettingsPath), "\"future\": 1");

        AddinSettings.SavePlatformRoot(SettingsPath, null);
        Assert.IsNull(AddinSettings.Load(SettingsPath).PlatformRoot);
        Assert.AreEqual("C:/feed", AddinSettings.Load(SettingsPath).DevelopmentFeed);
    }
}
