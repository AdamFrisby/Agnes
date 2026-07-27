using Agnes.Abstractions;
using Agnes.Ui.Core.Diff;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Agnes.Ui.Core.Transcript;

/// <summary>Base type for a rendered item in a session transcript.</summary>
public abstract class TranscriptItem : ObservableObject
{
    /// <summary>
    /// Identity for scrolling <em>within this client</em>. Freshly generated per item, so it is emphatically
    /// not an address anyone else can use — see <see cref="Sequence"/> for the one that travels.
    /// </summary>
    public string AnchorId { get; } = Guid.NewGuid().ToString("n");

    /// <summary>
    /// The event-log sequence this item was built from, stamped by the builder. Unlike
    /// <see cref="AnchorId"/> this is the same number on every client, which is what makes "look at this
    /// moment" shareable: a link carries the sequence, and the receiving client resolves it back to whichever
    /// item its own render produced. Zero for an item no single event produced.
    /// </summary>
    public long Sequence { get; set; }

    /// <summary>Which agent produced this item: null for the main agent, else a subagent id.</summary>
    public string? AgentId { get; init; }

    /// <summary>When this item's originating event occurred (stamped by the builder). Drives the
    /// scroll-position timestamp hint.</summary>
    public DateTimeOffset Timestamp { get; set; }
}

/// <summary>A coalesced run of message (or thought) chunks from one speaker.</summary>
public sealed class MessageBubbleItem : TranscriptItem
{
    private string _text = string.Empty;

    public MessageBubbleItem(MessageRole role, bool isThought)
    {
        Role = role;
        IsThought = isThought;
    }

    public MessageRole Role { get; }
    public bool IsThought { get; }
    public bool IsUser => Role == MessageRole.User && !IsThought;

    /// <summary>Short speaker label for the UI.</summary>
    public string Speaker => IsThought ? "thinking" : IsUser ? "You" : "Agent";

    public string Text
    {
        get => _text;
        set
        {
            if (SetProperty(ref _text, value))
            {
                OnPropertyChanged(nameof(IsLong));
                OnPropertyChanged(nameof(CondensedText));
            }
        }
    }

    public void Append(string text) => Text += text;

    /// <summary>Long assistant messages are condensed in the chat and open in full in the preview.</summary>
    public bool IsLong => !IsUser && !IsThought && Text.Length > 360;

    public string CondensedText => IsLong ? Text[..360].TrimEnd() + " …" : Text;
}

/// <summary>A tool call, updated in place as its status changes.</summary>
public sealed class ToolCallItem : TranscriptItem
{
    private ToolCallStatus _status;
    private string _detail = string.Empty;
    private DateTimeOffset? _completedAt;

    public ToolCallItem(string toolCallId, string title, ToolKind kind, ToolCallStatus status, string? diff = null)
    {
        ToolCallId = toolCallId;
        Title = title;
        Kind = kind;
        _status = status;
        Diff = diff;
        DiffLines = diff is null ? [] : DiffParser.Parse(diff);
        InlineDiffLines = DiffLines.Where(l => !l.IsHunk).ToList();
    }

    public string ToolCallId { get; }

    /// <summary>What the call acts on, verbatim and never abbreviated: the shell command, the path, the
    /// pattern. Views may trim it to fit a row, but the whole of it is always available here.</summary>
    public string Title { get; }

    public ToolKind Kind { get; }

    /// <summary>
    /// The change this call makes, as a unified diff, when it edits a file. Built from the call's input at
    /// the moment it starts — the result that arrives later is only a confirmation, so a UI that showed the
    /// result showed "… has been updated successfully" where the diff belongs.
    /// </summary>
    public string? Diff { get; }

    /// <summary>The parsed <see cref="Diff"/>, ready to render; empty when this call isn't a file edit.</summary>
    public IReadOnlyList<DiffLine> DiffLines { get; }

    public bool HasDiff => Diff is not null;

    /// <summary>
    /// The diff as it reads inline: content lines only. The file header repeats the title, and the hunk
    /// header of an Edit is <c>@@ -1,n +1,m @@</c> whatever part of the file it actually touched — the
    /// diff is built from the replaced fragment, so those line numbers would name lines that don't exist.
    /// The preview shows the whole thing, headers and all.
    /// </summary>
    public IReadOnlyList<DiffLine> InlineDiffLines { get; }

    /// <summary>Changed lines only — the size of the edit, not of the file it landed in.</summary>
    private int ChangedLines => DiffLines.Count(l => l.IsAdded || l.IsRemoved);

    /// <summary>
    /// A small edit renders in the conversation itself: at this size the diff is quicker to read in place
    /// than to open, and seeing changes go by is the point of watching an agent work. Anything larger would
    /// bury the conversation, so it stays one click away.
    ///
    /// Both limits matter. A big edit is obviously too big; so is a two-line change delivered inside a
    /// hundred lines of surrounding context, which counts as small by the first measure but takes just as
    /// much of the screen.
    /// </summary>
    public bool HasInlineDiff => HasDiff
        && ChangedLines is > 0 and <= InlineChangedLimit
        && InlineDiffLines.Count <= InlineTotalLimit;

