using System.Text.Encodings.Web;
using System.Text.Json;

namespace Agnes.App.Desktop.Keymaps;

public sealed record KeymapCommandRow(
    AgnesCommand Command,
    string CommandId,
    string Description,
    string Context,
    string Gesture,
    string? JsonRule)
{
    public bool CanCopyJson => JsonRule is not null;

    /// <summary>The gesture's alternatives, one per key cap ("Ctrl+Tab / Ctrl+PageDown" is two).</summary>
    public IReadOnlyList<string> Gestures => Gesture.Split(" / ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>True when no rule binds the command, so the cap can read as a gap rather than a key.</summary>
    public bool IsUnassigned => string.Equals(Gesture, "Unassigned", StringComparison.Ordinal);

    public static string FormatJson(KeymapRule rule)
    {
        var key = Quote(KeyGestureParser.ToKeymapString(rule.Gesture));
        var command = Quote(rule.Command!.Value.Id());
        var context = Quote(rule.Context.Id());
        return $"{{ \"key\": {key}, \"command\": {command}, \"when\": {context} }}";
    }

    private static string Quote(string value)
        => $"\"{JsonEncodedText.Encode(value, JavaScriptEncoder.UnsafeRelaxedJsonEscaping)}\"";
}

public sealed record KeymapCommandGroup(string Title, IReadOnlyList<KeymapCommandRow> Commands);
