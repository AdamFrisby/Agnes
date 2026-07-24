using System.Text;
using System.Threading.Channels;
using Agnes.Abstractions;
using Agnes.Wrap.Pty;

namespace Agnes.Wrap;

/// <summary>
/// An <see cref="IAgentSession"/> backed by a real pseudo-terminal running a coding CLI. The wrapper reads
/// the PTY's byte stream, decodes it, and emits each chunk as a <see cref="TerminalOutputEvent"/> — exactly
/// the PTY-fallback shape the host already logs and replays (sessions/07 reuses that path rather than
/// inventing a new one). The same bytes are surfaced verbatim via <see cref="OutputReceived"/> so the local
/// terminal can tee them for the interactive user, and input written to the PTY is echoed by the terminal so
/// it is captured in the same stream. Handoff is left to the host's Replay path (the log <em>is</em> the
/// transcript).
/// </summary>
public sealed class WrappedCliSession : IAgentSession, ISteerableSession
{
    // Cancel-current-line / interrupt (^C) and end-of-input (^D) control bytes, for CancelAsync / clean close.
    private const byte Etx = 0x03;
    private const byte Eot = 0x04;

    // ESC (0x1b): interrupts the CLI's current generation the way pressing Escape does interactively — the
    // primitive behind true mid-turn steering (see TrySteerAsync).
    private const byte Esc = 0x1b;

    private readonly IPtyProcess _pty;
    private readonly string _terminalId;
    private readonly Channel<SessionEvent> _events =
        Channel.CreateUnbounded<SessionEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;
    private int _disposed;

    public WrappedCliSession(IPtyProcess pty, string? agentSessionId = null)
    {
        _pty = pty;
        AgentSessionId = agentSessionId ?? "wrap-" + Guid.NewGuid().ToString("n");
        _terminalId = "wrap-term-" + Guid.NewGuid().ToString("n");
        _pump = Task.Run(PumpOutputAsync);
    }

    /// <summary>Raised with each raw output chunk as the PTY produces it — the local terminal writes these
    /// through verbatim so the user's interactive experience is byte-identical to the bare CLI.</summary>
    public event Action<ReadOnlyMemory<byte>>? OutputReceived;

    /// <inheritdoc/>
    public string AgentSessionId { get; }

    /// <summary>The terminal id stamped on this session's <see cref="TerminalOutputEvent"/>s.</summary>
    public string TerminalId => _terminalId;

    /// <inheritdoc/>
    public ChannelReader<SessionEvent> Events => _events.Reader;

    /// <summary>Completes with the wrapped CLI's exit code once it exits.</summary>
    public Task<int> WaitForExitAsync(CancellationToken cancellationToken = default)
        => _pty.WaitForExitAsync(cancellationToken);

    /// <summary>Whether the wrapped CLI has exited.</summary>
    public bool HasExited => _pty.HasExited;

    /// <summary>The wrapped CLI's process id.</summary>
    public int ProcessId => _pty.ProcessId;

    /// <summary>
    /// Feeds bytes to the wrapped CLI's stdin. This is how a remote client (or the local tee) types into the
    /// session; the terminal echoes the bytes back on the output stream, so they are captured as
    /// <see cref="TerminalOutputEvent"/>s without a separate input-event kind.
    /// </summary>
    public async Task WriteInputAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (_disposed != 0)
        {
            return;
        }

        await _pty.Writer.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        await _pty.Writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resizes the wrapped terminal (a client reflow or the local window changing size).</summary>
    public Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default)
    {
        if (_disposed == 0)
        {
            _pty.Resize(columns, rows);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// A remote prompt against a terminal session is injected as typed input (text is written to the CLI's
    /// stdin followed by a newline). There is no discrete "turn" for a raw terminal, so this completes as soon
    /// as the input is delivered.
    /// </summary>
    public async Task<StopReason> PromptAsync(IReadOnlyList<ContentBlock> content, CancellationToken cancellationToken = default)
    {
        foreach (var block in content)
        {
            if (block is TextContent text)
            {
                await WriteInputAsync(Encoding.UTF8.GetBytes(text.Text + "\n"), cancellationToken).ConfigureAwait(false);
            }
        }

        return StopReason.EndTurn;
    }

    /// <summary>Interrupt maps to sending ^C to the terminal — the same thing pressing Ctrl+C locally would do.</summary>
    public Task CancelAsync(CancellationToken cancellationToken = default)
        => WriteInputAsync(new[] { Etx }, cancellationToken);

    /// <summary>Signals end-of-input (^D) to the wrapped CLI, so a stdin-reading command (e.g. a shell or
    /// <c>cat</c>) sees EOF and exits — the graceful counterpart to <see cref="CancelAsync"/>.</summary>
    public Task SendEndOfInputAsync(CancellationToken cancellationToken = default)
        => WriteInputAsync(new[] { Eot }, cancellationToken);

    /// <summary>A raw terminal has no ACP permission protocol, so there is nothing to answer.</summary>
    public Task RespondToPermissionAsync(string requestId, string optionId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <summary>
    /// True mid-turn steering (sessions/03): because we own the PTY's input stream we can inject a message
    /// into the running turn rather than cancel-and-restart. Press Escape (write the ESC byte) to interrupt
    /// the current generation the way an interactive user would, then type the new message text followed by a
    /// newline. Returns true once injected; returns false only if the session is already disposed (no input
    /// stream to write to), so the host falls back to cancel-then-resend.
    /// </summary>
    public async Task<bool> TrySteerAsync(IReadOnlyList<ContentBlock> content, CancellationToken cancellationToken = default)
    {
        if (_disposed != 0)
        {
            return false;
        }

        await WriteInputAsync(new[] { Esc }, cancellationToken).ConfigureAwait(false);
        foreach (var block in content)
        {
            if (block is TextContent text)
            {
                await WriteInputAsync(Encoding.UTF8.GetBytes(text.Text + "\n"), cancellationToken).ConfigureAwait(false);
            }
        }

        return true;
    }

    private async Task PumpOutputAsync()
    {
        var decoder = Encoding.UTF8.GetDecoder();
        var bytes = new byte[8192];
        var chars = new char[8192];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var read = await _pty.Reader.ReadAsync(bytes.AsMemory(), _cts.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break; // the PTY closed: the wrapped CLI exited.
                }

                OutputReceived?.Invoke(bytes.AsMemory(0, read));

                var charCount = decoder.GetChars(bytes, 0, read, chars, 0);
                if (charCount > 0)
                {
                    _events.Writer.TryWrite(new TerminalOutputEvent(_terminalId, new string(chars, 0, charCount)));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed — an intentional stop.
        }
        catch (Exception)
        {
            // The PTY stream was closed as the child exited; treat it as a normal end of output.
        }

        // Deliberately DO NOT complete the event channel here. A wrapped CLI exiting is a normal terminal end,
        // not an agent crash: leaving the channel open keeps the host from treating it as a fault to auto-restart
        // (which would respawn the CLI the user just quit). The final transcript stays intact and the session
        // remains watchable/handoff-capable until it is disposed.
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        await _pty.DisposeAsync().ConfigureAwait(false); // kills the child and closes the streams, unblocking the pump
        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The pump observed the cancellation / stream close during teardown — expected.
        }

        _events.Writer.TryComplete();
        _cts.Dispose();
    }
}
