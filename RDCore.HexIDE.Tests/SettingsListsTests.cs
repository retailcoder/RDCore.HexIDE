using System.Text.Json;
using HexIDE.Addins;
using NSubstitute;
using RDCore.HexIDE.Server;
using RDCore.SDK.Platform.Protocol;

namespace RDCore.HexIDE.Tests;

[TestClass]
public sealed class SettingsListsTests
{
    private const string Key = "Analysis.DisabledDiagnostics";

    private readonly IAddinServerConnection connection = Substitute.For<IAddinServerConnection>();
    private readonly List<string> written = [];

    public SettingsListsTests()
    {
        connection.SetSettingsAsync(Arg.Do<JsonElement>(settings => written.Add(settings.GetRawText())), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
    }

    public TestContext TestContext { get; set; } = null!;

    private void Stored(string? json) =>
        connection.GetSettings().Returns(json is null ? null : JsonDocument.Parse(json).RootElement.Clone());

    private Task<ListSettingValueResult> Apply(string value, bool remove = false, SettingScope scope = SettingScope.Platform,
        string key = Key, IAddinServerConnection? to = null) =>
        new SettingsLists(() => to ?? connection).ApplyAsync(
            new ListSettingValueParams { Key = key, Value = value, Remove = remove, Scope = scope }, TestContext.CancellationToken);

    [TestMethod]
    public async Task A_diagnostic_disabled_with_no_settings_yet_is_the_first_item_of_rdcore_Analysis_DisabledDiagnostics()
    {
        Stored(null);

        Assert.IsTrue((await Apply("RDC01001")).Applied);

        CollectionAssert.AreEqual(new[] { """{"rdcore":{"Analysis":{"DisabledDiagnostics":["RDC01001"]}}}""" }, written);
    }

    [TestMethod]
    public async Task A_diagnostic_disabled_goes_after_the_ones_already_disabled_and_the_other_settings_stay()
    {
        Stored("""{"rdcore":{"enabled":true,"Analysis":{"DisabledDiagnostics":["RDC00001"]}},"other":1}""");

        Assert.IsTrue((await Apply("RDC01001")).Applied);

        CollectionAssert.AreEqual(
            new[] { """{"rdcore":{"enabled":true,"Analysis":{"DisabledDiagnostics":["RDC00001","RDC01001"]}},"other":1}""" }, written);
    }

    [TestMethod]
    public async Task A_diagnostic_already_disabled_in_any_case_is_applied_and_nothing_is_written()
    {
        Stored("""{"rdcore":{"Analysis":{"DisabledDiagnostics":["rdc01001"]}}}""");

        Assert.IsTrue((await Apply("RDC01001")).Applied);

        Assert.IsEmpty(written);
    }

    [TestMethod]
    public async Task A_diagnostic_enabled_again_leaves_the_list_and_one_not_in_it_writes_nothing()
    {
        Stored("""{"rdcore":{"Analysis":{"DisabledDiagnostics":["RDC00001","RDC01001"]}}}""");

        Assert.IsTrue((await Apply("RDC01001", remove: true)).Applied);
        Stored("""{"rdcore":{"Analysis":{"DisabledDiagnostics":["RDC00001"]}}}""");
        Assert.IsTrue((await Apply("RDC01001", remove: true)).Applied);
        Stored(null);
        Assert.IsTrue((await Apply("RDC01001", remove: true)).Applied);

        CollectionAssert.AreEqual(new[] { """{"rdcore":{"Analysis":{"DisabledDiagnostics":["RDC00001"]}}}""" }, written);
    }

    [TestMethod]
    public async Task A_list_of_a_workspace_s_settings_is_declined_as_HexIDE_keeps_the_server_s_settings_for_the_user()
    {
        Stored(null);

        Assert.IsFalse((await Apply("RDC01001", scope: SettingScope.Workspace)).Applied);

        Assert.IsEmpty(written);
    }

    [TestMethod]
    [DataRow("""{"rdcore":{"Analysis":{"DisabledDiagnostics":"RDC00001"}}}""", false)]
    [DataRow("""{"rdcore":{"Analysis":7}}""", false)]
    [DataRow("""{"rdcore":{"Analysis":{"DisabledDiagnostics":"RDC00001"}}}""", true)]
    [DataRow("""{"rdcore":"off"}""", true)]
    public async Task A_key_that_holds_something_other_than_a_list_is_declined(string stored, bool remove)
    {
        Stored(stored);

        Assert.IsFalse((await Apply("RDC01001", remove)).Applied);

        Assert.IsEmpty(written);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("Analysis..DisabledDiagnostics")]
    public async Task A_key_that_names_no_setting_is_declined(string key)
    {
        Stored(null);

        Assert.IsFalse((await Apply("RDC01001", key: key)).Applied);

        Assert.IsEmpty(written);
    }

    [TestMethod]
    public async Task A_change_asked_for_while_the_server_is_taken_out_is_declined()
    {
        Assert.IsFalse((await new SettingsLists(() => null).ApplyAsync(
            new ListSettingValueParams { Key = Key, Value = "RDC01001" }, TestContext.CancellationToken)).Applied);

        var taken = Substitute.For<IAddinServerConnection>();
        taken.SetSettingsAsync(default, default).ReturnsForAnyArgs<Task>(_ => throw new ObjectDisposedException("rdcore"));
        Assert.IsFalse((await Apply("RDC01001", to: taken)).Applied);
    }
}
