using System.Collections.ObjectModel;
using System.Windows.Input;
using Agnes.Abstractions;
using Agnes.Client;
using Agnes.Ui.Core.Mvvm;
using Agnes.Ui.Core.Transcript;

namespace Agnes.Ui.Core.ViewModels;

/// <summary>
/// Drives one live session: the chat transcript (middle column), the contextual left-column lists
/// (plan, files modified, tools run) aggregated from the event stream, and the right-column preview
/// shown when the user opens a condensed item (a tool's full diff, or a long message). Side columns
/// surface only when there's content or the user requests them.
/// </summary>
public sealed class SessionViewModel : ObservableObject
{
    private readonly IAgnesHost _host;
    private readonly SessionView _view;
    private readonly IUiDispatcher _dispatcher;
    private readonly TranscriptBuilder _transcript = new();
    private readonly Dictionary<string, ToolEntry> _tools = new();
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
        ShowToolPreviewCommand = new RelayCommand<ToolCallItem>(t => { if (t is not null) { Preview(t.Header, t.Detail); } });
        ShowFilePreviewCommand = new RelayCommand<ToolEntry>(f => { if (f is not null) { Preview(f.Name, f.Detail); } });
        ShowMessagePreviewCommand = new RelayCommand<MessageBubbleItem>(m =>
        {
            if (m is { IsLong: true })
            {
                Preview($"{m.Speaker} message", m.Text);
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
    public ObservableCollection<ToolEntry> ModifiedFiles { get; } = [];
    public ObservableCollection<ToolEntry> ToolActivity { get; } = [];

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
    public bool HasTools => ToolActivity.Count > 0;
    public bool HasSidebarContent => Plan is not null || HasFiles || HasTools;
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
    public ICommand ShowMessagePreviewCommand { get; }

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

            case ToolCallEvent tc:
                var entry = new ToolEntry(tc.ToolCallId, tc.Title, tc.Kind.ToString(), tc.Status.ToString(), TextOf(tc.Content));
                _tools[tc.ToolCallId] = entry;
                (IsFileTool(tc.Kind) ? ModifiedFiles : ToolActivity).Add(entry);
                RaisePanels();
                break;

            case ToolCallUpdateEvent u when _tools.TryGetValue(u.ToolCallId, out var tracked):
                if (u.Status is { } status)
                {
                    tracked.StatusText = status.ToString();
                }

                if (u.Content is { } content && TextOf(content) is { Length: > 0 } text)
                {
                    tracked.Detail = text;
                }

                break;
        }
    }

    private void Preview(string title, string body) => SelectedPreview = new PreviewViewModel(title, body);

    private void RaisePanels()
    {
        Raise(nameof(HasFiles));
        Raise(nameof(HasTools));
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
