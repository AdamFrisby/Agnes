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

    /// <summary>
    /// The CodeyBox fleet — present only on a device that has been given an orchestrator to watch.
    /// </summary>
    /// <remarks>
    /// <b>Why a fifth destination and not a card on Sessions.</b> docs/mobile.md's four tabs are four
    /// <em>jobs</em>, and a fleet of autonomous agents is a fifth one: it is not a session, it has its own
    /// three-segment page stack and its own item pages, and a row at the top of Sessions would put every
    /// visit two taps deep and pop back into a list it has nothing to do with — while spending the top of
    /// the one screen whose whole value is being readable at a glance. It appears only when CodeyBox is
    /// configured, so for everyone else this is byte-for-byte the four-destination app the brief
    /// describes: no tab, no inbox rows, no requests. Five equal targets across 411 dp is 82 dp each,
    /// comfortably over the 48 dp floor, and on the tablet it is not close.
    /// </remarks>
    CodeyBox,
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
    private readonly Func<bool>? _isMetered;

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
        Action<string>? clearNotification = null,
        IReceivedFileHandler? receivedFiles = null,
        Func<bool>? isMeteredNetwork = null,
        Func<Agnes.App.Mobile.Services.CodeyBoxEndpoint, Agnes.Plugins.CodeyBox.CodeyBoxClient?>? codeyBoxClient = null)
    {
        _connector = connector;
        Dispatcher = dispatcher;
        Settings = settings;
        DeviceName = deviceName;
        Haptics = haptics ?? NullHaptics.Instance;
        Notifier = notifier ?? NullNotifier.Instance;
        // Null rather than "unsupported": the handler reports what it can do, and the sheet shows only the
        // buttons that are real. The headless preview and the tests get this one.
        ReceivedFiles = receivedFiles ?? NullReceivedFileHandler.Instance;
        _dictate = dictate;
        _copy = copyToClipboard;
        _openUrl = openUrl;
        _clearNotification = clearNotification;
        // Android's ConnectivityManager, injected rather than reached for: the harness and the tests get
        // no probe and so always report an unmetered connection.
        _isMetered = isMeteredNetwork;

        _prompts = new FilePromptStore(JsonStore.PathFor("prompts.json"));
        _policy = new FilePermissionPolicy(JsonStore.PathFor("permission-policy.json"));

        Hosts = new HostBook(connector, dispatcher);
        Sessions = new SessionsViewModel(this, Hosts, _prompts, _policy, Notifier);
        // Built before the Inbox, because the Inbox projects its "waiting on you" rows. The client
        // factory is injected for the same reason the received-file handler is: the headless harness and
        // the render tests need these screens without an orchestrator behind them, and returning null
        // there means "configured, but nothing to talk to" — which is exactly a canned fleet.
        CodeyBox = new CodeyBoxViewModel(this, clientFactory: codeyBoxClient);
        Layout.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WindowLayout.TwoPane))
            {
                OnLayoutChanged();
            }
        };
        CodeyBox.ConfigurationChanged += () => Dispatcher.Post(() =>
        {
            OnPropertyChanged(nameof(HasCodeyBox));
            if (!HasCodeyBox && Tab == ShellTab.CodeyBox)
            {
                SelectTab(ShellTab.Sessions);
            }
        });
        Inbox = new InboxViewModel(this, Hosts, Sessions, CodeyBox);
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

    /// <inheritdoc />
    public IReceivedFileHandler ReceivedFiles { get; }

    public HostBook Hosts { get; }

    /// <inheritdoc />
    public bool IsMeteredNetwork => _isMetered?.Invoke() ?? false;

    public MobileSettings Settings { get; private set; }

    public SessionsViewModel Sessions { get; }

    public InboxViewModel Inbox { get; }

    public SearchViewModel Search { get; }

    public MoreViewModel More { get; }

    /// <summary>The fleet, or an inert object that has never been given an orchestrator to watch.</summary>
    public CodeyBoxViewModel CodeyBox { get; }

    /// <summary>Whether the fifth tab exists at all.</summary>
    public bool HasCodeyBox => CodeyBox.IsConfigured;

    public bool CanDictate => _dictate is not null;

    // ---- tabs ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSessionsTab))]
    [NotifyPropertyChangedFor(nameof(IsInboxTab))]
    [NotifyPropertyChangedFor(nameof(IsSearchTab))]
    [NotifyPropertyChangedFor(nameof(IsMoreTab))]
    [NotifyPropertyChangedFor(nameof(IsCodeyBoxTab))]
    private ShellTab _tab = ShellTab.Sessions;

    public bool IsSessionsTab => Tab == ShellTab.Sessions;
    public bool IsInboxTab => Tab == ShellTab.Inbox;
    public bool IsSearchTab => Tab == ShellTab.Search;
    public bool IsMoreTab => Tab == ShellTab.More;
    public bool IsCodeyBoxTab => Tab == ShellTab.CodeyBox;

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
        var leaving = Tab;
        Tab = tab;
        Haptics.Tick();

        // The fleet's feed and timers belong to the tab, not to the app: a phone in a pocket must not be
        // holding an SSE connection open to an orchestrator nobody is looking at.
        if (leaving == ShellTab.CodeyBox && tab != ShellTab.CodeyBox)
        {
            CodeyBox.OnHidden();
        }

        switch (tab)
        {
            case ShellTab.Inbox:
                _ = Inbox.RefreshAsync();
                break;
            case ShellTab.Search:
                Search.OnShown();
                break;
            case ShellTab.CodeyBox:
                CodeyBox.OnShown();
                break;
        }
    }

    // ---- navigation stack ----

    /// <summary>Pages stacked over the tabs; the last one is what's on screen.</summary>
    public ObservableCollection<PageViewModel> Stack { get; } = [];

    // ---- the window ----

    /// <summary>How wide, how tall, and everything the views decide from that. Fed by the shell view.</summary>
    public WindowLayout Layout { get; } = new();

    // ---- the detail pane ----
    //
    // With two panes, a detail page (a session, a fleet item) opens beside the list rather than over it:
    // the list stays, the rail stays, and switching sessions is one tap. The page is the same view model
    // either way; only where it is shown changes, and a resize moves it between the two without losing it.

    /// <summary>What the detail pane holds; the stack is what the phone's single pane holds.</summary>
    public ObservableCollection<PageViewModel> DetailStack { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDetailShown))]
    private PageViewModel? _detail;

    public bool IsDetailShown => Detail is not null;

    private void ShowDetail(PageViewModel page)
    {
        Detail?.OnDisappearing();
        DetailStack.Clear();
        DetailStack.Add(page);
        Detail = page;
        page.OnAppearing();
    }

    private void CloseDetail()
    {
        if (Detail is not { } page)
        {
            return;
        }

        Detail = null;
        DetailStack.Clear();
        page.OnDisappearing();
    }

    /// <summary>A fold, a rotation: the detail moves to wherever a detail now lives, and stays open.</summary>
    private void OnLayoutChanged()
    {
        if (!Layout.TwoPane && Detail is { } detail)
        {
            Detail = null;
            DetailStack.Clear();
            Stack.Add(detail);
            CurrentPage = detail;
        }
        else if (Layout.TwoPane && CurrentPage is { IsDetail: true } top)
        {
            Stack.Remove(top);
            CurrentPage = Stack.Count > 0 ? Stack[^1] : null;
            DetailStack.Clear();
            DetailStack.Add(top);
            Detail = top;
        }
    }

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
        if (Layout.TwoPane && page.IsDetail && Stack.Count == 0)
        {
            ShowDetail(page);
            return;
        }

        CurrentPage?.OnDisappearing();
        Stack.Add(page);
        CurrentPage = page;
        page.OnAppearing();
    }

    public void Pop()
    {
        if (Stack.Count == 0)
        {
            CloseDetail();
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
        CloseDetail();
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

        if (Detail is not null)
        {
            CloseDetail();
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

#if DEBUG
        // Debug only: first launch with nothing paired seeds the offline demo so there is something to
        // look at. A shipped build shows an honestly empty list and the pairing prompt instead.
        if (!Settings.DemoSeeded && Sessions.All.Count == 0 && !Hosts.Real.Any())
        {
            UpdateSettings(s => s with { DemoSeeded = true });
            await Sessions.SeedDemoAsync().ConfigureAwait(false);
        }
#endif

        _ = Inbox.RefreshAsync();
    }

    /// <summary>Opens the connect screen pre-filled from an <c>agnes://</c> deep link, so a host's QR
    /// removes the address-and-code typing entirely.</summary>
    /// <param name="autoSubmit">True for a scanned grant: it wasn't typed, so there is nothing for the
    /// user to check before submitting, and making them tap a button adds a step and no safety.</param>
    public void BeginPairing(
        string hostUrl, string? code, string? sessionId = null, bool autoSubmit = false, string? fingerprint = null)
    {
        PopToRoot();
        SelectTab(ShellTab.Sessions);
        Push(new ConnectPageViewModel(this, Hosts, Sessions, hostUrl, code, sessionId, autoSubmit, fingerprint));
    }

    /// <summary>
    /// Opens a session someone shared a link to.
    ///
    /// The link carries no credential — that's what makes it safe to paste into a group chat — so it only
    /// works against a host this phone is already paired with. If it isn't, say so and stop: offering to pair
    /// off the back of a message anyone could have sent would turn a shared link into a way to talk a stranger
    /// into enrolling with a host they've never heard of.
    /// </summary>
    public void ViewSharedSession(string hostUrl, string sessionId, long? sequence)
    {
        var link = Hosts.Links.FirstOrDefault(l =>
            string.Equals(l.Url.TrimEnd('/'), hostUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
        if (link is null)
        {
            Toast($"You don't have access to {hostUrl}. Pair with it first, then open the link again.", ToastKind.Warning);
            return;
        }

        PopToRoot();
        SelectTab(ShellTab.Sessions);
        Sessions.OpenById(link, sessionId, sequence);
    }

    /// <summary>
    /// Opens a session by id if this device knows it (used by notification taps).
    /// </summary>
    /// <param name="anchorId">The transcript item the notification was about, if it named one. Resolved to
    /// its event sequence here rather than passed around as an anchor, because an anchor is a per-render
    /// GUID: it only means anything to the client that minted it, and after a cold start (which is exactly
    /// when a notification is tapped) that client is gone. A sequence survives the restart.</param>
    public void OpenSessionById(string sessionId, string? anchorId = null)
    {
        var entry = Sessions.All.FirstOrDefault(s => s.SessionId == sessionId);
        if (entry is null)
        {
            return;
        }

        SelectTab(ShellTab.Sessions);

        var sequence = anchorId is { Length: > 0 }
            ? entry.Session?.Items.FirstOrDefault(i => i.AnchorId == anchorId)?.Sequence ?? 0
            : 0;

        if (sequence > 0)
        {
            Sessions.OpenAt(entry, sequence);
        }
        else
        {
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
