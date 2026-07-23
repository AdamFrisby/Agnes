namespace Agnes.Abstractions;

/// <summary>
/// A channel bridge (e.g. Telegram, Slack) lets a user act on a waiting session from a chat app they
/// already have open — approve a permission request, or send a quick reply — without switching to an Agnes
/// client. See <c>.ideas/extensibility/04-channel-bridges.md</c>.
/// <para>
/// It is a specialization of the (not-yet-built) notification-channel idea: outbound it delivers the same
/// "worth acting on" moments push notifications would (today it subscribes to the event spine directly —
/// see <c>ChannelBridgeNotifier</c>); inbound it adds a reply path push notifications don't have. An inbound
/// message is only trust-worthy once the external chat id has been explicitly linked to an Agnes identity
/// (see the host's <c>ChannelLinkStore</c>) — knowing a chat id is never, on its own, authorization.
/// </para>
/// Each implementation owns its own transport (webhook vs. long-poll, formatting, rate limits) behind this
/// common interface; the linking/authorization and event-triggering logic stays shared in the host.
/// </summary>
public interface IChannelBridge
{
    /// <summary>Stable id for this bridge (e.g. <c>"telegram"</c>), used as the plugin-registry key and the
    /// first half of a link's composite key.</summary>
    string Id { get; }

    /// <summary>Delivers <paramref name="message"/> to the external chat, carrying the originating session
    /// context so the bridge can format/route it. Purely outbound; the reply comes back via
    /// <see cref="OnInboundMessage"/>.</summary>
    Task SendAsync(string externalChatId, string message, ChannelBridgeContext context, CancellationToken ct = default);

    /// <summary>Inbound: the bridge's own webhook/poll handler raises this to hand a received chat message to
    /// the host, which resolves it against the link store and (only if authorized) routes it as if it came
    /// from a paired client. Null when nothing is subscribed.</summary>
    event Func<InboundChannelMessage, Task>? OnInboundMessage;
}

/// <summary>Which spine moment triggered an outbound bridge message — the same set that would drive a push
/// notification. Kept small and typed; a bridge switches formatting/affordances on it.</summary>
public enum ChannelBridgeEventKind
{
    /// <summary>A tool-call permission request is waiting for an allow/deny decision.</summary>
    PermissionRequest,

    /// <summary>An agent turn finished (informational; no reply required).</summary>
    TurnEnded,

    /// <summary>The agent asked a structured question.</summary>
    Question,
}

/// <summary>The session context an outbound bridge message refers to, so a reply can be routed back to the
/// exact session and request. <see cref="RequestId"/> is null for kinds that carry no answerable request.</summary>
public sealed record ChannelBridgeContext(string SessionId, string? RequestId, ChannelBridgeEventKind Kind);

/// <summary>A message a bridge received from an external chat. <see cref="RequestId"/> is optional — most
/// chat replies ("allow") don't name a request, so the host resolves the reply against the most recent
/// prompt it sent to that chat.</summary>
public sealed record InboundChannelMessage(string BridgeId, string ExternalChatId, string Text, string? RequestId = null);
