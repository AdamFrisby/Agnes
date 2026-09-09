using System.Text.Json;
using System.Collections.Immutable;

namespace Agnes.App.Desktop.Keymaps;

public static class KeymapLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static bool TryResolve(
        IEnumerable<(string Name, string Json)> layers,
        out EffectiveKeymap keymap,
        out KeymapDiagnostic? diagnostic)
    {
        var candidates = new List<CandidateRule>();
        var layerIndex = 0;
        foreach (var (name, json) in layers)
        {
            if (!TryParse(name, json, out var parsed, out diagnostic))
            {
                keymap = new EffectiveKeymap([]);
                return false;
            }

            var overriddenCommands = new HashSet<(AgnesCommand Command, KeymapContext Context)>();
            foreach (var rule in parsed)
            {
                if (rule.Removes)
                {
                    candidates.RemoveAll(existing => SameGesture(existing.Rule.Gesture, rule.Gesture)
                        && existing.Rule.Context == rule.Context && existing.Rule.Command == rule.Command);
                    continue;
                }

                // The first positive rule for a command/context in a new layer replaces every binding
                // inherited from lower layers. Further rules in this layer remain deliberate aliases.
                if (rule.Command is { } command && overriddenCommands.Add((command, rule.Context)))
                {
                    candidates.RemoveAll(existing => existing.Layer < layerIndex
                        && existing.Rule.Command == command && existing.Rule.Context == rule.Context);
                }

                candidates.Add(new CandidateRule(new KeymapRule(rule.Gesture, rule.Command, rule.Context), layerIndex));
            }

            layerIndex++;
        }

        // Collapse only after every removal has run. This preserves a shadowed earlier binding so removing
        // its later override reveals it again; the last remaining rule for a key/context is the winner.
        var seen = new HashSet<(Avalonia.Input.Key Key, Avalonia.Input.KeyModifiers Modifiers, KeymapContext Context)>();
        var effective = new List<KeymapRule>();
        for (var index = candidates.Count - 1; index >= 0; index--)
        {
            var candidate = candidates[index].Rule;
            if (seen.Add((candidate.Gesture.Key, candidate.Gesture.KeyModifiers, candidate.Context)))
                effective.Add(candidate);
        }

        effective.Reverse();
        keymap = new EffectiveKeymap(effective.ToImmutableArray());
        diagnostic = null;
        return true;
    }

    private static bool TryParse(
        string name,
        string json,
        out IReadOnlyList<ParsedRule> rules,
        out KeymapDiagnostic? diagnostic)
    {
        RawRule[]? raw;
        try
        {
            raw = JsonSerializer.Deserialize<RawRule[]>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            rules = [];
            diagnostic = new KeymapDiagnostic($"{name}: {ex.Message}", checked((int)(ex.LineNumber ?? 0) + 1));
            return false;
        }

        if (raw is null)
        {
            rules = [];
            diagnostic = new KeymapDiagnostic($"{name}: the file must contain a JSON array.", 1);
            return false;
        }

        var result = new List<ParsedRule>(raw.Length);
        for (var index = 0; index < raw.Length; index++)
        {
            var item = raw[index];
            var line = FindRuleLine(json, index);
            if (item.Key is null || item.Command is null || item.When is null)
            {
                rules = [];
                diagnostic = new KeymapDiagnostic($"{name}: every rule requires string key, command, and when fields.", line);
                return false;
            }

            if (!KeyGestureParser.TryParse(item.Key, out var gesture, out var gestureError))
            {
                rules = [];
                diagnostic = new KeymapDiagnostic($"{name}: {gestureError}", line);
                return false;
            }

            if (!KeymapNames.TryContext(item.When, out var context))
            {
                rules = [];
                diagnostic = new KeymapDiagnostic($"{name}: unknown context '{item.When}'. Boolean when expressions are not supported in v1.", line);
                return false;
            }

            var commandText = item.Command.Trim();
            var removes = commandText.StartsWith("-", StringComparison.Ordinal);
            if (removes) commandText = commandText[1..].Trim();
            if (commandText.Length == 0 && removes)
            {
                rules = [];
                diagnostic = new KeymapDiagnostic($"{name}: a removal must name a command after '-'.", line);
                return false;
            }

            AgnesCommand? command = null;
            if (commandText.Length > 0)
            {
                if (!KeymapNames.TryCommand(commandText, out var known))
                {
                    rules = [];
                    diagnostic = new KeymapDiagnostic($"{name}: unknown command '{commandText}'.", line);
                    return false;
                }

                command = known;
            }

            result.Add(new ParsedRule(gesture, command, context, removes));
        }

        rules = result;
        diagnostic = null;
        return true;
    }

    private static int FindRuleLine(string json, int index)
    {
        var seen = -1;
        for (var i = 0; i < json.Length; i++)
        {
            if (json[i] != '{') continue;
            seen++;
            if (seen == index) return json.AsSpan(0, i).Count('\n') + 1;
        }

        return 1;
    }

    private static bool SameGesture(Avalonia.Input.KeyGesture left, Avalonia.Input.KeyGesture right)
        => left.Key == right.Key && left.KeyModifiers == right.KeyModifiers;

    private sealed record RawRule(string? Key, string? Command, string? When);
    private sealed record ParsedRule(Avalonia.Input.KeyGesture Gesture, AgnesCommand? Command, KeymapContext Context, bool Removes);
    private sealed record CandidateRule(KeymapRule Rule, int Layer);
}
