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

/// <summary>Host settings for the WhatsApp (Meta Cloud API) bridge, under <c>Agnes:Channels:WhatsApp:*</c>. The
/// phone-number id, access token, verify token and app secret are all required — absent any of them the bridge
/// is not registered (see <see cref="FromConfiguration"/>).</summary>
public sealed record WhatsAppBridgeOptions(
    string PhoneNumberId,
    string AccessToken,
    string VerifyToken,
    string AppSecret,
    string ApiBaseUrl = "https://graph.facebook.com/v21.0")
{
    /// <summary>Binds the options from config, or returns null when any required secret is absent (config-gating).
    /// Pure over the configuration so the gate is testable without booting the host.</summary>
    public static WhatsAppBridgeOptions? FromConfiguration(IConfiguration configuration)
    {
        var phoneNumberId = configuration["Agnes:Channels:WhatsApp:PhoneNumberId"];
        var accessToken = configuration["Agnes:Channels:WhatsApp:AccessToken"];
        var verifyToken = configuration["Agnes:Channels:WhatsApp:VerifyToken"];
        var appSecret = configuration["Agnes:Channels:WhatsApp:AppSecret"];
        if (string.IsNullOrWhiteSpace(phoneNumberId) || string.IsNullOrWhiteSpace(accessToken)
            || string.IsNullOrWhiteSpace(verifyToken) || string.IsNullOrWhiteSpace(appSecret))
        {
            return null;
        }

        var baseUrl = configuration["Agnes:Channels:WhatsApp:ApiBaseUrl"];
        return new WhatsAppBridgeOptions(phoneNumberId, accessToken, verifyToken, appSecret,
            string.IsNullOrWhiteSpace(baseUrl) ? "https://graph.facebook.com/v21.0" : baseUrl.TrimEnd('/'));
    }
}

/// <summary>
/// The WhatsApp <see cref="IChannelBridge"/> over the Meta Cloud API. OUTBOUND: posts a text message to
/// <c>/{phone-number-id}/messages</c> with the access token as a Bearer credential. INBOUND: its
/// <c>/channels/whatsapp/webhook</c> endpoint has a GET verify leg (<see cref="VerifyChallenge"/>, echoing
/// <c>hub.challenge</c> only when <c>hub.verify_token</c> matches) and a POST leg
/// (<see cref="HandleWebhookAsync"/>) that verifies the <c>X-Hub-Signature-256</c> HMAC-SHA256 (BCL
/// <see cref="System.Security.Cryptography.HMACSHA256"/> + constant-time compare) before mapping each inbound
/// text message to <see cref="OnInboundMessage"/>. Authorization of that message stays in the shared router.
/// </summary>
public sealed class WhatsAppBridge : IChannelBridge
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly WhatsAppBridgeOptions _options;
    private readonly ILogger<WhatsAppBridge>? _logger;

    public WhatsAppBridge(HttpClient http, WhatsAppBridgeOptions options, ILogger<WhatsAppBridge>? logger = null)
    {
        _http = http;
        _options = options;
        _logger = logger;
    }

    public string Id => "whatsapp";

    public event Func<InboundChannelMessage, Task>? OnInboundMessage;

    // Outbound message body for the Cloud API. snake_case on the wire (messaging_product, etc.).
    private sealed record SendMessageRequest(string MessagingProduct, string To, string Type, TextBody Text);

    private sealed record TextBody(string Body);

    public async Task SendAsync(string externalChatId, string message, ChannelBridgeContext context, CancellationToken ct = default)
    {
        try
        {
            var body = new SendMessageRequest("whatsapp", externalChatId, "text", new TextBody(message));
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_options.ApiBaseUrl}/{_options.PhoneNumberId}/messages")
            {
                Content = JsonContent.Create(body, options: Json),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.AccessToken);

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogWarning("WhatsApp send to {To} returned {Status}", externalChatId, response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            // Independent delivery: a WhatsApp failure is logged and swallowed.
            _logger?.LogWarning(ex, "WhatsApp bridge failed to deliver to {To}", externalChatId);
        }
    }

    /// <summary>The GET verify handshake: returns <paramref name="challenge"/> to echo when the mode is
    /// <c>subscribe</c> and the token matches the configured verify token, else null (the endpoint 403s). The
    /// token compare is constant-time so a wrong token can't be probed by timing.</summary>
    public string? VerifyChallenge(string? mode, string? token, string? challenge)
    {
        if (!string.Equals(mode, "subscribe", StringComparison.Ordinal) || token is null)
        {
            return null;
        }

        var matches = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(_options.VerifyToken));
        return matches ? challenge : null;
    }

    /// <summary>
    /// Verifies a Meta <c>X-Hub-Signature-256</c> header: HMAC-SHA256 keyed by the app secret over the raw body,
    /// formatted <c>sha256=&lt;hex&gt;</c>, compared constant-time. Pure and directly unit-testable; returns
    /// false — never throws — for a missing/malformed header or any mismatch.
    /// </summary>
    public static bool VerifySignature(string appSecret, string rawBody, string signatureHeader)
    {
        if (string.IsNullOrEmpty(appSecret) || string.IsNullOrEmpty(signatureHeader))
        {
            return false;
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(appSecret));
        var mac = hmac.ComputeHash(Encoding.UTF8.GetBytes(rawBody));
        var expected = "sha256=" + Convert.ToHexStringLower(mac);

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(signatureHeader));
    }

    // ---- inbound webhook payload (Cloud API); typed at the boundary ----
    private sealed record WebhookPayload([property: JsonPropertyName("entry")] IReadOnlyList<Entry>? Entry);

    private sealed record Entry([property: JsonPropertyName("changes")] IReadOnlyList<Change>? Changes);

    private sealed record Change([property: JsonPropertyName("value")] ChangeValue? Value);

    private sealed record ChangeValue([property: JsonPropertyName("messages")] IReadOnlyList<Message>? Messages);

    private sealed record Message(
        [property: JsonPropertyName("from")] string? From,
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("text")] MessageText? Text);

    private sealed record MessageText([property: JsonPropertyName("body")] string? Body);

    /// <summary>
    /// Handles a raw inbound Cloud API webhook POST: verifies the signature, then raises
    /// <see cref="OnInboundMessage"/> for each text message in the payload (keyed by the sender's phone number as
    /// the external chat id). Never raises for an unverified request.
    /// </summary>
    public async Task<ChannelWebhookResult> HandleWebhookAsync(string rawBody, string? signature)
    {
        if (!VerifySignature(_options.AppSecret, rawBody, signature ?? string.Empty))
        {
            return ChannelWebhookResult.Unauthorized;
        }

        WebhookPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<WebhookPayload>(rawBody, Json);
        }
        catch (JsonException)
        {
            return ChannelWebhookResult.BadRequest;
        }

        if (payload?.Entry is null)
        {
            return ChannelWebhookResult.Ok; // status/read receipts and other non-message events: nothing to route.
        }

        foreach (var entry in payload.Entry)
        {
            foreach (var change in entry.Changes ?? [])
            {
                foreach (var message in change.Value?.Messages ?? [])
                {
                    if (message is { Type: "text", From: { Length: > 0 } from, Text.Body: { Length: > 0 } text })
                    {
                        await RaiseInboundAsync(new InboundChannelMessage(Id, from, text)).ConfigureAwait(false);
                    }
                }
            }
        }

        return ChannelWebhookResult.Ok;
    }

    private Task RaiseInboundAsync(InboundChannelMessage message)
        => OnInboundMessage?.Invoke(message) ?? Task.CompletedTask;
}
