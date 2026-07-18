using Agnes.Abstractions;
using Agnes.Ui.Core.Mvvm;

namespace Agnes.Ui.Core.Transcript;

/// <summary>Base type for a rendered item in a session transcript.</summary>
public abstract class TranscriptItem : ObservableObject;

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
        set => Set(ref _text, value);
    }

    public void Append(string text) => Text += text;
}

/// <summary>A tool call, updated in place as its status changes.</summary>
public sealed class ToolCallItem : TranscriptItem
{
    private ToolCallStatus _status;
    private string _detail = string.Empty;

    public ToolCallItem(string toolCallId, string title, ToolKind kind, ToolCallStatus status)
    {
        ToolCallId = toolCallId;
        Title = title;
        Kind = kind;
        _status = status;
    }

    public string ToolCallId { get; }
    public string Title { get; }
    public ToolKind Kind { get; }

    /// <summary>Header label for the UI, e.g. "Read — Read a.cs".</summary>
    public string Header => $"{Kind} — {Title}";

    public ToolCallStatus Status
    {
        get => _status;
        set { if (Set(ref _status, value)) { Raise(nameof(StatusText)); } }
    }

    public string StatusText => Status.ToString();

    public string Detail
    {
        get => _detail;
        set => Set(ref _detail, value);
    }
}

/// <summary>The agent's current plan.</summary>
public sealed class PlanItemView : TranscriptItem
{
    private IReadOnlyList<PlanEntry> _entries = [];

    public IReadOnlyList<PlanEntry> Entries
    {
        get => _entries;
        set => Set(ref _entries, value);
    }
}

/// <summary>A permission request; becomes resolved once answered.</summary>
public sealed class PermissionItem : TranscriptItem
{
    private bool _resolved;
    private string? _resolutionText;

    public PermissionItem(string requestId, string title, IReadOnlyList<PermissionOption> options)
    {
        RequestId = requestId;
        Title = title;
        Options = options;
    }

    public string RequestId { get; }
    public string Title { get; }
    public IReadOnlyList<PermissionOption> Options { get; }

    public bool Resolved
    {
        get => _resolved;
        set => Set(ref _resolved, value);
    }

    public string? ResolutionText
    {
        get => _resolutionText;
        set => Set(ref _resolutionText, value);
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
