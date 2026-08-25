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
    private readonly Dictionary<string, QuestionItem> _questions = new();
    private MessageBubbleItem? _openBubble;

    public ObservableCollection<TranscriptItem> Items { get; } = [];

    /// <summary>
    /// The agent's plan, once it has one — the single place a plan is assembled, whatever shape it arrived
    /// in. ACP agents send <see cref="PlanEvent"/>; Claude instead drives its task list through the
    /// TodoWrite/TaskCreate/TaskUpdate tools, so anything reading only <see cref="PlanEvent"/> sees no plan
    /// at all from Claude. Both fold into this one view, which is then live: entries are updated in place
    /// as the agent ticks them off, so every surface bound to it follows along.
    /// </summary>
    public PlanItemView? Plan { get; private set; }

    /// <summary>Raised when a plan first appears (its entries thereafter update in place).</summary>
    public event Action? PlanChanged;

    /// <summary>The unanswered permission request, if any.</summary>
    public PermissionItem? PendingPermission { get; private set; }

    public event Action? PendingPermissionChanged;

    /// <summary>The unanswered structured-question set, if any.</summary>
    public QuestionItem? PendingQuestion { get; private set; }

    public event Action? PendingQuestionChanged;

    /// <summary>Raised when a subagent is announced (for the session's agent tree).</summary>
    public event Action<SubagentStartedEvent>? SubagentAdded;

    /// <summary>Raised when a subagent reports that it has finished, so the roster can retire its row.
    /// Separate from the tool call completing: a background subagent's launch call completes in seconds
    /// while the subagent itself runs for minutes, and only the payload knows which is which.</summary>
    public event Action<string>? SubagentFinished;

    public void Apply(SessionEvent @event)
    {
        var agentId = @event.AgentId;
        var before = Items.Count;
        ApplyCore(@event, agentId);
        // Stamp every item this event created with its time and its place in the log (one choke point covers
        // all the cases above): the time drives the scroll-position hint, and the sequence is the stable
        // address a shared link uses to point at this moment.
        for (var i = before; i < Items.Count; i++)
        {
            Items[i].Timestamp = @event.Timestamp;
            Items[i].Sequence = @event.Sequence;
        }
    }

    private void ApplyCore(SessionEvent @event, string? agentId)
    {
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
                else if (IsDelegationTool(tc.Title))
                {
                    // OpenCode's lowercase "task" tool. It says nothing at call time — the subagent's id
                    // and state arrive in the result — so remember the call and decide when that lands.
                    _delegations.Add(tc.ToolCallId);
                }

                CloseBubble();
                // The diff comes from the call's INPUT, captured here at the start: the update that
                // completes the call carries only a confirmation, so this is the sole chance to keep it.
                var tool = new ToolCallItem(tc.ToolCallId, tc.Title, tc.Kind, tc.Status, ToolDiff.For(tc.Kind, tc.Content))
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

                if (_delegations.Contains(u.ToolCallId))
                {
                    ApplyDelegationResult(existing);
                }

                break;

            case PlanEvent p:
                SetPlan(p.Entries, agentId);
                break;

            case PermissionRequestedEvent pr:
                CloseBubble();
                _tools.TryGetValue(pr.ToolCallId, out var linkedTool);
                // Fall back to the linked tool's own target when the agent sent no detail of its own, so
                // the card still says what is about to run rather than only that something is.
                var permission = new PermissionItem(
                    pr.RequestId, pr.Title, pr.Options, linkedTool?.Kind, linkedTool?.Title,
                    pr.Detail ?? linkedTool?.Title) { AgentId = agentId };
                _permissions[pr.RequestId] = permission;
                Items.Add(permission);
                PendingPermission = permission;
                PendingPermissionChanged?.Invoke();
                break;

            case QuestionAskedEvent q:
                CloseBubble();
                var question = new QuestionItem(q.RequestId, q.Questions) { AgentId = agentId };
                _questions[q.RequestId] = question;
                Items.Add(question);
                PendingQuestion = question;
                PendingQuestionChanged?.Invoke();
                break;

            case QuestionAnsweredEvent qa when _questions.TryGetValue(qa.RequestId, out var qItem):
                qItem.Resolved = true;
                if (PendingQuestion == qItem)
                {
                    PendingQuestion = null;
                    PendingQuestionChanged?.Invoke();
                }

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

            case ForkedFromEvent:
                CloseBubble();
                Items.Add(new NoticeItem("Forked from a prior session — the branch continues below.") { AgentId = agentId });
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

        SetPlan(_tasks.Select(t => new PlanEntry(t.Content, t.Status)).ToList(), agentId);
    }

    /// <summary>Creates the plan on first sight, and thereafter updates the same view in place so that
    /// anything already bound to it — the transcript row, the sidebar, a sheet — ticks along with it.</summary>
    private void SetPlan(IReadOnlyList<PlanEntry> entries, string? agentId)
    {
        if (Plan is null)
        {
            Plan = new PlanItemView { Entries = entries, AgentId = agentId };
            Items.Add(Plan);
            PlanChanged?.Invoke();
        }
        else
        {
            Plan.Entries = entries;
        }
    }

    // ---- delegation to a subagent that reports through its tool result (OpenCode's `task`) ----

    private readonly HashSet<string> _delegations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _subagentNames = new(StringComparer.Ordinal);

    /// <summary>Tool names that hand work to a subagent rather than doing it. Claude's own are matched
    /// exactly above (they carry their description in the call); this is the by-shape case.</summary>
    private static bool IsDelegationTool(string title) => string.Equals(title, "task", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Turns a delegating tool call's result into a subagent the roster can show. Three things change on
    /// the row itself, all of which were wrong before: it is a Subagent, not a "Think"; it is named after
    /// the subagent rather than after the tool; and it is still *running* if the payload says so, where
    /// the transport had already called the launch completed. The envelope is replaced by whatever the
    /// subagent actually reported, so the transcript stops showing raw markup addressed to the model.
    /// </summary>
    private void ApplyDelegationResult(ToolCallItem item)
    {
        if (SubagentTaskPayload.TryParse(item.Detail) is not { } task)
        {
            return;
        }

        if (!_subagentNames.TryGetValue(task.TaskId, out var name))
        {
            // OpenCode gives a subagent no description of its own — only an opaque id — so the roster
            // numbers them in the order they appear. The count is derived from the log, which every
            // client replays identically, so the same subagent is "Subagent 3" on all of them.
            name = $"Subagent {_subagentNames.Count + 1}";
            _subagentNames[task.TaskId] = name;
            SubagentAdded?.Invoke(new SubagentStartedEvent(task.TaskId, name));
        }

        item.Kind = ToolKind.Subagent;
        item.Title = name;
        item.AgentId = task.TaskId;
        item.Detail = task.Body;
        item.Status = task.IsRunning ? ToolCallStatus.InProgress : ToolCallStatus.Completed;

        if (task.IsRunning)
        {
            // The launch call is over, but the subagent isn't; a duration here would time the dispatch.
            item.CompletedAt = null;
        }
        else
        {
            SubagentFinished?.Invoke(task.TaskId);
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
        var result = new List<(string Content, string Status)>();
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
            // best-effort — a clipped/odd task-list payload just yields no entries.
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
