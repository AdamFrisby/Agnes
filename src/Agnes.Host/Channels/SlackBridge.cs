using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agnes.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Channels;

/// <summary>Host settings for the Slack bridge, under <c>Agnes:Channels:Slack:*</c>. Both the bot token and the
/// signing secret are required — without them the bridge is not registered (see
/// <see cref="FromConfiguration"/>), so knowing the endpoint URL is never on its own enough to drive it.</summary>
public sealed record SlackBridgeOptions(string BotToken, string SigningSecret, string ApiBaseUrl = "https://slack.com/api")
{
    /// <summary>Binds the options from config, or returns null when the required secrets are absent (config-gating:
    /// a bridge with no token is simply not created). Kept a pure function of the configuration so the gate is
    /// testable without booting the host.</summary>
    public static SlackBridgeOptions? FromConfiguration(IConfiguration configuration)
    {
        var token = configuration["Agnes:Channels:Slack:BotToken"];
        var secret = configuration["Agnes:Channels:Slack:SigningSecret"];
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(secret))
        {
            return null;
        }

        var baseUrl = configuration["Agnes:Channels:Slack:ApiBaseUrl"];
        return new SlackBridgeOptions(token, secret,
            string.IsNullOrWhiteSpace(baseUrl) ? "https://slack.com/api" : baseUrl.TrimEnd('/'));
    }
}

/// <summary>
/// The Slack <see cref="IChannelBridge"/>. OUTBOUND: posts to <c>chat.postMessage</c> with the bot token as a
/// Bearer credential. INBOUND: its <c>/channels/slack/events</c> endpoint hands raw request bytes to
/// <see cref="HandleWebhookAsync"/>, which verifies the Slack <c>v0</c> request signature (HMAC-SHA256 over the
/// timestamped body, BCL <see cref="System.Security.Cryptography.HMACSHA256"/> + constant-time compare) BEFORE
/// trusting anything — an unsigned/forged/stale request never raises <see cref="OnInboundMessage"/>. The
/// url-verification handshake is echoed; a real <c>message</c> event becomes an inbound chat message the host
/// router then authorizes against the link store. Transport-only: authorization lives in the shared router.
/// </summary>
public sealed class SlackBridge : IChannelBridge
{
    /// <summary>Slack rejects requests whose timestamp is more than five minutes old to blunt replay attacks; we
    /// apply the same window (symmetric, so clock skew either direction is bounded).</summary>
    internal static readonly TimeSpan MaxTimestampSkew = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly SlackBridgeOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<SlackBridge>? _logger;

    public SlackBridge(HttpClient http, SlackBridgeOptions options, TimeProvider? time = null, ILogger<SlackBridge>? logger = null)
    {
        _http = http;
        _options = options;
        _time = time ?? TimeProvider.System;
        _logger = logger;
    }

    public string Id => "slack";

    public event Func<InboundChannelMessage, Task>? OnInboundMessage;

    // Outbound message body for chat.postMessage. snake_case on the wire (both single words here).
    private sealed record PostMessageRequest(string Channel, string Text);

    public async Task SendAsync(string externalChatId, string message, ChannelBridgeContext context, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_options.ApiBaseUrl}/chat.postMessage")
            {
                Content = JsonContent.Create(new PostMessageRequest(externalChatId, message), options: Json),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.BotToken);

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogWarning("Slack chat.postMessage to {Channel} returned {Status}", externalChatId, response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            // Delivery is independent: a Slack failure is logged and swallowed, never propagated to the notifier.
            _logger?.LogWarning(ex, "Slack bridge failed to deliver to channel {Channel}", externalChatId);
        }
    }

    /// <summary>
    /// Verifies a Slack request signature (the <c>v0</c> scheme): HMAC-SHA256 keyed by the signing secret over
    /// <c>v0:{timestamp}:{rawBody}</c>, compared constant-time to the <c>X-Slack-Signature</c> header. Pure and
    /// side-effect-free so it is directly unit-testable. Returns false — never throws — for a bad timestamp, a
    /// timestamp outside the replay window, or any mismatch.
    /// </summary>
    public static bool VerifySignature(string signingSecret, string timestamp, string rawBody, string signature, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(signingSecret) || string.IsNullOrEmpty(signature)
            || !long.TryParse(timestamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch))
        {
            return false;
        }

        var age = now - DateTimeOffset.FromUnixTimeSeconds(epoch);
        if (age > MaxTimestampSkew || age < -MaxTimestampSkew)
        {
            return false; // stale (or implausibly future-dated) — reject to blunt replay.
        }

        var basestring = $"v0:{timestamp}:{rawBody}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(signingSecret));
        var mac = hmac.ComputeHash(Encoding.UTF8.GetBytes(basestring));
        var expected = "v0=" + Convert.ToHexStringLower(mac);

        // Constant-time compare of the ASCII bytes; differing lengths return false without leaking timing.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(signature));
    }

    // ---- inbound webhook payload (Slack Events API); typed at the boundary, no dynamic/JsonElement inward ----
    private sealed record SlackEnvelope(
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("challenge")] string? Challenge,
        [property: JsonPropertyName("event")] SlackEvent? Event);

    private sealed record SlackEvent(
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("channel")] string? Channel,
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("bot_id")] string? BotId,
        [property: JsonPropertyName("subtype")] string? Subtype);

    /// <summary>
    /// Handles a raw inbound Slack Events API request: verifies the signature first, then either echoes the
    /// url-verification challenge or maps a user <c>message</c> event to <see cref="OnInboundMessage"/>. Never
    /// raises the inbound event for an unverified request.
    /// </summary>
    public async Task<ChannelWebhookResult> HandleWebhookAsync(string rawBody, string? timestamp, string? signature)
    {
        if (!VerifySignature(_options.SigningSecret, timestamp ?? string.Empty, rawBody, signature ?? string.Empty, _time.GetUtcNow()))
        {
            return ChannelWebhookResult.Unauthorized;
        }

        SlackEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<SlackEnvelope>(rawBody, Json);
        }
        catch (JsonException)
        {
            return ChannelWebhookResult.BadRequest;
        }

        if (envelope is null)
        {
            return ChannelWebhookResult.BadRequest;
        }

        if (string.Equals(envelope.Type, "url_verification", StringComparison.Ordinal))
        {
            // The one-time endpoint handshake: echo the challenge back verbatim.
            return ChannelWebhookResult.Echo(envelope.Challenge ?? string.Empty, "text/plain");
        }

        var evt = envelope.Event;
        // Only human message events are actionable; ignore bot echoes and edit/delete subtypes to avoid loops.
        if (string.Equals(envelope.Type, "event_callback", StringComparison.Ordinal)
            && evt is { Type: "message" }
            && string.IsNullOrEmpty(evt.BotId)
            && string.IsNullOrEmpty(evt.Subtype)
            && !string.IsNullOrEmpty(evt.Channel)
            && !string.IsNullOrEmpty(evt.Text))
        {
            await RaiseInboundAsync(new InboundChannelMessage(Id, evt.Channel, evt.Text)).ConfigureAwait(false);
        }

        return ChannelWebhookResult.Ok;
    }

    private Task RaiseInboundAsync(InboundChannelMessage message)
        => OnInboundMessage?.Invoke(message) ?? Task.CompletedTask;
}
