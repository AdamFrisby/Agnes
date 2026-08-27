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

    // Updates that arrived for an id we hold no session for, tallied per id. During session/load that is the
    // expected case and the tally is the whole point: see OnSessionUpdateAsync.
    private readonly ConcurrentDictionary<string, long> _updatesBeforeRegistration = new();
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

    /// <summary>
    /// Resumes a prior conversation by id (<c>session/load</c>). The agent replays the whole conversation as
    /// <c>session/update</c> notifications before the call returns; the session is deliberately registered
    /// only <b>after</b> that, so the replay lands on an unknown id and is dropped. Agnes already holds that
    /// history in its own event log — appending it a second time would duplicate the entire transcript.
    ///
    /// <para>Awaiting the response alone is not enough to know the replay is over: the completion resumes on
    /// the thread pool and can overtake notification handlers still queued on the dispatch pump, so the tail
    /// of the replay would land on an already-registered session. Flushing the pump first closes that window
    /// — a race that showed up as the last replayed line, and only under load.</para>
    /// </summary>
    public async Task<AcpAgentSession> LoadSessionAsync(string sessionId, string workingDirectory, CancellationToken cancellationToken)
    {
        var result = await _rpc.InvokeWithParameterObjectAsync<AcpLoadSessionResult?>(
            "session/load",
            new AcpLoadSessionParams { SessionId = sessionId, Cwd = workingDirectory },
            cancellationToken).ConfigureAwait(false);

        await _dispatch.FlushAsync().ConfigureAwait(false);

        var modes = result?.Modes?.AvailableModes
            .Select(m => new SessionMode(m.Id, string.IsNullOrEmpty(m.Name) ? m.Id : m.Name))
            .ToArray();
        var session = new AcpAgentSession(sessionId, this, _dispatch, _logger, modes, result?.Modes?.CurrentModeId);
        _sessions[sessionId] = session;

        // The replay is over and its size is worth one line: it is the cost of this resume, it grows with the
        // conversation, and a run of ever-larger numbers here is the signature of a session being restarted
        // repeatedly. Reporting it per-update instead once produced a million warnings in a single log.
        _updatesBeforeRegistration.TryRemove(sessionId, out var replayed);
        _logger.LogInformation(
            "ACP resumed session {SessionId} (discarded {Replayed} replayed update(s) already in the event log)",
            sessionId, replayed);
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
                // Not necessarily an error: session/load replays the whole conversation before we register
                // the session, precisely so the replay lands here and is dropped (see LoadSessionAsync).
                // Only the first is logged — one line per update turned a routine resume into six figures of
                // log — and the running tally is reported when the session registers, or on dispose if it
                // never does, which is the case that would be a real fault.
                if (_updatesBeforeRegistration.AddOrUpdate(note.SessionId, 1, static (_, n) => n + 1) == 1)
                {
                    _logger.LogDebug(
                        "session/update for unregistered session {SessionId}; dropping (expected during session/load replay)",
                        note.SessionId);
                }
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

        // A tally still standing here was never claimed by a session/load, so those updates were dropped
        // without the replay explanation covering them. That is worth a warning, once, with the count.
        foreach (var (sessionId, dropped) in _updatesBeforeRegistration)
        {
            _logger.LogWarning(
                "Dropped {Dropped} update(s) for session {SessionId}, which was never registered on this connection",
                dropped, sessionId);
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
