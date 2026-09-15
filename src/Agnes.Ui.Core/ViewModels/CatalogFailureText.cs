using System.Text.RegularExpressions;

namespace Agnes.Ui.Core.ViewModels;

/// <summary>
/// Turns a registry failure as the host reports it — <c>"SkillsHub (skillshub.wtf): Response status code
/// does not indicate success: 402 (Payment Required)"</c> — into the sentence a person needs:
/// <c>"SkillsHub (skillshub.wtf) couldn't be reached: HTTP 402."</c> The registry's name is the part that
/// tells you which index is out; the HTTP status is the part that tells you why; the rest is the client
/// library talking to itself.
/// </summary>
public static partial class CatalogFailureText
{
    [GeneratedRegex(@"^(?<who>[^:]+?):\s*(?<rest>.+)$", RegexOptions.Singleline)]
    private static partial Regex WhoAndRest();

    [GeneratedRegex(@"\b(?<code>[1-5]\d\d)\b")]
    private static partial Regex HttpStatus();

    /// <summary>One failure, shortened. A failure with no recognisable shape is returned trimmed, as it came.</summary>
    public static string Shorten(string failure)
    {
        if (string.IsNullOrWhiteSpace(failure))
        {
            return string.Empty;
        }

        var text = failure.Trim().TrimEnd('.');
        var split = WhoAndRest().Match(text);
        if (!split.Success)
        {
            return text + ".";
        }

        var who = split.Groups["who"].Value.Trim();
        var rest = split.Groups["rest"].Value.Trim();
        var http = HttpStatus().Match(rest);
        if (http.Success && rest.Contains("status", StringComparison.OrdinalIgnoreCase))
        {
            return $"{who} couldn't be reached: HTTP {http.Groups["code"].Value}.";
        }

        // Not an HTTP status: keep the registry's name and the first clause of what went wrong.
        var clause = rest.Split(':', 2)[0].Trim();
        return $"{who} couldn't be reached: {clause}.";
    }

    /// <summary>Every failure, one sentence each, on one line — or empty when there were none.</summary>
    public static string Sentence(IReadOnlyList<string> failures)
        => failures.Count == 0 ? string.Empty : string.Join(' ', failures.Select(Shorten).Where(s => s.Length > 0));

    /// <summary>"3 servers", "one skill", "no prompts" — a count with its noun, for status sentences.</summary>
    public static string Count(int n, string singular, string? plural = null)
        => n switch
        {
            0 => $"no {plural ?? singular + "s"}",
            1 => $"one {singular}",
            _ => $"{n} {plural ?? singular + "s"}",
        };
}
