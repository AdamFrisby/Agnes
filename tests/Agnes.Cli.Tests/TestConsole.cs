using Agnes.Cli;

namespace Agnes.Cli.Tests;

/// <summary>An <see cref="IConsole"/> that records everything, so a command's output and diagnostics can be
/// asserted without touching the real terminal. Optional scripted stdin lines feed prompts (pairing codes).</summary>
internal sealed class TestConsole : IConsole
{
    private readonly Queue<string> _input;

    public TestConsole(params string[] input) => _input = new Queue<string>(input);

    public List<string> OutLines { get; } = [];

    public List<string> ErrorLines { get; } = [];

    public string OutText => string.Join("\n", OutLines);

    public void Out(string text) => OutLines.Add(text);

    public void Error(string text) => ErrorLines.Add(text);

    public string? ReadLine() => _input.Count > 0 ? _input.Dequeue() : null;
}
