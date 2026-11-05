using System.Collections.Concurrent;
using System.Text.Json;
using HexIDE.Addins;
using RDCore.SDK.Client;

namespace RDCore.HexIDE.Server;

/// <summary>
/// RDCore's client protocol over HexIDE's own connection to the language server: requests and notifications go out through
/// the add-in's channel on it, and the server's requests to the client come back through the methods the add-in claimed.
/// </summary>
/// <remarks>
/// HexIDE takes the methods an add-in answers when it registers the server, while RDCore's client sets its handlers once it
/// is connected. So every server-to-client method the protocol has is claimed up front (<see cref="ServerMethods"/>) and
/// answered by whichever handler is set by then, or refused as unknown while none is.
/// </remarks>
internal sealed class ConnectionChannel : IRDCorePlatformChannel
{
    /// <summary>The server-to-client requests and notifications RDCore's protocol has.</summary>
    public static readonly IReadOnlyList<string> ServerToClient =
    [
        RDCorePlatformProtocol.SessionMessageBox,
        RDCorePlatformProtocol.SessionInputBox,
        RDCorePlatformProtocol.RubberduckIndenterSettings,
        RDCorePlatformProtocol.RubberduckIndenterSettingsApply,
    ];

    private readonly ConcurrentDictionary<string, Func<JsonElement?, CancellationToken, Task<JsonElement?>>> handlers = new();
    private IAddinServerConnection? connection;

    /// <summary>The methods to claim when the server is registered, each answered by the handler set for it.</summary>
    public IReadOnlyDictionary<string, AddinServerMethod> ServerMethods => ServerToClient.ToDictionary(
        method => method,
        method => (AddinServerMethod)((parameters, token) => handlers.TryGetValue(method, out var handler)
            ? handler(parameters, token)
            : throw new InvalidOperationException($"Nothing in RDCore for HexIDE answers {method} yet.")));

    /// <summary>The connection of the server's current process: a restart is a new one.</summary>
    public void Connect(IAddinServerConnection current) => connection = current;

    /// <inheritdoc/>
    public Task<JsonElement?> SendRequestAsync(string method, JsonElement? parameters, CancellationToken token) =>
        Connected.RequestAsync(method, parameters, Timeout.InfiniteTimeSpan, token);

    /// <inheritdoc/>
    public Task SendNotificationAsync(string method, JsonElement? parameters, CancellationToken token) =>
        Connected.NotifyAsync(method, parameters, token);

    /// <inheritdoc/>
    public IDisposable OnRequest(string method, Func<JsonElement?, CancellationToken, Task<JsonElement?>> handler)
    {
        if (!ServerToClient.Contains(method))
        {
            throw new ArgumentException($"{method} isn't a server-to-client method this channel claims.", nameof(method));
        }

        handlers[method] = handler;
        return new Release(() => handlers.TryRemove(new KeyValuePair<string, Func<JsonElement?, CancellationToken, Task<JsonElement?>>>(method, handler)));
    }

    /// <inheritdoc/>
    public IDisposable OnNotification(string method, Func<JsonElement?, CancellationToken, Task> handler) =>
        OnRequest(method, async (parameters, token) =>
        {
            await handler(parameters, token);
            return null;
        });

    private IAddinServerConnection Connected =>
        connection ?? throw new InvalidOperationException("RDCore's language server isn't connected.");

    private sealed class Release(Action release) : IDisposable
    {
        public void Dispose() => release();
    }
}
