using System.Diagnostics;
using System.Text.Json;
using HexIDE.Addins;
using HexIDE.Lsp.Messages;
using RDCore.HexIDE.Resources;
using RDCore.SDK.Model.Source;
using RDCore.SDK.Semantics.Refactoring;
using LspRange = HexIDE.Lsp.Messages.Range;

namespace RDCore.HexIDE.Server;

/// <summary>
/// Two of the commands of RDCore's code actions and code lenses that a client runs itself (<see cref="RefactoringCommands"/>), run in HexIDE's
/// code window: <c>rdcore.rename</c> starts HexIDE's rename, and <c>rdcore.showReferences</c> shows the references in Find References.
/// </summary>
/// <remarks>
/// RDCore.SDK reads a command's arguments (<see cref="RenameCommandArguments"/>, <see cref="ShowReferencesCommandArguments"/>), and the add-in
/// gives HexIDE what it reads, as HexIDE's own positions and locations.
/// <para>
/// HexIDE gives a command its arguments only, so each runs on the registration's connection, as it is when the command runs. One that
/// can't run says why, in the message HexIDE shows its user: its arguments aren't the ones the command takes (what RDCore.SDK found
/// wrong goes to the trace log, in English), HexIDE couldn't start the rename, or the server was taken out.
/// </para>
/// <para>
/// HexIDE gives the add-in a command by its name, not by the prefix: a command of RDCore's that the add-in doesn't claim, such as
/// <c>rdcore.refactor</c>, is HexIDE's own to run or to refuse.
/// </para>
/// </remarks>
internal sealed class ClientCommands(Func<IAddinServerConnection?> connection)
{
    /// <summary>
    /// The prefix of the commands the add-in claims: the namespace of RDCore's own commands.
    /// </summary>
    public const string Prefix = "rdcore.";

    /// <summary>
    /// The commands to claim when the server is registered, each by its name.
    /// </summary>
    public IReadOnlyDictionary<string, AddinClientCommand> Handlers => new Dictionary<string, AddinClientCommand>(StringComparer.Ordinal)
    {
        [RefactoringCommands.Rename] = RenameAsync,
        [RefactoringCommands.ShowReferences] = ShowReferencesAsync,
    };

    private async Task RenameAsync(JsonElement[]? arguments, CancellationToken token)
    {
        if (!RenameCommandArguments.TryRead(arguments, out var read, out var failure))
        {
            throw Unreadable(RefactoringCommands.Rename, failure);
        }

        if (!await Connected.StartRenameAsync(read.DocumentUri, ToHexIde(read.Position), token))
        {
            throw new AddinCommandException(Strings.RenameNotStarted);
        }
    }

    private async Task ShowReferencesAsync(JsonElement[]? arguments, CancellationToken token)
    {
        if (!ShowReferencesCommandArguments.TryRead(arguments, out var read, out var failure))
        {
            throw Unreadable(RefactoringCommands.ShowReferences, failure);
        }

        var locations = read.Locations
            .Select(location => new Location(location.DocumentUri, new LspRange(ToHexIde(location.Range.Start), ToHexIde(location.Range.End))))
            .ToArray();
        await Connected.ShowReferencesAsync(locations, token);
    }

    // What RDCore.SDK found wrong is English, for a log: the user sees the localized sentence.
    private static AddinCommandException Unreadable(string command, string? failure)
    {
        Trace.TraceError($"RDCore for HexIDE: {failure}");
        return new(Strings.CommandArgumentsUnreadable(command));
    }

    private static Position ToHexIde(SourcePosition position) => new(position.Line, position.Character);

    private IAddinServerConnection Connected => connection() ?? throw new AddinCommandException(Strings.ServerNotConnected);
}
