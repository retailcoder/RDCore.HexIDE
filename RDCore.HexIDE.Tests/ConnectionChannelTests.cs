using System.Text.Json;
using RDCore.HexIDE.Server;
using RDCore.SDK.Client;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;

namespace RDCore.HexIDE.Tests;

[TestClass]
public class ConnectionChannelTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task A_method_claimed_at_registration_is_answered_by_the_handler_set_later_and_refused_before_and_after()
    {
        var channel = new ConnectionChannel();
        var claimed = channel.ServerMethods[RDCorePlatformProtocol.SessionMessageBox];
        var answer = JsonDocument.Parse("""{ "result": 1 }""").RootElement;

        await RefusedAsMethodNotFound(() => claimed(null, TestContext.CancellationToken));
        using (channel.OnRequest(RDCorePlatformProtocol.SessionMessageBox, (_, _) => Task.FromResult<JsonElement?>(answer)))
        {
            Assert.AreEqual(answer, await claimed(null, TestContext.CancellationToken));
        }

        await RefusedAsMethodNotFound(() => claimed(null, TestContext.CancellationToken));
    }

    // HexIDE passes a LocalRpcException on to the server as it is; anything else it answers as an invocation error.
    private static async Task RefusedAsMethodNotFound(Func<Task> answer)
    {
        var refusal = await Assert.ThrowsExactlyAsync<LocalRpcException>(answer);
        Assert.AreEqual((int)JsonRpcErrorCode.MethodNotFound, refusal.ErrorCode);
    }

    [TestMethod]
    public void A_handler_for_a_method_that_wasn_t_claimed_is_refused_as_it_could_never_be_called()
    {
        var channel = new ConnectionChannel();

        Assert.ThrowsExactly<ArgumentException>(() =>
            channel.OnRequest("rdcore/something/else", (_, _) => Task.FromResult<JsonElement?>(null)));
    }

    [TestMethod]
    public async Task Nothing_is_sent_before_the_server_is_connected()
    {
        var channel = new ConnectionChannel();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            channel.SendRequestAsync(RDCorePlatformProtocol.SessionStatus, null, TestContext.CancellationToken));
    }
}
