using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Agnes.Abstractions;
using Agnes.Client;
using Agnes.Protocol;
using Agnes.Ui.Core.Diff;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Agnes.Ui.Core.ViewModels;

/// <summary>
/// Drives the review-comment surface for a session: it loads the comments left on the session's
/// <em>project</em> (durable across sessions), groups them by file, and lets the user add, delete, jump to,
/// and "send into the session" a comment (as a <see cref="ResourceLinkContent"/> prompt the agent can act
/// on). Staleness is computed by re-hashing the diff line the comment is anchored to and comparing it to the
/// hash stored at comment time — a mismatch (or a missing line) flags the comment rather than silently
/// re-attaching it to unrelated code. Framework-agnostic: it talks to whatever <see cref="IAgnesHost"/> it's
/// given, so the desktop app and the offline simulation drive it identically.
/// </summary>
public sealed class ReviewCommentsViewModel : ObservableObject
{
    private readonly IAgnesHost _host;
    private readonly string _sessionId;
    private readonly string? _projectId;
    private readonly IUiDispatcher _dispatcher;

    // The latest diff lines per file (keyed by file path), used both to hash a new comment's anchored line
    // and to recompute staleness of existing comments as the diff changes underneath them.
    private readonly Dictionary<string, IReadOnlyList<DiffLine>> _diffs = new(StringComparer.Ordinal);

    public ReviewCommentsViewModel(IAgnesHost host, string sessionId, string? projectId, IUiDispatcher dispatcher)
    {
        _host = host;
        _sessionId = sessionId;
        _projectId = projectId;
        _dispatcher = dispatcher;

        RefreshCommand = new AsyncRelayCommand(LoadAsync);
        AddCommand = new AsyncRelayCommand(AddFromDraftAsync, () => CanAdd);
        RemoveCommand = new AsyncRelayCommand<ReviewCommentRow>(RemoveAsync);
        SendCommand = new AsyncRelayCommand<ReviewCommentRow>(SendAsync);
        JumpCommand = new RelayCommand<ReviewCommentRow>(Jump);
    }

    /// <summary>Comments grouped by file (each group is a file with its comment rows).</summary>
    public ObservableCollection<ReviewFileGroup> Files { get; } = [];

    /// <summary>Whether any comments are present (drives the panel's visibility).</summary>
    public bool HasComments => Files.Count > 0;

    private string _status = string.Empty;
    public string Status { get => _status; set => SetProperty(ref _status, value); }

    // ---- draft (the "leave a comment" input) ----
    private string _draftFilePath = string.Empty;
    public string DraftFilePath { get => _draftFilePath; set { if (SetProperty(ref _draftFilePath, value)) { RaiseCanAdd(); } } }

    private int _draftLineNumber = 1;
    public int DraftLineNumber { get => _draftLineNumber; set => SetProperty(ref _draftLineNumber, value); }

    private string _draftText = string.Empty;
    public string DraftText { get => _draftText; set { if (SetProperty(ref _draftText, value)) { RaiseCanAdd(); } } }

    /// <summary>Whether the draft is complete enough to add (a file and some text).</summary>
    public bool CanAdd => !string.IsNullOrWhiteSpace(_draftFilePath) && !string.IsNullOrWhiteSpace(_draftText);

    public ICommand RefreshCommand { get; }
    public IAsyncRelayCommand AddCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand SendCommand { get; }
    public ICommand JumpCommand { get; }

    /// <summary>Raised when the user asks to jump to a comment's anchor, so the view can reveal the file/line.</summary>
    public event Action<ReviewComment>? JumpRequested;

    /// <summary>
    /// A small, process-stable hash of a line's content (trimmed), used as a comment's drift anchor. Not
    /// <see cref="string.GetHashCode()"/> — that is randomized per process, so it can't be persisted and
    /// compared later. FNV-1a keeps it deterministic and dependency-free.
    /// </summary>
    public static string HashLine(string text)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;
        var hash = offset;
        foreach (var c in (text ?? string.Empty).Trim())
        {
            hash = (hash ^ c) * prime;
        }

