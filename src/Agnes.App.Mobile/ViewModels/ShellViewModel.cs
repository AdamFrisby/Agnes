using System.Collections.ObjectModel;
using Agnes.App.Mobile.Services;
using Agnes.Client;
using Agnes.Ui.Core;
using Agnes.Ui.Core.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Agnes.App.Mobile.ViewModels;

/// <summary>The bottom-navigation destinations.</summary>
public enum ShellTab
{
    /// <summary>What your agents are doing right now.</summary>
    Sessions,

    /// <summary>What is waiting on you: approvals, questions, finished background runs.</summary>
    Inbox,

    /// <summary>Everything ever said, across every session the host has recorded.</summary>
    Search,

    /// <summary>Hosts, appearance, notifications, the rest.</summary>
    More,
}

/// <summary>
/// The root view model: four tabs, one navigation stack, one sheet layer, one toast.
///
/// The shape is deliberately not the desktop client's. The desktop is a workbench — docked panels, a
/// tab strip, a terminal. A phone is a cockpit: it answers "what is happening", "what needs me", and
/// "let me say one thing back", and everything else is a sheet away. So the four destinations are the
/// four jobs, the session screen owns the full display, and every secondary surface is summoned.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject, IAppShell
{
    private readonly IAgnesConnector _connector;
    private readonly IPromptStore _prompts;
    private readonly IPermissionPolicy _policy;
    private readonly Func<string, Task<string?>>? _dictate;
    private readonly Action<string>? _copy;
    private readonly Action<string>? _openUrl;
    private readonly Action<string>? _clearNotification;

    public ShellViewModel(
        IAgnesConnector connector,
        IUiDispatcher dispatcher,
        MobileSettings settings,
        string deviceName,
        IHaptics? haptics = null,
        INotifier? notifier = null,
        Func<string, Task<string?>>? dictate = null,
        Action<string>? copyToClipboard = null,
        Action<string>? openUrl = null,
        Action<string>? clearNotification = null)
    {
        _connector = connector;
        Dispatcher = dispatcher;
        Settings = settings;
        DeviceName = deviceName;
        Haptics = haptics ?? NullHaptics.Instance;
        Notifier = notifier ?? NullNotifier.Instance;
        _dictate = dictate;
        _copy = copyToClipboard;
        _openUrl = openUrl;
        _clearNotification = clearNotification;

        _prompts = new FilePromptStore(JsonStore.PathFor("prompts.json"));
        _policy = new FilePermissionPolicy(JsonStore.PathFor("permission-policy.json"));

        Hosts = new HostBook(connector, dispatcher);
        Sessions = new SessionsViewModel(this, Hosts, _prompts, _policy, Notifier);
        Inbox = new InboxViewModel(this, Hosts, Sessions);
        Search = new SearchViewModel(this, Hosts, Sessions);
        More = new MoreViewModel(this);

        SelectTabCommand = new RelayCommand<string>(name =>
        {
            if (Enum.TryParse<ShellTab>(name, out var tab))
            {
                SelectTab(tab);
            }
        });
        DismissToastCommand = new RelayCommand(() => CurrentToast = null);
        CloseSheetCommand = new RelayCommand(CloseSheet);
        BackCommand = new RelayCommand(() => GoBack());
    }

    public IUiDispatcher Dispatcher { get; }

    public string DeviceName { get; }

    public IHaptics Haptics { get; }

    public INotifier Notifier { get; }

    public HostBook Hosts { get; }

    public MobileSettings Settings { get; private set; }

    public SessionsViewModel Sessions { get; }

    public InboxViewModel Inbox { get; }

    public SearchViewModel Search { get; }

    public MoreViewModel More { get; }

    public bool CanDictate => _dictate is not null;

    // ---- tabs ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSessionsTab))]
    [NotifyPropertyChangedFor(nameof(IsInboxTab))]
    [NotifyPropertyChangedFor(nameof(IsSearchTab))]
    [NotifyPropertyChangedFor(nameof(IsMoreTab))]
    private ShellTab _tab = ShellTab.Sessions;

    public bool IsSessionsTab => Tab == ShellTab.Sessions;
    public bool IsInboxTab => Tab == ShellTab.Inbox;
    public bool IsSearchTab => Tab == ShellTab.Search;
    public bool IsMoreTab => Tab == ShellTab.More;

    public IRelayCommand<string> SelectTabCommand { get; }

    /// <summary>Switching to a tab pops the stack, so each destination is a fresh start rather than
    /// resuming wherever you happened to be three screens deep.</summary>
    public void SelectTab(ShellTab tab)
    {
        if (Tab == tab && Stack.Count == 0)
        {
            return;
        }

        PopToRoot();
        Tab = tab;
        Haptics.Tick();
        switch (tab)
        {
            case ShellTab.Inbox:
                _ = Inbox.RefreshAsync();
                break;
            case ShellTab.Search:
                Search.OnShown();
                break;
        }
    }

    // ---- navigation stack ----

    /// <summary>Pages stacked over the tabs; the last one is what's on screen.</summary>
    public ObservableCollection<PageViewModel> Stack { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTabs))]
    private PageViewModel? _currentPage;

    /// <summary>The tab bar belongs to the four destinations. A pushed page owns the whole display — the
    /// session screen needs the bottom edge for its composer, and a detail screen with a tab bar under it
    /// invites you to lose your place.</summary>
    public bool ShowTabs => CurrentPage is null;

    public IRelayCommand BackCommand { get; }

    public void Push(PageViewModel page)
    {
        CurrentPage?.OnDisappearing();
        Stack.Add(page);
        CurrentPage = page;
        page.OnAppearing();
    }

    public void Pop()
    {
        if (Stack.Count == 0)
        {
            return;
        }

        var top = Stack[^1];
        Stack.RemoveAt(Stack.Count - 1);
        top.OnDisappearing();
        CurrentPage = Stack.Count > 0 ? Stack[^1] : null;
        CurrentPage?.OnAppearing();
    }

    public void PopToRoot()
    {
        CloseSheet();
        while (Stack.Count > 0)
        {
            Pop();
        }
    }

    /// <summary>
    /// The single back handler for the whole app, wired to the Android back gesture: close the sheet,
    /// else let the page handle it, else pop, else report "nothing left" so the OS can leave the app.
    /// </summary>
    public bool GoBack()
    {
        if (CurrentSheet is not null)
        {
            CloseSheet();
            return true;
        }

        if (CurrentPage?.OnBackRequested() == true)
        {
            return true;
        }

        if (Stack.Count > 0)
        {
            Pop();
            return true;
        }

        // On a tab other than the first, back returns to Sessions rather than exiting — the usual
        // Android convention for a bottom-nav app.
        if (Tab != ShellTab.Sessions)
        {
            SelectTab(ShellTab.Sessions);
            return true;
        }

        return false;
    }

    // ---- sheets ----

    [ObservableProperty]
    private SheetViewModel? _currentSheet;

    public IRelayCommand CloseSheetCommand { get; }

    public void ShowSheet(SheetViewModel sheet)
    {
        CloseSheet();
        sheet.CloseRequested += CloseSheet;
        CurrentSheet = sheet;
        Haptics.Tick();
    }

    public void CloseSheet() => CurrentSheet = null;

    // ---- toast ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasToast))]
    [NotifyPropertyChangedFor(nameof(ToastText))]
    [NotifyPropertyChangedFor(nameof(ToastIsSuccess))]
    [NotifyPropertyChangedFor(nameof(ToastIsWarning))]
    [NotifyPropertyChangedFor(nameof(ToastIsDanger))]
    private ToastMessage? _currentToast;

    // Flattened rather than bound through the nullable record: `{Binding CurrentToast.IsSuccess}`
    // resolves to nothing while there's no toast, and Avalonia logs a binding error for each one every
    // time the toast clears.
    public bool HasToast => CurrentToast is not null;
    public string ToastText => CurrentToast?.Text ?? string.Empty;
    public bool ToastIsSuccess => CurrentToast?.Kind == ToastKind.Success;
    public bool ToastIsWarning => CurrentToast?.Kind == ToastKind.Warning;
    public bool ToastIsDanger => CurrentToast?.Kind == ToastKind.Danger;

    public IRelayCommand DismissToastCommand { get; }

    private int _toastGeneration;

    public void Toast(string message, ToastKind kind = ToastKind.Info)
    {
        Dispatcher.Post(() =>
        {
            var generation = ++_toastGeneration;
            CurrentToast = new ToastMessage(message, kind);
            _ = Task.Delay(kind == ToastKind.Danger ? 5200 : 3200).ContinueWith(_ =>
                Dispatcher.Post(() =>
                {
                    if (generation == _toastGeneration)
                    {
                        CurrentToast = null;
                    }
                }), TaskScheduler.Default);
        });
    }

    public void CopyToClipboard(string text, string what)
    {
        _copy?.Invoke(text);
        Haptics.Tick();
        Toast($"{what} copied", ToastKind.Success);
    }

    public void OpenUrl(string url) => _openUrl?.Invoke(url);

    public Task<string?> DictateAsync()
        => _dictate?.Invoke("Say your prompt") ?? Task.FromResult<string?>(null);

    /// <summary>Clears a session's notification from the shade (called when its screen opens).</summary>
    public void ClearNotification(string sessionId) => _clearNotification?.Invoke(sessionId);

    // ---- settings ----

    /// <summary>Applies and persists a settings change, then republishes it to whoever reads it.</summary>
    public void UpdateSettings(Func<MobileSettings, MobileSettings> change)
    {
        Settings = change(Settings);
        Settings.Save();
        OnPropertyChanged(nameof(Settings));
        SettingsChanged?.Invoke(Settings);
    }

    public event Action<MobileSettings>? SettingsChanged;

    // ---- startup ----

    /// <summary>
    /// Brings the app back to life: connect every paired host, then re-subscribe every session this
    /// device had open. Both are best-effort and run in the background — the session list renders
    /// immediately from local state and fills in as connections land.
    /// </summary>
    public async Task StartAsync()
    {
        await Sessions.RestoreAsync().ConfigureAwait(false);

        // First launch with nothing paired: seed the offline demo so the app has something true to show.
        if (!Settings.DemoSeeded && Sessions.All.Count == 0 && !Hosts.Real.Any())
        {
            UpdateSettings(s => s with { DemoSeeded = true });
            await Sessions.SeedDemoAsync().ConfigureAwait(false);
        }

        _ = Inbox.RefreshAsync();
    }

    /// <summary>Opens the connect screen pre-filled from an <c>agnes://</c> deep link, so a host's QR
    /// removes the address-and-code typing entirely.</summary>
    public void BeginPairing(string hostUrl, string? code)
    {
        PopToRoot();
        SelectTab(ShellTab.Sessions);
        Push(new ConnectPageViewModel(this, Hosts, Sessions, hostUrl, code));
    }

    /// <summary>Opens a session by id if this device knows it (used by notification taps).</summary>
    public void OpenSessionById(string sessionId)
    {
        var entry = Sessions.All.FirstOrDefault(s => s.SessionId == sessionId);
        if (entry is not null)
        {
            SelectTab(ShellTab.Sessions);
            Sessions.Open(entry);
        }
    }
}

/// <summary>A transient in-app message.</summary>
public sealed record ToastMessage(string Text, ToastKind Kind)
{
    public bool IsInfo => Kind == ToastKind.Info;
    public bool IsSuccess => Kind == ToastKind.Success;
    public bool IsWarning => Kind == ToastKind.Warning;
    public bool IsDanger => Kind == ToastKind.Danger;
}
