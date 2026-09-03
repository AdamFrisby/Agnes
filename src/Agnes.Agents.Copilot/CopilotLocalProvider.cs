using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agnes.Agents.Copilot;

/// <summary>
/// The known incompatibilities between Copilot's BYOK requests and a plain OpenAI-compatible server, and
/// what to do about each.
///
/// <para>Copilot's BYOK path sends requests shaped for its own hosted models. Two of those shapes are not
/// part of the OpenAI API that local servers implement, and both fail the <b>whole request</b> rather
/// than degrading — so a local model appears completely broken until they are dealt with. Both were
/// observed by capturing what Copilot actually puts on the wire (v1.0.81).</para>
/// </summary>
public static class CopilotLocalCompatibility
{
    /// <summary>
    /// Tools worth withholding from a local provider.
    ///
    /// <para><c>apply_patch</c> is offered as an OpenAI <b>custom tool with a Lark grammar</b>
    /// (<c>"type": "custom"</c>). A server implementing only <c>"type": "function"</c> rejects the entire
    /// tools array — <c>Failed to parse tools: Unsupported tool type</c> — so no turn can start at all.
    /// Withholding it costs one editing tool; Copilot still has its ordinary file tools.</para>
    /// </summary>
    public static IReadOnlyList<string> RecommendedExcludedTools { get; } = ["apply_patch"];

    /// <summary>
    /// The request field that most often breaks a strict local server, and why naming a well-known
    /// <see cref="CopilotProviderOptions.ModelId"/> is the fix.
    ///
    /// <para>Copilot sends <c>reasoning_effort</c> derived from the model id, and for an unrecognised id
    /// it sends <c>"max"</c> — which is not an OpenAI-standard value (the standard set is low / medium /
    /// high). A server whose chat template validates the field rejects the request outright; one such
    /// answered <c>Jinja Exception: Unexpected reasoning effort max. Supported types are xhigh
    /// (default), medium, and low.</c></para>
    ///
    /// <para>Setting <see cref="CopilotProviderOptions.ModelId"/> to a well-known id changes what is
    /// sent — <c>gpt-5.4</c> produced <c>"medium"</c> where <c>gpt-4.1</c> and <c>claude-sonnet-4</c>
    /// both produced <c>"max"</c>. Agnes does <b>not</b> pick one automatically: the id also selects
    /// prompting strategy and token limits, so choosing it is a real decision about how the agent
    /// behaves, not a compatibility detail to paper over.</para>
    /// </summary>
    public const string ReasoningEffortGuidance =
        "If the provider rejects the request with an error mentioning reasoning effort, set the model id " +
        "to a well-known model whose effort profile it accepts (gpt-5.4 sends \"medium\"); the wire model " +
        "stays your local model's name.";
}

/// <summary>One model a local provider is serving.</summary>
public sealed record CopilotLocalModel(string Id, string? OwnedBy)
{
    /// <summary>What to show in a picker. The owner is included where the server gives one, because a
    /// local server's own name ("lemonade", "library") is often the only thing distinguishing an
    /// endpoint from another on the same machine.</summary>
    public string DisplayName => OwnedBy is { Length: > 0 } owner ? $"{Id}  ({owner})" : Id;
}

/// <summary>
/// Asks an OpenAI-compatible endpoint what it serves, so a local provider can be configured by picking a
/// model rather than typing its name.
///
/// <para>This is the one piece of BYOK that does not have to be guesswork: every OpenAI-compatible
/// server implements <c>GET /v1/models</c>, and Copilot itself calls it on startup. Agnes calling it
/// first means a misconfigured URL or key fails in a settings dialog with a readable message, rather
/// than inside an agent session as a failed turn.</para>
/// </summary>
public static class CopilotLocalModels
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Lists the models at <paramref name="baseUrl"/>. Returns null when the endpoint could not be
    /// reached or did not answer with a model list — the caller distinguishes "no models" (an empty list,
    /// a reachable but empty server) from "could not ask".
    /// </summary>
    /// <param name="baseUrl">The provider base URL, with or without a trailing <c>/v1</c>.</param>
    /// <param name="apiKey">Sent as a bearer token when present. Local servers commonly need none.</param>
    public static async Task<IReadOnlyList<CopilotLocalModel>?> ListAsync(
        string? baseUrl,
        string? apiKey,
        HttpMessageHandler? handler = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(ModelsUrl(baseUrl), UriKind.Absolute, out var url))
        {
            return null;
        }

        using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromSeconds(20);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (apiKey is { Length: > 0 } key)
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
            }

            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var payload = await response.Content
                .ReadFromJsonSafeAsync<ModelListResponse>(Json, cancellationToken).ConfigureAwait(false);

            return payload?.Data is null
                ? null
                : [.. payload.Data
                    .Where(m => !string.IsNullOrWhiteSpace(m.Id))
                    .Select(m => new CopilotLocalModel(m.Id!, m.OwnedBy))];
        }
        catch (Exception)
        {
            // Unreachable, wrong scheme, TLS failure, timeout — all "could not ask", which the caller
            // reports as such rather than as an empty catalogue.
            return null;
        }
    }

    /// <summary>
    /// Resolves the models URL. A base URL may or may not already carry <c>/v1</c>: Copilot's own
    /// documented examples end in it, but an operator pasting a server's home page will not, and getting
    /// this wrong produces a 404 that reads like an auth failure.
    /// </summary>
    public static string ModelsUrl(string baseUrl)
    {
        var trimmed = baseUrl.TrimEnd('/');
        return trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? trimmed + "/models"
            : trimmed + "/v1/models";
    }

    private sealed record ModelListResponse
    {
        [JsonPropertyName("data")]
        public IReadOnlyList<ModelEntry>? Data { get; init; }
    }

    private sealed record ModelEntry
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("owned_by")]
        public string? OwnedBy { get; init; }
    }
}

internal static class HttpContentJsonExtensions
{
    /// <summary>Reads JSON without throwing on a body that is not JSON at all — a plain-text error page
    /// from a reverse proxy is a common answer from a mistyped URL.</summary>
    public static async Task<T?> ReadFromJsonSafeAsync<T>(
        this HttpContent content, JsonSerializerOptions options, CancellationToken cancellationToken)
    {
        try
        {
            var text = await content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<T>(text, options);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
