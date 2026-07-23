using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Agnes.Client;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Agnes.Ui.Core.ViewModels;

/// <summary>
/// The multi-server surface (connectivity/02): a merged, host-aware view over every host the client is
/// connected to at once — possibly across mixed transports (one Direct LAN, one via relay, one via Tailscale).
/// It shows a <see cref="Hosts"/> list with each host's independent connection state and a <see cref="Sessions"/>
/// aggregate that unions the sessions of all hosts, every row tagged with the host it lives on so a same-named
/// session on two servers is never conflated. Adding, removing, or reconnecting a host is per-host and does not
/// disturb the others.
/// <para>
/// Framework-agnostic and additive: it's a read-mostly aggregation over the same <see cref="IAgnesConnector"/>
/// the app already drives per tab, so the desktop app and the offline simulation exercise it identically and a
/// single-host user sees exactly one host and its sessions — no behaviour change.
/// </para>
/// </summary>
public sealed class MultiHostViewModel : ObservableObject
{
    private readonly IAgnesConnector _connector;
    private readonly IUiDispatcher _dispatcher;
    private readonly HashSet<IAgnesHost> _wired = [];

    public MultiHostViewModel(IAgnesConnector connector, IUiDispatcher dispatcher)
    {
        _connector = connector;
        _dispatcher = dispatcher;

        AddHostCommand = new AsyncRelayCommand<(string Url, string Token)>(p => AddHostAsync(p.Url, p.Token));
        RemoveHostCommand = new AsyncRelayCommand<string>(RemoveHostAsync);
        ReconnectHostCommand = new AsyncRelayCommand<string>(ReconnectHostAsync);
        RefreshCommand = new RelayCommand(Refresh);
    }

    /// <summary>One row per connected host, with its identity, transport, live state, and session count.</summary>
    public ObservableCollection<HostRow> Hosts { get; } = [];

    /// <summary>The union of sessions across every connected host, each tagged with its host.</summary>
    public ObservableCollection<HostSessionRow> Sessions { get; } = [];

    /// <summary>How many hosts are currently connected.</summary>
    public int HostCount => Hosts.Count;

    /// <summary>How many sessions are held across all hosts.</summary>
    public int SessionCount => Sessions.Count;

    public ICommand AddHostCommand { get; }
    public ICommand RemoveHostCommand { get; }
    public ICommand ReconnectHostCommand { get; }
    public ICommand RefreshCommand { get; }

    /// <summary>Connects (or re-uses) a host by address — the transport is chosen from the address scheme, so a
    /// LAN URL, an <c>agnes-relay://</c> address, or a tailnet name are all added the same way — then refreshes
    /// the aggregate.</summary>
    public async Task<IAgnesHost> AddHostAsync(string hostUrl, string token)
    {
        var host = await _connector.ConnectAsync(hostUrl, token).ConfigureAwait(false);
        _dispatcher.Post(Refresh);
        return host;
    }

    /// <summary>Removes a single host (by its id) from the pool; the other hosts stay connected.</summary>
    public async Task RemoveHostAsync(string? hostId)
    {
        var host = FindById(hostId);
        if (host is null)
        {
            return;
        }

        await _connector.RemoveAsync(host.HostUrl).ConfigureAwait(false);
        _dispatcher.Post(Refresh);
    }

    /// <summary>Reconnects a single host (by its id) in place — independently of every other host.</summary>
    public async Task ReconnectHostAsync(string? hostId)
    {
        var host = FindById(hostId);
        if (host is null)
        {
            return;
        }

        if (host.State != AgnesConnectionState.Connected)
        {
            await host.ConnectAsync().ConfigureAwait(false);
        }

        _dispatcher.Post(Refresh);
    }

    /// <summary>Rebuilds the host list and the session aggregate from the connector's current hosts. Cheap
    /// enough to call on demand — when a host is added/removed, when a session opens, or on a state change.</summary>
    public void Refresh()
    {
        var hosts = _connector.Hosts.ToArray();
        foreach (var host in hosts)
        {
            // Keep each host's state changes reflected in the aggregate, wiring a host at most once.
            if (_wired.Add(host))
            {
                host.StateChanged += _ => _dispatcher.Post(Refresh);
            }
        }

        Hosts.Clear();
        Sessions.Clear();
        foreach (var host in hosts.OrderBy(h => h.HostId, StringComparer.Ordinal))
        {
            Hosts.Add(new HostRow(host));
            foreach (var session in host.Sessions)
            {
                Sessions.Add(new HostSessionRow(host, session));
            }
        }

        OnPropertyChanged(nameof(HostCount));
        OnPropertyChanged(nameof(SessionCount));
    }

    private IAgnesHost? FindById(string? hostId)
        => hostId is null ? null : _connector.Hosts.FirstOrDefault(h => string.Equals(h.HostId, hostId, StringComparison.Ordinal));
}

/// <summary>One connected host as a bindable row, tagged with the underlying connection so the shell can route
/// per-host actions (reconnect, remove, jump) to it.</summary>
public sealed class HostRow
{
    public HostRow(IAgnesHost host) => Host = host;

    public IAgnesHost Host { get; }

    public string HostId => Host.HostId;
    public string HostUrl => Host.HostUrl;
    public ClientTransportKind Transport => Host.Transport;

    /// <summary>The host-side transport id this host is reached through (<c>direct</c> / <c>agnes-relay</c> /
    /// <c>tailscale</c>) — a stable badge for the row.</summary>
    public string TransportId => ClientTransport.ProviderId(Host.Transport);

    public AgnesConnectionState State => Host.State;
    public bool IsConnected => Host.State == AgnesConnectionState.Connected;
    public int SessionCount => Host.Sessions.Count;
}

/// <summary>One session in the cross-host aggregate as a bindable row, tagged with the host it belongs to.</summary>
public sealed class HostSessionRow
{
    public HostSessionRow(IAgnesHost host, SessionView session)
    {
        Host = host;
        Session = session;
    }

    public IAgnesHost Host { get; }
    public SessionView Session { get; }

    public string HostId => Host.HostId;
    public string HostUrl => Host.HostUrl;
    public ClientTransportKind Transport => Host.Transport;
    public string SessionId => Session.SessionId;
}
