using System.Windows.Input;
using Agnes.Ui.Core.Diff;
using Agnes.Ui.Core.Mvvm;

namespace Agnes.Ui.Core.ViewModels;

/// <summary>
/// Full content shown in a tab's right-hand preview column. If the body is a unified diff it's
/// parsed into <see cref="Diff"/> for the diff viewer; otherwise <see cref="Body"/> is shown as text.
/// </summary>
public sealed class PreviewViewModel : ObservableObject
{
    private bool _split;

    public PreviewViewModel(string title, string body)
    {
        Title = title;
        Body = body;
        if (DiffParser.LooksLikeDiff(body))
        {
            Diff = DiffParser.Parse(body);
            SplitRows = DiffParser.ToSplit(Diff);
            HunkCount = Diff.Count(l => l.Kind == DiffLineKind.Hunk);
        }

        ToggleSplitCommand = new RelayCommand(() => IsSplit = !IsSplit);
    }

    public ICommand ToggleSplitCommand { get; }

    public string Title { get; }
    public string Body { get; }
    public IReadOnlyList<DiffLine>? Diff { get; }
    public IReadOnlyList<DiffSplitRow>? SplitRows { get; }
    public int HunkCount { get; }
    public bool IsDiff => Diff is not null;
    public bool IsText => Diff is null;

    /// <summary>Unified vs side-by-side rendering of a diff.</summary>
    public bool IsSplit
    {
        get => _split;
        set { if (Set(ref _split, value)) { Raise(nameof(ShowUnified)); Raise(nameof(ShowSplit)); } }
    }

    public bool ShowUnified => IsDiff && !IsSplit;
    public bool ShowSplit => IsDiff && IsSplit;
}

/// <summary>A tool call listed in a left-panel list (Files modified / Tools run); opens in the preview.</summary>
public sealed class ToolEntry : ObservableObject
{
    private string _statusText;
    private string _detail;

    public ToolEntry(string toolCallId, string name, string kind, string statusText, string detail)
    {
        ToolCallId = toolCallId;
        Name = name;
        Kind = kind;
        _statusText = statusText;
        _detail = detail;
    }

    public string ToolCallId { get; }
    public string Name { get; }
    public string Kind { get; }

    public string StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value);
    }

    /// <summary>Full detail (e.g. a diff) shown in the preview when this entry is opened.</summary>
    public string Detail
    {
        get => _detail;
        set => Set(ref _detail, value);
    }
}