    /// <summary>A diff too big to sit in the conversation — the row shows its size instead.</summary>
    public bool HasCollapsedDiff => HasDiff && !HasInlineDiff;

    /// <summary>The most changed lines an edit may render inline before it belongs in the preview.</summary>
    public const int InlineChangedLimit = 24;

    /// <summary>The most lines an inline diff may occupy in total, context included.</summary>
    public const int InlineTotalLimit = 40;

    /// <summary>"+12 −3" — the shape of a change, legible before reading it.</summary>
    public string DiffSummary => HasDiff
        ? $"+{DiffLines.Count(l => l.IsAdded)}  −{DiffLines.Count(l => l.IsRemoved)}"
        : string.Empty;

    /// <summary>What opening this call should show: the diff for an edit, else the tool's own output.</summary>
    public string PreviewBody => Diff ?? Detail;

    /// <summary>When the tool call started (first event's timestamp).</summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>When the tool call finished, once it has.</summary>
    public DateTimeOffset? CompletedAt
    {
        get => _completedAt;
        set { if (SetProperty(ref _completedAt, value)) { OnPropertyChanged(nameof(HasDuration)); OnPropertyChanged(nameof(DurationText)); } }
    }

    public bool HasDuration => CompletedAt is { } end && end > StartedAt;

    /// <summary>Elapsed time, e.g. "820ms" or "1.4s".</summary>
    public string DurationText
    {
        get
        {
            if (CompletedAt is not { } end || end <= StartedAt)
            {
                return string.Empty;
            }

            var ms = (end - StartedAt).TotalMilliseconds;
            return ms < 1000 ? $"{ms:0}ms" : $"{ms / 1000:0.0}s";
        }
    }

    /// <summary>Header label for the UI, e.g. "Read — Read a.cs".</summary>
    public string Header => $"{Kind} — {Title}";

    /// <summary>The tool kind on its own (e.g. "Read"), for the compact single-line row.</summary>
    public string KindLabel => Kind.ToString();

    public ToolCallStatus Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(ShowStatus));
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(IsDone));
                OnPropertyChanged(nameof(IsFailed));
            }
        }
    }

    public string StatusText => Status.ToString();

    /// <summary>Status as three flags a view can hang a tone off: in motion, finished, broken.</summary>
    public bool IsRunning => Status is ToolCallStatus.Pending or ToolCallStatus.InProgress;
    public bool IsDone => Status is ToolCallStatus.Completed;
    public bool IsFailed => Status is ToolCallStatus.Failed;

    /// <summary>Only surface a status word when it's not the (common, unremarkable) completed case.</summary>
    public bool ShowStatus => Status != ToolCallStatus.Completed;

    public string Detail
    {
        get => _detail;
        set
        {
            if (SetProperty(ref _detail, value))
            {
                OnPropertyChanged(nameof(Summary));
                OnPropertyChanged(nameof(ShowSummary));
                OnPropertyChanged(nameof(HasDetail));
                OnPropertyChanged(nameof(PreviewBody));
            }
        }
    }

    /// <summary>The first line of the output, whole. Views ellipsize it to fit their row — a trim that a
    /// wider window undoes, unlike cutting the string here, which no view could recover from.</summary>
    public string Summary => Detail.Split('\n', 2)[0].Trim();

    /// <summary>Whether the one-line summary adds anything: next to an inline diff it would just repeat
    /// the diff's own first line.</summary>
    public bool ShowSummary => !HasInlineDiff && Summary.Length > 0;

    /// <summary>Whether opening this call would show more than its row already does.</summary>
    public bool HasDetail => HasDiff || Detail.Contains('\n') || Detail.Length > 80;
}

/// <summary>The agent's current plan.</summary>
public sealed class PlanItemView : TranscriptItem
{
    private IReadOnlyList<PlanEntry> _entries = [];

    public IReadOnlyList<PlanEntry> Entries
    {
        get => _entries;
        set => SetProperty(ref _entries, value);
    }
}

/// <summary>
/// A permission request; becomes resolved once answered. Carries the derived facts a good
/// approval card shows: what will happen, what it touches, whether it is reversible, why it
/// was asked, and which option grants the least.
/// </summary>
public sealed class PermissionItem : TranscriptItem
{
    private bool _resolved;
    private string? _resolutionText;

    public PermissionItem(
        string requestId,
        string title,
        IReadOnlyList<PermissionOption> options,
        ToolKind? toolKind = null,
        string? toolTarget = null,
        string? detail = null)
    {
        RequestId = requestId;
        Title = title;
        Options = options;
        ToolKind = toolKind;
        ToolTarget = toolTarget;
        Detail = detail;
    }

    public string RequestId { get; }
    public string Title { get; }
    public IReadOnlyList<PermissionOption> Options { get; }
    public ToolKind? ToolKind { get; }
    public string? ToolTarget { get; }

