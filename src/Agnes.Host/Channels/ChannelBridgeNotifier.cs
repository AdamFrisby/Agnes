using Agnes.Abstractions;
using Agnes.Abstractions.Events;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Channels;

/// <summary>
/// The OUTBOUND half of channel bridges: watches the event spine and, when a session raises a permission
/// request, pushes a describing message to every chat linked on every registered bridge.
/// <para>
/// It observes <see cref="BeforeAgentEventEvent"/> rather than <see cref="PermissionRequestedEvent"/>
/// directly because that is the single spine event that pairs a <see cref="SessionEvent"/> with the session
/// id it belongs to — the notifier needs the session id both to describe the request and to let an inbound
/// reply target the right session. It is a pure observer: it never cancels the event or changes the action's
/// outcome (a bridge send failing is swallowed per-bridge), so it can't affect whether the request reaches
/// clients. Adding a new <see cref="IChannelBridge"/> needs no change here — it enumerates the registry.
/// </para>
/// NOTE: when the shared push-notification layer lands, the set of "worth acting on" moments (turn-ready,
/// permission requested, user-action requested) becomes one trigger set that both push and bridges consume;
/// bridges would then be a delivery target of that layer rather than subscribing to the spine themselves.
/// Until then this subscribes to the spine directly, and delivers independently of any push path.
/// </summary>
public sealed class ChannelBridgeNotifier : IEventObserver<BeforeAgentEventEvent>, IDisposable
{
    private readonly IPluginRegistry<IChannelBridge> _bridges;
    private readonly ChannelLinkStore _links;
    private readonly ChannelPromptTracker _prompts;
    private readonly ILogger<ChannelBridgeNotifier>? _logger;
    private readonly IDisposable _subscription;

    public ChannelBridgeNotifier(
        IEventBus bus,
        IPluginRegistry<IChannelBridge> bridges,
        ChannelLinkStore links,
        ChannelPromptTracker prompts,
        ILogger<ChannelBridgeNotifier>? logger = null)
    {
        _bridges = bridges;
        _links = links;
        _prompts = prompts;
        _logger = logger;
        _subscription = bus.Observe(this);
    }

    public async ValueTask ObserveAsync(BeforeAgentEventEvent evt, CancellationToken cancellationToken = default)
    {
        if (evt.Event is not PermissionRequestedEvent request)
        {
            return; // only permission requests are actionable from a chat for this first pass.
        }

        var allow = request.Options.FirstOrDefault(o => o.Kind is PermissionOptionKind.AllowOnce or PermissionOptionKind.AllowAlways)?.OptionId;
        var deny = request.Options.FirstOrDefault(o => o.Kind is PermissionOptionKind.RejectOnce or PermissionOptionKind.RejectAlways)?.OptionId;
        var context = new ChannelBridgeContext(evt.SessionId, request.RequestId, ChannelBridgeEventKind.PermissionRequest);
        var message = $"Permission requested in session {evt.SessionId}: {request.Title}. Reply \"allow\" to approve or \"deny\" to reject.";

        foreach (var bridge in _bridges.All)
        {
            foreach (var link in _links.ListForBridge(bridge.Id))
            {
                // Remember what this chat was asked so its bare "allow"/"deny" reply resolves to this request.
                _prompts.Record(bridge.Id, link.ExternalChatId, new ChannelPendingPrompt(evt.SessionId, request.RequestId, allow, deny));
                try
                {
                    await bridge.SendAsync(link.ExternalChatId, message, context, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Independent delivery: one bridge/chat failing must not stop the others (or any push path).
                    _logger?.LogWarning(ex, "Channel bridge {Bridge} failed to deliver to chat {Chat}", bridge.Id, link.ExternalChatId);
                }
            }
        }
    }

    public void Dispose() => _subscription.Dispose();
}
