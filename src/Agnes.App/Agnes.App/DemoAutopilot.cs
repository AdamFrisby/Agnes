namespace Agnes.App;

/// <summary>Optional unattended demo/kiosk config: auto-connect, open an agent, send a prompt.</summary>
public sealed record AutopilotConfig(string Url, string Token, string Cwd, string Agent, string? Prompt);

/// <summary>
/// Reads autopilot config from environment variables (native heads) or the URL query
/// string (WebAssembly). Off unless configured — a convenience for demos and screenshots.
/// </summary>
public static class DemoAutopilot
{
    public static AutopilotConfig? GetConfig()
    {
#if __WASM__
        string search;
        try
        {
            search = Uno.Foundation.WebAssemblyRuntime.InvokeJS("window.location.search") ?? string.Empty;
        }
        catch
        {
            return null;
        }

        var query = ParseQuery(search);
        if (!query.TryGetValue("agnesUrl", out var url) || string.IsNullOrEmpty(url))
        {
            return null;
        }

        return new AutopilotConfig(
            url,
            query.GetValueOrDefault("token", string.Empty),
            query.GetValueOrDefault("cwd", "."),
            query.GetValueOrDefault("agent", "opencode"),
            query.GetValueOrDefault("prompt"));
#else
        var url = Environment.GetEnvironmentVariable("AGNES_DEMO_URL");
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        return new AutopilotConfig(
            url,
            Environment.GetEnvironmentVariable("AGNES_DEMO_TOKEN") ?? string.Empty,
            Environment.GetEnvironmentVariable("AGNES_DEMO_CWD") ?? ".",
            Environment.GetEnvironmentVariable("AGNES_DEMO_AGENT") ?? "opencode",
            Environment.GetEnvironmentVariable("AGNES_DEMO_PROMPT"));
#endif
    }

    private static Dictionary<string, string> ParseQuery(string search)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in search.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = pair.IndexOf('=');
            if (index <= 0)
            {
                continue;
            }

            var key = Uri.UnescapeDataString(pair[..index]);
            var value = Uri.UnescapeDataString(pair[(index + 1)..]);
            result[key] = value;
        }

        return result;
    }
}
