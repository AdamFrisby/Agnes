namespace Agnes.Agents.Copilot;

/// <summary>Which wire dialect a BYOK endpoint speaks (<c>COPILOT_PROVIDER_TYPE</c>). "OpenAi" covers every
/// OpenAI-compatible endpoint, including Ollama, vLLM and Foundry Local.</summary>
public enum CopilotProviderType
{
    OpenAi,
    Azure,
    Anthropic,
}

/// <summary>Which OpenAI API surface to call (<c>COPILOT_PROVIDER_WIRE_API</c>). GPT-5 series models need
/// <see cref="Responses"/>.</summary>
public enum CopilotWireApi
{
    Completions,
    Responses,
}

/// <summary>Transport for the provider call (<c>COPILOT_PROVIDER_TRANSPORT</c>). WebSockets is only
/// meaningful together with <see cref="CopilotWireApi.Responses"/>.</summary>
public enum CopilotTransport
{
    Http,
    WebSockets,
}

/// <summary>
/// Copilot's "bring your own key" configuration. Copilot exposes this axis <b>only</b> through the
/// environment — there are no argv flags for it — which is why it is modelled as a typed record here and
/// rendered to variables by <see cref="CopilotAgent.BuildProviderEnvironment"/> rather than left as a
/// string bag in host config. Setting <see cref="BaseUrl"/> is what activates BYOK; with it, Copilot needs
/// no GitHub authentication at all, which is the mode that matters for a sandboxed or offline host.
/// </summary>
public sealed record CopilotProviderOptions
{
    /// <summary>API endpoint URL. Required — BYOK is inactive until this is set, and every other member
    /// here is ignored without it.</summary>
    public string? BaseUrl { get; init; }

    /// <summary>Provider dialect. Defaults to OpenAI-compatible, as Copilot itself does.</summary>
    public CopilotProviderType Type { get; init; } = CopilotProviderType.OpenAi;

    /// <summary>API key. Optional for local providers (Ollama and friends need none).</summary>
    public string? ApiKey { get; init; }

    /// <summary>Bearer token. Takes precedence over <see cref="ApiKey"/> in Copilot itself, so setting both
    /// is ambiguous — state one.</summary>
    public string? BearerToken { get; init; }

    public CopilotWireApi WireApi { get; init; } = CopilotWireApi.Completions;

    public CopilotTransport Transport { get; init; } = CopilotTransport.Http;

    /// <summary>Azure API version. Null uses the GA versionless v1 route.</summary>
    public string? AzureApiVersion { get; init; }

    /// <summary>Extra HTTP headers sent only to the provider endpoint, as <c>Name: Value</c> entries. Joined
    /// with newlines, which is the separator Copilot parses.</summary>
    public IReadOnlyList<string> Headers { get; init; } = [];

    /// <summary>Well-known model id used for token limits and prompting strategy. Defaults to
    /// <see cref="Model"/>.</summary>
    public string? ModelId { get; init; }

    /// <summary>Model name actually sent to the provider (a fine-tuned variant, or an Azure deployment
    /// name). Defaults to <see cref="Model"/>.</summary>
    public string? WireModel { get; init; }

    /// <summary>Sets both the model id and the wire model — the simple case. BYOK requires a model from one
    /// of these three, or from the session's own selection.</summary>
    public string? Model { get; init; }

    public int? MaxPromptTokens { get; init; }

    public int? MaxOutputTokens { get; init; }

    /// <summary>Whether BYOK is actually configured. Everything else is inert without a base URL.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl);
}
