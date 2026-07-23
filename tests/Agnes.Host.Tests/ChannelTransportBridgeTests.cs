using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agnes.Abstractions;
using Agnes.Host.Channels;
using Microsoft.Extensions.Configuration;

namespace Agnes.Host.Tests;

/// <summary>
/// The three REAL channel-bridge transports (extensibility/04): Slack, Discord, WhatsApp. Everything is offline
/// — outbound requests hit a stub <see cref="HttpMessageHandler"/> (no network), and inbound signature
/// verification is exercised as the pure BCL-crypto function it is. Discord's inbound Ed25519 path is deferred
/// (no standalone Ed25519 in the .NET 10 BCL), so only its outbound + deferral seam are asserted here.
/// </summary>
public sealed class ChannelTransportBridgeTests
{
    private static readonly ChannelBridgeContext PermissionContext = new("session-1", "req-1", ChannelBridgeEventKind.PermissionRequest);

    /// <summary>Captures the single outbound request a bridge issues, so a test can assert URL/auth/body offline.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;

        public CapturingHandler(HttpStatusCode status = HttpStatusCode.OK) => _status = status;

        public HttpMethod? Method { get; private set; }
        public Uri? RequestUri { get; private set; }
        public AuthenticationHeaderValue? Authorization { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri;
            Authorization = request.Headers.Authorization;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(_status) { Content = new StringContent("{\"ok\":true}") };
        }
    }

    private static string HmacHex(string key, string message)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(message)));
    }

    // ---------------------------------------------------------------- Slack ----

    [Fact]
    public async Task Slack_send_posts_to_chat_postMessage_with_bearer_and_body()
    {
        var handler = new CapturingHandler();
        var options = new SlackBridgeOptions("xoxb-token", "secret", "https://slack.test/api");
        var bridge = new SlackBridge(new HttpClient(handler), options);

        await bridge.SendAsync("C123", "hello there", PermissionContext);

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("https://slack.test/api/chat.postMessage", handler.RequestUri?.ToString());
        Assert.Equal("Bearer", handler.Authorization?.Scheme);
        Assert.Equal("xoxb-token", handler.Authorization?.Parameter);
        using var json = JsonDocument.Parse(handler.Body!);
        Assert.Equal("C123", json.RootElement.GetProperty("channel").GetString());
        Assert.Equal("hello there", json.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public void Slack_signature_accepts_correct_and_rejects_tampered_wrong_secret_and_stale()
    {
        const string secret = "8f742231b10e8f0d0e1b";
        var now = DateTimeOffset.UnixEpoch.AddSeconds(1_700_000_000);
        var ts = now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        const string body = "{\"type\":\"event_callback\"}";
        var good = "v0=" + HmacHex(secret, $"v0:{ts}:{body}");

        Assert.True(SlackBridge.VerifySignature(secret, ts, body, good, now));
        // Tampered body.
        Assert.False(SlackBridge.VerifySignature(secret, ts, body + "x", good, now));
        // Wrong secret.
        Assert.False(SlackBridge.VerifySignature("other-secret", ts, body, good, now));
        // Stale timestamp (signature itself valid, but > 5 min old relative to now).
        Assert.False(SlackBridge.VerifySignature(secret, ts, body, good, now.AddMinutes(6)));
    }

    [Fact]
    public async Task Slack_inbound_message_verified_raises_inbound_with_channel_and_text()
    {
        var options = new SlackBridgeOptions("xoxb", "sign-secret");
        var bridge = new SlackBridge(new HttpClient(new CapturingHandler()), options);
        InboundChannelMessage? received = null;
        bridge.OnInboundMessage += m => { received = m; return Task.CompletedTask; };

        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var body = "{\"type\":\"event_callback\",\"event\":{\"type\":\"message\",\"channel\":\"C42\",\"text\":\"allow\"}}";
        var sig = "v0=" + HmacHex("sign-secret", $"v0:{ts}:{body}");

        var result = await bridge.HandleWebhookAsync(body, ts, sig);

        Assert.Equal(ChannelWebhookStatus.Ok, result.Status);
        Assert.NotNull(received);
        Assert.Equal("slack", received!.BridgeId);
        Assert.Equal("C42", received.ExternalChatId);
        Assert.Equal("allow", received.Text);
    }

    [Fact]
    public async Task Slack_url_verification_challenge_is_echoed()
    {
        var options = new SlackBridgeOptions("xoxb", "sign-secret");
        var bridge = new SlackBridge(new HttpClient(new CapturingHandler()), options);

        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var body = "{\"type\":\"url_verification\",\"challenge\":\"abc123\"}";
        var sig = "v0=" + HmacHex("sign-secret", $"v0:{ts}:{body}");

        var result = await bridge.HandleWebhookAsync(body, ts, sig);

        Assert.Equal(ChannelWebhookStatus.Ok, result.Status);
        Assert.Equal("abc123", result.Body);
    }

    [Fact]
    public async Task Slack_inbound_with_bad_signature_is_unauthorized_and_does_not_raise()
    {
        var bridge = new SlackBridge(new HttpClient(new CapturingHandler()), new SlackBridgeOptions("xoxb", "sign-secret"));
        var raised = false;
        bridge.OnInboundMessage += _ => { raised = true; return Task.CompletedTask; };

        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var body = "{\"type\":\"event_callback\",\"event\":{\"type\":\"message\",\"channel\":\"C1\",\"text\":\"allow\"}}";

        var result = await bridge.HandleWebhookAsync(body, ts, "v0=deadbeef");

        Assert.Equal(ChannelWebhookStatus.Unauthorized, result.Status);
        Assert.False(raised);
    }

    // -------------------------------------------------------------- Discord ----

    [Fact]
    public async Task Discord_send_posts_create_message_with_bot_auth_and_content()
    {
        var handler = new CapturingHandler();
        var options = new DiscordBridgeOptions("bot-token", "0".PadLeft(64, '0'), "https://discord.test/api/v10");
        var bridge = new DiscordBridge(new HttpClient(handler), options);

        await bridge.SendAsync("999888777", "deploy?", PermissionContext);

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("https://discord.test/api/v10/channels/999888777/messages", handler.RequestUri?.ToString());
        Assert.Equal("Bot", handler.Authorization?.Scheme);
        Assert.Equal("bot-token", handler.Authorization?.Parameter);
        using var json = JsonDocument.Parse(handler.Body!);
        Assert.Equal("deploy?", json.RootElement.GetProperty("content").GetString());
    }

    // Discord inbound uses an Ed25519 interaction signature. .NET 10's BCL has no standalone Ed25519 verify
    // primitive, and repo policy forbids hand-rolled curve math / obscure third-party crypto — so inbound
    // verification is DEFERRED behind a seam that refuses every request rather than trust an unverifiable one.
    [Fact]
    public async Task Discord_inbound_is_deferred_and_refuses_until_bcl_ed25519_is_available()
    {
        var bridge = new DiscordBridge(new HttpClient(new CapturingHandler()), new DiscordBridgeOptions("bot", "pubkey"));
        var result = await bridge.HandleWebhookAsync("{\"type\":1}", "12345", " signature ");
        Assert.Equal(ChannelWebhookStatus.Unauthorized, result.Status);
    }

    [Fact(Skip = "Discord inbound Ed25519 verification (PING->PONG, interaction->inbound) deferred: no standalone Ed25519 in the .NET 10 BCL.")]
    public void Discord_ping_responds_pong()
    {
        // Re-enable once a first-party/BCL Ed25519 verify is available; the parse + PONG + inbound mapping slot
        // behind the existing DiscordBridge.HandleWebhookAsync seam without an interface change.
    }

    // ------------------------------------------------------------- WhatsApp ----

    [Fact]
    public async Task WhatsApp_send_posts_to_phone_number_messages_with_bearer_and_body()
    {
        var handler = new CapturingHandler();
        var options = new WhatsAppBridgeOptions("PN123", "access-token", "verify", "app-secret", "https://graph.test/v21.0");
        var bridge = new WhatsAppBridge(new HttpClient(handler), options);

        await bridge.SendAsync("15551234567", "your turn", PermissionContext);

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("https://graph.test/v21.0/PN123/messages", handler.RequestUri?.ToString());
        Assert.Equal("Bearer", handler.Authorization?.Scheme);
        Assert.Equal("access-token", handler.Authorization?.Parameter);
        using var json = JsonDocument.Parse(handler.Body!);
        Assert.Equal("whatsapp", json.RootElement.GetProperty("messaging_product").GetString());
        Assert.Equal("15551234567", json.RootElement.GetProperty("to").GetString());
        Assert.Equal("your turn", json.RootElement.GetProperty("text").GetProperty("body").GetString());
    }

    [Fact]
    public void WhatsApp_signature_accepts_correct_and_rejects_tampered_and_wrong_secret()
    {
        const string appSecret = "meta-app-secret";
        const string body = "{\"object\":\"whatsapp_business_account\"}";
        var good = "sha256=" + HmacHex(appSecret, body);

        Assert.True(WhatsAppBridge.VerifySignature(appSecret, body, good));
        Assert.False(WhatsAppBridge.VerifySignature(appSecret, body + "x", good)); // tampered body
        Assert.False(WhatsAppBridge.VerifySignature("wrong-secret", body, good));   // wrong secret
        Assert.False(WhatsAppBridge.VerifySignature(appSecret, body, "sha256=00"));  // malformed/mismatched
    }

    [Fact]
    public async Task WhatsApp_inbound_message_verified_raises_inbound_with_from_and_text()
    {
        var options = new WhatsAppBridgeOptions("PN", "token", "verify", "app-secret");
        var bridge = new WhatsAppBridge(new HttpClient(new CapturingHandler()), options);
        InboundChannelMessage? received = null;
        bridge.OnInboundMessage += m => { received = m; return Task.CompletedTask; };

        var body = "{\"entry\":[{\"changes\":[{\"value\":{\"messages\":[{\"from\":\"15559999999\",\"type\":\"text\",\"text\":{\"body\":\"allow\"}}]}}]}]}";
        var sig = "sha256=" + HmacHex("app-secret", body);

        var result = await bridge.HandleWebhookAsync(body, sig);

        Assert.Equal(ChannelWebhookStatus.Ok, result.Status);
        Assert.NotNull(received);
        Assert.Equal("whatsapp", received!.BridgeId);
        Assert.Equal("15559999999", received.ExternalChatId);
        Assert.Equal("allow", received.Text);
    }

    [Fact]
    public async Task WhatsApp_inbound_with_bad_signature_is_unauthorized_and_does_not_raise()
    {
        var bridge = new WhatsAppBridge(new HttpClient(new CapturingHandler()), new WhatsAppBridgeOptions("PN", "token", "verify", "app-secret"));
        var raised = false;
        bridge.OnInboundMessage += _ => { raised = true; return Task.CompletedTask; };

        var body = "{\"entry\":[{\"changes\":[{\"value\":{\"messages\":[{\"from\":\"1\",\"type\":\"text\",\"text\":{\"body\":\"allow\"}}]}}]}]}";

        var result = await bridge.HandleWebhookAsync(body, "sha256=deadbeef");

        Assert.Equal(ChannelWebhookStatus.Unauthorized, result.Status);
        Assert.False(raised);
    }

    [Fact]
    public void WhatsApp_get_verify_echoes_challenge_only_for_matching_token()
    {
        var options = new WhatsAppBridgeOptions("PN", "token", "the-verify-token", "app-secret");
        var bridge = new WhatsAppBridge(new HttpClient(new CapturingHandler()), options);

        Assert.Equal("nonce-42", bridge.VerifyChallenge("subscribe", "the-verify-token", "nonce-42"));
        Assert.Null(bridge.VerifyChallenge("subscribe", "wrong-token", "nonce-42"));
        Assert.Null(bridge.VerifyChallenge("unsubscribe", "the-verify-token", "nonce-42"));
    }

    // -------------------------------------------------------- config-gating ----

    [Fact]
    public void Bridges_are_only_configurable_when_their_credential_block_is_present()
    {
        var empty = new ConfigurationBuilder().Build();
        Assert.Null(SlackBridgeOptions.FromConfiguration(empty));
        Assert.Null(DiscordBridgeOptions.FromConfiguration(empty));
        Assert.Null(WhatsAppBridgeOptions.FromConfiguration(empty));

        var configured = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agnes:Channels:Slack:BotToken"] = "xoxb",
            ["Agnes:Channels:Slack:SigningSecret"] = "s",
            ["Agnes:Channels:Discord:BotToken"] = "bot",
            ["Agnes:Channels:Discord:PublicKey"] = "pub",
            ["Agnes:Channels:WhatsApp:PhoneNumberId"] = "PN",
            ["Agnes:Channels:WhatsApp:AccessToken"] = "at",
            ["Agnes:Channels:WhatsApp:VerifyToken"] = "vt",
            ["Agnes:Channels:WhatsApp:AppSecret"] = "as",
        }).Build();

        Assert.NotNull(SlackBridgeOptions.FromConfiguration(configured));
        Assert.NotNull(DiscordBridgeOptions.FromConfiguration(configured));
        Assert.NotNull(WhatsAppBridgeOptions.FromConfiguration(configured));
    }

    [Fact]
    public void Partial_credential_block_still_gates_the_bridge_off()
    {
        // Slack token without the signing secret must NOT produce usable options — a half-configured bridge is
        // as good as unconfigured, so an inbound webhook can never be trusted.
        var partial = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agnes:Channels:Slack:BotToken"] = "xoxb",
        }).Build();
        Assert.Null(SlackBridgeOptions.FromConfiguration(partial));

        var whatsAppPartial = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agnes:Channels:WhatsApp:PhoneNumberId"] = "PN",
            ["Agnes:Channels:WhatsApp:AccessToken"] = "at",
            // missing VerifyToken + AppSecret
        }).Build();
        Assert.Null(WhatsAppBridgeOptions.FromConfiguration(whatsAppPartial));
    }
}
