namespace Agnes.Client;

/// <summary>The real connector: pools SignalR <see cref="HostConnection"/>s over <see cref="AgnesClient"/>.</summary>
public sealed class SignalRConnector : IAgnesConnector
{
    private readonly AgnesClient _client;

    public SignalRConnector(AgnesClient? client = null) => _client = client ?? new AgnesClient();

    public IReadOnlyCollection<IAgnesHost> Hosts => _client.Hosts.Cast<IAgnesHost>().ToArray();

    public async Task<IAgnesHost> ConnectAsync(string hostUrl, string token, CancellationToken cancellationToken = default)
        => await _client.AddHostAsync(hostUrl, token, null, cancellationToken).ConfigureAwait(false);

    public Task RemoveAsync(string hostUrl) => _client.RemoveHostAsync(hostUrl);
}
