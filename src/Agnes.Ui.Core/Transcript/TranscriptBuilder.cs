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

            case ToolCallEvent tc when tc.Title is "TaskCreate" or "TaskUpdate" or "TodoWrite":
                // Claude's task-list tools drive the plan/tasks panel, not a noisy tool row.
                ApplyTaskTool(tc, agentId);
                break;

            case ToolCallEvent tc:
                // Claude's subagent tool ("Agent"; "Task" on older builds) also registers in the agent
                // tree — but still renders as a tool row so its result stays readable.
                if (tc.Title is "Agent" or "Task")
                {
                    SubagentAdded?.Invoke(new SubagentStartedEvent(tc.ToolCallId, SubagentName(tc)));
                }

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

            case NoticeEvent notice:
                CloseBubble();
                Items.Add(new NoticeItem(notice.Message, notice.IsError) { AgentId = agentId });
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

    // ---- Claude task-list tools (TaskCreate/TaskUpdate/TodoWrite) → the plan/tasks panel ----
    private readonly List<TaskRow> _tasks = [];

    private sealed class TaskRow { public string Id = ""; public string Content = ""; public string Status = "pending"; }

    private void ApplyTaskTool(ToolCallEvent tc, string? agentId)
    {
        var input = string.Concat(tc.Content.Select(TextOf));
        switch (tc.Title)
        {
            case "TaskCreate":
                _tasks.Add(new TaskRow
                {
                    Id = (_tasks.Count + 1).ToString(),   // TaskUpdate references sequential ids ("1","2",…)
                    Content = JsonField(input, "subject") ?? JsonField(input, "description") ?? "task",
                    Status = "pending",
                });
                break;

            case "TaskUpdate":
                var id = JsonField(input, "taskId");
                var row = _tasks.FirstOrDefault(t => t.Id == id);
                if (row is not null)
                {
                    row.Status = JsonField(input, "status") ?? "in_progress";
                }

                break;

            case "TodoWrite":
                // Older Claude sends the whole list each time: replace it.
                _tasks.Clear();
                foreach (var (content, status) in ParseTodos(input))
                {
                    _tasks.Add(new TaskRow { Id = (_tasks.Count + 1).ToString(), Content = content, Status = status });
                }

                break;
        }

        var entries = _tasks.Select(t => new PlanEntry(t.Content, t.Status)).ToList();
        if (_plan is null)
        {
            _plan = new PlanItemView { Entries = entries, AgentId = agentId };
            Items.Add(_plan);
        }
        else
        {
            _plan.Entries = entries;
        }
    }

    private static string SubagentName(ToolCallEvent tc)
    {
        var input = string.Concat(tc.Content.Select(TextOf));
        return JsonField(input, "description") ?? JsonField(input, "subagent_type") ?? "subagent";
    }

    // Truncation-tolerant single-field extraction (the tool-input summary may be clipped).
    private static string? JsonField(string source, string field)
    {
        var m = System.Text.RegularExpressions.Regex.Match(source, "\"" + field + "\"\\s*:\\s*\"([^\"]*)\"");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static List<(string Content, string Status)> ParseTodos(string input)
    {
        var result = new List<(string, string)>();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(input);
            if (doc.RootElement.TryGetProperty("todos", out var todos) && todos.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var t in todos.EnumerateArray())
                {
                    var content = t.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
                    var status = t.TryGetProperty("status", out var s) ? s.GetString() ?? "pending" : "pending";
                    if (content.Length > 0)
                    {
                        result.Add((content, status));
                    }
                }
            }
        }
        catch
        {
            // best-effort — a clipped/odd todo payload just yields no tasks.
        }

        return result;
    }

    private static string TextOf(ContentBlock content) => content switch
    {
        TextContent t => t.Text,
        ImageContent => "[image]",
        ResourceLinkContent r => r.Name ?? r.Uri,
        DiffContent d => UnifiedDiff.Format(d.Path, d.OldText, d.NewText),
        _ => string.Empty,
    };
}
