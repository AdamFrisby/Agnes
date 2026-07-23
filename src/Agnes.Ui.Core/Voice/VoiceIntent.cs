using Agnes.Abstractions;

namespace Agnes.Ui.Core.Voice;

/// <summary>What the voice controller should do with one transcript.</summary>
public enum VoiceActionKind
{
    /// <summary>No confident mapping — the input is not acted on (AC5). The controller signals "not understood".</summary>
    None,

    /// <summary>Relay a spoken instruction to the target session as a prompt.</summary>
    Prompt,

    /// <summary>Answer an open permission request by voice.</summary>
    RespondPermission,

    /// <summary>Switch the target session's mode.</summary>
    SetMode,
}

/// <summary>
/// The immutable result of mapping a transcript to an intent. Deliberately a plain value: the mapper is a
/// pure function (transcript + session state -> this), so it is fully unit-testable with no host or
/// microphone, and the controller is the only thing that turns it into an <c>IAgnesHost</c> call.
/// </summary>
public sealed record VoiceAction(
    VoiceActionKind Kind,
    string? Text = null,
    string? OptionId = null,
    string? ModeId = null)
{
    /// <summary>The input could not be mapped to a known action.</summary>
    public static VoiceAction None { get; } = new(VoiceActionKind.None);

    public static VoiceAction Prompt(string text) => new(VoiceActionKind.Prompt, Text: text);

    public static VoiceAction RespondPermission(string optionId) => new(VoiceActionKind.RespondPermission, OptionId: optionId);

    public static VoiceAction SetMode(string modeId) => new(VoiceActionKind.SetMode, ModeId: modeId);
}

/// <summary>An open permission request the controller may answer by voice.</summary>
public sealed record VoicePermissionState(string RequestId, IReadOnlyList<PermissionOption> Options);

/// <summary>
/// The slice of target-session state the intent mapper reads to disambiguate a transcript. Passed in
/// explicitly (not fetched) so the mapper stays a pure function of its inputs.
/// </summary>
public sealed record VoiceSessionState(
    VoicePermissionState? OpenPermission = null,
    IReadOnlyList<SessionMode>? Modes = null,
    string? CurrentModeId = null);

/// <summary>
/// Maps a transcript onto a <see cref="VoiceAction"/> given current session state. Small, pure, and
/// conservative: anything it can't confidently classify becomes <see cref="VoiceAction.None"/> rather than
/// a guessed prompt or permission response (AC5). Kept entirely separate from audio transport.
/// </summary>
public static class VoiceIntentMapper
{
    // Explicit "relay this to the agent" lead-ins. An explicit relay intent always wins over an accidental
    // keyword match (e.g. "tell it to stop" is a prompt, not a permission denial), so these are checked first.
    // Longest-first so "tell the agent to" is matched before "tell".
    private static readonly string[] RelayLeadIns =
    [
        "tell the agent to", "ask the agent to", "tell the agent", "ask the agent",
        "tell it to", "ask it to", "have it", "tell it", "ask it",
    ];

    private static readonly string[] Affirmatives =
    [
        "yes", "yeah", "yep", "yup", "sure", "ok", "okay", "allow", "allow it", "allow once",
        "approve", "approved", "accept", "confirm", "permit", "go ahead", "do it", "grant",
    ];

    private static readonly string[] Negatives =
    [
        "no", "nope", "nah", "deny", "denied", "reject", "decline", "refuse", "don't",
        "do not", "stop", "cancel", "block", "disallow",
    ];

    private static readonly string[] ModePrefixes =
    [
        "switch to", "change to", "set mode to", "change mode to", "enter", "go to", "use",
    ];

