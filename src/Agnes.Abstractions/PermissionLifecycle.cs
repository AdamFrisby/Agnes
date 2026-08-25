namespace Agnes.Abstractions;

/// <summary>
/// When a permission request stops being answerable.
/// </summary>
/// <remarks>
/// A request is normally closed by a <see cref="PermissionResolvedEvent"/> — someone decided. But an
/// agent can also stop waiting, and when it does, nothing says so: the request simply stays in the log
/// with no resolution, forever. Two ways that happens in practice, both observed live:
///
/// <list type="bullet">
/// <item>The agent asks and proceeds anyway. A CLI running with its own permissions already granted
/// (<c>copilot --allow-all-tools</c>) still announces the request, then runs the tool without waiting —
/// so the very next thing in the log is that tool call finishing.</item>
/// <item>The turn ends. Whatever the agent was going to do with the answer, it is no longer doing it.</item>
/// </list>
///
/// <para>Either way the answer buttons on the card are a lie: pressing them sends a response to a
/// request the agent has forgotten. Worse, the request keeps counting toward "what needs me right now",
/// which is how one session accumulated sixteen approvals that nobody could ever clear — the exact
/// situation someone stepping away from their desk walks back into.</para>
///
/// <para>The rule lives here, once, because both sides need the same answer: the host, which aggregates
/// the cross-session inbox from durable state, and each client, which decides live what a card should
/// offer. It is a pure function of events either side already has, so no new event, protocol field or
/// host round-trip is involved — and, being inference over the log rather than a timer, it gives the
/// same answer on a session replayed weeks later as it did while the session was live.</para>
/// </remarks>
public static class PermissionLifecycle
{
    /// <summary>
    /// Whether <paramref name="e"/> — occurring after a request that is still unresolved — means that
    /// request can no longer be answered. <paramref name="toolCallId"/> is the request's own tool call.
    /// </summary>
    /// <remarks>
    /// Only ever asked about requests with no resolution. The ordinary sequence — ask, allow, tool runs,
    /// tool completes — trips the tool-call test too, and would read as an expiry if it were asked about
    /// a request that had already been answered.
    /// </remarks>
    public static bool Withdraws(SessionEvent e, string toolCallId) => e switch
    {
        // The agent went ahead without us: the call this request was gating has already finished.
        ToolCallUpdateEvent u => u.ToolCallId == toolCallId && IsTerminal(u.Status),
        ToolCallEvent t => t.ToolCallId == toolCallId && IsTerminal(t.Status),

        // The turn is over, so nothing is waiting on an answer any more.
        TurnEndedEvent => true,
        _ => false,
    };

    /// <summary>
    /// The requests in <paramref name="events"/> that were asked, never resolved, and can no longer be
    /// answered. <paramref name="events"/> must be in log order; anything not named here that is
    /// unresolved is genuinely still waiting on a human.
    /// </summary>
    public static IReadOnlySet<string> ExpiredRequests(IEnumerable<SessionEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        var pending = new Dictionary<string, string>(StringComparer.Ordinal); // requestId → toolCallId
        var expired = new HashSet<string>(StringComparer.Ordinal);

        foreach (var e in events)
        {
            switch (e)
            {
                case PermissionRequestedEvent p:
                    pending[p.RequestId] = p.ToolCallId;
                    break;

                case PermissionResolvedEvent r:
                    // Decided, so it was never expired — and a late tool completion must not make it so.
                    pending.Remove(r.RequestId);
                    expired.Remove(r.RequestId);
                    break;

                default:
                    foreach (var (requestId, toolCallId) in pending)
                    {
                        if (Withdraws(e, toolCallId))
                        {
                            expired.Add(requestId);
                        }
                    }

                    // Expired requests stay out of `pending`: once withdrawn, later events say nothing new.
                    foreach (var requestId in expired)
                    {
                        pending.Remove(requestId);
                    }

                    break;
            }
        }

        return expired;
    }

    private static bool IsTerminal(ToolCallStatus? status)
        => status is ToolCallStatus.Completed or ToolCallStatus.Failed;
}
