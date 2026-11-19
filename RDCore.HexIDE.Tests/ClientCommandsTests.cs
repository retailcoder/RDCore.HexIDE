using System.Globalization;
using System.Text.Json;
using HexIDE.Addins;
using HexIDE.Lsp.Messages;
using LspRange = HexIDE.Lsp.Messages.Range;
using NSubstitute;
using RDCore.HexIDE.Resources;
using RDCore.HexIDE.Server;
using RDCore.SDK.Semantics.Refactoring;

namespace RDCore.HexIDE.Tests;

[TestClass]
public sealed class ClientCommandsTests
{
    private const string Uri = "file:///C:/Ledger/Module1.bas";

    private readonly IAddinServerConnection connection = Substitute.For<IAddinServerConnection>();

    public TestContext TestContext { get; set; } = null!;

    private ClientCommands Commands(bool connected = true) => new(() => connected ? connection : null);

    // The arguments as the language server sends them, read back from their JSON.
    private static JsonElement[] Arguments(string json) => JsonDocument.Parse(json).RootElement.EnumerateArray().ToArray();

    private Task Run(string command, string arguments, bool connected = true) =>
        Commands(connected).Handlers[command](Arguments(arguments), TestContext.CancellationToken);

    [TestMethod]
    public void The_add_in_claims_RDCore_s_two_client_commands_under_its_prefix()
    {
        var claimed = Commands().Handlers.Keys.ToArray();

        CollectionAssert.AreEquivalent(new[] { RefactoringCommands.Rename, RefactoringCommands.ShowReferences }, claimed);
        Assert.IsTrue(claimed.All(name => name.StartsWith(ClientCommands.Prefix, StringComparison.Ordinal) && name.Length > ClientCommands.Prefix.Length),
            "HexIDE refuses a command that isn't under the prefix");
    }

