using System.Collections.ObjectModel;
using Agnes.Abstractions;
using Agnes.Ui.Core.Diff;

namespace Agnes.Ui.Core.Transcript;

/// <summary>
/// Folds the raw <see cref="SessionEvent"/> stream into a live list of display items:
/// consecutive message chunks coalesce into one bubble, tool calls update in place, the
/// plan is kept current, and permission requests track their resolution. UI-agnostic and
/// unit-tested; the same logic drives every frontend.
/// </summary>
public sealed class TranscriptBuilder
{
    private readonly Dictionary<string, ToolCallItem> _tools = new();
    private readonly Dictionary<string, PermissionItem> _permissions = new();
    private MessageBubbleItem? _openBubble;
    private PlanItemView? _plan;

    public ObservableCollection<TranscriptItem> Items { get; } = [];

    /// <summary>The unanswered permission request, if any.</summary>
    public PermissionItem? PendingPermission { get; private set; }

    public event Action? PendingPermissionChanged;

    /// <summary>Raised when a subagent is announced (for the session's agent tree).</summary>
    public event Action<SubagentStartedEvent>? SubagentAdded;

    public void Apply(SessionEvent @event)
    {
        var agentId = @event.AgentId;
        switch (@event)
        {
            case MessageChunkEvent m:
                AppendToBubble(m.Role, isThought: false, TextOf(m.Content), agentId);
                break;

            case ThoughtChunkEvent t:
                AppendToBubble(MessageRole.Assistant, isThought: true, TextOf(t.Content), agentId);
                break;

            case SubagentStartedEvent sub:
                SubagentAdded?.Invoke(sub);
                break;

            case ToolCallEvent tc:
                CloseBubble();
                var tool = new ToolCallItem(tc.ToolCallId, tc.Title, tc.Kind, tc.Status)
                {
                    StartedAt = tc.Timestamp,
                    Detail = string.Concat(tc.Content.Select(TextOf)),
                    AgentId = agentId,
                };
                if (tc.Status is ToolCallStatus.Completed or ToolCallStatus.Failed)
                {
                    tool.CompletedAt = tc.Timestamp;
                }

                _tools[tc.ToolCallId] = tool;
                Items.Add(tool);
                break;

            case ToolCallUpdateEvent u when _tools.TryGetValue(u.ToolCallId, out var existing):
                if (u.Status is { } status)
                {
                    existing.Status = status;
                    if (status is ToolCallStatus.Completed or ToolCallStatus.Failed)
                    {
                        existing.CompletedAt = u.Timestamp;
                    }
                }

                if (u.Content is { } content)
                {
                    existing.Detail = string.Concat(content.Select(TextOf));
                }

                break;

            case PlanEvent p:
                if (_plan is null)
                {
                    _plan = new PlanItemView { Entries = p.Entries, AgentId = agentId };
                    Items.Add(_plan);
                }
                else
                {
                    _plan.Entries = p.Entries;
                }

                break;

            case PermissionRequestedEvent pr:
                CloseBubble();
                _tools.TryGetValue(pr.ToolCallId, out var linkedTool);
                var permission = new PermissionItem(pr.RequestId, pr.Title, pr.Options, linkedTool?.Kind, linkedTool?.Title) { AgentId = agentId };
                _permissions[pr.RequestId] = permission;
                Items.Add(permission);
                PendingPermission = permission;
                PendingPermissionChanged?.Invoke();
                break;

            case PermissionResolvedEvent rr when _permissions.TryGetValue(rr.RequestId, out var item):
                item.Resolved = true;
                item.ResolutionText = rr.Outcome.ToString();
                if (PendingPermission == item)
                {
                    PendingPermission = null;
                    PendingPermissionChanged?.Invoke();
                }

                break;

            case ModeChangedEvent mode:
                Items.Add(new NoticeItem($"Mode: {mode.ModeId}") { AgentId = agentId });
                break;

            case AgentErrorEvent err:
                CloseBubble();
                Items.Add(new NoticeItem(err.Message, isError: true) { AgentId = agentId });
                break;

            case TurnEndedEvent:
                CloseBubble();
                break;
        }
    }

    private void AppendToBubble(MessageRole role, bool isThought, string text, string? agentId)
    {
        if (_openBubble is null || _openBubble.Role != role || _openBubble.IsThought != isThought || _openBubble.AgentId != agentId)
        {
            _openBubble = new MessageBubbleItem(role, isThought) { AgentId = agentId };
            Items.Add(_openBubble);
        }

        _openBubble.Append(text);
    }

    private void CloseBubble() => _openBubble = null;

    private static string TextOf(ContentBlock content) => content switch
    {
        TextContent t => t.Text,
        ImageContent => "[image]",
        ResourceLinkContent r => r.Name ?? r.Uri,
        DiffContent d => UnifiedDiff.Format(d.Path, d.OldText, d.NewText),
        _ => string.Empty,
    };
}
