using Agnes.Abstractions;

namespace Agnes.Ui.Core.ViewModels;

/// <summary>A saved prompt as a list row: the prompt itself (what Edit and Delete take), plus the slash
/// tokens that expand it and a one-line preview of the body.</summary>
public sealed class PromptRow
{
    public PromptRow(LibraryPrompt prompt, IReadOnlyList<string> tokens)
    {
        Prompt = prompt;
        Tokens = tokens;
    }

    public LibraryPrompt Prompt { get; }

    public IReadOnlyList<string> Tokens { get; }

    public string Title => Prompt.Title;

    public bool IsSystemPromptAddition => Prompt.IsSystemPromptAddition;

    /// <summary>The first non-empty line of the body, so two prompts with similar titles can be told apart.</summary>
    public string Preview
    {
        get
        {
            var line = Prompt.MarkdownBody.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
            return line.Length > 140 ? line[..140] + "…" : line;
        }
    }

    public bool HasTokens => Tokens.Count > 0;

    /// <summary>"/review · /sec" — how this prompt is invoked from the composer.</summary>
    public string TokensLabel => string.Join(" · ", Tokens);
}
