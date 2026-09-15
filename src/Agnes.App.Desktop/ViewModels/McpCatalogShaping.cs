using Agnes.Abstractions;

namespace Agnes.App.Desktop.ViewModels;

/// <summary>
/// What the MCP registries return is not what a person should read. The official registry lists the same
/// server once per published version and once per mirror, so a search for anything came back with
/// inference.sh four times and docs-mcp three. One row per server is the rule: the first hit for a
/// (name, where it runs) pair wins, where "where it runs" is the URL for a hosted server and the command
/// line for a local one — two servers with one name but different launches are genuinely different.
/// </summary>
public static class McpCatalogShaping
{
    public static IReadOnlyList<CatalogHit<McpCatalogEntry>> Distinct(IEnumerable<CatalogHit<McpCatalogEntry>> hits)
    {
        var seen = new HashSet<(string Name, string Where)>();
        var kept = new List<CatalogHit<McpCatalogEntry>>();
        foreach (var hit in hits)
        {
            var key = (Normalise(hit.Entry.Name), Where(hit.Entry));
            if (seen.Add(key))
            {
                kept.Add(hit);
            }
        }

        return kept;
    }

    /// <summary>
    /// The quick-install list the host sends is every catalogue's front page — the curated four, then
    /// whatever the registry leads with, duplicates and all. This keeps one row per server, in order.
    /// </summary>
    public static IReadOnlyList<Agnes.Protocol.McpServerInfo> DistinctPresets(IEnumerable<Agnes.Protocol.McpServerInfo> presets)
    {
        var seen = new HashSet<(string Name, string Where)>();
        var kept = new List<Agnes.Protocol.McpServerInfo>();
        foreach (var p in presets)
        {
            var key = (Normalise(p.Name), Where(p.Url, p.Command, p.Args));
            if (seen.Add(key))
            {
                kept.Add(p);
            }
        }

        return kept;
    }

    /// <summary>The launch, normalised: a hosted server's URL without scheme or trailing slash; a local
    /// server's command and arguments as one line. Blank when the entry says neither.</summary>
    internal static string Where(McpCatalogEntry entry) => Where(entry.Url, entry.Command, entry.LaunchArgs);

    private static string Where(string? url, string? command, IReadOnlyList<string> args)
    {
        if (url is { Length: > 0 })
        {
            var trimmed = url.Trim().TrimEnd('/');
            var scheme = trimmed.IndexOf("://", StringComparison.Ordinal);
            return Normalise(scheme >= 0 ? trimmed[(scheme + 3)..] : trimmed);
        }

        return Normalise(string.Join(' ', new[] { command }.Concat(args).Where(a => !string.IsNullOrWhiteSpace(a))));
    }

    private static string Normalise(string? s) => (s ?? string.Empty).Trim().ToLowerInvariant();
}
