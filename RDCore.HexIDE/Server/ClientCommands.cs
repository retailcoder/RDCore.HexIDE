using System.Text.Json;
using HexIDE.Addins;
using HexIDE.Lsp.Messages;
using RDCore.HexIDE.Resources;
using RDCore.SDK.Semantics.Refactoring;

namespace RDCore.HexIDE.Server;

/// <summary>
/// Two of the commands of RDCore's code actions and code lenses that a client runs itself (<see cref="RefactoringCommands"/>), run in HexIDE's
/// code window: <c>rdcore.rename</c> starts HexIDE's rename, and <c>rdcore.showReferences</c> shows the references in Find References.
/// </summary>
/// <remarks>
/// HexIDE gives a command its arguments only, so each runs on the registration's connection, as it is when the command runs. One that
/// can't run says why, in the message HexIDE shows its user: its arguments aren't the ones the command takes, HexIDE couldn't start the
/// rename, or the server was taken out.
/// </remarks>
internal sealed class ClientCommands(Func<IAddinServerConnection?> connection)
{
    /// <summary>
    /// The prefix of the commands the add-in claims: the namespace of RDCore's own commands.
    /// </summary>
    public const string Prefix = "rdcore.";

    // A field a command's arguments leave out, or give as null, is refused rather than read as its default.
    private static readonly JsonSerializerOptions Strict = new()
    {
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
    };

    /// <summary>
    /// The commands to claim when the server is registered, each by its name.
    /// </summary>
    public IReadOnlyDictionary<string, AddinClientCommand> Handlers => new Dictionary<string, AddinClientCommand>(StringComparer.Ordinal)
    {
        [RefactoringCommands.Rename] = RenameAsync,
        [RefactoringCommands.ShowReferences] = ShowReferencesAsync,
    };

    // [uri, position]: the document and the position of the name to rename.
    private async Task RenameAsync(JsonElement[]? arguments, CancellationToken token)
    {
        var (uri, position) = Read(RefactoringCommands.Rename, arguments, 2,
            given => (Uri(given[0]), given[1].Deserialize<Position>(Strict) ?? throw new JsonException()));
        if (!await Connected.StartRenameAsync(uri, position, token))
        {
            throw new AddinCommandException(Strings.RenameNotStarted);
        }
    }

    // [uri, position, locations]: the arguments of VS Code's editor.action.showReferences, of which Find References shows the locations.
    private async Task ShowReferencesAsync(JsonElement[]? arguments, CancellationToken token)
    {
        var locations = Read(RefactoringCommands.ShowReferences, arguments, 3, given =>
            given[2].Deserialize<Location[]>(Strict) is { } read && read.All(location => location is not null) ? read : throw new JsonException());
        await Connected.ShowReferencesAsync(locations, token);
    }

    // The arguments the command takes, read in order; any it can't read are refused, before anything is asked of HexIDE.
    private static T Read<T>(string command, JsonElement[]? arguments, int count, Func<JsonElement[], T> read)
    {
        if (arguments is not { } given || given.Length < count)
        {
            throw new AddinCommandException(Strings.CommandArgumentsUnreadable(command));
        }

        try
        {
            return read(given);
        }
        catch (JsonException)
        {
            throw new AddinCommandException(Strings.CommandArgumentsUnreadable(command));
        }
    }

    private static string Uri(JsonElement argument) =>
        argument.ValueKind == JsonValueKind.String ? argument.GetString()! : throw new JsonException();

    private IAddinServerConnection Connected => connection() ?? throw new AddinCommandException(Strings.ServerNotConnected);
}