    /// <summary>
    /// Exactly what is being approved — the command line, the path, the tool input — in full. A title like
    /// "Allow Bash?" is not something anyone can consent to, and a command trimmed to fit a card is worse
    /// than none: the dangerous part of a long command line is usually its tail.
    /// </summary>
    public string? Detail { get; }

    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);

    /// <summary>What resources this touches, e.g. "Delete · build/".</summary>
    public string ResourceText => ToolKind is { } k
        ? string.IsNullOrWhiteSpace(ToolTarget) ? k.ToString() : $"{k} · {ToolTarget}"
        : "Not specified";

    public bool Reversible => ToolKind is Abstractions.ToolKind.Read or Abstractions.ToolKind.Search
        or Abstractions.ToolKind.Fetch or Abstractions.ToolKind.Think
        or Abstractions.ToolKind.Edit or Abstractions.ToolKind.Move;

    /// <summary>Plain-language reversibility note derived from the tool kind.</summary>
    public string ReversibleText => ToolKind switch
    {
        Abstractions.ToolKind.Read or Abstractions.ToolKind.Search
            or Abstractions.ToolKind.Fetch or Abstractions.ToolKind.Think => "Read-only — nothing is changed",
        Abstractions.ToolKind.Edit or Abstractions.ToolKind.Move => "Reversible — changes can be undone",
        Abstractions.ToolKind.Delete => "Not easily reversible — data may be lost",
        Abstractions.ToolKind.Execute => "Runs a command — effects depend on it",
        _ => "Effect is unknown",
    };

    /// <summary>Why the agent asked, derived from the tool step (ACP carries no explicit reason).</summary>
    public string Rationale => ToolKind is { } k
        ? string.IsNullOrWhiteSpace(ToolTarget)
            ? $"Requested by the agent's {k} step."
            : $"Requested by the agent's {k} step on {ToolTarget}."
        : "Requested by the agent.";

    /// <summary>Hint toward the narrowest option (e.g. prefer once over always).</summary>
    public string? NarrowestOptionHint =>
        Options.Any(o => o.Kind == PermissionOptionKind.AllowOnce)
        && Options.Any(o => o.Kind == PermissionOptionKind.AllowAlways)
            ? "“Allow once” grants the least — prefer it over “always”."
            : null;

    public bool HasNarrowestHint => NarrowestOptionHint is not null;

    public bool Resolved
    {
        get => _resolved;
        set => SetProperty(ref _resolved, value);
    }

    public string? ResolutionText
    {
        get => _resolutionText;
        set => SetProperty(ref _resolutionText, value);
    }
}

/// <summary>A low-key notice (mode change, error, etc.).</summary>
public sealed class NoticeItem : TranscriptItem
{
    public NoticeItem(string text, bool isError = false)
    {
        Text = text;
        IsError = isError;
    }

    public string Text { get; }
    public bool IsError { get; }
}

/// <summary>A structured "ask the user" card: one or more questions, each with selectable options and
/// optional free-text notes. The user picks answers and submits (or dismisses).</summary>
public sealed class QuestionItem : TranscriptItem
{
    private bool _resolved;

    public QuestionItem(string requestId, IReadOnlyList<AgentQuestion> questions)
    {
        RequestId = requestId;
        Questions = questions.Select(q => new QuestionView(q)).ToList();
    }

    public string RequestId { get; }
    public IReadOnlyList<QuestionView> Questions { get; }

    public bool Resolved
    {
        get => _resolved;
        set => SetProperty(ref _resolved, value);
    }

    /// <summary>The answers to submit — chosen labels + any notes, per question.</summary>
    public IReadOnlyList<QuestionAnswer> BuildAnswers()
        => Questions.Select(q => new QuestionAnswer(
            q.Id,
            q.Options.Where(o => o.IsSelected).Select(o => o.Label).ToList(),
            string.IsNullOrWhiteSpace(q.Notes) ? null : q.Notes.Trim())).ToList();
}

/// <summary>One question in a <see cref="QuestionItem"/> with its selectable options + notes.</summary>
public sealed class QuestionView : ObservableObject
{
    private string _notes = string.Empty;

    public QuestionView(AgentQuestion question)
    {
        Id = question.Id;
        Header = question.Header;
        Prompt = question.Prompt;
        MultiSelect = question.MultiSelect;
        AllowFreeText = question.AllowFreeText;
        GroupName = "q-" + Guid.NewGuid().ToString("n"); // radio group for single-select
        Options = question.Options.Select(o => new QuestionOptionView(o.Label, o.Description)).ToList();
    }

    public string Id { get; }
    public string Header { get; }
    public string Prompt { get; }
    public bool MultiSelect { get; }
    public bool SingleSelect => !MultiSelect;
    public bool AllowFreeText { get; }
    public string GroupName { get; }
    public IReadOnlyList<QuestionOptionView> Options { get; }

    public string Notes
    {
        get => _notes;
        set => SetProperty(ref _notes, value);
    }
}

/// <summary>One selectable option within a <see cref="QuestionView"/>.</summary>
public sealed class QuestionOptionView : ObservableObject
{
    private bool _isSelected;

    public QuestionOptionView(string label, string description)
    {
        Label = label;
        Description = description;
    }

    public string Label { get; }
    public string Description { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
