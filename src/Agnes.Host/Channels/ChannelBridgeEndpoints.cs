using System.IO;
using Agnes.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Agnes.Host.Channels;

/// <summary>
/// The inbound webhook surface for the real channel bridges: one minimal-API endpoint per bridge that reads the
/// raw request, hands it to the (config-gated) bridge for signature verification + parsing, and echoes back
/// whatever handshake the platform requires. A bridge that isn't configured isn't registered, so its endpoint
/// returns 404 — the surface exists but is inert until credentials are supplied. Signature verification and
/// authorization live in the bridge/router, not here; this layer only shuffles bytes and status codes.
/// </summary>
public static class ChannelBridgeEndpoints
{
    public static void MapChannelBridgeEndpoints(this WebApplication app, IPluginRegistry<IChannelBridge> bridges)
    {
        // Slack Events API: URL-verification challenge + message events, signed v0 (HMAC-SHA256).
        app.MapPost("/channels/slack/events", async (HttpContext ctx) =>
        {
            if (bridges.Find("slack") is not SlackBridge slack)
            {
                return Results.NotFound();
            }

            var body = await ReadBodyAsync(ctx).ConfigureAwait(false);
            var result = await slack.HandleWebhookAsync(
                body,
                ctx.Request.Headers["X-Slack-Request-Timestamp"].ToString(),
                ctx.Request.Headers["X-Slack-Signature"].ToString()).ConfigureAwait(false);
            return ToResult(result);
        });

        // Discord interactions: Ed25519-signed. Inbound is DEFERRED (no BCL Ed25519) — the endpoint is wired but
        // the bridge refuses every request until verification is available. See DiscordBridge.
        app.MapPost("/channels/discord/interactions", async (HttpContext ctx) =>
        {
            if (bridges.Find("discord") is not DiscordBridge discord)
            {
                return Results.NotFound();
            }

            var body = await ReadBodyAsync(ctx).ConfigureAwait(false);
            var result = await discord.HandleWebhookAsync(
                body,
                ctx.Request.Headers["X-Signature-Timestamp"].ToString(),
                ctx.Request.Headers["X-Signature-Ed25519"].ToString()).ConfigureAwait(false);
            return ToResult(result);
        });

        // WhatsApp (Meta Cloud API): GET verify handshake + POST inbound, signed X-Hub-Signature-256 (HMAC-SHA256).
        app.MapGet("/channels/whatsapp/webhook", (HttpContext ctx) =>
        {
            if (bridges.Find("whatsapp") is not WhatsAppBridge whatsapp)
            {
                return Results.NotFound();
            }

            var challenge = whatsapp.VerifyChallenge(
                ctx.Request.Query["hub.mode"],
                ctx.Request.Query["hub.verify_token"],
                ctx.Request.Query["hub.challenge"]);
            return challenge is null ? Results.Forbid() : Results.Text(challenge, "text/plain");
        });

        app.MapPost("/channels/whatsapp/webhook", async (HttpContext ctx) =>
        {
            if (bridges.Find("whatsapp") is not WhatsAppBridge whatsapp)
            {
                return Results.NotFound();
            }

            var body = await ReadBodyAsync(ctx).ConfigureAwait(false);
            var result = await whatsapp.HandleWebhookAsync(
                body, ctx.Request.Headers["X-Hub-Signature-256"].ToString()).ConfigureAwait(false);
            return ToResult(result);
        });
    }

    private static async Task<string> ReadBodyAsync(HttpContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.Body);
        return await reader.ReadToEndAsync(ctx.RequestAborted).ConfigureAwait(false);
    }

    private static IResult ToResult(ChannelWebhookResult result) => result.Status switch
    {
        ChannelWebhookStatus.Ok => result.Body is null ? Results.Ok() : Results.Text(result.Body, result.ContentType),
        ChannelWebhookStatus.Unauthorized => Results.Unauthorized(),
        _ => Results.BadRequest(),
    };
}