    public static VoiceAction Map(string transcript, VoiceSessionState state)
    {
        var text = Normalize(transcript);
        if (text.Length == 0)
        {
            return VoiceAction.None;
        }

        // 1. Explicit relay ("tell it to …") — a prompt, regardless of any keywords in the remainder.
        foreach (var leadIn in RelayLeadIns)
        {
            if (TryStripPrefix(text, leadIn, out var remainder) && remainder.Length > 0)
            {
                // Preserve the caller's original casing/wording for the remainder rather than the lowercased form.
                var original = ExtractRemainderFromOriginal(transcript, leadIn);
                return VoiceAction.Prompt(original.Length > 0 ? original : remainder);
            }
        }

        // 2. Mode switch ("switch to plan mode").
        var mode = MatchMode(text, state.Modes);
        if (mode is not null)
        {
            return VoiceAction.SetMode(mode.Id);
        }

        // 3. Permission response — only meaningful while a request is actually open.
        if (state.OpenPermission is { } permission)
        {
            if (ContainsWord(text, Affirmatives))
            {
                var option = ChooseAllow(permission.Options);
                if (option is not null)
                {
                    return VoiceAction.RespondPermission(option.OptionId);
                }
            }

            if (ContainsWord(text, Negatives))
            {
                var option = ChooseReject(permission.Options);
                if (option is not null)
                {
                    return VoiceAction.RespondPermission(option.OptionId);
                }
            }
        }

        // 4. Nothing matched with confidence — do not guess (AC5).
        return VoiceAction.None;
    }

    private static SessionMode? MatchMode(string text, IReadOnlyList<SessionMode>? modes)
    {
        if (modes is null || modes.Count == 0)
        {
            return null;
        }

        // Require a mode-switch phrasing so a bare mention of a mode word isn't over-eagerly acted on.
        var hasSwitchCue = text.Contains("mode", StringComparison.Ordinal)
            || ModePrefixes.Any(p => text.Contains(p, StringComparison.Ordinal));
        if (!hasSwitchCue)
        {
            return null;
        }

        foreach (var mode in modes)
        {
            var name = mode.Name.ToLowerInvariant();
            var id = mode.Id.ToLowerInvariant();
            if (ContainsWord(text, [name]) || ContainsWord(text, [id]))
            {
                return mode;
            }
        }

        return null;
    }

    private static PermissionOption? ChooseAllow(IReadOnlyList<PermissionOption> options)
        => options.FirstOrDefault(o => o.Kind == PermissionOptionKind.AllowOnce)
           ?? options.FirstOrDefault(o => o.Kind == PermissionOptionKind.AllowAlways);

    private static PermissionOption? ChooseReject(IReadOnlyList<PermissionOption> options)
        => options.FirstOrDefault(o => o.Kind == PermissionOptionKind.RejectOnce)
           ?? options.FirstOrDefault(o => o.Kind == PermissionOptionKind.RejectAlways);

    private static string Normalize(string transcript)
    {
        var trimmed = (transcript ?? string.Empty).Trim().ToLowerInvariant();
        // Drop trailing/leading punctuation that would otherwise defeat whole-word matching.
        return trimmed.Trim('.', ',', '!', '?', ';', ':', ' ');
    }

    private static bool TryStripPrefix(string text, string prefix, out string remainder)
    {
        if (text.StartsWith(prefix + " ", StringComparison.Ordinal))
        {
            remainder = text[(prefix.Length + 1)..].Trim();
            return true;
        }

        remainder = string.Empty;
        return false;
    }

    private static string ExtractRemainderFromOriginal(string original, string leadIn)
    {
        var idx = original.IndexOf(leadIn, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return string.Empty;
        }

        return original[(idx + leadIn.Length)..].Trim().TrimEnd('.', ',', '!', '?', ';', ':');
    }

    private static bool ContainsWord(string text, IReadOnlyList<string> candidates)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var candidate in candidates)
        {
            if (candidate.Contains(' ', StringComparison.Ordinal))
            {
                // Multi-word phrase: substring match on the normalized text is sufficient.
                if (text.Contains(candidate, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            else if (Array.IndexOf(words, candidate) >= 0)
            {
                return true;
            }
        }

        return false;
    }
}
