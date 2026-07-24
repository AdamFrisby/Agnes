using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agnes.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSec.Cryptography;

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
/// The Discord <see cref="IChannelBridge"/>. OUTBOUND: creates a message via <c>POST /channels/{id}/messages</c>
/// with the bot token as an <c>Authorization: Bot</c> credential.
/// <para>
/// INBOUND: Discord authenticates interaction webhooks with an <b>Ed25519</b> signature —
/// <c>X-Signature-Ed25519</c> (hex) over <c>X-Signature-Timestamp</c> + the raw request body, verified against
/// the application's public key (hex). The .NET 10 BCL still exposes no standalone Ed25519 verify primitive, so
/// <see cref="VerifySignature"/> uses <b>NSec.Cryptography</b> (libsodium-backed, constant-time by construction —
/// see the dependency note in <c>Agnes.Host.csproj</c>). <see cref="HandleWebhookAsync"/> verifies before
/// trusting anything: a correctly-signed PING (type 1) is answered with a PONG, a signed command/component
/// interaction is mapped to <see cref="OnInboundMessage"/>, and an unsigned/forged/stale request never raises it.
/// Authorization of an inbound message stays in the shared router.
/// </para>
/// </summary>
public sealed class DiscordBridge : IChannelBridge
{
    /// <summary>Reject interaction timestamps more than five minutes from now (either direction) to blunt replay —
    /// the signature covers the timestamp, so a captured request stays valid forever without this window.</summary>
    internal static readonly TimeSpan MaxTimestampSkew = TimeSpan.FromMinutes(5);

    // Discord interaction types + response types we care about (Discord API v10).
    private const int InteractionTypePing = 1;
    private const int InteractionTypeApplicationCommand = 2;
    private const int InteractionTypeMessageComponent = 3;
    private const int InteractionResponseTypePong = 1;

    // Ed25519: raw public keys are 32 bytes, signatures 64 bytes.
    private const int PublicKeyLength = 32;
    private const int SignatureLength = 64;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly DiscordBridgeOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<DiscordBridge>? _logger;

    public DiscordBridge(HttpClient http, DiscordBridgeOptions options, TimeProvider? time = null, ILogger<DiscordBridge>? logger = null)
    {
        _http = http;
        _options = options;
        _time = time ?? TimeProvider.System;
        _logger = logger;
    }

    public string Id => "discord";

    public event Func<InboundChannelMessage, Task>? OnInboundMessage;

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
    /// Verifies a Discord interaction signature: the Ed25519 <paramref name="signature"/> (hex) over
    /// <c>{timestamp}{rawBody}</c>, checked against the application <paramref name="publicKeyHex"/> (hex) with
    /// NSec/libsodium (constant-time). Pure and side-effect-free so it is directly unit-testable. Returns false —
    /// never throws — for a missing/malformed public key or signature, a bad or stale timestamp, or any mismatch.
    /// </summary>
    public static bool VerifySignature(string publicKeyHex, string timestamp, string rawBody, string signature, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(publicKeyHex) || string.IsNullOrEmpty(signature)
            || !long.TryParse(timestamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch))
        {
            return false;
        }

        var age = now - DateTimeOffset.FromUnixTimeSeconds(epoch);
        if (age > MaxTimestampSkew || age < -MaxTimestampSkew)
        {
            return false; // stale (or implausibly future-dated) — reject to blunt replay.
        }

        // Parse both hex inputs; a malformed hex string or an off-length key/signature is a rejection, not a throw.
        if (!TryFromHex(publicKeyHex, PublicKeyLength, out var publicKeyBytes)
            || !TryFromHex(signature, SignatureLength, out var signatureBytes))
        {
            return false;
        }

        var data = Encoding.UTF8.GetBytes(timestamp + rawBody);
        try
        {
            var publicKey = NSec.Cryptography.PublicKey.Import(
                SignatureAlgorithm.Ed25519, publicKeyBytes, KeyBlobFormat.RawPublicKey);
            return SignatureAlgorithm.Ed25519.Verify(publicKey, data, signatureBytes);
        }
        catch (FormatException)
        {
            // NSec rejects a structurally-invalid key blob with FormatException — treat as an unverifiable request.
            return false;
        }
    }

    private static bool TryFromHex(string hex, int expectedLength, out byte[] bytes)
    {
        try
        {
            bytes = Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }

        return bytes.Length == expectedLength;
    }

    // ---- inbound interaction payload (Discord API v10); typed at the boundary, no dynamic/JsonElement inward ----
    private sealed record Interaction(
        [property: JsonPropertyName("type")] int Type,
        [property: JsonPropertyName("channel_id")] string? ChannelId,
        [property: JsonPropertyName("data")] InteractionData? Data);

    private sealed record InteractionData(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("custom_id")] string? CustomId,
        [property: JsonPropertyName("options")] IReadOnlyList<InteractionOption>? Options);

    // An option's value is genuinely polymorphic on the wire (string/int/bool/number per the command schema), so
    // this one sub-field stays JsonElement; we read only string values (a free-text reply arg) and ignore the rest.
    private sealed record InteractionOption(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("value")] JsonElement Value);

    /// <summary>
    /// Handles a raw inbound Discord interaction POST: verifies the Ed25519 signature first, then either answers a
    /// PING with a PONG (type 1) or maps a command/component interaction to <see cref="OnInboundMessage"/> (keyed
    /// by the interaction's channel id, carrying the reply text). Never raises the inbound event for an unverified
    /// request.
    /// </summary>
    public async Task<ChannelWebhookResult> HandleWebhookAsync(string rawBody, string? timestamp, string? signature)
    {
        if (!VerifySignature(_options.PublicKey, timestamp ?? string.Empty, rawBody, signature ?? string.Empty, _time.GetUtcNow()))
        {
            return ChannelWebhookResult.Unauthorized;
        }

        Interaction? interaction;
        try
        {
            interaction = JsonSerializer.Deserialize<Interaction>(rawBody, Json);
        }
        catch (JsonException)
        {
            return ChannelWebhookResult.BadRequest;
        }

        if (interaction is null)
        {
            return ChannelWebhookResult.BadRequest;
        }

        if (interaction.Type == InteractionTypePing)
        {
            // The endpoint-verification handshake: Discord expects a PONG (response type 1) echoed back.
            return ChannelWebhookResult.Echo($"{{\"type\":{InteractionResponseTypePong}}}");
        }

        if (interaction.Type is InteractionTypeApplicationCommand or InteractionTypeMessageComponent
            && interaction.ChannelId is { Length: > 0 } channelId
            && ExtractText(interaction.Data) is { Length: > 0 } text)
        {
            await RaiseInboundAsync(new InboundChannelMessage(Id, channelId, text)).ConfigureAwait(false);
        }

        return ChannelWebhookResult.Ok;
    }

    /// <summary>Pulls the actionable reply text out of an interaction: a free-text slash-command option value, else
    /// a component's custom id (a button carries its decision there), else the bare command name.</summary>
    private static string? ExtractText(InteractionData? data)
    {
        if (data is null)
        {
            return null;
        }

        foreach (var option in data.Options ?? [])
        {
            if (option.Value.ValueKind == JsonValueKind.String)
            {
                var value = option.Value.GetString();
                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }
            }
        }

        if (!string.IsNullOrEmpty(data.CustomId))
        {
            return data.CustomId;
        }

        return string.IsNullOrEmpty(data.Name) ? null : data.Name;
    }

    private Task RaiseInboundAsync(InboundChannelMessage message)
        => OnInboundMessage?.Invoke(message) ?? Task.CompletedTask;
}
