using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agnes.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Channels;

/// <summary>Host settings for the Discord bridge, under <c>Agnes:Channels:Discord:*</c>. The bot token (outbound)
/// and application public key (inbound signature verification) are both required — absent either the bridge is
/// not registered (see <see cref="FromConfiguration"/>).</summary>
public sealed record DiscordBridgeOptions(string BotToken, string PublicKey, string ApiBaseUrl = "https://discord.com/api/v10")
{
    /// <summary>Binds the options from config, or returns null when the required secrets are absent (config-gating).
    /// Pure over the configuration so the gate is testable without booting the host.</summary>
    public static DiscordBridgeOptions? FromConfiguration(IConfiguration configuration)
    {
        var token = configuration["Agnes:Channels:Discord:BotToken"];
        var publicKey = configuration["Agnes:Channels:Discord:PublicKey"];
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(publicKey))
        {
            return null;
        }

        var baseUrl = configuration["Agnes:Channels:Discord:ApiBaseUrl"];
        return new DiscordBridgeOptions(token, publicKey,
            string.IsNullOrWhiteSpace(baseUrl) ? "https://discord.com/api/v10" : baseUrl.TrimEnd('/'));
    }
}

/// <summary>
/// The Discord <see cref="IChannelBridge"/>. OUTBOUND is fully implemented: it creates a message via
/// <c>POST /channels/{id}/messages</c> with the bot token as an <c>Authorization: Bot</c> credential.
/// <para>
/// INBOUND is DEFERRED. Discord authenticates interaction webhooks with an <b>Ed25519</b> signature
/// (<c>X-Signature-Ed25519</c> over <c>X-Signature-Timestamp</c> + raw body, against the app public key).
/// .NET 10's <c>System.Security.Cryptography</c> does NOT expose a standalone Ed25519 verify primitive (only
/// composite ML-DSA-with-Ed25519), and the repo's dependency policy forbids pulling in an obscure third-party
/// crypto package or hand-rolling curve math. So <see cref="HandleWebhookAsync"/> is a clearly-marked seam: it
/// refuses every request (never trusting an unverifiable signature) until a first-party/BCL Ed25519 option is
/// available, at which point signature verification + PING→PONG + interaction→<see cref="OnInboundMessage"/>
/// mapping slot in behind the same interface with no other change. The outbound path ships today.
/// </para>
/// </summary>
public sealed class DiscordBridge : IChannelBridge
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly DiscordBridgeOptions _options;
    private readonly ILogger<DiscordBridge>? _logger;

    public DiscordBridge(HttpClient http, DiscordBridgeOptions options, ILogger<DiscordBridge>? logger = null)
    {
        _http = http;
        _options = options;
        _logger = logger;
    }

    public string Id => "discord";

    // The inbound reply path is deferred (see the type doc): no code raises this yet, but the interface requires
    // it and the shared router subscribes to it, so the seam stays in place for when Ed25519 verification lands.
#pragma warning disable CS0067 // Event is never used — inbound is deferred pending a BCL Ed25519 option.
    public event Func<InboundChannelMessage, Task>? OnInboundMessage;
#pragma warning restore CS0067

    // Outbound create-message body. snake_case on the wire ("content").
    private sealed record CreateMessageRequest(string Content);

    public async Task SendAsync(string externalChatId, string message, ChannelBridgeContext context, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_options.ApiBaseUrl}/channels/{externalChatId}/messages")
            {
                Content = JsonContent.Create(new CreateMessageRequest(message), options: Json),
            };
            // Discord bot auth uses the "Bot" scheme, not "Bearer".
            request.Headers.Authorization = new AuthenticationHeaderValue("Bot", _options.BotToken);

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogWarning("Discord create-message to channel {Channel} returned {Status}", externalChatId, response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            // Independent delivery: a Discord failure is logged and swallowed.
            _logger?.LogWarning(ex, "Discord bridge failed to deliver to channel {Channel}", externalChatId);
        }
    }

    /// <summary>
    /// Inbound webhook handler — DEFERRED. Discord's Ed25519 interaction signature cannot be verified with the
    /// current .NET 10 BCL (no standalone Ed25519 primitive), so this refuses every request rather than trust an
    /// unverifiable one. When a first-party/BCL Ed25519 verify becomes available, replace the body with:
    /// verify(<paramref name="timestamp"/> + <paramref name="rawBody"/>, <paramref name="signature"/>) against
    /// the app public key, respond to a PING interaction (type 1) with a PONG, and map command/component
    /// interactions to <see cref="OnInboundMessage"/>. Kept async-shaped so that drop-in needs no signature change.
    /// </summary>
    public Task<ChannelWebhookResult> HandleWebhookAsync(string rawBody, string? timestamp, string? signature)
    {
        _logger?.LogWarning(
            "Discord inbound interaction rejected: Ed25519 signature verification is not available in the current BCL (inbound deferred).");
        return Task.FromResult(ChannelWebhookResult.Unauthorized);
    }
}
