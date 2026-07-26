using Agnes.App.Mobile.Services;
using Agnes.Ui.Core;
using Agnes.Ui.Core.Transcript;
using Agnes.Ui.Core.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Agnes.App.Mobile.ViewModels;

/// <summary>
/// One row in the sessions list: a saved session pointer, the host it lives on, and — once subscribed —
/// the live <see cref="SessionViewModel"/> behind it.
///
/// The card has to answer "should I care about this one?" at arm's length, so everything it exposes is
/// a glanceable derivative of the live session: what state it's in, what it last said, how long ago,
/// how much it changed.
/// </summary>
public sealed partial class SessionEntry : ObservableObject
{
    public SessionEntry(SavedSession saved, HostLink host)
    {
        Saved = saved;
        Host = host;
        host.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(HostLink.State))
            {
                OnPropertyChanged(nameof(IsHostOnline));
                OnPropertyChanged(nameof(StatusText));
            }
        };
    }

    public SavedSession Saved { get; private set; }

    public HostLink Host { get; }

    public string SessionId => Saved.SessionId;

    public string HostName => Host.Name;

    public bool IsHostOnline => Host.IsOnline;

    /// <summary>The live session, once <see cref="SessionsViewModel"/> has subscribed to it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLive))]
    private SessionViewModel? _session;

    public bool IsLive => Session is not null;

    /// <summary>True while the subscription is in flight, so the card can show a resting state instead of
    /// pretending to be idle.</summary>
    [ObservableProperty]
    private bool _isLoading;

    /// <summary>Why the session couldn't be reattached, if it couldn't.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    public bool HasError => Error is not null;

    [ObservableProperty]
    private bool _pinned;

    // ---- what the card shows ----

    /// <summary>The agent's own name for the conversation once it has produced one, else the folder.</summary>
    public string Title => Session?.HasAgentTitle == true ? Session.AgentTitle! : Saved.Title;

    /// <summary>The working folder's leaf name — what a developer actually calls the project. Derived
    /// from the folder, never the title: the agent renames the conversation, and deriving from the title
    /// would then show the conversation name twice.</summary>
    public string Project => LeafOf(
        string.IsNullOrEmpty(Saved.WorkingDirectory) ? Saved.Title : Saved.WorkingDirectory);

    public string AgentName => Saved.AdapterId;

    /// <summary>"project · agent", as one string. Deliberately not two &lt;Run&gt;s in the template: runs
    /// don't inherit the parent TextBlock's Foreground, so the line rendered white-on-white in the light
    /// theme.</summary>
    public string ProjectLine => $"{Project} · {AgentName}";

    public SessionActivity Activity => Session?.Activity ?? SessionActivity.Idle;

    public bool NeedsAttention => Session?.NeedsAttention ?? false;

    public bool IsRunning => Activity == SessionActivity.Running;

    public bool IsNeedsInput => Activity == SessionActivity.NeedsInput;

    public bool IsError => Activity == SessionActivity.Error || HasError;

    public bool IsReadyForReview => Activity == SessionActivity.ReadyForReview;

    public bool IsUnread => Session?.IsUnread ?? false;

    /// <summary>The headline state word for the card's pill.</summary>
    public string StatusText
    {
        get
        {
            if (Error is not null)
            {
                return "Unreachable";
            }

            if (Session is null)
            {
                return IsLoading ? "Reattaching" : Host.StateText;
            }

            return Session.PendingPermission is not null ? "Needs approval"
                : Session.PendingQuestion is not null ? "Asked you something"
                : Session.ActivityText;
        }
    }

    /// <summary>The last thing said in the conversation, clamped for a two-line preview.</summary>
    public string Preview
    {
        get
        {
            if (Session is null)
            {
                return Error ?? "Tap to reattach";
            }

            var last = Session.Items.OfType<MessageBubbleItem>().LastOrDefault(m => !m.IsThought);
            if (last is null)
            {
                return "No messages yet";
            }

            var text = last.Text.Replace('\n', ' ').Trim();
            var prefix = last.IsUser ? "You: " : string.Empty;
            return prefix + (text.Length > 160 ? text[..160] + "…" : text);
        }
    }

    /// <summary>When the session last did anything, as "now / 4m / 2h / 3d".</summary>
    public string Age => RelativeTime.Format(Session?.Items.LastOrDefault()?.Timestamp);

    public int FileCount => Session?.ModifiedFiles.Count ?? 0;

    public int ToolCount => Session?.ToolActivity.Count ?? 0;

    public bool HasCounts => FileCount > 0 || ToolCount > 0;

    /// <summary>"3 files · 12 tools", omitting whichever half is zero.</summary>
    public string CountsText
    {
        get
        {
            var parts = new List<string>(2);
            if (FileCount > 0)
            {
                parts.Add($"{FileCount} file{(FileCount == 1 ? string.Empty : "s")}");
            }

            if (ToolCount > 0)
            {
                parts.Add($"{ToolCount} tool{(ToolCount == 1 ? string.Empty : "s")}");
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>Sort key: what needs a human first, then what's moving, then what's recent.</summary>
    public (int Rank, DateTimeOffset When) Order => (
        NeedsAttention ? 0 : IsRunning ? 1 : IsUnread ? 2 : 3,
        Session?.Items.LastOrDefault()?.Timestamp ?? DateTimeOffset.MinValue);

    /// <summary>Attaches the live session and starts mirroring its state onto the card.</summary>
    public void Attach(SessionViewModel session)
    {
        Session = session;
        IsLoading = false;
        Error = null;
        session.PropertyChanged += (_, _) => RaiseAll();
        session.Items.CollectionChanged += (_, _) => RaiseAll();
        session.ModifiedFiles.CollectionChanged += (_, _) => RaiseAll();
        session.ToolActivity.CollectionChanged += (_, _) => RaiseAll();
        RaiseAll();
    }

    /// <summary>Records a new title for the session (the agent named the conversation).</summary>
    public void UpdateSavedTitle(string title)
    {
        Saved = Saved with { Title = title };
        OnPropertyChanged(nameof(Title));
    }

    public void SetPinned(bool pinned)
    {
        Pinned = pinned;
        Saved = Saved with { Pinned = pinned };
    }

    /// <summary>Re-raises every derived property. The card has a dozen of them, all cheap, and all
    /// downstream of the same live session — a targeted invalidation map would be more code and more
    /// ways to miss one.</summary>
    public void RaiseAll()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(Age));
        OnPropertyChanged(nameof(Activity));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(NeedsAttention));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsNeedsInput));
        OnPropertyChanged(nameof(IsError));
        OnPropertyChanged(nameof(IsReadyForReview));
        OnPropertyChanged(nameof(IsUnread));
        OnPropertyChanged(nameof(FileCount));
        OnPropertyChanged(nameof(ToolCount));
        OnPropertyChanged(nameof(HasCounts));
        OnPropertyChanged(nameof(CountsText));
        Changed?.Invoke(this);
    }

    /// <summary>Re-raises just the relative timestamp, ticked once a minute by the list.</summary>
    public void RaiseAge() => OnPropertyChanged(nameof(Age));

    /// <summary>Raised whenever anything the list sorts on may have changed.</summary>
    public event Action<SessionEntry>? Changed;

    private static string LeafOf(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        var slash = trimmed.LastIndexOfAny(['/', '\\']);
        return slash >= 0 && slash < trimmed.Length - 1 ? trimmed[(slash + 1)..] : trimmed;
    }
}
