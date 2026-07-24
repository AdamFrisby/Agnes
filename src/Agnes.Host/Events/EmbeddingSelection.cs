using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OpenAI;

namespace Agnes.Host.Events;

/// <summary>
/// Resolves the memory-search embedding source from configuration (see .ideas/ops/02-memory-search.md's
/// "custom local or remote OpenAI-compatible endpoint" open question). Kept as a pure function of
/// <see cref="IConfiguration"/> so the choice is testable and Program.cs and any test share one source of
/// truth. Crucially it is <b>config-gated</b>: for the default <c>none</c> it returns <see langword="null"/>
/// and constructs no client at all, so a FTS5-only host never touches Microsoft.Extensions.AI.OpenAI.
/// </summary>
/// <remarks>
/// Both concrete providers go through the same OpenAI connector — <c>openai</c> hits api.openai.com, and
/// <c>local</c> points the very same connector at an OpenAI-compatible base URL (Ollama, LM Studio, vLLM…).
/// Because both surface as <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/>, the index code stays
/// entirely provider-agnostic and is exercised in tests with a fake generator instead of either backend.
/// </remarks>
internal static class EmbeddingSelection
{
    /// <summary>The default OpenAI embedding model — a balanced size/quality/cost pick.</summary>
    private const string DefaultOpenAIModel = "text-embedding-3-small";

    /// <summary>
    /// Builds the configured embedding generator, or <see langword="null"/> when embeddings are disabled
    /// (<c>Agnes:Search:Embeddings:Provider</c> unset or <c>none</c>). Throws only when a provider is
    /// explicitly selected but its required settings (key/base URL/model) are missing — a misconfiguration
    /// worth surfacing loudly rather than silently degrading to keyword-only.
    /// </summary>
    public static IEmbeddingGenerator<string, Embedding<float>>? Build(IConfiguration configuration)
    {
        var provider = configuration["Agnes:Search:Embeddings:Provider"]?.Trim().ToLowerInvariant();
        return provider switch
        {
            null or "" or "none" => null,
            "openai" => BuildOpenAI(configuration),
            "local" => BuildLocal(configuration),
            _ => throw new InvalidOperationException(
                $"Unknown Agnes:Search:Embeddings:Provider '{provider}' (expected none|openai|local)."),
        };
    }

    private static IEmbeddingGenerator<string, Embedding<float>> BuildOpenAI(IConfiguration configuration)
    {
        var apiKey = Require(configuration, "Agnes:Search:Embeddings:OpenAI:ApiKey");
        var model = configuration["Agnes:Search:Embeddings:OpenAI:Model"] ?? DefaultOpenAIModel;
        return Create(apiKey, model, baseUrl: null);
    }

    private static IEmbeddingGenerator<string, Embedding<float>> BuildLocal(IConfiguration configuration)
    {
        var baseUrl = Require(configuration, "Agnes:Search:Embeddings:Local:BaseUrl");
        var model = Require(configuration, "Agnes:Search:Embeddings:Local:Model");
        // A local OpenAI-compatible server (Ollama/LM Studio) usually ignores the key, but the connector
        // requires a non-empty credential — accept an optional override, else a harmless placeholder.
        var apiKey = configuration["Agnes:Search:Embeddings:Local:ApiKey"];
        apiKey = string.IsNullOrWhiteSpace(apiKey) ? "local" : apiKey;
        return Create(apiKey, model, new Uri(baseUrl));
    }

    private static IEmbeddingGenerator<string, Embedding<float>> Create(string apiKey, string model, Uri? baseUrl)
    {
        var options = new OpenAIClientOptions();
        if (baseUrl is not null)
        {
            options.Endpoint = baseUrl;
        }

        return new OpenAIClient(new ApiKeyCredential(apiKey), options)
            .GetEmbeddingClient(model)
            .AsIEmbeddingGenerator();
    }

    private static string Require(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Memory-search embeddings require '{key}' to be set.");
        }

        return value;
    }
}
