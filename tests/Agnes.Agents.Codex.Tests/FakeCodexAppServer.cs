using System.Text.Json;
using Nerdbank.Streams;
using StreamJsonRpc;

namespace Agnes.Agents.Codex.Tests;

/// <summary>
/// A minimal in-memory <c>codex app-server</c> stand-in: speaks the same newline-delimited JSON-RPC
/// over a duplex stream so the real <c>CodexConnection</c>/<c>CodexAgentSession</c> plumbing can be
/// driven without launching a process. The turn behaviour is scripted by the test.
/// </summary>
internal sealed class FakeCodexAppServer : IAsyncDisposable
{
    private readonly JsonRpc _rpc;

    /// <summary>Set by the test: what the server does when a turn starts (emit items, request approval…).</summary>
    public Func<JsonRpc, Task>? OnTurn { get; set; }

    /// <summary>The decision the client sent back for the last approval request (null if none).</summary>
    public string? LastApprovalDecision { get; private set; }

    /// <summary>The <c>answers</c> object the client returned for the last user-input request (null if none).</summary>
    public JsonElement? LastUserInputAnswers { get; private set; }

    public static (Stream Client, FakeCodexAppServer Server) Create()
    {
        var (a, b) = FullDuplexStream.CreatePair();
        return (a, new FakeCodexAppServer(b));
    }

    private FakeCodexAppServer(Stream stream)
    {
        var formatter = new SystemTextJsonFormatter
        {
            JsonSerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            },
        };
        var handler = new NewLineDelimitedMessageHandler(stream, stream, formatter);
        _rpc = new JsonRpc(handler);
        _rpc.AddLocalRpcTarget(new Target(this), new JsonRpcTargetOptions { AllowNonPublicInvocation = true });
        _rpc.StartListening();
    }

    /// <summary>Requests approval from the client and records the decision it returns.</summary>
    public async Task<string> RequestApprovalAsync(string method, object parameters)
    {
        var response = await _rpc.InvokeWithParameterObjectAsync<JsonElement>(method, parameters).ConfigureAwait(false);
        LastApprovalDecision = response.GetProperty("decision").GetString();
        return LastApprovalDecision!;
    }

    /// <summary>Requests structured user input from the client and records the answers it returns.</summary>
    public async Task<JsonElement> RequestUserInputAsync(object parameters)
    {
        var response = await _rpc.InvokeWithParameterObjectAsync<JsonElement>("item/tool/requestUserInput", parameters).ConfigureAwait(false);
        LastUserInputAnswers = response.GetProperty("answers").Clone();
        return LastUserInputAnswers!.Value;
    }

    public Task NotifyAsync(string method, object parameters) => _rpc.NotifyWithParameterObjectAsync(method, parameters);

    public ValueTask DisposeAsync()
    {
        _rpc.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class Target(FakeCodexAppServer server)
    {
        [JsonRpcMethod("initialize", UseSingleObjectParameterDeserialization = true)]
        public object Initialize(JsonElement _) => new { userAgent = "fake", codexHome = "/tmp/.codex" };

        [JsonRpcMethod("thread/start", UseSingleObjectParameterDeserialization = true)]
        public object ThreadStart(JsonElement _) => new { thread = new { id = "th-1" }, model = "gpt-test" };

        [JsonRpcMethod("turn/start", UseSingleObjectParameterDeserialization = true)]
        public async Task<object> TurnStart(JsonElement _)
        {
            if (server.OnTurn is { } script)
            {
                await script(server._rpc).ConfigureAwait(false);
            }

            return new { turn = new { id = "tn-1" } };
        }
    }
}
