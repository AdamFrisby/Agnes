namespace Agnes.Host.Channels;

/// <summary>The coarse outcome of handling an inbound webhook, mapped by the endpoint layer to an HTTP status.
/// Kept transport-agnostic so a bridge's webhook handler stays unit-testable without ASP.NET.</summary>
public enum ChannelWebhookStatus
{
    /// <summary>The request was accepted (signature valid); any payload was processed.</summary>
    Ok,

    /// <summary>Signature verification failed — the request is not trusted and MUST be rejected (401).</summary>
    Unauthorized,

    /// <summary>The request was authentic but malformed (400).</summary>
    BadRequest,
}

/// <summary>
/// The result of a bridge handling an inbound webhook POST: a status plus an optional body the endpoint should
/// echo back (Slack's url-verification challenge, Discord's PONG). Immutable; the endpoint maps it to an
/// <c>IResult</c>. Any inbound chat message is delivered as a side effect (the bridge raises
/// <see cref="Agnes.Abstractions.IChannelBridge.OnInboundMessage"/> before returning), not carried here.
/// </summary>
public sealed record ChannelWebhookResult(
    ChannelWebhookStatus Status,
    string? Body = null,
    string ContentType = "application/json")
{
    /// <summary>Accepted with no reply body (HTTP 200, empty).</summary>
    public static readonly ChannelWebhookResult Ok = new(ChannelWebhookStatus.Ok);

    /// <summary>Signature verification failed (HTTP 401).</summary>
    public static readonly ChannelWebhookResult Unauthorized = new(ChannelWebhookStatus.Unauthorized);

    /// <summary>Authentic but malformed (HTTP 400).</summary>
    public static readonly ChannelWebhookResult BadRequest = new(ChannelWebhookStatus.BadRequest);

    /// <summary>Accepted, echoing <paramref name="body"/> back to the caller (e.g. Slack challenge, Discord PONG).</summary>
    public static ChannelWebhookResult Echo(string body, string contentType = "application/json")
        => new(ChannelWebhookStatus.Ok, body, contentType);
}
