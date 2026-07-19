using System.Text.Json;
using System.Threading.Channels;
using Agnes.Abstractions;
using Microsoft.Extensions.Logging;

namespace Agnes.Agents.Native;

/// <summary>
/// An <see cref="IAgentSession"/> backed by a coding CLI running in its native stream-json mode:
/// user turns are written to <see cref="_stdin"/> as JSON lines; the agent's JSONL <see cref="_stdout"/>
/// is read line-by-line and mapped to <see cref="SessionEvent"/>s. Because a single reader loop emits
/// events in stream order, turn-end is naturally ordered after the preceding events (no dispatch
/// juggling needed). Takes plain readers/writers so it's testable over in-memory streams.
/// </summary>
internal sealed class NativeAgentSession : IAgentSession
{
    private readonly TextReader _stdout;
    private readonly TextWriter _stdin;
    private readonly INativeStreamMapper _mapper;
    private readonly IAsyncDisposable? _lifetime;
    private readonly ILogger _logger;
    private readonly Channel<SessionEvent> _events =
        Channel.CreateUnbounded<SessionEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly object _turnGate = new();
    private readonly SemaphoreSlim _stdinLock = new(1, 1);
    private TaskCompletionSource<StopReason>? _turn;

    public NativeAgentSession(TextReader stdout, TextWriter stdin, INativeStreamMapper mapper, ILogger logger, IAsyncDisposable? lifetime = null)
    {
        _stdout = stdout;
        _stdin = stdin;
        _mapper = mapper;
        _logger = logger;
        _lifetime = lifetime;
        _ = Task.Run(ReadLoopAsync);
    }

    public string AgentSessionId { get; private set; } = Guid.NewGuid().ToString("n");

    public ChannelReader<SessionEvent> Events => _events.Reader;

    public async Task<StopReason> PromptAsync(IReadOnlyList<ContentBlock> content, CancellationToken cancellationToken = default)
    {
        var turn = new TaskCompletionSource<StopReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_turnGate)
        {
            _turn = turn;
        }

        await WriteLineAsync(_mapper.BuildUserTurn(content), cancellationToken).ConfigureAwait(false);

        using (cancellationToken.Register(() => turn.TrySetCanceled()))
        {
            return await turn.Task.ConfigureAwait(false);
        }
    }

    // Native stream-json has no standard cancel; best-effort (documented). A future control
    // message can be wired through the mapper.
    public Task CancelAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task RespondToPermissionAsync(string requestId, string optionId, CancellationToken cancellationToken = default)
    {
        var allow = optionId.StartsWith("allow", StringComparison.OrdinalIgnoreCase);
        if (_mapper.BuildPermissionResponse(requestId, allow) is { } line)
        {
            await WriteLineAsync(line, cancellationToken).ConfigureAwait(false);
            Emit(new PermissionResolvedEvent(requestId, optionId, allow ? PermissionOutcome.Allowed : PermissionOutcome.Denied));
        }
    }

    /// <summary>Serialises writes to the agent's stdin (user turns and control responses may race).</summary>
    private async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        await _stdinLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stdin.WriteLineAsync(line).ConfigureAwait(false);
            await _stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _stdinLock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            string? line;
            while ((line = await _stdout.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                JsonElement root;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    root = doc.RootElement.Clone();
                }
                catch (JsonException)
                {
                    _logger.LogDebug("Skipping non-JSON native line: {Line}", line);
                    continue;
                }

                foreach (var @event in _mapper.ToEvents(root))
                {
                    if (@event is SessionStartedEvent started)
                    {
                        AgentSessionId = started.AgentSessionId;
                    }

                    Emit(@event);

                    if (@event is TurnEndedEvent turnEnded)
                    {
                        CompleteTurn(turnEnded.Reason);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Native read loop ended");
            Emit(new AgentErrorEvent(ex.Message));
        }
        finally
        {
            _events.Writer.TryComplete();
            CompleteTurn(StopReason.EndTurn);
        }
    }

    private void CompleteTurn(StopReason reason)
    {
        TaskCompletionSource<StopReason>? turn;
        lock (_turnGate)
        {
            turn = _turn;
            _turn = null;
        }

        turn?.TrySetResult(reason);
    }

    private void Emit(SessionEvent @event)
        => _events.Writer.TryWrite(@event with { Timestamp = DateTimeOffset.UtcNow });

    public async ValueTask DisposeAsync()
    {
        if (_lifetime is not null)
        {
            await _lifetime.DisposeAsync().ConfigureAwait(false);
        }
    }
}
