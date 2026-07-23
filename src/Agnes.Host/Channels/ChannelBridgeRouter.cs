using Agnes.Abstractions;
using Agnes.Host.Sessions;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Channels;

/// <summary>
/// The INBOUND half of channel bridges: subscribes to every registered bridge's
/// <see cref="IChannelBridge.OnInboundMessage"/> and decides whether a received chat message is authorized
/// to act on a session.
/// <para>
/// Authorization is the whole point. A message is only acted on if its <c>(bridgeId, chatId)</c> resolves to
/// a link in the <see cref="ChannelLinkStore"/>; an unlinked (or since-unlinked) chat id is anonymous and is
/// rejected with a log notice — it can neither approve a permission nor steer a session. An affirmative from
/// a LINKED chat is funneled through the exact same <see cref="SessionManager.RespondPermissionAsync"/> path
/// a paired client uses, so an inbound approval gets identical scrutiny (and the same spine hooks) as a
/// device approval — this router never touches the live agent directly.
/// </para>
/// </summary>
public sealed class ChannelBridgeRouter : IDisposable
{
    private static readonly HashSet<string> Affirmatives = new(StringComparer.OrdinalIgnoreCase) { "allow", "yes", "y", "approve", "ok", "okay" };
    private static readonly HashSet<string> Negatives = new(StringComparer.OrdinalIgnoreCase) { "deny", "no", "n", "reject", "decline" };

    private readonly IPluginRegistry<IChannelBridge> _bridges;
    private readonly ChannelLinkStore _links;
    private readonly ChannelPromptTracker _prompts;
    private readonly SessionManager _sessions;
    private readonly ILogger<ChannelBridgeRouter>? _logger;
    private readonly List<(IChannelBridge Bridge, Func<InboundChannelMessage, Task> Handler)> _subscriptions = [];

    public ChannelBridgeRouter(
        IPluginRegistry<IChannelBridge> bridges,
        ChannelLinkStore links,
        ChannelPromptTracker prompts,
        SessionManager sessions,
        ILogger<ChannelBridgeRouter>? logger = null)
    {
        _bridges = bridges;
        _links = links;
        _prompts = prompts;
        _sessions = sessions;
        _logger = logger;

        // Bind to the bridges known at construction. A bridge added later (via the mutable registry) can be
        // bound by re-running Subscribe over the delta; the core wiring is unchanged either way.
        foreach (var bridge in _bridges.All)
        {
            Subscribe(bridge);
        }
    }

    /// <summary>Wires a bridge's inbound event to this router. Idempotent per bridge instance.</summary>
    public void Subscribe(IChannelBridge bridge)
    {
        if (_subscriptions.Any(s => ReferenceEquals(s.Bridge, bridge)))
        {
            return;
        }

        Func<InboundChannelMessage, Task> handler = HandleAsync;
        bridge.OnInboundMessage += handler;
        _subscriptions.Add((bridge, handler));
    }

    private async Task HandleAsync(InboundChannelMessage message)
    {
        var link = _links.Resolve(message.BridgeId, message.ExternalChatId);
        if (link is null)
        {
            // Unlinked (or since-unlinked): anonymous. Never acted on — knowing a chat id is not authorization.
            _logger?.LogWarning(
                "Rejected inbound message from unlinked {Bridge} chat {Chat}: not authorized",
                message.BridgeId, message.ExternalChatId);
            return;
        }

        var text = message.Text.Trim();
        var affirmative = Affirmatives.Contains(text);
        var negative = Negatives.Contains(text);
        if (!affirmative && !negative)
        {
            // A linked chat sent something that isn't an approve/deny. Free-text session steering is a
            // follow-up; for this pass we only act on the permission decision, so leave the prompt pending.
            _logger?.LogInformation(
                "Ignoring non-decision message from linked {Bridge} chat {Chat}", message.BridgeId, message.ExternalChatId);
            return;
        }

        var pending = _prompts.TryTake(message.BridgeId, message.ExternalChatId);
        if (pending is null)
        {
            _logger?.LogInformation(
                "No outstanding prompt for linked {Bridge} chat {Chat}; nothing to answer", message.BridgeId, message.ExternalChatId);
            return;
        }

        var optionId = affirmative ? pending.AllowOptionId : pending.DenyOptionId;
        if (optionId is null)
        {
            _logger?.LogWarning(
                "Prompt for {Bridge} chat {Chat} had no matching option for the reply", message.BridgeId, message.ExternalChatId);
            return;
        }

        // Identical path to a paired-device approval: dispatches the Before* spine hook and forwards to the
        // live session. The reply is scoped to the linked device identity ({Device}).
        _logger?.LogInformation(
            "Linked {Bridge} chat {Chat} (device {Device}) answered {Request} with {Option}",
            message.BridgeId, message.ExternalChatId, link.DeviceId, pending.RequestId, optionId);
        await _sessions.RespondPermissionAsync(pending.SessionId, pending.RequestId, optionId).ConfigureAwait(false);
    }

    public void Dispose()
    {
        foreach (var (bridge, handler) in _subscriptions)
        {
            bridge.OnInboundMessage -= handler;
        }

        _subscriptions.Clear();
    }
}