    [TestMethod]
    public async Task Rename_starts_HexIDE_s_rename_at_the_position_of_the_document_it_names()
    {
        connection.StartRenameAsync(default!, default!, default).ReturnsForAnyArgs(true);

        await Run(RefactoringCommands.Rename, $$"""["{{Uri}}", { "line": 4, "character": 12 }]""");

        await connection.Received(1).StartRenameAsync(Uri, new Position(4, 12), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task A_rename_HexIDE_can_t_start_says_so()
    {
        connection.StartRenameAsync(default!, default!, default).ReturnsForAnyArgs(false);

        var failure = await Assert.ThrowsExactlyAsync<AddinCommandException>(
            () => Run(RefactoringCommands.Rename, $$"""["{{Uri}}", { "line": 400, "character": 0 }]"""));

        Assert.AreEqual(Strings.RenameNotStarted, failure.Message);
    }

    [TestMethod]
    public async Task Show_references_shows_the_locations_it_is_given_in_Find_References()
    {
        await Run(RefactoringCommands.ShowReferences, $$"""
            ["{{Uri}}", { "line": 4, "character": 12 },
             [{ "uri": "{{Uri}}", "range": { "start": { "line": 9, "character": 4 }, "end": { "line": 9, "character": 8 } } },
              { "uri": "file:///C:/Ledger/Shapes.cls", "range": { "start": { "line": 2, "character": 0 }, "end": { "line": 2, "character": 4 } } }]]
            """);

        await connection.Received(1).ShowReferencesAsync(
            Arg.Is<IReadOnlyList<Location>>(locations => locations.SequenceEqual(new[]
            {
                new Location(Uri, new LspRange(new Position(9, 4), new Position(9, 8))),
                new Location("file:///C:/Ledger/Shapes.cls", new LspRange(new Position(2, 0), new Position(2, 4))),
            })),
            Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task No_references_shows_an_empty_Find_References()
    {
        await Run(RefactoringCommands.ShowReferences, $$"""["{{Uri}}", { "line": 4, "character": 12 }, []]""");

        await connection.Received(1).ShowReferencesAsync(Arg.Is<IReadOnlyList<Location>>(locations => locations.Count == 0), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [DataRow(RefactoringCommands.Rename, "[]", DisplayName = "rename: none")]
    [DataRow(RefactoringCommands.Rename, $$"""["{{Uri}}"]""", DisplayName = "rename: no position")]
    [DataRow(RefactoringCommands.Rename, """[42, { "line": 4, "character": 12 }]""", DisplayName = "rename: a uri that isn't a string")]
    [DataRow(RefactoringCommands.Rename, $$"""["{{Uri}}", { "line": 4 }]""", DisplayName = "rename: a position without its character")]
    [DataRow(RefactoringCommands.ShowReferences, $$"""["{{Uri}}", { "line": 4, "character": 12 }]""", DisplayName = "references: no locations")]
    [DataRow(RefactoringCommands.ShowReferences, $$"""["{{Uri}}", { "line": 4, "character": 12 }, { }]""", DisplayName = "references: not an array")]
    [DataRow(RefactoringCommands.ShowReferences, $$"""["{{Uri}}", { "line": 4, "character": 12 }, [{ "uri": "{{Uri}}" }]]""", DisplayName = "references: a location without its range")]
    [DataRow(RefactoringCommands.Rename, """[null, { "line": 4, "character": 12 }]""", DisplayName = "rename: a null uri")]
    [DataRow(RefactoringCommands.ShowReferences, $$"""["{{Uri}}", { "line": 4, "character": 12 }, [null]]""", DisplayName = "references: a null location")]
    public async Task Arguments_the_command_doesn_t_take_are_refused_and_HexIDE_is_asked_nothing(string command, string arguments)
    {
        var failure = await Assert.ThrowsExactlyAsync<AddinCommandException>(() => Run(command, arguments));

        Assert.AreEqual(Strings.CommandArgumentsUnreadable(command), failure.Message);
        await connection.DidNotReceiveWithAnyArgs().StartRenameAsync(default!, default!, default);
        await connection.DidNotReceiveWithAnyArgs().ShowReferencesAsync(default!, default);
    }

    [TestMethod]
    public async Task No_arguments_at_all_are_refused_too()
    {
        var failure = await Assert.ThrowsExactlyAsync<AddinCommandException>(
            () => Commands().Handlers[RefactoringCommands.Rename](null, TestContext.CancellationToken));

        Assert.AreEqual(Strings.CommandArgumentsUnreadable(RefactoringCommands.Rename), failure.Message);
    }

    [TestMethod]
    [DataRow(RefactoringCommands.Rename, $$"""["{{Uri}}", { "line": 4, "character": 12 }]""")]
    [DataRow(RefactoringCommands.ShowReferences, $$"""["{{Uri}}", { "line": 4, "character": 12 }, []]""")]
    public async Task A_command_after_the_server_was_taken_out_says_so(string command, string arguments)
    {
        var failure = await Assert.ThrowsExactlyAsync<AddinCommandException>(() => Run(command, arguments, connected: false));

        Assert.AreEqual(Strings.ServerNotConnected, failure.Message);
    }

    [TestMethod]
    public void The_messages_are_in_English_and_in_French()
    {
        var before = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-CA");
            Assert.AreEqual("RDCore sent rdcore.rename with arguments it doesn't take.", Strings.CommandArgumentsUnreadable("rdcore.rename"));
            var english = (Strings.RenameNotStarted, Strings.ServerNotConnected);

            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-CA");
            Assert.AreEqual("RDCore a envoyé rdcore.rename avec des arguments que cette commande ne prend pas.", Strings.CommandArgumentsUnreadable("rdcore.rename"));
            Assert.AreNotEqual(english.RenameNotStarted, Strings.RenameNotStarted);
            Assert.AreNotEqual(english.ServerNotConnected, Strings.ServerNotConnected);
        }
        finally
        {
            CultureInfo.CurrentUICulture = before;
        }
    }
}
