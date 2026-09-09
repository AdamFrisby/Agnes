using System.Text.RegularExpressions;

namespace Agnes.Ui.Core.Markdown;

/// <summary>
/// Recognises fenced blocks whose info string explicitly asks to be interpreted as Markdown.
///
/// <para>The expression is shared by both Avalonia heads so desktop and mobile agree on the exact
/// opt-in syntax. It accepts CommonMark's two fence characters, a closing fence at least as long as
/// the opener, and an unfinished block at end-of-input so a streaming reply can render immediately.</para>
/// </summary>
public static partial class MarkdownFence
{
    public const string ParserName = "AgnesMarkdownFence";

    public static Regex Pattern { get; } = CreatePattern();

    public static string Body(Match match) => match.Groups["body"].Value;

    public static string Language(Match match) => match.Groups["language"].Value;

    [GeneratedRegex(
        "^(?<indent>[ ]{0,3})(?:(?<ticks>`{3,})|(?<tildes>~{3,}))[ \\t]*(?<language>markdown|md)[ \\t]*\\r?\\n(?<body>.*?)(?:^[ ]{0,3}(?(ticks)\\k<ticks>`*|\\k<tildes>~*)[ \\t]*(?:\\r?\\n|\\z)|\\z)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex CreatePattern();
}
