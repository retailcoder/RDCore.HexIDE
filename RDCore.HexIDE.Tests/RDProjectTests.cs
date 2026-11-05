using HexIDE.Addins;
using RDCore.HexIDE.Projects;

namespace RDCore.HexIDE.Tests;

[TestClass]
public sealed class RDProjectTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "rdcore-rdproj-" + Guid.NewGuid().ToString("N"));

    public RDProjectTests()
    {
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
            Directory.Delete(root, recursive: true);
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
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "rdcore-outside.bas"), "Attribute VB_Name = \"Outside\"\r\n");
        var absolute = Path.Combine(Path.GetTempPath(), "rdcore-outside.bas").Replace("\\", "\\\\");
        WriteProject($$"""["src/Main.bas", "../rdcore-outside.bas", "src/../../rdcore-outside.bas", "{{absolute}}"]""");

        var project = RDProject.Read(ProjectFile);

        CollectionAssert.AreEqual(new[] { Full("src/Main.bas"), Full("README.md") }, project.Members.Select(member => member.Path).ToArray());
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
    public async Task An_open_project_s_file_is_followed_until_HexIDE_closes_it()
    {
        WriteProject("""["src/Main.bas"]""", otherFiles: "[]");
        using var type = new RDCoreProjectType();
        var changes = new Changes();

        var content = await type.Type.OpenAsync(ProjectFile, changes, TestContext.CancellationToken);
        Assert.AreEqual("Ledger", content.Name);
        Assert.AreEqual(1, type.Watching);

        Write("src/Helpers.bas", "Attribute VB_Name = \"Helpers\"\r\n");
        WriteProject("""["src/Main.bas", "src/Helpers.bas"]""", otherFiles: "[]");
        // The deadline only bounds a change the file system never reports.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (changes.Calls.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, TestContext.CancellationToken);
        }

        CollectionAssert.AreEqual(new[] { $"added {Full("src/Helpers.bas")} StandardModule" }, changes.Calls);

        changes.Close();
        Assert.AreEqual(0, type.Watching, "HexIDE closed the project");
    }

    [TestMethod]
    public void A_file_caught_mid_write_is_read_again_and_nothing_is_reported_for_it()
    {
        WriteProject("""["src/Main.bas"]""", otherFiles: "[]");
        var changes = new Changes();
        using var watcher = new RDProjectWatcher(ProjectFile, RDProject.Read(ProjectFile), changes);

        File.WriteAllText(ProjectFile, """{ "Version": "0.1", "ProjectInfo": { "Name": "Ledger", "Modu""");
        watcher.Reread();
        Assert.AreEqual(0, changes.Calls.Count);

        Write("src/Helpers.bas", "Attribute VB_Name = \"Helpers\"\r\n");
        WriteProject("""["src/Main.bas", "src/Helpers.bas"]""", otherFiles: "[]");
        watcher.Reread();
        CollectionAssert.AreEqual(new[] { $"added {Full("src/Helpers.bas")} StandardModule" }, changes.Calls);
    }

    private sealed class Changes : IAddinProjectChanges
    {
        private readonly CancellationTokenSource closed = new();

        public List<string> Calls { get; } = [];

        public CancellationToken Closed => closed.Token;

        public void Close() => closed.Cancel();

        public void MemberAdded(AddinProjectMember member) => Record($"added {member.Path} {member.Kind}");

        public void MemberRemoved(string path) => Record($"removed {path}");

        public void MemberMoved(string oldPath, string newPath) => Record($"moved {oldPath} {newPath}");

        public void ReferencesChanged(IReadOnlyList<AddinProjectReference> references) =>
            Record("references " + string.Join(",", references.Select(reference => reference.Name)));

        private void Record(string call)
        {
            lock (Calls)
            {
                Calls.Add(call);
            }
        }
    }
}
