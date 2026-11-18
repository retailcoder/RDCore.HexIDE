using System.Text.Json;
using System.Text.Json.Nodes;
using HexIDE.Addins;
using RDCore.SDK.Platform.Protocol;

namespace RDCore.HexIDE.Server;

/// <summary>
/// The language server's requests to add an item to the list of a setting, or to remove one from it (<c>rdcore/settings/listValue</c>), applied
/// to the server's settings in HexIDE: <c>Analysis.DisabledDiagnostics</c> is <c>rdcore.Analysis.DisabledDiagnostics</c> in the object HexIDE
/// keeps for the server, which it sends the running server when it changes.
/// </summary>
/// <remarks>
/// HexIDE keeps a server's settings for the user, so a list kept in a workspace's settings is declined, as is a key whose value is not a list.
/// One change is applied at a time, so two in a row both land.
/// </remarks>
internal sealed class SettingsLists(Func<IAddinServerConnection?> connection)
{
    /// <summary>
    /// The section of the server's settings RDCore's language server reads.
    /// </summary>
    public const string Section = "rdcore";

    private static readonly ListSettingValueResult Declined = new() { Applied = false };
    private static readonly ListSettingValueResult Applied = new() { Applied = true };

    private readonly SemaphoreSlim changing = new(1, 1);

    /// <summary>
    /// Adds the item to the list, or removes it from the list.
    /// </summary>
    /// <param name="request">The setting's key and scope, the item, and whether it is removed.</param>
    /// <param name="token">Cancels the change, and the wait for an earlier one to finish.</param>
    /// <returns>Applied where the list now holds the item, or no longer holds it when it was removed; declined otherwise.</returns>
    public async Task<ListSettingValueResult> ApplyAsync(ListSettingValueParams request, CancellationToken token)
    {
        var path = $"{Section}.{request.Key}".Split('.');
        if (request.Scope != SettingScope.Platform || request.Value.Length == 0 || path.Any(name => name.Length == 0)
            || connection() is not { } current)
        {
            return Declined;
        }

        await changing.WaitAsync(token);
        try
        {
            var settings = current.GetSettings() is { ValueKind: JsonValueKind.Object } stored
                ? JsonNode.Parse(stored.GetRawText())!.AsObject()
                : [];
            if (ListOf(settings, path, create: !request.Remove) is not { } list)
            {
                return request.Remove && IsAbsent(settings, path) ? Applied : Declined;
            }

            var held = list.Where(item => item is JsonValue value && value.TryGetValue<string>(out var text)
                && string.Equals(text, request.Value, StringComparison.OrdinalIgnoreCase)).ToList();
            if (request.Remove ? held.Count == 0 : held.Count > 0)
            {
                return Applied;
            }

            if (request.Remove)
            {
                held.ForEach(item => list.Remove(item));
            }
            else
            {
                list.Add(request.Value);
            }

            await current.SetSettingsAsync(JsonSerializer.SerializeToElement(settings), token);
            return Applied;
        }
        catch (ObjectDisposedException)
        {
            // The server was taken out while the change was asked for.
            return Declined;
        }
        finally
        {
            changing.Release();
        }
    }

    // The list at the path, its objects made where create is true; null where a name on the path holds something else, or nothing is there to remove.
    private static JsonArray? ListOf(JsonObject settings, string[] path, bool create)
    {
        var parent = settings;
        foreach (var name in path[..^1])
        {
            switch (parent[name])
            {
                case JsonObject next:
                    parent = next;
                    break;
                case null when create:
                    var made = new JsonObject();
                    parent[name] = made;
                    parent = made;
                    break;
                default:
                    return null;
            }
        }

        return parent[path[^1]] switch
        {
            JsonArray list => list,
            null when create => (JsonArray)(parent[path[^1]] = new JsonArray()),
            _ => null,
        };
    }

    // Whether nothing is at the path, so that an item to be removed is not in a list there.
    private static bool IsAbsent(JsonObject settings, string[] path)
    {
        JsonNode? node = settings;
        foreach (var name in path)
        {
            if (node is not JsonObject parent || parent[name] is not { } next)
            {
                return node is JsonObject;
            }

            node = next;
        }

        return false;
    }
}
