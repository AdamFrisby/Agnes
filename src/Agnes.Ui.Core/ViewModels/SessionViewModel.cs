using System.Collections.ObjectModel;
using Agnes.Abstractions;
using Agnes.Client;
using Agnes.Ui.Core.Mvvm;
using Agnes.Ui.Core.Transcript;

namespace Agnes.Ui.Core.ViewModels;

/// <summary>Drives one live session: renders its transcript, sends prompts, answers permissions.</summary>
public sealed class SessionViewModel : ObservableObject
{
    private readonly HostConnection _host;
    private readonly SessionView _view;
    private readonly IUiDispatcher _dispatcher;
    private readonly TranscriptBuilder _transcript = new();
    private string _promptText = string.Empty;

    public SessionViewModel(HostConnection host, SessionView view, IUiDispatcher dispatcher, string title)
    {
        _host = host;
        _view = view;
        _dispatcher = dispatcher;
        Title = title;

        SendCommand = new AsyncRelayCommand(SendAsync, () => !string.IsNullOrWhiteSpace(PromptText));
        AllowCommand = new AsyncRelayCommand(() => RespondAsync(allow: true));
        DenyCommand = new AsyncRelayCommand(() => RespondAsync(allow: false));

        _transcript.PendingPermissionChanged += () => Raise(nameof(PendingPermission));

        foreach (var @event in _view.Events)
        {
            _transcript.Apply(@event);
        }

        _view.EventAppended += OnEvent;
    }

    public string Title { get; }
    public string SessionId => _view.SessionId;
    public ObservableCollection<TranscriptItem> Items => _transcript.Items;
    public PermissionItem? PendingPermission => _transcript.PendingPermission;

    public string PromptText
    {
        get => _promptText;
        set { if (Set(ref _promptText, value)) { SendCommand.RaiseCanExecuteChanged(); } }
    }

    public AsyncRelayCommand SendCommand { get; }
    public AsyncRelayCommand AllowCommand { get; }
    public AsyncRelayCommand DenyCommand { get; }

    private void OnEvent(SessionEvent @event) => _dispatcher.Post(() => _transcript.Apply(@event));

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
