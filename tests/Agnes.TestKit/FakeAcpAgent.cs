using System.Text.Json;
using Nerdbank.Streams;
using StreamJsonRpc;

namespace Agnes.TestKit;

/// <summary>Result of a permission request from the agent's point of view.</summary>
public sealed record FakePermissionResult(bool Selected, string? OptionId);

/// <summary>
/// Passed to a prompt handler so a scripted agent can stream <c>session/update</c>
/// notifications and issue permission requests back to the client under test.
/// </summary>
public sealed class FakePromptContext
{
    private readonly JsonRpc _rpc;
    private readonly string _sessionId;

    internal FakePromptContext(JsonRpc rpc, string sessionId, string promptText)
    {
        _rpc = rpc;
        _sessionId = sessionId;
        PromptText = promptText;
    }

    /// <summary>The concatenated text of the incoming prompt.</summary>
    public string PromptText { get; }

    public Task SendAgentMessageAsync(string text)
        => SendUpdate(new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text } });

    public Task SendThoughtAsync(string text)
        => SendUpdate(new { sessionUpdate = "agent_thought_chunk", content = new { type = "text", text } });

    public Task SendToolCallAsync(string toolCallId, string title, string kind, string status)
        => SendUpdate(new { sessionUpdate = "tool_call", toolCallId, title, kind, status, content = Array.Empty<object>() });

    public Task SendPlanAsync(params (string Content, string Status)[] entries)
        => SendUpdate(new
        {
            sessionUpdate = "plan",
            entries = entries.Select(e => new { content = e.Content, status = e.Status }).ToArray(),
        });

    public async Task<FakePermissionResult> RequestPermissionAsync(
        string toolCallId,
        string title,
        params (string Id, string Name, string Kind)[] options)
    {
        var response = await _rpc.InvokeWithParameterObjectAsync<JsonElement>(
            "session/request_permission",
            new
            {
                sessionId = _sessionId,
                toolCall = new { toolCallId, title },
                options = options.Select(o => new { optionId = o.Id, name = o.Name, kind = o.Kind }).ToArray(),
            }).ConfigureAwait(false);

        var outcome = response.GetProperty("outcome");
        var kind = outcome.GetProperty("outcome").GetString();
        if (kind == "selected")
        {
            return new FakePermissionResult(true, outcome.GetProperty("optionId").GetString());
        }

        return new FakePermissionResult(false, null);
    }

    private Task SendUpdate(object update)
        => _rpc.NotifyWithParameterObjectAsync("session/update", new { sessionId = _sessionId, update });
}

/// <summary>
/// A minimal, scriptable ACP <em>agent</em> (the far side of the protocol) for driving
/// the client under test over an in-memory duplex stream. Not a real coding agent.
/// </summary>
public sealed class FakeAcpAgent : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly JsonRpc _rpc;
    private readonly Func<FakePromptContext, Task<string>> _onPrompt;

    public FakeAcpAgent(Stream duplex, Func<FakePromptContext, Task<string>> onPrompt)
    {
        _onPrompt = onPrompt;
        var formatter = new SystemTextJsonFormatter { JsonSerializerOptions = Options };
        var handler = new NewLineDelimitedMessageHandler(duplex, duplex, formatter);
        _rpc = new JsonRpc(handler);
        _rpc.AddLocalRpcTarget(new Methods(this), new JsonRpcTargetOptions { AllowNonPublicInvocation = true });
        _rpc.StartListening();
    }

    /// <summary>Creates a connected (client, agent) duplex stream pair.</summary>
    public static (Stream Client, Stream Agent) CreateTransport()
    {
        var (a, b) = FullDuplexStream.CreatePair();
        return (a, b);
    }

    public ValueTask DisposeAsync()
    {
        _rpc.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class Methods(FakeAcpAgent agent)
    {
        [JsonRpcMethod("initialize", UseSingleObjectParameterDeserialization = true)]
        public object Initialize(JsonElement _) => new
        {
            protocolVersion = 1,
            agentCapabilities = new
            {
                loadSession = false,
                promptCapabilities = new { image = false, audio = false, embeddedContext = false },
            },
        };

        [JsonRpcMethod("session/new", UseSingleObjectParameterDeserialization = true)]
        public object NewSession(JsonElement _) => new { sessionId = "sess-1" };

        [JsonRpcMethod("session/prompt", UseSingleObjectParameterDeserialization = true)]
        public async Task<object> Prompt(JsonElement request)
        {
            var sessionId = request.GetProperty("sessionId").GetString() ?? "sess-1";
            var text = ExtractText(request);
            var context = new FakePromptContext(agent._rpc, sessionId, text);
            var stopReason = await agent._onPrompt(context).ConfigureAwait(false);
            return new { stopReason };
        }

        [JsonRpcMethod("session/cancel", UseSingleObjectParameterDeserialization = true)]
        public void Cancel(JsonElement _)
        {
            // No-op for the fake; scripted prompts complete on their own.
        }

        private static string ExtractText(JsonElement request)
        {
            if (!request.TryGetProperty("prompt", out var prompt) || prompt.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            foreach (var block in prompt.EnumerateArray())
            {
                if (block.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                {
                    parts.Add(t.GetString() ?? string.Empty);
                }
            }

            return string.Concat(parts);
        }
    }
}
