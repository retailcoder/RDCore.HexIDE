using HexIDE.Addins;
using RDCore.HexIDE.Projects;
using RDCore.HexIDE.Resources;

namespace RDCore.HexIDE.Tests;

[TestClass]
public sealed class RDProjectTests : IDisposable
{
    // The project's folder is in a folder of its own, where a test puts what is outside the project; both go with the test.
    private readonly string container = Path.Combine(Path.GetTempPath(), "rdcore-rdproj-" + Guid.NewGuid().ToString("N"));
    private readonly string root;
    private readonly List<string> told = [];

    public RDProjectTests()
    {
        root = Path.Combine(container, "Ledger");
        Directory.CreateDirectory(Path.Combine(root, "src", "Classes"));
        Write("src/Main.bas", "Attribute VB_Name = \"Startup\"\r\nSub Main()\r\nEnd Sub\r\n");
        Write("src/Classes/Account.cls", "VERSION 1.0 CLASS\r\nBEGIN\r\n  MultiUse = -1  'True\r\nEND\r\nAttribute VB_Name = \"Account\"\r\n");
        Write("Sheet1.doccls", "VERSION 1.0 CLASS\r\nAttribute VB_Name = \"Sheet1\"\r\n");
        Write("README.md", "# Ledger");
    }

    public TestContext TestContext { get; set; } = null!;

