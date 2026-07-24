namespace Agnes.Wrap.Pty;

/// <summary>
/// What to launch in a pseudo-terminal. <see cref="Command"/> is the executable (resolved on PATH by the
/// PTY layer's <c>execvp</c>), <see cref="Arguments"/> are its argv tail (argv[0] is filled in from the
/// command). Kept as an immutable record so a spawn request is a value, not shared mutable state.
/// </summary>
public sealed record PtyLaunch(
    string Command,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string>? Environment = null,
    int Columns = 120,
    int Rows = 30);

/// <summary>
/// A live pseudo-terminal child process: the wrapper reads the child's output from <see cref="Reader"/> and
/// writes the user's (or a remote client's) input to <see cref="Writer"/>. Abstracted from the concrete PTY
/// library so the wrapper's session logic is testable and the one real dependency (Porta.Pty) is injected at
/// the edge. Disposing kills the child and closes both streams.
/// </summary>
public interface IPtyProcess : IAsyncDisposable
{
    /// <summary>The child's combined output stream (stdout+stderr multiplexed by the PTY).</summary>
    Stream Reader { get; }

    /// <summary>The child's input stream (its stdin).</summary>
    Stream Writer { get; }

    /// <summary>The child process id.</summary>
    int ProcessId { get; }

    /// <summary>Whether the child has already exited.</summary>
    bool HasExited { get; }

    /// <summary>Resizes the terminal window (rows/cols) the child sees.</summary>
    void Resize(int columns, int rows);

    /// <summary>Completes with the child's exit code once it exits (never faults on a normal exit).</summary>
    Task<int> WaitForExitAsync(CancellationToken cancellationToken = default);
}

/// <summary>Spawns a child process attached to a real pseudo-terminal. The single seam a test replaces (or,
/// here, exercises for real — <c>/dev/ptmx</c> is available) to run the wrapper without a live user terminal.</summary>
public interface IPtySpawner
{
    Task<IPtyProcess> SpawnAsync(PtyLaunch launch, CancellationToken cancellationToken = default);
}
