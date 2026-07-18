using System.Collections.Concurrent;
using Agnes.Abstractions;
using Agnes.Protocol;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;

namespace Agnes.Client;

/// <summary>
/// A live connection to one Agnes host: invokes the wire contract, receives pushed events,
/// and maintains a <see cref="SessionView"/> per subscribed session (with snapshot+tail and
/// automatic reconnect). Multiple of these are pooled by <see cref="AgnesClient"/>.
/// </summary>
public sealed class HostConnection : IAsyncDisposable
{
    private readonly HubConnection _hub;
    private readonly ConcurrentDictionary<string, SessionView> _views = new();

    public HostConnection(string hostUrl, string token, Action<HttpConnectionOptions>? configureHttp = null)
    {
        HostUrl = hostUrl.TrimEnd('/');
        var url = $"{HostUrl}{WireProtocol.HubPath}?{WireProtocol.TokenParameter}={Uri.EscapeDataString(token)}";
        _hub = new HubConnectionBuilder()
            .WithUrl(url, options => configureHttp?.Invoke(options))
            .WithAutomaticReconnect()
            .Build();

        _hub.On<string, SessionEvent>(nameof(IAgnesClient.OnSessionEvent), (sessionId, @event) =>
        {
            if (_views.TryGetValue(sessionId, out var view))
            {
                view.Apply(@event);
            }
        });

        _hub.On<IReadOnlyList<AgentInfo>>(nameof(IAgnesClient.OnAgentsChanged), agents =>
        {
            AgentsChanged?.Invoke(agents);
        });

        // On reconnect, re-subscribe each view from its last applied sequence.
        _hub.Reconnected += async _ =>
        {
            foreach (var view in _views.Values)
            {
                var snapshot = await _hub.InvokeAsync<SessionSnapshot>(
                    nameof(IAgnesServer.Subscribe), view.SessionId, view.LastSequence);
                view.ApplySnapshot(snapshot);
            }
        };
    }

    public string HostUrl { get; }

    public HubConnectionState State => _hub.State;

    public event Action<IReadOnlyList<AgentInfo>>? AgentsChanged;

    public Task ConnectAsync(CancellationToken cancellationToken = default) => _hub.StartAsync(cancellationToken);

    public Task<HostInfo> GetHostInfoAsync()
        => _hub.InvokeAsync<HostInfo>(nameof(IAgnesServer.GetHostInfo));

    public Task<IReadOnlyList<AgentInfo>> ListAgentsAsync()
        => _hub.InvokeAsync<IReadOnlyList<AgentInfo>>(nameof(IAgnesServer.ListAgents));

    public Task<SessionInfo> OpenSessionAsync(string adapterId, string workingDirectory)
        => _hub.InvokeAsync<SessionInfo>(nameof(IAgnesServer.OpenSession), new OpenSessionRequest(adapterId, workingDirectory));

    /// <summary>Subscribes to a session, returning a live view seeded from a snapshot.</summary>
    public async Task<SessionView> SubscribeAsync(string sessionId, long since = 0)
    {
        var view = _views.GetOrAdd(sessionId, id => new SessionView(id));
        var snapshot = await _hub.InvokeAsync<SessionSnapshot>(nameof(IAgnesServer.Subscribe), sessionId, since);
        view.ApplySnapshot(snapshot);
        return view;
    }

    public Task PromptAsync(string sessionId, IReadOnlyList<ContentBlock> content)
        => _hub.InvokeAsync(nameof(IAgnesServer.Prompt), new PromptRequest(sessionId, content));

    public Task RespondPermissionAsync(string sessionId, string requestId, string optionId)
        => _hub.InvokeAsync(nameof(IAgnesServer.RespondPermission), new PermissionResponseRequest(sessionId, requestId, optionId));

    public async ValueTask DisposeAsync() => await _hub.DisposeAsync();
}
