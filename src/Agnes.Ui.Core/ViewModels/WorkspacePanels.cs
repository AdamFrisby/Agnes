using System.Windows.Input;
using Agnes.Abstractions;
using Agnes.Ui.Core.Diff;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Agnes.Ui.Core.ViewModels;

/// <summary>
/// Full content shown in a tab's right-hand preview column. If the body is a unified diff it's
/// parsed into <see cref="Diff"/> for the diff viewer; otherwise <see cref="Body"/> is shown as text.
/// </summary>
public sealed class PreviewViewModel : ObservableObject
{
    private bool _split;

    public PreviewViewModel(string title, string body, bool markdown = false, string? command = null)
    {
        Title = title;
        Body = body;
        Command = command;
        if (DiffParser.LooksLikeDiff(body))
        {
            Diff = DiffParser.Parse(body);
            SplitRows = DiffParser.ToSplit(Diff);
            HunkCount = Diff.Count(l => l.Kind == DiffLineKind.Hunk);
        }

        // Chat messages render as Markdown; tool/file output is shown verbatim (it's often code or logs).
        IsMarkdown = markdown && Diff is null;
        ToggleSplitCommand = new RelayCommand(() => IsSplit = !IsSplit);
    }

    public ICommand ToggleSplitCommand { get; }

    public string Title { get; }
    public string Body { get; }

    /// <summary>The full tool command/target, shown verbatim above the result — the title bar truncates it,
    /// so a long command (a shell line, a full path) is otherwise unreadable. Null for non-tool previews.</summary>
    public string? Command { get; }

    public bool HasCommand => !string.IsNullOrWhiteSpace(Command);

    public IReadOnlyList<DiffLine>? Diff { get; }
    public IReadOnlyList<DiffSplitRow>? SplitRows { get; }
    public int HunkCount { get; }
    public bool IsDiff => Diff is not null;
    public bool IsText => Diff is null;

    /// <summary>The text body is Markdown (a chat message) → render it formatted, not as plain text.</summary>
    public bool IsMarkdown { get; }

    /// <summary>Plain, verbatim text preview (tool output, logs) — text that is not Markdown.</summary>
    public bool IsPlainText => IsText && !IsMarkdown;

    /// <summary>Unified vs side-by-side rendering of a diff.</summary>
    public bool IsSplit
    {
        get => _split;
        set { if (SetProperty(ref _split, value)) { OnPropertyChanged(nameof(ShowUnified)); OnPropertyChanged(nameof(ShowSplit)); } }
    }

    public bool ShowUnified => IsDiff && !IsSplit;
    public bool ShowSplit => IsDiff && IsSplit;
}

/// <summary>A tool call listed in a left-panel list (Files modified / Tools run); opens in the preview.</summary>
public sealed class ToolEntry : ObservableObject
{
    private ToolCallStatus _status;
    private string _detail;

    public ToolEntry(string toolCallId, string name, ToolKind kind, ToolCallStatus status, string detail)
    {
        ToolCallId = toolCallId;
        Name = name;
        Kind = kind;
        _status = status;
        _detail = detail;
    }

    public string ToolCallId { get; }
    public string Name { get; }
    public ToolKind Kind { get; }

    /// <summary>The tool kind as a label, e.g. "Edit".</summary>
    public string KindLabel => Kind.ToString();

    public ToolCallStatus Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusText));
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

    /// <summary>Whether the call removes something — a deletion reads as destructive, not as an edit.</summary>
    public bool IsDestructive => Kind is ToolKind.Delete;

    /// <summary>Full detail (e.g. a diff) shown in the preview when this entry is opened.</summary>
    public string Detail
    {
        get => _detail;
        set => SetProperty(ref _detail, value);
    }
}
