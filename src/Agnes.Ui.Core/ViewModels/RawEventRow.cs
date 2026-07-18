using Agnes.Abstractions;

namespace Agnes.Ui.Core.ViewModels;

/// <summary>One row in the raw-event inspector: the underlying <see cref="SessionEvent"/> as data.</summary>
public sealed class RawEventRow
{
    public RawEventRow(SessionEvent @event)
    {
        Sequence = @event.Sequence;
        Time = @event.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");
        Kind = Name(@event);
        Summary = Describe(@event);
    }

    public long Sequence { get; }
    public string Time { get; }
    public string Kind { get; }
    public string Summary { get; }

    private static string Name(SessionEvent e)
    {
        var n = e.GetType().Name;
        return n.EndsWith("Event", StringComparison.Ordinal) ? n[..^"Event".Length] : n;
    }

    private static string Describe(SessionEvent e) => e switch
    {
        MessageChunkEvent m => $"{m.Role}: {Short(TextOf(m.Content))}",
        ThoughtChunkEvent t => Short(TextOf(t.Content)),
        ToolCallEvent tc => $"{tc.Kind} {tc.Title} [{tc.Status}]",
        ToolCallUpdateEvent u => $"{u.ToolCallId} → {u.Status?.ToString() ?? "…"}",
        PlanEvent p => $"{p.Entries.Count} step(s)",
        PermissionRequestedEvent pr => pr.Title,
        PermissionResolvedEvent rr => $"{rr.Outcome} ({rr.OptionId})",
        ModeChangedEvent mode => mode.ModeId,
        TurnEndedEvent te => te.Reason.ToString(),
        AgentErrorEvent err => err.Message,
        TerminalOutputEvent to => Short(to.Data),
        SessionStartedEvent s => s.AgentSessionId,
        _ => string.Empty,
    };

    private static string TextOf(ContentBlock content) => content switch
    {
        TextContent t => t.Text,
        ImageContent => "[image]",
        ResourceLinkContent r => r.Name ?? r.Uri,
        DiffContent d => $"[diff] {d.Path}",
        _ => string.Empty,
    };

    private static string Short(string s)
    {
        s = s.ReplaceLineEndings(" ").Trim();
        return s.Length > 90 ? s[..90] + "…" : s;
    }
}
