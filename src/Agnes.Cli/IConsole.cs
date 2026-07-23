namespace Agnes.Cli;

/// <summary>
/// The CLI's only channel to the terminal, injected so command logic (and its exit codes / output)
/// can be unit-tested without touching the real <see cref="System.Console"/>.
/// </summary>
public interface IConsole
{
    /// <summary>Writes a line to stdout (the pipeline-clean channel — JSON and ids go here).</summary>
    void Out(string text);

    /// <summary>Writes a line to stderr (human-facing diagnostics, so they don't pollute piped output).</summary>
    void Error(string text);

    /// <summary>Reads a line from stdin, or null at end of input.</summary>
    string? ReadLine();
}

/// <summary>The real console binding used by <c>Main</c>.</summary>
public sealed class SystemConsole : IConsole
{
    public void Out(string text) => System.Console.Out.WriteLine(text);

    public void Error(string text) => System.Console.Error.WriteLine(text);

    public string? ReadLine() => System.Console.In.ReadLine();
}
