using System.Net.Http;
using System.Text.Json;

namespace Agnes.App.Desktop;

/// <summary>The result of an update check: the latest published version and its release page.</summary>
public sealed record UpdateInfo(bool IsNewer, string Version, string Url);

/// <summary>
/// Checks GitHub Releases for a newer version. Best-effort and non-blocking: any failure (offline,
/// no releases yet, rate-limited) simply returns null and the app carries on. Foreground-first — we
/// notify and link to the download rather than auto-installing.
/// </summary>
public static class UpdateCheck
{
    private const string LatestReleaseApi = "https://api.github.com/repos/AdamFrisby/Agnes/releases/latest";

    public static async Task<UpdateInfo?> CheckAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Agnes-Desktop");
            http.Timeout = TimeSpan.FromSeconds(6);

            var json = await http.GetStringAsync(LatestReleaseApi, cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString()?.TrimStart('v', 'V') : null;
            var url = root.TryGetProperty("html_url", out var u) ? u.GetString() : null;
            if (string.IsNullOrEmpty(tag) || string.IsNullOrEmpty(url))
            {
                return null;
            }

            var newer = Version.TryParse(tag, out var latest)
                        && Version.TryParse(currentVersion, out var current)
                        && latest > current;
            return new UpdateInfo(newer, tag, url);
        }
        catch
        {
            return null;
        }
    }
}