    public void Dispose()
    {
        try
        {
            Directory.Delete(container, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private string ProjectFile => Path.Combine(root, ".rdproj");

    private string Full(string relative) => Path.GetFullPath(Path.Combine(root, relative));

    private void Write(string relative, string text) => File.WriteAllText(Full(relative), text);

    private void WriteProject(string modules, string references = "[]", string otherFiles = """["README.md"]""") =>
        File.WriteAllText(ProjectFile, $$"""
            { "Version": "0.1", "Configuration": [], "ProjectInfo": { "Name": "Ledger", "References": {{references}},
              "Modules": {{modules}}, "OtherFiles": {{otherFiles}}, "Folders": ["src", "src/Classes", "docs"] } }
            """);

    // A watcher of the project as it is now, which the test tells of the file system's reports itself.
    private RDProjectWatcher Following(Changes changes, TimeProvider time)
    {
        var watcher = new RDProjectWatcher(ProjectFile, changes, time, told.Add);
        watcher.Begin(RDProject.Read(ProjectFile));
        return watcher;
    }

    private const string Scripting =
        """[{ "Name": "Scripting", "Guid": "420b2830-e718-11cf-893d-00a0c9054228", "Major": 1, "Minor": 0, "AbsolutePath": "C:\\Windows\\System32\\scrrun.dll" }]""";

    [TestMethod]
    public void A_project_file_is_read_with_each_module_s_kind_by_RDCore_s_rule_in_either_form()
    {
        WriteProject("""["src/Main.bas", { "RelativeUri": "src/Classes/Account.cls" }, { "RelativeUri": "Sheet1.doccls", "Super": "ExcelWorksheet" }]""", Scripting);

        var project = RDProject.Read(ProjectFile);

        Assert.AreEqual("Ledger", project.Name);
        CollectionAssert.AreEqual(
            new[]
            {
                (Full("src/Main.bas"), AddinMemberKind.StandardModule),
                (Full("src/Classes/Account.cls"), AddinMemberKind.ClassModule),
                (Full("Sheet1.doccls"), AddinMemberKind.DocumentModule),
                (Full("README.md"), AddinMemberKind.Other),
            },
            project.Members.Select(member => (member.Path, member.Kind)).ToArray());
        Assert.AreEqual(new AddinProjectReference("Scripting", Guid.Parse("420b2830-e718-11cf-893d-00a0c9054228"), 1, 0,
            @"C:\Windows\System32\scrrun.dll"), project.References.Single());
        CollectionAssert.AreEqual(new[] { "src", "src/Classes", "docs" }, project.Folders.ToArray());
    }

    [TestMethod]
    public void A_member_or_folder_outside_the_project_s_folder_is_left_out()
    {
        var outside = Path.Combine(container, "Outside.bas");
        File.WriteAllText(outside, "Attribute VB_Name = \"Outside\"\r\n");
        WriteProject($$"""["src/Main.bas", "../Outside.bas", "src/../../Outside.bas", "{{outside.Replace("\\", "\\\\")}}"]""");

        var project = RDProject.Read(ProjectFile);

        CollectionAssert.AreEqual(new[] { Full("src/Main.bas"), Full("README.md") }, project.Members.Select(member => member.Path).ToArray());
    }

    [TestMethod]
    public void A_module_is_decoded_as_the_platform_decodes_it_so_a_UTF16_class_is_a_class()
    {
        File.WriteAllText(Full("src/Classes/Account.cls"), "VERSION 1.0 CLASS\r\nAttribute VB_Name = \"Account\"\r\n", System.Text.Encoding.Unicode);
        WriteProject("""["src/Classes/Account.cls"]""");

        var project = RDProject.Read(ProjectFile);

        Assert.AreEqual(AddinMemberKind.ClassModule, project.Members.First().Kind);
    }

    [TestMethod]
    public void The_standard_library_is_no_reference_of_a_project()
    {
        WriteProject("""["src/Main.bas"]""",
            $$"""[{ "Name": "VBA", "Guid": "000204ef-0000-0000-c000-000000000046", "Major": 4, "Minor": 2, "AbsolutePath": "C:\\Program Files\\Microsoft Office\\VBE7.DLL" }, {{Scripting[1..^1]}}]""");

        var project = RDProject.Read(ProjectFile);

        Assert.AreEqual("Scripting", project.References.Single().Name);
    }

    [TestMethod]
    public void Lists_a_project_file_gives_as_null_read_as_empty()
    {
        WriteProject("null", references: "null", otherFiles: "null");

        var project = RDProject.Read(ProjectFile);

        Assert.AreEqual(0, project.Members.Count);
        Assert.AreEqual(0, project.References.Count);
    }

    [TestMethod]
    public void A_project_the_file_gives_as_null_is_an_empty_one_named_for_its_folder()
    {
        File.WriteAllText(ProjectFile, """{ "Version": "0.1", "ProjectInfo": null }""");

        var project = RDProject.Read(ProjectFile);

        Assert.AreEqual(Path.GetFileName(root), project.Name);
        Assert.AreEqual(0, project.Members.Count);
    }

    [TestMethod]
    public void A_file_listed_twice_under_any_spelling_of_its_path_is_one_member()
    {
        var spellings = OperatingSystem.IsWindows()
            ? """["src/Main.bas", "src\\Main.bas", "./src/Main.bas", "SRC/MAIN.BAS"]"""
            : """["src/Main.bas", "src\\Main.bas", "./src/Main.bas"]""";
        WriteProject(spellings, otherFiles: """["README.md", "src/Main.bas"]""");

        var project = RDProject.Read(ProjectFile);

        CollectionAssert.AreEqual(
            new[] { (Full("src/Main.bas"), AddinMemberKind.StandardModule), (Full("README.md"), AddinMemberKind.Other) },
            project.Members.Select(member => (member.Path, member.Kind)).ToArray());
    }

    [TestMethod]
    public void A_path_with_a_NUL_in_it_is_left_out()
    {
        WriteProject("""["src/Ma\u0000in.bas", "src/Main.bas"]""", otherFiles: "[]");

        var project = RDProject.Read(ProjectFile);

        CollectionAssert.AreEqual(new[] { Full("src/Main.bas") }, project.Members.Select(member => member.Path).ToArray());
    }

    [TestMethod]
    public void A_change_HexIDE_throws_on_is_not_reported_twice_and_lets_nothing_escape_the_timer_s_thread()
    {
        WriteProject("""["src/Main.bas"]""", otherFiles: "[]");
        var changes = new Changes { ThrowOnAdd = true };
        using var watcher = Following(changes, new ManualTime());
        Write("src/Helpers.bas", "Attribute VB_Name = \"Helpers\"\r\n");
        WriteProject("""["src/Main.bas", "src/Helpers.bas"]""", otherFiles: "[]");

        watcher.Reread();

        changes.ThrowOnAdd = false;
        Write("src/Tools.bas", "Attribute VB_Name = \"Tools\"\r\n");
        WriteProject("""["src/Main.bas", "src/Helpers.bas", "src/Tools.bas"]""", otherFiles: "[]");
        watcher.Reread();
        CollectionAssert.AreEqual(new[] { $"added {Full("src/Tools.bas")} StandardModule" }, changes.Calls);
    }

    [TestMethod]
    public void A_file_that_isn_t_a_project_file_is_refused()
    {
        File.WriteAllText(ProjectFile, "Type=Exe\r\nModule=Main; Main.bas\r\n");

        Assert.ThrowsExactly<InvalidDataException>(() => RDProject.Read(ProjectFile));
    }

    [TestMethod]
    public void A_change_is_reported_as_moves_then_removals_then_additions_then_references()
    {
        WriteProject("""["src/Main.bas", "src/Classes/Account.cls"]""");
        var before = RDProject.Read(ProjectFile);
        // Main.bas moved, Account.cls became a document module, Helpers.bas joined, README.md left, and a reference came.
        File.Move(Full("src/Main.bas"), Full("src/Program.bas"));
        Write("src/Helpers.bas", "Attribute VB_Name = \"Helpers\"\r\n");
        WriteProject("""["src/Program.bas", { "RelativeUri": "src/Classes/Account.cls", "Super": "ExcelWorkbook" }, "src/Helpers.bas"]""",
            Scripting, otherFiles: "[]");
        var changes = new Changes();

        RDProjectChanges.Report(before, RDProject.Read(ProjectFile), changes);

        CollectionAssert.AreEqual(
            new[]
            {
                $"moved {Full("src/Main.bas")} {Full("src/Program.bas")}",
                $"removed {Full("src/Classes/Account.cls")}",
                $"removed {Full("README.md")}",
                $"added {Full("src/Classes/Account.cls")} DocumentModule",
                $"added {Full("src/Helpers.bas")} StandardModule",
                "references Scripting",
            },
            changes.Calls);
    }

    [TestMethod]
    [DataRow(true, DisplayName = "a copy, its original still there")]
    [DataRow(false, DisplayName = "another file, the original gone")]
    public void A_member_replaced_by_another_file_is_removed_and_added_rather_than_moved(bool copy)
    {
        WriteProject("""["src/Main.bas"]""", otherFiles: "[]");
        var before = RDProject.Read(ProjectFile);
        if (copy)
        {
            File.Copy(Full("src/Main.bas"), Full("src/Other.bas"));
        }
        else
        {
            File.Delete(Full("src/Main.bas"));
            Write("src/Other.bas", "Attribute VB_Name = \"Other\"\r\n");
        }
        WriteProject("""["src/Other.bas"]""", otherFiles: "[]");
        var changes = new Changes();

        RDProjectChanges.Report(before, RDProject.Read(ProjectFile), changes);

        CollectionAssert.AreEqual(new[] { $"removed {Full("src/Main.bas")}", $"added {Full("src/Other.bas")} StandardModule" }, changes.Calls);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)] // bounds a write the file system never reports; nothing waits on it otherwise
    public async Task An_open_project_s_file_is_followed_until_HexIDE_closes_it()
    {
        WriteProject("""["src/Main.bas"]""", otherFiles: "[]");
        var time = new ManualTime();
        using var type = new RDCoreProjectType(new Prompts(), time);
        var changes = new Changes();

        var content = await type.Type.OpenAsync(ProjectFile, changes, TestContext.CancellationToken);
        Assert.AreEqual("Ledger", content.Name);
        Assert.AreEqual(1, type.Watching);

        var noticed = time.WhenArmed();
        Write("src/Helpers.bas", "Attribute VB_Name = \"Helpers\"\r\n");
        WriteProject("""["src/Main.bas", "src/Helpers.bas"]""", otherFiles: "[]");
        // The file system reports the write, which arms the reread; the write is whole by then, so the reread sees all of it.
        await noticed.WaitAsync(TestContext.CancellationToken);
        Assert.AreEqual(0, changes.Calls.Count, "nothing is reread before the write settles");
        time.Advance(RDProjectWatcher.Settle);

        CollectionAssert.AreEqual(new[] { $"added {Full("src/Helpers.bas")} StandardModule" }, changes.Calls);

        changes.Close();
        Assert.AreEqual(0, type.Watching, "HexIDE closed the project");
    }

    [TestMethod]
    public async Task A_project_HexIDE_has_closed_already_is_not_followed()
    {
        WriteProject("""["src/Main.bas"]""", otherFiles: "[]");
        using var type = new RDCoreProjectType(new Prompts(), new ManualTime());
        var changes = new Changes();
        changes.Close();

        await type.Type.OpenAsync(ProjectFile, changes, TestContext.CancellationToken);

        Assert.AreEqual(0, type.Watching);
    }

    [TestMethod]
    public void A_write_reported_before_the_first_read_ends_is_reread_after_it()
    {
        WriteProject("""["src/Main.bas"]""", otherFiles: "[]");
        var changes = new Changes();
        var time = new ManualTime();
        using var watcher = new RDProjectWatcher(ProjectFile, changes, time, told.Add);
        var opened = RDProject.Read(ProjectFile);
        Write("src/Helpers.bas", "Attribute VB_Name = \"Helpers\"\r\n");
        WriteProject("""["src/Main.bas", "src/Helpers.bas"]""", otherFiles: "[]");

        watcher.Schedule();
        time.Advance(RDProjectWatcher.Settle);
        Assert.IsTrue(time.IsArmed, "the reread waits for the first read");
        watcher.Begin(opened);
        time.Advance(RDProjectWatcher.Settle);

        CollectionAssert.AreEqual(new[] { $"added {Full("src/Helpers.bas")} StandardModule" }, changes.Calls);
    }

    [TestMethod]
    public void A_burst_of_writes_that_never_goes_quiet_is_reread_after_the_longest_wait()
    {
        WriteProject("""["src/Main.bas"]""", otherFiles: "[]");
        var changes = new Changes();
        var time = new ManualTime();
        using var watcher = Following(changes, time);
        Write("src/Helpers.bas", "Attribute VB_Name = \"Helpers\"\r\n");
        WriteProject("""["src/Main.bas", "src/Helpers.bas"]""", otherFiles: "[]");
        var step = RDProjectWatcher.Settle / 2;

        watcher.Schedule();
        for (var waited = TimeSpan.Zero; waited + step < RDProjectWatcher.MaxWait; waited += step)
        {
            time.Advance(step);
            Assert.AreEqual(0, changes.Calls.Count, $"still writing at {waited + step}");
            watcher.Schedule();
        }
        time.Advance(step);

        CollectionAssert.AreEqual(new[] { $"added {Full("src/Helpers.bas")} StandardModule" }, changes.Calls);
    }

    // Rereads until the watcher gives up on a file it can't read.
    private static void Exhaust(ManualTime time)
    {
        for (var attempt = 0; attempt < 5 && time.IsArmed; attempt++)
        {
            time.Advance(RDProjectWatcher.Settle);
        }
    }

    [TestMethod]
    public void A_project_file_that_is_gone_is_told_to_the_user_once_until_it_is_back()
    {
        WriteProject("""["src/Main.bas"]""", otherFiles: "[]");
        var changes = new Changes();
        var time = new ManualTime();
        using var watcher = Following(changes, time);
        var written = File.ReadAllText(ProjectFile);
        File.Delete(ProjectFile);

        watcher.Noticed(ProjectFile);
        Exhaust(time);
        watcher.Noticed(ProjectFile);
        Exhaust(time);

        CollectionAssert.AreEqual(new[] { Strings.ProjectFileGone(ProjectFile) }, told);
        File.WriteAllText(ProjectFile, written);
        watcher.Noticed(ProjectFile);
        time.Advance(RDProjectWatcher.Settle);
        File.Delete(ProjectFile);
        watcher.Noticed(ProjectFile);
        Exhaust(time);
        Assert.AreEqual(2, told.Count, "back, then gone again");
        Assert.AreEqual(0, changes.Calls.Count);
    }

    [TestMethod]
    public void A_project_file_that_can_t_be_read_after_it_changed_is_told_to_the_user()
    {
        WriteProject("""["src/Main.bas"]""", otherFiles: "[]");
        var time = new ManualTime();
        using var watcher = Following(new Changes(), time);
        File.WriteAllText(ProjectFile, "Type=Exe\r\n");

        watcher.Noticed(ProjectFile);
        Exhaust(time);

        CollectionAssert.AreEqual(new[] { Strings.ProjectFileUnreadable(ProjectFile) }, told);
    }

    [TestMethod]
    public void A_project_whose_folder_was_moved_is_told_to_the_user_as_gone()
    {
        WriteProject("""["src/Main.bas"]""", otherFiles: "[]");
        var time = new ManualTime();
        using var watcher = Following(new Changes(), time);
        var moved = root + "-moved";
        Directory.Move(root, moved);
        try
        {
            // What a rename of the folder, reported by its parent's watcher, does.
            watcher.Schedule();
            Exhaust(time);
        }
        finally
        {
            Directory.Move(moved, root);
        }

        CollectionAssert.AreEqual(new[] { Strings.ProjectFileGone(ProjectFile) }, told);
    }

    [TestMethod]
    public void Changes_the_file_system_lost_track_of_are_found_by_reading_the_project_again()
    {
        WriteProject("""["src/Main.bas"]""", otherFiles: "[]");
        var changes = new Changes();
        var time = new ManualTime();
        using var watcher = Following(changes, time);
        Write("src/Helpers.bas", "Attribute VB_Name = \"Helpers\"\r\n");
        WriteProject("""["src/Main.bas", "src/Helpers.bas"]""", otherFiles: "[]");

        watcher.Lost(new InternalBufferOverflowException());
        time.Advance(RDProjectWatcher.Settle);

        CollectionAssert.AreEqual(new[] { $"added {Full("src/Helpers.bas")} StandardModule" }, changes.Calls);
        Assert.AreEqual(0, told.Count);
    }

    [TestMethod]
    public void A_module_written_after_the_project_lists_it_is_read_when_it_appears()
    {
        WriteProject("""["src/Main.bas", "src/Classes/Late.cls"]""");
        var changes = new Changes();
        var time = new ManualTime();
        using var watcher = Following(changes, time);

        watcher.Noticed(Full("README.md"));
        Assert.IsFalse(time.IsArmed, "a file the project has read already changes nothing");
        Write("src/Classes/Late.cls", "VERSION 1.0 CLASS\r\nAttribute VB_Name = \"Late\"\r\n");
        watcher.Noticed(Full("src/Classes/Late.cls"));
        time.Advance(RDProjectWatcher.Settle);

        CollectionAssert.AreEqual(
            new[] { $"removed {Full("src/Classes/Late.cls")}", $"added {Full("src/Classes/Late.cls")} ClassModule" },
            changes.Calls);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)] // bounds the deadlock this guards against; nothing waits on it otherwise
    public void A_project_closed_on_another_thread_while_HexIDE_takes_a_change_closes_rather_than_deadlocks()
    {
        WriteProject("""["src/Main.bas"]""", otherFiles: "[]");
        var changes = new Changes();
        var time = new ManualTime();
        using var watcher = Following(changes, time);
        var closedInTime = false;
        // HexIDE takes the change on its UI thread, which is closing the project: the report waits for the close.
        changes.OnAdd = _ => closedInTime = Task.Run(watcher.Dispose).Wait(Timeout.Infinite, TestContext.CancellationToken);
        Write("src/Helpers.bas", "Attribute VB_Name = \"Helpers\"\r\n");
        WriteProject("""["src/Main.bas", "src/Helpers.bas"]""", otherFiles: "[]");

        watcher.Reread();

        Assert.IsTrue(closedInTime);
    }

    [TestMethod]
    public void A_reread_that_ends_after_HexIDE_closed_the_project_reports_nothing_more_into_it()
    {
        WriteProject("""["src/Main.bas"]""", otherFiles: "[]");
        var changes = new Changes();
        var time = new ManualTime();
        using var watcher = Following(changes, time);
        changes.OnAdd = _ => watcher.Dispose();
        Write("src/Helpers.bas", "Attribute VB_Name = \"Helpers\"\r\n");
        Write("src/Tools.bas", "Attribute VB_Name = \"Tools\"\r\n");
        WriteProject("""["src/Main.bas", "src/Helpers.bas", "src/Tools.bas"]""", otherFiles: "[]");

        watcher.Reread();

        CollectionAssert.AreEqual(new[] { $"added {Full("src/Helpers.bas")} StandardModule" }, changes.Calls);
    }

    [TestMethod]
    public void A_file_caught_mid_write_is_read_again_and_nothing_is_reported_for_it()
    {
        WriteProject("""["src/Main.bas"]""", otherFiles: "[]");
        var changes = new Changes();
        var time = new ManualTime();
        using var watcher = Following(changes, time);

        File.WriteAllText(ProjectFile, """{ "Version": "0.1", "ProjectInfo": { "Name": "Ledger", "Modu""");
        watcher.Reread();
        Assert.AreEqual(0, changes.Calls.Count);
        Assert.IsTrue(time.IsArmed, "read again once it settles");

        Write("src/Helpers.bas", "Attribute VB_Name = \"Helpers\"\r\n");
        WriteProject("""["src/Main.bas", "src/Helpers.bas"]""", otherFiles: "[]");
        watcher.Reread();
        CollectionAssert.AreEqual(new[] { $"added {Full("src/Helpers.bas")} StandardModule" }, changes.Calls);
    }

    private sealed class Changes : IAddinProjectChanges
    {
        private readonly CancellationTokenSource closed = new();

        private readonly List<string> calls = [];

        /// <summary>What HexIDE was told, so far.</summary>
        public List<string> Calls
        {
            get
            {
                lock (calls)
                {
                    return [.. calls];
                }
            }
        }

        public CancellationToken Closed => closed.Token;

        public void Close() => closed.Cancel();

        /// <summary>Whether a member added throws, as a HexIDE that fails to take it would.</summary>
        public bool ThrowOnAdd { get; set; }

        /// <summary>What HexIDE does on taking a member, once it is recorded.</summary>
        public Action<AddinProjectMember>? OnAdd { get; set; }

        public void MemberAdded(AddinProjectMember member)
        {
            if (ThrowOnAdd)
            {
                throw new InvalidOperationException("HexIDE couldn't take the member.");
            }

            Record($"added {member.Path} {member.Kind}");
            OnAdd?.Invoke(member);
        }

        public void MemberRemoved(string path) => Record($"removed {path}");

        public void MemberMoved(string oldPath, string newPath) => Record($"moved {oldPath} {newPath}");

        public void ReferencesChanged(IReadOnlyList<AddinProjectReference> references) =>
            Record("references " + string.Join(",", references.Select(reference => reference.Name)));

        private void Record(string call)
        {
            lock (calls)
            {
                calls.Add(call);
            }
        }
    }
}
