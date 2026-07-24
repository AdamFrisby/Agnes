using System.Collections;
using Porta.Pty;

namespace Agnes.Wrap.Pty;

/// <summary>The real PTY spawner, over <c>Porta.Pty</c> (ConPTY on Windows, <c>/dev/ptmx</c> on Unix).</summary>
public sealed class PortaPtySpawner : IPtySpawner
{
    public async Task<IPtyProcess> SpawnAsync(PtyLaunch launch, CancellationToken cancellationToken = default)
    {
        var options = new PtyOptions
        {
            Name = "agnes-wrap",
            Cols = launch.Columns,
            Rows = launch.Rows,
            Cwd = launch.WorkingDirectory,
            App = launch.Command,
            // Porta fills argv[0] from App and appends CommandLine as argv[1..], so this is just the arg tail.
            CommandLine = [.. launch.Arguments],
            Environment = BuildEnvironment(launch.Environment),
        };

        var connection = await PtyProvider.SpawnAsync(options, cancellationToken).ConfigureAwait(false);
        return new PortaPtyProcess(connection);
    }

    /// <summary>Inherit the current process environment and layer any per-launch overrides on top. With no
    /// overrides we hand back an empty set, which Porta treats as "inherit the parent environment unchanged"
    /// (it passes a null envp to the child) — so the wrapped CLI never runs with a bare environment.</summary>
    private static IDictionary<string, string> BuildEnvironment(IReadOnlyDictionary<string, string>? overrides)
    {
        if (overrides is null || overrides.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                merged[key] = value;
            }
        }

        foreach (var pair in overrides)
        {
            merged[pair.Key] = pair.Value;
        }

        return merged;
    }
}

/// <summary>Adapts a <c>Porta.Pty</c> connection to <see cref="IPtyProcess"/>.</summary>
internal sealed class PortaPtyProcess : IPtyProcess
{
    private readonly IPtyConnection _connection;
    private readonly TaskCompletionSource<int> _exited =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    public PortaPtyProcess(IPtyConnection connection)
    {
        _connection = connection;
        _connection.ProcessExited += OnProcessExited;

        // The exit may already have happened between spawn and our subscription — settle the TCS eagerly.
        if (_connection.WaitForExit(0))
        {
            _exited.TrySetResult(_connection.ExitCode);
        }
    }

    public Stream Reader => _connection.ReaderStream;

    public Stream Writer => _connection.WriterStream;

    public int ProcessId => _connection.Pid;

    public bool HasExited => _connection.WaitForExit(0);

    public void Resize(int columns, int rows) => _connection.Resize(columns, rows);

    public Task<int> WaitForExitAsync(CancellationToken cancellationToken = default)
        => _exited.Task.WaitAsync(cancellationToken);

    private void OnProcessExited(object? sender, PtyExitedEventArgs e) => _exited.TrySetResult(e.ExitCode);

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        _connection.ProcessExited -= OnProcessExited;
        try
        {
            if (!_connection.WaitForExit(0))
            {
                _connection.Kill();
            }
        }
        catch (Exception)
        {
            // Best-effort: the child may have exited (or never fully started) between the check and the kill.
        }

        _exited.TrySetResult(_connection.WaitForExit(0) ? _connection.ExitCode : -1);
        _connection.Dispose(); // closes both streams, unblocking any in-flight read
        return ValueTask.CompletedTask;
    }
}
