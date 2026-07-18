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
