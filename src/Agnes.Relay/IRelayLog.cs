namespace Agnes.Relay;

/// <summary>
/// A deliberately tiny logging surface. The relay logs only <b>metadata</b> — host-id, source IP,
/// byte counts, timings — never payload bytes; keeping the log API to plain caller-built strings
/// makes the blindness invariant easy to audit (there is no overload that takes payload buffers).
/// </summary>
public interface IRelayLog
{
    void Info(string message);

    void Warn(string message);
}

/// <summary>Writes timestamped metadata lines to the console.</summary>
public sealed class ConsoleRelayLog : IRelayLog
{
    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    private static void Write(string level, string message) =>
        Console.WriteLine($"{DateTimeOffset.UtcNow:O} [{level}] {message}");
}

/// <summary>Discards everything (default when no logger is supplied; used in tests).</summary>
public sealed class NullRelayLog : IRelayLog
{
    public static readonly NullRelayLog Instance = new();

    public void Info(string message)
    {
        // Intentionally discards metadata log lines.
    }

    public void Warn(string message)
    {
        // Intentionally discards metadata log lines.
    }
}
