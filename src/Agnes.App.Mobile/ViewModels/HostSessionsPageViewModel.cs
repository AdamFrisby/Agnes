using Agnes.App.Mobile.Services;
using Agnes.Client;
using Agnes.Ui.Core.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Agnes.App.Mobile.ViewModels;

/// <summary>
/// What's already running on a host you just paired with.
///
/// Pairing is never the goal — the goal is the work. Most of the time that work already exists: an agent
/// mid-turn on the desktop you walked away from, or one blocked on a permission an hour ago. So the first
/// screen after pairing offers those sessions, with starting a new one as the alternative rather than the
/// only option. A host with nothing running skips this screen entirely (see
/// <see cref="ConnectPageViewModel"/>) — an empty list is not worth a tap.
/// </summary>
public sealed partial class HostSessionsPageViewModel : PageViewModel
{
    private readonly IAppShell _shell;
    private readonly HostBook _hosts;
    private readonly SessionsViewModel _sessions;
    private readonly HostLink _link;

    public HostSessionsPageViewModel(IAppShell shell, HostBook hosts, SessionsViewModel sessions, HostLink link)
    {
        _shell = shell;
        _hosts = hosts;
        _sessions = sessions;
        _link = link;

        Catalog = new SessionCatalogViewModel(
            () => link.Host is { } host ? [host] : [],
            shell.Dispatcher,
            id => sessions.All.Any(e => e.SessionId == id));
        Catalog.AttachRequested += row => _ = OpenAsync(row);

        OpenCommand = new AsyncRelayCommand<CatalogSessionRow>(row => row is null ? Task.CompletedTask : OpenAsync(row));
        NewSessionCommand = new RelayCommand(() =>
        {
            _shell.Haptics.Tick();
            _shell.Push(new NewSessionPageViewModel(_shell, _hosts, _sessions));
        });
        RefreshCommand = new AsyncRelayCommand(() => Catalog.LoadAsync());
    }

    public override string Title => "On this host";

    public override string? Subtitle => _link.Name;

    /// <summary>The host's session catalogue — only what this device is allowed to reach.</summary>
    public SessionCatalogViewModel Catalog { get; }

    public IAsyncRelayCommand<CatalogSessionRow> OpenCommand { get; }
    public IRelayCommand NewSessionCommand { get; }
    public IAsyncRelayCommand RefreshCommand { get; }

    [ObservableProperty]
    private string _status = string.Empty;

    public bool HasStatus => Status.Length > 0;

    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    /// <summary>Refreshes on every appearance: coming back from a session is exactly when the list is
    /// most likely to be out of date.</summary>
    public override void OnAppearing() => _ = Catalog.LoadAsync();

    /// <summary>
    /// Joins a listed session: subscribe, adopt it into this device's list, and open it. Nothing is created
    /// host-side — a session already open on this device is simply brought to the front instead.
    /// </summary>
    private async Task OpenAsync(CatalogSessionRow row)
    {
        var existing = _sessions.All.FirstOrDefault(e => e.SessionId == row.SessionId);
        if (existing is not null)
        {
            _sessions.Open(existing);
            return;
        }

        _shell.Dispatcher.Post(() => Status = $"Joining {row.Title}…");
        try
        {
            var host = await _link.ConnectAsync().ConfigureAwait(false)
                ?? throw new InvalidOperationException("host unreachable");
            var view = await host.SubscribeAsync(row.SessionId).ConfigureAwait(false);

            _shell.Dispatcher.Post(() =>
            {
                Status = string.Empty;
                var session = _sessions.Build(host, view, row.Title);
                var saved = new SavedSession(_link.Name, _link.Url, _link.Saved.Token, row.SessionId,
                    row.AdapterId, row.Title, row.WorkingDirectory);
                _shell.Haptics.Success();
                Catalog.MarkOpen(row.SessionId);
                _sessions.Adopt(_link, session, saved);
            });
        }
        catch (Exception ex)
        {
            _shell.Dispatcher.Post(() => Status = "Couldn't join that session: " + ex.Message);
        }
    }
}
