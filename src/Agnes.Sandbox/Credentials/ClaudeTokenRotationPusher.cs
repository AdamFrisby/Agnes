using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Agnes.Sandbox.Credentials;

/// <summary>
/// Keeps live sandboxes' Claude access tokens fresh (ported from CodeyBox's ClaudeTokenRotationPusher).
/// A sandboxed VM can outlive its access token; when the host <c>claude</c> CLI rotates the token
/// (its file changes), re-inject the fresh sanitised bundle into every registered sandbox. The
/// refresh_token is never pushed — the host CLI stays the sole refresher.
/// </summary>
public sealed class ClaudeTokenRotationPusher : IDisposable
{
    private readonly ClaudeCredentialProvider _provider;
    private readonly ILogger<ClaudeTokenRotationPusher> _logger;
    private readonly ConcurrentDictionary<string, ISandbox> _active = new();
    private readonly FileSystemWatcher? _watcher;

    public ClaudeTokenRotationPusher(ClaudeCredentialProvider provider, ILogger<ClaudeTokenRotationPusher> logger)
    {
        _provider = provider;
        _logger = logger;

        var credsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");
        var dir = Path.GetDirectoryName(credsPath);
        if (dir is not null && Directory.Exists(dir))
        {
            _watcher = new FileSystemWatcher(dir, ".credentials.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (_, _) => _ = OnRotatedAsync();
            _watcher.Created += (_, _) => _ = OnRotatedAsync();
        }
    }

    /// <summary>Registers a live sandbox to receive token rotations; dispose to unregister.</summary>
    public IDisposable RegisterActiveSandbox(ISandbox sandbox)
    {
        _active[sandbox.Id] = sandbox;
        return new Registration(this, sandbox.Id);
    }

    private async Task OnRotatedAsync()
    {
        if (_active.IsEmpty)
        {
            return;
        }

        try
        {
            var credential = await _provider.GetAsync("claude-code").ConfigureAwait(false);
            if (credential.Files.Count == 0)
            {
                return;
            }

            foreach (var sandbox in _active.Values)
            {
                try
                {
                    await sandbox.MaterializeCredentialAsync(credential with { EnvironmentVariables = new Dictionary<string, string>() }).ConfigureAwait(false);
                    _logger.LogInformation("Refreshed Claude token in sandbox {Id}", sandbox.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not push rotated token to sandbox {Id}", sandbox.Id);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Token rotation handling failed");
        }
    }

    public void Dispose() => _watcher?.Dispose();

    private sealed class Registration(ClaudeTokenRotationPusher owner, string id) : IDisposable
    {
        public void Dispose() => owner._active.TryRemove(id, out _);
    }
}
