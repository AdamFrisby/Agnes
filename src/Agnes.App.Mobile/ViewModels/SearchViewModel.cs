using System.Collections.ObjectModel;
using Agnes.App.Mobile.Services;
using Agnes.Ui.Core.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Agnes.App.Mobile.ViewModels;

/// <summary>A hit in an open session's transcript, on this device.</summary>
public sealed record LocalHit(SessionEntry Entry, SearchHit Hit)
{
    public string SessionTitle => Entry.Title;
    public string Kind => Hit.Kind;
    public string Snippet => Hit.Snippet;
}

/// <summary>
/// Search across everything that was ever said.
///
/// Two tiers, because they answer different questions. The open sessions on this device are searched
/// locally and instantly as you type — "where in this conversation did it mention the config file".
/// The host's full-text index covers every session it has ever recorded, including closed ones, and
/// runs when you submit — "what did I do about that bug last week".
/// </summary>
public sealed partial class SearchViewModel : ObservableObject
{
    private readonly IAppShell _shell;
    private readonly HostBook _hosts;
    private readonly SessionsViewModel _sessions;

    public SearchViewModel(IAppShell shell, HostBook hosts, SessionsViewModel sessions)
    {
        _shell = shell;
        _hosts = hosts;
        _sessions = sessions;

        History = new MemorySearchViewModel(
            () => _hosts.Links.FirstOrDefault(l => l.IsOnline && !l.IsBuiltIn)?.Host
                  ?? _hosts.Links.FirstOrDefault(l => l.IsOnline)?.Host,
            shell.Dispatcher);
        History.OpenRequested += OpenHistoryHit;

        OpenLocalCommand = new RelayCommand<LocalHit>(hit =>
        {
            if (hit is not null)
            {
                _sessions.Open(hit.Entry);
                hit.Entry.Session?.ScrollTo(hit.Hit.AnchorId);
            }
        });
        SubmitCommand = new RelayCommand(() =>
        {
            History.Query = Query;
            History.SearchCommand.Execute(null);
        });
        ClearCommand = new RelayCommand(() =>
        {
            Query = string.Empty;
            History.Results.Clear();
            History.Status = string.Empty;
        });
    }

    /// <summary>Host-backed search over every recorded session.</summary>
    public MemorySearchViewModel History { get; }

    /// <summary>Matches inside the sessions this device currently has open.</summary>
    public ObservableCollection<LocalHit> Local { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQuery))]
    private string _query = string.Empty;

    partial void OnQueryChanged(string value) => RunLocal(value);

    public bool HasQuery => Query.Trim().Length > 0;

    public bool HasLocal => Local.Count > 0;

    public IRelayCommand<LocalHit> OpenLocalCommand { get; }
    public IRelayCommand SubmitCommand { get; }
    public IRelayCommand ClearCommand { get; }

    public void OnShown() => RunLocal(Query);

    /// <summary>Searches the open sessions in memory. Capped per session so one enormous transcript can't
    /// crowd out every other session's matches.</summary>
    private void RunLocal(string query)
    {
        Local.Clear();
        var term = query.Trim();
        if (term.Length >= 2)
        {
            foreach (var entry in _sessions.All)
            {
                if (entry.Session is not { } session)
                {
                    continue;
                }

                foreach (var hit in session.Find(term, entry.Title).Take(6))
                {
                    Local.Add(new LocalHit(entry, hit));
                }
            }
        }

        OnPropertyChanged(nameof(HasLocal));
    }

    private void OpenHistoryHit(MemorySearchResultRow row)
    {
        var entry = _sessions.All.FirstOrDefault(e => e.SessionId == row.SessionId);
        if (entry is not null)
        {
            _sessions.Open(entry);
            return;
        }

        // A session this device doesn't hold: it lives on the host, so adopt the pointer and open it.
        var link = _hosts.Links.FirstOrDefault(l => l.IsOnline);
        if (link is null)
        {
            _shell.Toast("That session isn't on a connected host", ToastKind.Warning);
            return;
        }

        _shell.Toast("Opening from history…");
        _ = OpenRemoteAsync(link, row.SessionId);
    }

    private async Task OpenRemoteAsync(HostLink link, string sessionId)
    {
        var host = await link.ConnectAsync().ConfigureAwait(false);
        if (host is null)
        {
            _shell.Toast($"Can't reach {link.Name}", ToastKind.Danger);
            return;
        }

        try
        {
            var view = await host.SubscribeAsync(sessionId).ConfigureAwait(false);
            _shell.Dispatcher.Post(() =>
            {
                var title = view.Info?.WorkingDirectory ?? sessionId;
                var session = _sessions.Build(host, view, title);
                var saved = new SavedSession(link.Name, link.Url, link.Saved.Token, sessionId,
                    view.Info?.AdapterId ?? "agent", title, view.Info?.WorkingDirectory ?? string.Empty);
                _sessions.Adopt(link, session, saved);
            });
        }
        catch (Exception ex)
        {
            _shell.Toast("Couldn't open that session: " + ex.Message, ToastKind.Danger);
        }
    }
}
