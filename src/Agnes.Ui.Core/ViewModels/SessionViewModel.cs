using System.Collections.ObjectModel;
using System.Windows.Input;
using Agnes.Abstractions;
using Agnes.Client;
using Agnes.Ui.Core.Mvvm;
using Agnes.Ui.Core.Transcript;

namespace Agnes.Ui.Core.ViewModels;

/// <summary>
/// Drives one live session: the chat transcript (middle column), the contextual left-column
/// lists (current plan, files modified) aggregated from the event stream, and the right-column
/// preview shown when the user opens a condensed item (e.g. a tool's full diff). The side columns
/// surface only when there's content or the user requests them.
/// </summary>
public sealed class SessionViewModel : ObservableObject
{
    private readonly IAgnesHost _host;
    private readonly SessionView _view;
    private readonly IUiDispatcher _dispatcher;
    private readonly TranscriptBuilder _transcript = new();
    private readonly Dictionary<string, FileEntry> _files = new();
    private string _promptText = string.Empty;
    private PlanItemView? _plan;
    private PreviewViewModel? _selectedPreview;
    private bool _leftHidden;

    public SessionViewModel(IAgnesHost host, SessionView view, IUiDispatcher dispatcher, string title)
    {
        _host = host;
        _view = view;
        _dispatcher = dispatcher;
        Title = title;

        SendCommand = new AsyncRelayCommand(SendAsync, () => !string.IsNullOrWhiteSpace(PromptText));
        AllowCommand = new AsyncRelayCommand(() => RespondAsync(allow: true));
        DenyCommand = new AsyncRelayCommand(() => RespondAsync(allow: false));
        ToggleLeftCommand = new RelayCommand(() => { _leftHidden = !_leftHidden; Raise(nameof(ShowLeftPanel)); });
        ClosePreviewCommand = new RelayCommand(() => SelectedPreview = null);
        ShowToolPreviewCommand = new RelayCommand<ToolCallItem>(t =>
        {
            if (t is not null)
            {
                SelectedPreview = new PreviewViewModel(t.Header, t.Detail, "tool");
            }
        });
        ShowFilePreviewCommand = new RelayCommand<FileEntry>(f =>
        {
            if (f is not null)
            {
                SelectedPreview = new PreviewViewModel(f.Name, f.Detail, "diff");
            }
        });

        _transcript.PendingPermissionChanged += () => Raise(nameof(PendingPermission));

        foreach (var @event in _view.Events)
        {
            Apply(@event);
        }

        _view.EventAppended += OnEvent;
    }

    public string Title { get; }
    public string SessionId => _view.SessionId;
    public ObservableCollection<TranscriptItem> Items => _transcript.Items;
    public PermissionItem? PendingPermission => _transcript.PendingPermission;

    // Left column
    public ObservableCollection<FileEntry> ModifiedFiles { get; } = [];

    public PlanItemView? Plan
    {
        get => _plan;
        private set { if (Set(ref _plan, value)) { RaisePanels(); } }
    }

    // Right column
    public PreviewViewModel? SelectedPreview
    {
        get => _selectedPreview;
        private set { if (Set(ref _selectedPreview, value)) { Raise(nameof(ShowRightPanel)); } }
    }

    public bool HasFiles => ModifiedFiles.Count > 0;
    public bool HasSidebarContent => Plan is not null || ModifiedFiles.Count > 0;
    public bool ShowLeftPanel => HasSidebarContent && !_leftHidden;
    public bool ShowRightPanel => SelectedPreview is not null;

    public string PromptText
    {
        get => _promptText;
        set { if (Set(ref _promptText, value)) { SendCommand.RaiseCanExecuteChanged(); } }
    }

    public AsyncRelayCommand SendCommand { get; }
    public AsyncRelayCommand AllowCommand { get; }
    public AsyncRelayCommand DenyCommand { get; }
    public ICommand ToggleLeftCommand { get; }
    public ICommand ClosePreviewCommand { get; }
    public ICommand ShowToolPreviewCommand { get; }
    public ICommand ShowFilePreviewCommand { get; }

    private void OnEvent(SessionEvent @event) => _dispatcher.Post(() => Apply(@event));

    private void Apply(SessionEvent @event)
    {
        _transcript.Apply(@event);
        UpdateSidebar(@event);
    }

    private void UpdateSidebar(SessionEvent @event)
    {
        switch (@event)
        {
            case PlanEvent p:
                if (Plan is null)
                {
                    Plan = new PlanItemView { Entries = p.Entries };
                }
                else
                {
                    Plan.Entries = p.Entries;
                }

                break;

            case ToolCallEvent tc when IsFileTool(tc.Kind):
                var detail = TextOf(tc.Content);
                if (_files.TryGetValue(tc.ToolCallId, out var existing))
                {
                    existing.StatusText = tc.Status.ToString();
                    if (detail.Length > 0)
                    {
                        existing.Detail = detail;
                    }
                }
                else
                {
                    var entry = new FileEntry(tc.ToolCallId, tc.Title, tc.Status.ToString(), detail);
                    _files[tc.ToolCallId] = entry;
                    ModifiedFiles.Add(entry);
                    RaisePanels();
                }

                break;

            case ToolCallUpdateEvent u when _files.TryGetValue(u.ToolCallId, out var file):
                if (u.Status is { } status)
                {
                    file.StatusText = status.ToString();
                }

                if (u.Content is { } content && TextOf(content) is { Length: > 0 } text)
                {
                    file.Detail = text;
                }

                break;
        }
    }

    private void RaisePanels()
    {
        Raise(nameof(HasFiles));
        Raise(nameof(HasSidebarContent));
        Raise(nameof(ShowLeftPanel));
    }

    private static bool IsFileTool(ToolKind kind)
        => kind is ToolKind.Edit or ToolKind.Delete or ToolKind.Move;

    private static string TextOf(IReadOnlyList<ContentBlock> content)
        => string.Concat(content.OfType<TextContent>().Select(c => c.Text));

    private async Task SendAsync()
    {
        var text = PromptText;
        PromptText = string.Empty;
        await _host.PromptAsync(SessionId, [new TextContent(text)]);
    }

    private async Task RespondAsync(bool allow)
    {
        if (PendingPermission is not { } permission)
        {
            return;
        }

        var option = permission.Options.FirstOrDefault(o => IsAllow(o.Kind) == allow)
                     ?? permission.Options[0];
        await _host.RespondPermissionAsync(SessionId, permission.RequestId, option.OptionId);
    }

    private static bool IsAllow(PermissionOptionKind kind)
        => kind is PermissionOptionKind.AllowOnce or PermissionOptionKind.AllowAlways;
}
