using System.Text.Json;
using Agnes.Agents.Codex.Wire;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;

namespace Agnes.Agents.Codex;

/// <summary>
/// Owns one JSON-RPC channel to a <c>codex app-server</c> process (newline-delimited JSON over the
/// process's stdio). Performs the <c>initialize</c> handshake, starts one thread, drives turns, and
/// routes inbound item/turn notifications and approval requests to the <see cref="CodexAgentSession"/>.
/// Mirrors <c>AcpConnection</c> — same transport, different protocol.
/// </summary>
internal sealed class CodexConnection : ICodexRpc, IAsyncDisposable
{
    private const string ClientVersion = "0.1.0";

    private readonly ILogger _logger;
    private readonly IAsyncDisposable? _transportLifetime;
    private readonly JsonRpc _rpc;
    private readonly SerialSynchronizationContext _dispatch = new();
    private CodexAgentSession? _session;
    private int _disposed;

    public CodexConnection(Stream writer, Stream reader, ILogger logger, IAsyncDisposable? transportLifetime = null)
    {
        _logger = logger;
        _transportLifetime = transportLifetime;

        var formatter = new SystemTextJsonFormatter { JsonSerializerOptions = CodexJson.CreateOptions() };
        var handler = new NewLineDelimitedMessageHandler(writer, reader, formatter);

        _rpc = new JsonRpc(handler) { SynchronizationContext = _dispatch };
        _rpc.AddLocalRpcTarget(new InboundHandlers(this), new JsonRpcTargetOptions { AllowNonPublicInvocation = true });
        _rpc.StartListening();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _rpc.InvokeWithParameterObjectAsync<CodexInitializeResult>(
            "initialize",
            new CodexInitializeParams(new CodexClientInfo("Agnes", ClientVersion)),
            cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Codex app-server initialized");
    }

    public async Task<CodexAgentSession> StartThreadAsync(
        string workingDirectory, string approvalPolicy, string sandbox, CancellationToken cancellationToken)
    {
        var result = await _rpc.InvokeWithParameterObjectAsync<CodexThreadStartResult>(
            "thread/start",
            new CodexThreadStartParams { Cwd = workingDirectory, ApprovalPolicy = approvalPolicy, Sandbox = sandbox },
            cancellationToken).ConfigureAwait(false);

        _session = new CodexAgentSession(result.Thread.Id, this, _logger);
        return _session;
    }

    // ---- ICodexRpc: outbound calls from the session ----

    public async Task<string> StartTurnAsync(string threadId, IReadOnlyList<CodexUserInput> input, CancellationToken cancellationToken)
    {
        var result = await _rpc.InvokeWithParameterObjectAsync<CodexTurnStartResult>(
            "turn/start",
            new CodexTurnStartParams(threadId, input),
            cancellationToken).ConfigureAwait(false);
        return result.Turn.Id;
    }

    public async Task InterruptAsync(string threadId)
    {
        try
        {
            await _rpc.InvokeWithParameterObjectAsync<JsonElement>(
                "turn/interrupt", new CodexTurnInterruptParams(threadId), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Interrupt is best-effort — the turn may have already finished.
            _logger.LogDebug(ex, "Codex turn/interrupt failed (turn likely already ended)");
        }
    }

    // ---- inbound routing ----

    private void RouteNotification(Action<CodexAgentSession, JsonElement> handler, JsonElement p, string method)
    {
        if (_session is { } session)
        {
            try
            {
                handler(session, p);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Codex {Method} handler threw", method);
            }
        }
    }

    private Task<CodexApprovalResponse> OnApprovalAsync(JsonElement p, CancellationToken cancellationToken)
    {
        if (_session is { } session)
        {
            return session.HandleApprovalAsync(p, cancellationToken);
        }

        return Task.FromResult(new CodexApprovalResponse("denied"));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_session is not null)
        {
            await _session.DisposeAsync().ConfigureAwait(false);
        }

        _rpc.Dispose();
        _dispatch.Dispose();

        if (_transportLifetime is not null)
        {
            await _transportLifetime.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Codex app-server notifications/requests this client handles. Unlisted methods are ignored.</summary>
    private sealed class InboundHandlers(CodexConnection connection)
    {
        [JsonRpcMethod("item/started", UseSingleObjectParameterDeserialization = true)]
        public void ItemStarted(JsonElement p) => connection.RouteNotification((s, e) => s.HandleItemStarted(e), p, "item/started");

        [JsonRpcMethod("item/completed", UseSingleObjectParameterDeserialization = true)]
        public void ItemCompleted(JsonElement p) => connection.RouteNotification((s, e) => s.HandleItemCompleted(e), p, "item/completed");

        [JsonRpcMethod("item/agentMessage/delta", UseSingleObjectParameterDeserialization = true)]
        public void AgentMessageDelta(JsonElement p) => connection.RouteNotification((s, e) => s.HandleAgentMessageDelta(e), p, "item/agentMessage/delta");

        [JsonRpcMethod("thread/tokenUsage/updated", UseSingleObjectParameterDeserialization = true)]
        public void TokenUsage(JsonElement p) => connection.RouteNotification((s, e) => s.HandleTokenUsage(e), p, "thread/tokenUsage/updated");

        [JsonRpcMethod("turn/completed", UseSingleObjectParameterDeserialization = true)]
        public void TurnCompleted(JsonElement p) => connection.RouteNotification((s, e) => s.HandleTurnCompleted(e), p, "turn/completed");

        [JsonRpcMethod("error", UseSingleObjectParameterDeserialization = true)]
        public void Error(JsonElement p) => connection.RouteNotification((s, e) => s.HandleError(e), p, "error");

        [JsonRpcMethod("item/commandExecution/requestApproval", UseSingleObjectParameterDeserialization = true)]
        public Task<CodexApprovalResponse> CommandApproval(JsonElement p, CancellationToken ct) => connection.OnApprovalAsync(p, ct);

        [JsonRpcMethod("item/fileChange/requestApproval", UseSingleObjectParameterDeserialization = true)]
        public Task<CodexApprovalResponse> FileChangeApproval(JsonElement p, CancellationToken ct) => connection.OnApprovalAsync(p, ct);

        [JsonRpcMethod("item/permissions/requestApproval", UseSingleObjectParameterDeserialization = true)]
        public Task<CodexApprovalResponse> PermissionsApproval(JsonElement p, CancellationToken ct) => connection.OnApprovalAsync(p, ct);
    }
}
