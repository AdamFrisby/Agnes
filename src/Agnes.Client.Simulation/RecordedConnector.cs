using Agnes.Client;
using Agnes.Recording;

namespace Agnes.Client.Simulation;

/// <summary>Serves a <see cref="RecordedHost"/> that plays back the fixtures in a directory.</summary>
public sealed class RecordedConnector : IAgnesConnector
{
    private readonly string _directory;
    private readonly double _speed;
    private RecordedHost? _host;

    public RecordedConnector(string directory, double speed = 1.0)
    {
        _directory = directory;
        _speed = speed;
    }

    public IReadOnlyCollection<IAgnesHost> Hosts => _host is null ? [] : [_host];

    public async Task<IAgnesHost> ConnectAsync(string hostUrl, string token, CancellationToken cancellationToken = default)
    {
        _host ??= new RecordedHost(hostUrl, RecordingStore.LoadDirectory(_directory), _speed);
        if (_host.State != AgnesConnectionState.Connected)
        {
            await _host.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }

        return _host;
    }

    public async Task RemoveAsync(string hostUrl)
    {
        if (_host is { } host && string.Equals(host.HostUrl, hostUrl, StringComparison.Ordinal))
        {
            _host = null;
            await host.DisposeAsync().ConfigureAwait(false);
        }
    }
}
