namespace Agnes.Abstractions;

/// <summary>Options for opening a fallback terminal.</summary>
public sealed record TerminalOptions
{
    public required string Command { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public required string WorkingDirectory { get; init; }
    public int Columns { get; init; } = 120;
    public int Rows { get; init; } = 30;
}

/// <summary>A live handle to a fallback terminal (real PTY).</summary>
public interface ITerminalHandle : IAsyncDisposable
{
    string TerminalId { get; }
    Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
    Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default);
}

/// <summary>
/// The true-CLI fallback path: a real terminal for commands ACP cannot express.
/// Output is surfaced into the session log as <see cref="TerminalOutputEvent"/>s.
/// </summary>
public interface ICliFallback
{
    Task<ITerminalHandle> OpenTerminalAsync(TerminalOptions options, CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional companion to <see cref="ITerminalHandle"/>: a fallback whose PTY streams its output back to the
/// host, so the host can append each chunk to the session log as a <see cref="TerminalOutputEvent"/>
/// (interleaved with everything else, replayed via the normal snapshot/tail). A handle that surfaces its
/// output some other way — or a test fake that produces none — simply doesn't implement it.
/// </summary>
public interface ITerminalOutputSource
{
    /// <summary>Raised as the PTY produces output: the terminal id and the decoded chunk.</summary>
    event Action<string, string>? OutputReceived;
}

/// <summary>
/// Optional companion to <see cref="ITerminalHandle"/>: a fallback whose PTY reports when its process exits,
/// so the host can react to completion — e.g. tear down the provider-login scratch session and refresh the
/// provider's login badge (platform/03). A handle that can't observe its process exit — or a test fake that
/// never ends — simply doesn't implement it, and the consumer just keeps the terminal open until shutdown.
/// </summary>
public interface ITerminalExitSource
{
    /// <summary>Raised once when the terminal's underlying process exits.</summary>
    event Action? Exited;
}