        return hash.ToString("x8");
    }

    /// <summary>Loads the project's review comments from the host and (re)groups them by file.</summary>
    public async Task LoadAsync()
    {
        if (_projectId is null)
        {
            _dispatcher.Post(() => { Files.Clear(); OnPropertyChanged(nameof(HasComments)); Status = "No project for this session."; });
            return;
        }

        try
        {
            var comments = await _host.ListReviewCommentsAsync(_projectId).ConfigureAwait(false);
            _dispatcher.Post(() => Rebuild(comments));
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => Status = "Couldn't load review comments: " + ex.Message);
        }
    }

    /// <summary>
    /// Supplies the current diff for each modified file (path + unified-diff text) so staleness can be
    /// recomputed and a new comment's line can be hashed. Safe to call whenever the diff set changes.
    /// </summary>
    public void UpdateDiffs(IEnumerable<(string FilePath, string DiffText)> files)
    {
        _diffs.Clear();
        foreach (var (path, diff) in files)
        {
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }

            _diffs[path] = DiffParser.LooksLikeDiff(diff) ? DiffParser.Parse(diff) : [];
        }

        _dispatcher.Post(RecomputeStaleness);
    }

    /// <summary>Adds a comment from the draft inputs, anchoring its hash to the current diff line (if known).</summary>
    private Task AddFromDraftAsync()
        => AddAsync(DraftFilePath, DraftLineNumber, CurrentLineText(DraftFilePath, DraftLineNumber), DraftText);

    /// <summary>Adds a comment on <paramref name="filePath"/> at <paramref name="lineNumber"/>; the anchor hash
    /// is taken from <paramref name="lineText"/> (the line's content at comment time).</summary>
    public async Task AddAsync(string filePath, int lineNumber, string lineText, string text)
    {
        if (_projectId is null || string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            var request = new AddReviewCommentRequest(_projectId, filePath, lineNumber, HashLine(lineText), text);
            var added = await _host.AddReviewCommentAsync(request).ConfigureAwait(false);
            _dispatcher.Post(() =>
            {
                Insert(added);
                DraftText = string.Empty;
                Status = "Comment added.";
            });
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => Status = "Couldn't add comment: " + ex.Message);
        }
    }

    private async Task RemoveAsync(ReviewCommentRow? row)
    {
        if (row is null)
        {
            return;
        }

        try
        {
            await _host.RemoveReviewCommentAsync(row.Comment.Id).ConfigureAwait(false);
            _dispatcher.Post(() =>
            {
                var group = Files.FirstOrDefault(g => g.Comments.Contains(row));
                group?.Comments.Remove(row);
                if (group is { Comments.Count: 0 })
                {
                    Files.Remove(group);
                }

                OnPropertyChanged(nameof(HasComments));
                Status = "Comment removed.";
            });
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => Status = "Couldn't remove comment: " + ex.Message);
        }
    }

    private async Task SendAsync(ReviewCommentRow? row)
    {
        if (row is null)
        {
            return;
        }

        var c = row.Comment;
        // Reference the exact file + line (ResourceLinkContent), then the reviewer's note — so the agent gets
        // a located pointer, not free-floating text (AC: "identifies the specific file and line").
        var uri = $"{c.FilePath}#L{c.LineNumber}";
        IReadOnlyList<ContentBlock> blocks =
        [
            new ResourceLinkContent(uri, $"{c.FilePath}:{c.LineNumber}"),
            new TextContent(c.Text),
        ];

        try
        {
            await _host.PromptAsync(_sessionId, blocks).ConfigureAwait(false);
            _dispatcher.Post(() => Status = "Sent into the session.");
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => Status = "Couldn't send comment: " + ex.Message);
        }
    }

    private void Jump(ReviewCommentRow? row)
    {
        if (row is not null)
        {
            JumpRequested?.Invoke(row.Comment);
        }
    }

    // ---- grouping + staleness ----

    private void Rebuild(IReadOnlyList<ReviewComment> comments)
    {
        Files.Clear();
        foreach (var group in comments.GroupBy(c => c.FilePath).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var file = new ReviewFileGroup(group.Key);
            foreach (var comment in group.OrderBy(c => c.LineNumber))
            {
                file.Comments.Add(new ReviewCommentRow(comment) { IsStale = IsStale(comment) });
            }

            Files.Add(file);
        }

        OnPropertyChanged(nameof(HasComments));
        Status = Files.Count == 0 ? "No review comments yet." : $"{comments.Count} comment(s) across {Files.Count} file(s).";
    }

    private void Insert(ReviewComment comment)
    {
        var group = Files.FirstOrDefault(g => g.FilePath == comment.FilePath);
        if (group is null)
        {
            group = new ReviewFileGroup(comment.FilePath);
            Files.Add(group);
        }

        group.Comments.Add(new ReviewCommentRow(comment) { IsStale = IsStale(comment) });
        OnPropertyChanged(nameof(HasComments));
    }

    private void RecomputeStaleness()
    {
        foreach (var group in Files)
        {
            foreach (var row in group.Comments)
            {
                row.IsStale = IsStale(row.Comment);
            }
        }
    }

    /// <summary>
    /// A comment is stale when the line currently at its anchored position no longer hashes to what it did at
    /// comment time. When no diff is loaded for the file we can't tell, so it's treated as not stale.
    /// </summary>
    private bool IsStale(ReviewComment comment)
    {
        if (!_diffs.TryGetValue(comment.FilePath, out var lines))
        {
            return false;
        }

        var current = CurrentLineText(comment.FilePath, comment.LineNumber);
        return HashLine(current) != comment.LineHash;
    }

    /// <summary>The text of the line currently at <paramref name="lineNumber"/> in the file's new-side diff, or
    /// empty if the file/line isn't present in the loaded diff.</summary>
    private string CurrentLineText(string filePath, int lineNumber)
    {
        if (!_diffs.TryGetValue(filePath, out var lines))
        {
            return string.Empty;
        }

        return lines.FirstOrDefault(l => l.NewLine == lineNumber)?.Text ?? string.Empty;
    }

    private void RaiseCanAdd()
    {
        OnPropertyChanged(nameof(CanAdd));
        AddCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>A file with the review comments left on it (grouping surface for the review panel).</summary>
public sealed class ReviewFileGroup(string filePath)
{
    public string FilePath { get; } = filePath;

    public ObservableCollection<ReviewCommentRow> Comments { get; } = [];
}

/// <summary>One review comment as a bindable row; <see cref="IsStale"/> is observable so the drift flag can
/// update in place as the diff changes.</summary>
public sealed class ReviewCommentRow : ObservableObject
{
    public ReviewCommentRow(ReviewComment comment) => Comment = comment;

    public ReviewComment Comment { get; }

    public string Text => Comment.Text;
    public int LineNumber => Comment.LineNumber;
    public string LineLabel => $"Line {Comment.LineNumber}";

    private bool _isStale;
    public bool IsStale { get => _isStale; set => SetProperty(ref _isStale, value); }
}
