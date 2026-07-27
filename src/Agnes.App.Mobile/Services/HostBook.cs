using Agnes.Client;
using Agnes.Ui.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Agnes.App.Mobile.Services;

/// <summary>
/// One paired host and its live connection. Connecting is idempotent and shared: several sessions on
/// the same host reuse a single link, which is what keeps a phone on a flaky network to one reconnect
/// loop rather than one per session.
/// </summary>
public sealed partial class HostLink : ObservableObject
{
    private readonly IAgnesConnector _connector;
    private readonly IUiDispatcher _dispatcher;
    private Task<IAgnesHost?>? _connecting;

    public HostLink(SavedHost saved, IAgnesConnector connector, IUiDispatcher dispatcher)
    {
        Saved = saved;
        _connector = connector;
        _dispatcher = dispatcher;
    }

    public SavedHost Saved { get; private set; }

    public string Name => Saved.Name;
    public string Url => Saved.Url;

    /// <summary>
    /// An HTTP client that trusts this host the way its hub connection does. A saved host is usually
    /// self-signed and pinned by fingerprint, so a REST management call made with a default client fails the
    /// handshake even though the session it sits beside is connected — every such call goes through here.
    /// </summary>
    public HttpClient Http => Agnes.Client.AgnesHttp.For(Saved.Fingerprint);

    /// <summary>The connected host, or null until <see cref="ConnectAsync"/> succeeds.</summary>
    public IAgnesHost? Host { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOnline))]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private AgnesConnectionState _state = AgnesConnectionState.Disconnected;

    /// <summary>The last connection failure, shown on the host row so a wrong address is visible rather
    /// than just "offline".</summary>
    [ObservableProperty]
    private string? _error;

    public bool IsOnline => State == AgnesConnectionState.Connected;

    public string StateText => State switch
    {
        AgnesConnectionState.Connected => "Online",
        AgnesConnectionState.Connecting => "Connecting",
        AgnesConnectionState.Reconnecting => "Reconnecting",
        _ => Error is null ? "Offline" : "Unreachable",
    };

    /// <summary>Whether this is a built-in host the user can't remove (the offline demo).</summary>
    public bool IsBuiltIn => DemoHost.IsDemo(Url);

    public void Rename(string name) => Saved = Saved with { Name = name };

    /// <summary>Connects (or returns the existing connection). Concurrent callers share one attempt, so a
    /// screen that needs the host and a background restore that also needs it don't dial twice.</summary>
    public Task<IAgnesHost?> ConnectAsync()
    {
        if (Host is { State: AgnesConnectionState.Connected })
        {
            return Task.FromResult<IAgnesHost?>(Host);
        }

        return _connecting ??= RunConnectAsync();
    }

    private async Task<IAgnesHost?> RunConnectAsync()
    {
        _dispatcher.Post(() => { State = AgnesConnectionState.Connecting; Error = null; });
        try
        {
            // The pin learned at pairing is what authenticates a self-signed host, on every reconnect and
            // not just the first one.
            var host = await _connector.ConnectAsync(Saved.Url, Saved.Token, Saved.Fingerprint)
                .ConfigureAwait(false);
            host.StateChanged += s => _dispatcher.Post(() => State = s);
            _dispatcher.Post(() =>
            {
                Host = host;
                State = host.State;
            });
            return host;
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() =>
            {
                State = AgnesConnectionState.Disconnected;
                Error = ex.Message;
            });
            return null;
        }
        finally
        {
            _connecting = null;
        }
    }
}

/// <summary>The device's paired hosts, and the connections to them.</summary>
public sealed class HostBook
{
    private readonly IAgnesConnector _connector;
    private readonly IUiDispatcher _dispatcher;
    private readonly List<HostLink> _links = [];

    public HostBook(IAgnesConnector connector, IUiDispatcher dispatcher)
    {
        _connector = connector;
        _dispatcher = dispatcher;

        // The offline demo host is always present: the app has something to show before you have
        // anywhere to connect to, which is the difference between "empty app" and "try it now".
        _links.Add(new HostLink(DemoHost.Saved, connector, dispatcher));
        foreach (var saved in HostRegistry.Load())
        {
            _links.Add(new HostLink(saved, connector, dispatcher));
        }
    }

    public IReadOnlyList<HostLink> Links => _links;

    /// <summary>The paired (non-demo) hosts.</summary>
    public IEnumerable<HostLink> Real => _links.Where(l => !l.IsBuiltIn);

    public HostLink? Find(string url)
        => _links.FirstOrDefault(l => string.Equals(l.Url, url, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds (or replaces) a paired host and persists it.</summary>
    public HostLink Add(SavedHost saved)
    {
        var existing = Find(saved.Url);
        if (existing is not null)
        {
            _links.Remove(existing);
        }

        var link = new HostLink(saved, _connector, _dispatcher);
        _links.Add(link);
        Persist();
        return link;
    }

    /// <summary>Forgets a paired host. The built-in demo can't be removed.</summary>
    public void Remove(HostLink link)
    {
        if (link.IsBuiltIn)
        {
            return;
        }

        _links.Remove(link);
        Persist();
        _ = _connector.RemoveAsync(link.Url);
    }

    public void Persist() => HostRegistry.Save(Real.Select(l => l.Saved));

    /// <summary>Connects every paired host, in parallel; failures leave that link marked unreachable.</summary>
    public Task ConnectAllAsync() => Task.WhenAll(_links.Select(l => l.ConnectAsync()));
}

/// <summary>The built-in, offline simulated host.</summary>
public static class DemoHost
{
    public const string Url = "sim://demo";

    public static SavedHost Saved { get; } = new("Demo (offline)", Url, string.Empty);

    public static bool IsDemo(string url) => url.StartsWith("sim:", StringComparison.OrdinalIgnoreCase);
}
