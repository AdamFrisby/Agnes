using System.Text;
using Agnes.Abstractions;
using Microsoft.Extensions.Logging;
using Porta.Pty;

namespace Agnes.Host.Sessions;

/// <summary>
/// A live handle to one real pseudo-terminal opened by <see cref="PortaPtyCliFallback"/>. It pumps the PTY's
/// output stream (decoded as UTF-8, tolerant of multi-byte sequences split across reads) out through
/// <see cref="ITerminalOutputSource.OutputReceived"/>, reports process exit through
/// <see cref="ITerminalExitSource.Exited"/>, forwards <see cref="WriteAsync"/> to the PTY's input and
/// <see cref="ResizeAsync"/> to its window size. Disposal kills the child process and frees the PTY.
/// </summary>
internal sealed class PortaPtyTerminalHandle : ITerminalHandle, ITerminalOutputSource, ITerminalExitSource
{
    private readonly IPtyConnection _connection;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _readCts = new();
    private readonly Task _readLoop;

    // Output produced before a subscriber attaches is buffered and replayed on subscription, so no early bytes
    // are lost in the microscopic window between opening the PTY and the SessionManager wiring OutputReceived.
    // The lock also serialises the producer (the read loop) against the subscribe-time flush to preserve order.
    private readonly Lock _gate = new();
    private readonly StringBuilder _pending = new();
    private Action<string, string>? _output;

    // Exit is latched: a process that exits before a subscriber attaches (a very fast command) still fires
    // Exited on the late subscriber, so no one misses the completion signal (e.g. the login-badge refresh).
    private Action? _exited;
    private bool _processExited;

    private int _disposed;

    public PortaPtyTerminalHandle(IPtyConnection connection, ILogger logger)
    {
        _connection = connection;
        _logger = logger;
        _connection.ProcessExited += OnProcessExited;
        _readLoop = Task.Run(() => PumpAsync(_readCts.Token));
    }

    public string TerminalId { get; } = "pty-" + Guid.NewGuid().ToString("N");

    /// <summary>The spawned child's process id — exposed for host diagnostics and tests (verifying the child is
    /// actually terminated on dispose).</summary>
    internal int ProcessId => _connection.Pid;

    /// <inheritdoc />
    public event Action<string, string>? OutputReceived
    {
        add
        {
            string? backlog;
            lock (_gate)
            {
                _output += value;
                backlog = _pending.Length > 0 ? _pending.ToString() : null;
                _pending.Clear();
                // Replay the backlog to the just-attached subscriber while holding the gate, so it lands before
                // any live chunk the read loop is about to raise (the read loop takes the same gate). The sink is
                // fire-and-forget (it enqueues a Task), so invoking under the lock does not block or re-enter.
                if (backlog is not null)
                {
                    value?.Invoke(TerminalId, backlog);
                }
            }
        }
        remove
        {
            lock (_gate)
            {
                _output -= value;
            }
        }
    }

    /// <inheritdoc />
    public event Action? Exited
    {
        add
        {
            bool fireNow;
            lock (_gate)
            {
                _exited += value;
                fireNow = _processExited;
            }

            // The process had already exited before this subscriber attached — replay the latched signal.
            if (fireNow)
            {
                value?.Invoke();
            }
        }
        remove
        {
            lock (_gate)
            {
                _exited -= value;
            }
        }
    }

    /// <inheritdoc />
    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        await _connection.WriterStream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        await _connection.WriterStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            _connection.Resize(Math.Max(1, columns), Math.Max(1, rows));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _connection.ProcessExited -= OnProcessExited;
        await _readCts.CancelAsync().ConfigureAwait(false);

        try
        {
            _connection.Kill();
        }
        catch (Exception ex)
        {
            // Best-effort teardown: the child may already be gone (raced its own exit) — nothing to kill.
            _logger.LogDebug(ex, "Killing terminal {TerminalId} on dispose failed", TerminalId);
        }

        try
        {
            await _readLoop.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The read loop already guards its own exceptions; this is a belt-and-braces guard on the await.
            _logger.LogDebug(ex, "Terminal {TerminalId} read loop faulted during dispose", TerminalId);
        }

        _readCts.Dispose();
        _connection.Dispose();
    }

    private void OnProcessExited(object? sender, PtyExitedEventArgs e) => MarkExited();

    // Latches the "process exited" fact and fires Exited exactly once, whichever signal arrives first — Porta's
    // ProcessExited event, or the read loop reaching the PTY's natural EOF (both can beat the other, and the
    // event is occasionally missed for an instant-exit child with no I/O, so we rely on either).
    private void MarkExited()
    {
        Action? handler;
        lock (_gate)
        {
            if (_processExited)
            {
                return;
            }

            _processExited = true;
            handler = _exited;
        }

        try
        {
            handler?.Invoke();
        }
        catch (Exception ex)
        {
            // An observer must never change the terminal's outcome — isolate its failure (design directive).
            _logger.LogDebug(ex, "Terminal {TerminalId} exit observer threw", TerminalId);
        }
    }

    // Reads the PTY master stream to end-of-file, decoding incrementally so a UTF-8 code point split across two
    // reads is never mangled, and raising each decoded chunk as output.
    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        var decoder = Encoding.UTF8.GetDecoder();
        var bytes = new byte[4096];
        var chars = new char[8192];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await _connection.ReaderStream.ReadAsync(bytes.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                var charCount = decoder.GetChars(bytes, 0, read, chars, 0, flush: false);
                if (charCount > 0)
                {
                    RaiseOutput(new string(chars, 0, charCount));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal teardown: the handle was disposed and the read cancelled — the child is being killed by us,
            // so we do NOT latch a natural exit here (dispose already unsubscribed Porta's exit event too).
            return;
        }
        catch (IOException)
        {
            // On Unix the PTY master read fails with EIO once the slave side closes (the child exited) — this is
            // the normal end-of-stream signal for a pseudo-terminal, not an error worth surfacing.
        }
        catch (ObjectDisposedException)
        {
            // The connection was torn down underneath us during dispose — end quietly, no exit latch.
            return;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Terminal {TerminalId} read loop ended unexpectedly", TerminalId);
        }

        // Reached the PTY's natural end-of-stream (EOF/EIO) rather than a dispose: the child has exited. Latch it,
        // unless we're already tearing down (dispose owns the exit story then).
        if (Volatile.Read(ref _disposed) == 0)
        {
            MarkExited();
        }
    }

    private void RaiseOutput(string text)
    {
        lock (_gate)
        {
            if (_output is null)
            {
                // No subscriber yet: buffer until one attaches (replayed under the same gate in the add accessor).
                _pending.Append(text);
                return;
            }

            try
            {
                _output.Invoke(TerminalId, text);
            }
            catch (Exception ex)
            {
                // Isolate an observer's failure — it must not break the read loop.
                _logger.LogDebug(ex, "Terminal {TerminalId} output observer threw", TerminalId);
            }
        }
    }
}
