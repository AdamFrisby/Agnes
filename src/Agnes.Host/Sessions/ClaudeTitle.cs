using System.Text.Json;

namespace Agnes.Host.Sessions;

/// <summary>
/// Reads the title Claude Code auto-generates for a conversation. Claude has no title in its
/// stream-json output (the terminal-title rename is a TUI feature), but it writes
/// <c>{"type":"ai-title","aiTitle":"..."}</c> lines into the session's on-disk transcript at
/// <c>&lt;home&gt;/.claude/projects/&lt;encoded-cwd&gt;/&lt;sessionId&gt;.jsonl</c>. This locates that file and
/// returns the most recent <c>aiTitle</c>.
/// </summary>
public static class ClaudeTitle
{
    /// <summary>Claude encodes a working directory into its projects-dir name by replacing every
    /// non-alphanumeric character with a dash (so <c>/work</c> → <c>-work</c>).</summary>
    public static string EncodeCwd(string cwd)
    {
        var chars = cwd.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (!char.IsAsciiLetterOrDigit(chars[i]))
            {
                chars[i] = '-';
            }
        }

        return new string(chars);
    }

    /// <summary>The path (relative to the Claude home) of a session's transcript.</summary>
    public static string TranscriptRelativePath(string cwd, string sessionId)
        => $".claude/projects/{EncodeCwd(cwd)}/{sessionId}.jsonl";

    /// <summary>Extracts the most recent <c>aiTitle</c> from transcript text (whole file or a filtered
    /// tail), or null if there is none yet. Malformed lines are skipped.</summary>
    public static string? ParseLatestTitle(string transcript)
    {
        string? title = null;
        foreach (var line in transcript.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || !trimmed.Contains("\"ai-title\"", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                if (doc.RootElement.TryGetProperty("type", out var t) && t.ValueEquals("ai-title")
                    && doc.RootElement.TryGetProperty("aiTitle", out var a) && a.ValueKind == JsonValueKind.String)
                {
                    var value = a.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        title = value.Trim(); // keep the last one — Claude refines the title over time
                    }
                }
            }
            catch (JsonException)
            {
                // A partially-written trailing line while Claude appends; ignore.
            }
        }

        return title;
    }

    /// <summary>A shell snippet that emits just the ai-title lines from a transcript (cheap to read over
    /// a sandbox exec instead of catting the whole growing file).</summary>
    public static string TailTitleCommand(string transcriptPath)
        => $"grep '\"type\":\"ai-title\"' '{transcriptPath}' 2>/dev/null | tail -3";
}
