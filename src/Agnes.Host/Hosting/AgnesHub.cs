using Agnes.Host.Sessions;
using Agnes.Protocol;
using Microsoft.AspNetCore.SignalR;

namespace Agnes.Host.Hosting;

/// <summary>SignalR endpoint implementing the Agnes wire contract (<see cref="IAgnesServer"/>).</summary>
public sealed class AgnesHub : Hub<IAgnesClient>, IAgnesServer
{
    private readonly SessionManager _sessions;
    private readonly HostIdentity _identity;
    private readonly DeviceTokenStore _tokens;

    public AgnesHub(SessionManager sessions, HostIdentity identity, DeviceTokenStore tokens)
    {
        _sessions = sessions;
        _identity = identity;
        _tokens = tokens;
    }

    public override async Task OnConnectedAsync()
    {
        var token = Context.GetHttpContext()?.Request.Query[WireProtocol.TokenParameter].ToString();
        if (!_tokens.IsValid(token))
        {
            Context.Abort();
            return;
        }

        await base.OnConnectedAsync();
    }

    public Task<HostInfo> GetHostInfo()
        => Task.FromResult(new HostInfo(_identity.HostId, _identity.DisplayName, _identity.Version));

    public Task<IReadOnlyList<AgentInfo>> ListAgents()
        => Task.FromResult(_sessions.ListAgents());

    public Task<SessionInfo> OpenSession(OpenSessionRequest request)
        => _sessions.OpenSessionAsync(request.AdapterId, request.WorkingDirectory);

    public async Task<SessionSnapshot> Subscribe(string sessionId, long sinceSequence)
    {
        // Join the group BEFORE snapshotting so no event is missed; the client dedupes by
        // sequence, so an event that both lands in the snapshot and is broadcast is harmless.
        await Groups.AddToGroupAsync(Context.ConnectionId, sessionId);
        return await _sessions.GetSnapshotAsync(sessionId, sinceSequence);
    }

    public Task Unsubscribe(string sessionId)
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, sessionId);

    public Task Prompt(PromptRequest request)
        => _sessions.PromptAsync(request.SessionId, request.Content);

    public Task Cancel(string sessionId)
        => _sessions.CancelAsync(sessionId);

    public Task RespondPermission(PermissionResponseRequest response)
        => _sessions.RespondPermissionAsync(response.SessionId, response.RequestId, response.OptionId);
}
