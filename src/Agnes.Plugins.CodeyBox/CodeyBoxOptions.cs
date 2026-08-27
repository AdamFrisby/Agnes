using System.Text.Json;

namespace Agnes.Plugins.CodeyBox;

/// <summary>
/// Where the CodeyBox orchestrator is and how to authenticate to it.
/// </summary>
/// <remarks>
/// Resolved from the same places CodeyBox's own CLI looks, and in the same order, so a machine already set
/// up for <c>codeybox</c> needs no second configuration: the environment first, then
/// <c>~/.config/codeybox/config.json</c>. Deliberately not a new Agnes setting — a second source of truth
/// for one credential is how the two drift apart.
/// </remarks>
public sealed record CodeyBoxOptions(string BaseUrl, string? ApiKey)
{
    /// <summary>What the CLI falls back to when nothing names a host.</summary>
    public const string DefaultBaseUrl = "http://localhost:5036";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

    public static CodeyBoxOptions Resolve()
    {
        var url = Environment.GetEnvironmentVariable("CODEYBOX_CLI_API_URL");
        var key = Environment.GetEnvironmentVariable("CODEYBOX_CLI_API_KEY");
        if (!string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(key))
        {
            return new CodeyBoxOptions(url.TrimEnd('/'), key);
        }

        var (fileUrl, fileKey) = ReadConfigFile();
        return new CodeyBoxOptions(
            (url ?? fileUrl ?? DefaultBaseUrl).TrimEnd('/'),
            key ?? fileKey);
    }

    /// <summary>
    /// Reads the CLI's config file, tolerating anything about it that isn't as expected. A plugin that
    /// throws while being constructed takes the whole client's plugin set down with it, and an
    /// unconfigured CodeyBox is a perfectly ordinary state — most Agnes users have no CodeyBox at all.
    /// </summary>
    private static (string? Url, string? Key) ReadConfigFile()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config", "codeybox", "config.json");
            if (!File.Exists(path))
            {
                return (null, null);
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return (Read(document, "apiBaseUrl"), Read(document, "apiKey"));
        }
        catch
        {
            return (null, null);
        }
    }

    // The file is written by another tool, so its casing is not ours to assume.
    private static string? Read(JsonDocument document, string name)
    {
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String)
            {
                var value = property.Value.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }

        return null;
    }
}
