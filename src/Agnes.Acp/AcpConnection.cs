using System.Collections.Concurrent;
using Agnes.Abstractions;
using Agnes.Acp.Wire;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;

namespace Agnes.Acp;

/// <summary>
/// Owns one JSON-RPC channel to an agent (newline-delimited JSON over a duplex byte
/// stream). Performs the ACP handshake, creates sessions, and routes inbound
/// notifications/requests to the right <see cref="AcpAgentSession"/>. The transport
/// (a real process's stdio, or an in-memory pair for tests) is supplied by the caller.
/// </summary>
internal sealed class AcpConnection : IAcpRpc, IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly IAsyncDisposable? _transportLifetime;
    private readonly JsonRpc _rpc;
    private readonly SerialSynchronizationContext _dispatch = new();
    private readonly ConcurrentDictionary<string, AcpAgentSession> _sessions = new();
    private int _disposed;

    /// <param name="writer">Stream the client sends requests on (e.g. the agent's stdin).</param>
    /// <param name="reader">Stream the client receives on (e.g. the agent's stdout).</param>
    /// <param name="transportLifetime">Optional owner (e.g. the process) disposed with the connection.</param>
    public AcpConnection(Stream writer, Stream reader, ILogger logger, IAsyncDisposable? transportLifetime = null)
    {
        _logger = logger;
        _transportLifetime = transportLifetime;

        var formatter = new SystemTextJsonFormatter { JsonSerializerOptions = AcpJson.CreateOptions() };
        var handler = new NewLineDelimitedMessageHandler(writer, reader, formatter);

        _rpc = new JsonRpc(handler) { SynchronizationContext = _dispatch };
        _rpc.AddLocalRpcTarget(new InboundHandlers(this), new JsonRpcTargetOptions { AllowNonPublicInvocation = true });
        _rpc.StartListening();
    }

    public async Task<AcpInitializeResult> InitializeAsync(CancellationToken cancellationToken)
    {
        var result = await _rpc.InvokeWithParameterObjectAsync<AcpInitializeResult>(
            "initialize",
            new AcpInitializeParams(),
            cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("ACP initialized (protocol v{Version})", result.ProtocolVersion);
        return result;
    }

    public async Task<AcpAgentSession> NewSessionAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        var result = await _rpc.InvokeWithParameterObjectAsync<AcpNewSessionResult>(
            "session/new",
            new AcpNewSessionParams { Cwd = workingDirectory },
            cancellationToken).ConfigureAwait(false);

        var modes = result.Modes?.AvailableModes
            .Select(m => new SessionMode(m.Id, string.IsNullOrEmpty(m.Name) ? m.Id : m.Name))
            .ToArray();
        var session = new AcpAgentSession(result.SessionId, this, _dispatch, _logger, modes, result.Modes?.CurrentModeId);
        _sessions[result.SessionId] = session;
        return session;
    }

    // ---- IAcpRpc: outbound calls made by sessions ----

    public Task<AcpPromptResult> PromptAsync(AcpPromptParams parameters, CancellationToken cancellationToken)
        => _rpc.InvokeWithParameterObjectAsync<AcpPromptResult>("session/prompt", parameters, cancellationToken);

    public Task CancelAsync(AcpCancelParams parameters)
        => _rpc.NotifyWithParameterObjectAsync("session/cancel", parameters);

    public Task SetModeAsync(AcpSetModeParams parameters)
        => _rpc.NotifyWithParameterObjectAsync("session/set_mode", parameters);

    // ---- inbound routing ----

    private Task OnSessionUpdateAsync(AcpSessionNotification note)
    {
        try
        {
            if (_sessions.TryGetValue(note.SessionId, out var session))
            {
                session.HandleUpdate(note.Update);
            }
            else
            {
                _logger.LogWarning("session/update for unknown session {SessionId}", note.SessionId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "session/update handler threw");
        }

        return Task.CompletedTask;
    }

    private Task<AcpRequestPermissionResult> OnRequestPermissionAsync(
        AcpRequestPermissionParams parameters,
        CancellationToken cancellationToken)
    {
        if (_sessions.TryGetValue(parameters.SessionId, out var session))
        {
            return session.HandlePermissionRequestAsync(parameters, cancellationToken);
        }

        _logger.LogWarning("session/request_permission for unknown session {SessionId}", parameters.SessionId);
        return Task.FromResult(new AcpRequestPermissionResult
        {
            Outcome = new AcpPermissionOutcome { Outcome = "cancelled" },
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var session in _sessions.Values)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        _rpc.Dispose();
        _dispatch.Dispose();

        if (_transportLifetime is not null)
        {
            await _transportLifetime.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Target object exposing ACP client-side methods to StreamJsonRpc.</summary>
    private sealed class InboundHandlers(AcpConnection connection)
    {
        [JsonRpcMethod("session/update", UseSingleObjectParameterDeserialization = true)]
        public Task SessionUpdate(AcpSessionNotification note) => connection.OnSessionUpdateAsync(note);

        [JsonRpcMethod("session/request_permission", UseSingleObjectParameterDeserialization = true)]
        public Task<AcpRequestPermissionResult> RequestPermission(AcpRequestPermissionParams parameters, CancellationToken cancellationToken)
            => connection.OnRequestPermissionAsync(parameters, cancellationToken);
    }
}
