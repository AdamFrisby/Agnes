namespace Agnes.Ui.Core.ViewModels;

/// <summary>
/// A reusable prompt invoked by typing "/name" in the composer. Built-ins expand inline; a library
/// <see cref="Agnes.Abstractions.PromptTemplate"/> also carries <see cref="SendImmediately"/> (InsertAndSend)
/// and <see cref="IsBroken"/> (its referenced prompt was deleted — surfaced, never silently empty).
/// </summary>
public sealed record SlashCommand(string Name, string Description, string Expansion, bool SendImmediately = false, bool IsBroken = false)
{
    /// <summary>Built-in commands available in every session.</summary>
    public static readonly IReadOnlyList<SlashCommand> BuiltIns =
    [
        new("explain", "Explain how this works", "Explain how this code works, step by step."),
        new("review", "Review the changes", "Review these changes for bugs, edge cases, and style issues."),
        new("test", "Write tests", "Write unit tests covering the important cases here."),
        new("fix", "Find and fix a bug", "Find the bug and fix it, then explain the root cause."),
        new("commit", "Draft a commit message", "Write a concise, conventional commit message for these changes."),
        new("compact", "Compact the conversation (free up context)", "/compact"),
        new("clear", "Clear the conversation history", "/clear"),
    ];
}
