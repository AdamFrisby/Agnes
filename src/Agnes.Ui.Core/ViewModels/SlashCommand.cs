namespace Agnes.Ui.Core.ViewModels;

/// <summary>A reusable prompt invoked by typing "/name" in the composer.</summary>
public sealed record SlashCommand(string Name, string Description, string Expansion)
{
    /// <summary>Built-in commands available in every session.</summary>
    public static readonly IReadOnlyList<SlashCommand> BuiltIns =
    [
        new("explain", "Explain how this works", "Explain how this code works, step by step."),
        new("review", "Review the changes", "Review these changes for bugs, edge cases, and style issues."),
        new("test", "Write tests", "Write unit tests covering the important cases here."),
        new("fix", "Find and fix a bug", "Find the bug and fix it, then explain the root cause."),
        new("commit", "Draft a commit message", "Write a concise, conventional commit message for these changes."),
    ];
}
