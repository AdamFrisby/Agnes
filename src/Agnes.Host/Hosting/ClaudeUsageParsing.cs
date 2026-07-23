using System.Text.Json;
using System.Text.Json.Serialization;
using Agnes.Abstractions;

namespace Agnes.Host.Hosting;

/// <summary>
/// Parses Anthropic's OAuth usage endpoint (<c>https://api.anthropic.com/api/oauth/usage</c>) into a
/// <see cref="QuotaSnapshot"/>. Adapted from CodeyBox's <c>ClaudeQuotaProbe.ParseResponse</c>, but the raw
/// JSON is deserialised <em>immediately</em> into the typed records below (rather than walked as
/// <c>JsonElement</c>) so nothing untyped flows inward — the loose JSON stays at this boundary only.
/// </summary>
/// <remarks>
/// The live endpoint has been observed in two shapes. The current one is a flat object of named windows —
/// <c>five_hour</c>, <c>seven_day</c>, <c>seven_day_opus</c>/<c>_sonnet</c>/<c>_haiku</c> — each carrying a
/// <c>utilization</c> (0-100, where 100 means the window is exhausted) and a <c>resets_at</c>. An older shape
/// nests a <c>rate_limit</c> object with <c>primary_window</c>/<c>secondary_window</c> reporting
/// <c>used_percent</c>. Both are handled; a <c>plan_type</c> (e.g. "max", "pro") captions the plan when
/// present. Every window is surfaced honestly as a percent-used meter (<c>Used</c>=utilisation,
/// <c>Limit</c>=100, <c>Unit</c>="%") — no numbers are invented.
/// </remarks>
internal static class ClaudeUsageParsing
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>
    /// Maps a usage-endpoint response body onto a <see cref="QuotaSnapshot"/>, or null when the body is not
    /// valid JSON or carries no recognisable usage window (an "unexpected shape" the caller treats as a
    /// permanent, non-retryable failure).
    /// </summary>
    public static QuotaSnapshot? Parse(string? json, DateTimeOffset fetchedAt)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        ClaudeUsageResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ClaudeUsageResponse>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }

        if (parsed is null)
        {
            return null;
        }

        var meters = new List<QuotaMeter>();

        // Flat shape: one meter per named window that actually reported a utilisation figure. A window key
        // present but null (e.g. "seven_day_opus": null) simply contributes no meter.
        AddUtilizationMeter(meters, "5-hour limit", parsed.FiveHour);
        AddUtilizationMeter(meters, "7-day limit", parsed.SevenDay);
        AddUtilizationMeter(meters, "7-day limit (Opus)", parsed.SevenDayOpus);
        AddUtilizationMeter(meters, "7-day limit (Sonnet)", parsed.SevenDaySonnet);
        AddUtilizationMeter(meters, "7-day limit (Haiku)", parsed.SevenDayHaiku);

        // Older rollup shape: primary/secondary windows carry used_percent instead of utilization.
        if (meters.Count == 0 && parsed.RateLimit is { } rate)
        {
            AddUsedPercentMeter(meters, "5-hour limit", rate.PrimaryWindow);
            AddUsedPercentMeter(meters, "7-day limit", rate.SecondaryWindow);
        }

        if (meters.Count == 0)
        {
            // Valid JSON but no window we recognise — an unexpected shape, not a usable reading.
            return null;
        }

        return new QuotaSnapshot(PlanLabel(parsed.PlanType), meters, fetchedAt);
    }

    private static void AddUtilizationMeter(List<QuotaMeter> meters, string name, ClaudeUsageBucket? bucket)
    {
        if (bucket?.Utilization is { } utilization)
        {
            meters.Add(new QuotaMeter(name, Used: Clamp(utilization), Limit: 100, Unit: "%"));
        }
    }

    private static void AddUsedPercentMeter(List<QuotaMeter> meters, string name, ClaudeRateWindow? window)
    {
        if (window?.UsedPercent is { } used)
        {
            meters.Add(new QuotaMeter(name, Used: Clamp(used), Limit: 100, Unit: "%"));
        }
    }

    private static double Clamp(double pct) => Math.Clamp(pct, 0.0, 100.0);

    private static string PlanLabel(string? planType)
    {
        if (string.IsNullOrWhiteSpace(planType))
        {
            return "Claude";
        }

        // "max" -> "Max", "pro" -> "Pro". Title-case the raw plan slug for a friendly badge caption.
        var trimmed = planType.Trim();
        return $"Claude {char.ToUpperInvariant(trimmed[0])}{trimmed[1..]}";
    }

    /// <summary>Typed boundary record for the usage response. Unknown keys are ignored by the deserialiser.</summary>
    private sealed record ClaudeUsageResponse(
        [property: JsonPropertyName("plan_type")] string? PlanType,
        [property: JsonPropertyName("five_hour")] ClaudeUsageBucket? FiveHour,
        [property: JsonPropertyName("seven_day")] ClaudeUsageBucket? SevenDay,
        [property: JsonPropertyName("seven_day_opus")] ClaudeUsageBucket? SevenDayOpus,
        [property: JsonPropertyName("seven_day_sonnet")] ClaudeUsageBucket? SevenDaySonnet,
        [property: JsonPropertyName("seven_day_haiku")] ClaudeUsageBucket? SevenDayHaiku,
        [property: JsonPropertyName("rate_limit")] ClaudeRateLimit? RateLimit);

    /// <summary>A flat-shape window: percent utilised (0-100) and an optional reset time.</summary>
    private sealed record ClaudeUsageBucket(
        [property: JsonPropertyName("utilization")] double? Utilization,
        [property: JsonPropertyName("resets_at")] DateTimeOffset? ResetsAt);

    /// <summary>Older rollup-shape rate limit with a short (5h) and long (7d) window.</summary>
    private sealed record ClaudeRateLimit(
        [property: JsonPropertyName("primary_window")] ClaudeRateWindow? PrimaryWindow,
        [property: JsonPropertyName("secondary_window")] ClaudeRateWindow? SecondaryWindow);

    /// <summary>A rollup-shape window reporting percent used.</summary>
    private sealed record ClaudeRateWindow(
        [property: JsonPropertyName("used_percent")] double? UsedPercent,
        [property: JsonPropertyName("reset_at")] long? ResetAt);
}
