using Agnes.Abstractions;

namespace Agnes.Ui.Core.Diff;

/// <summary>
/// Turns a file-editing tool call into the diff a reviewer actually wants to see.
///
/// The tool's <em>result</em> is a receipt ("The file … has been updated successfully"), so a UI that
/// shows the result shows nothing reviewable; the change itself is in the tool's <em>input</em>, which is
/// why this reads the start of the call rather than its end. ACP agents (and native adapters that emit
/// <see cref="DiffContent"/>) already send a real diff; Claude's Edit/Write input JSON is turned into one
/// here. The input is persisted with the event, so stored sessions replay identically.
/// </summary>
public static class ToolDiff
{
    /// <summary>Tool kinds whose call is a change to a file — the ones worth diffing.</summary>
    public static bool IsFileTool(ToolKind kind)
        => kind is ToolKind.Edit or ToolKind.Delete or ToolKind.Move;

    /// <summary>
    /// The unified diff for a file-editing call, or null when this call isn't one or its input doesn't
    /// describe a change (a partial stream, an unfamiliar edit shape) — callers fall back to the raw text.
    /// </summary>
    public static string? For(ToolKind kind, IReadOnlyList<ContentBlock> content)
        => IsFileTool(kind) ? For(TextOf(content)) : null;

    /// <summary>The unified diff described by a tool input, or null if it doesn't describe one.</summary>
    public static string? For(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        if (DiffParser.LooksLikeDiff(input))
        {
            return input;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(input);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object
                || !root.TryGetProperty("file_path", out var fp))
            {
                return null;
            }

            var path = fp.GetString() ?? string.Empty;
            if (root.TryGetProperty("old_string", out var oldS) && root.TryGetProperty("new_string", out var newS))
            {
                return UnifiedDiff.Format(path, oldS.GetString() ?? string.Empty, newS.GetString() ?? string.Empty);
            }

            if (root.TryGetProperty("content", out var whole)) // Write = a whole new file
            {
                return UnifiedDiff.Format(path, string.Empty, whole.GetString() ?? string.Empty);
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Not a JSON edit request (or a clipped one) — the caller shows the raw detail instead.
        }

        return null;
    }

    /// <summary>Tool content flattened to text, rendering any structured diff block as a unified diff.</summary>
    public static string TextOf(IReadOnlyList<ContentBlock> content)
        => string.Concat(content.Select(b => b switch
        {
            TextContent t => t.Text,
            DiffContent d => UnifiedDiff.Format(d.Path, d.OldText, d.NewText),
            _ => string.Empty,
        }));
}
