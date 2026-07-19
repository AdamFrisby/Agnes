using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Agnes.Sandbox.Incus;

/// <summary>
/// Provisions Incus VMs to run CLI agents in. Sequence (ported from CodeyBox): <c>init --vm</c> →
/// <c>config device add … nic</c> → <c>config set … user.user-data</c> (cloud-init) → <c>start</c> →
/// wait for the guest ready marker. VMs are managed via <c>user.agnes.*</c> config keys and persist
/// until explicitly deleted.
/// </summary>
public sealed class IncusSandboxProvider : ISandboxProvider
{
    public const string ProviderId = "incus";

    private readonly IncusOptions _options;
    private readonly IncusCliRunner _cli;
    private readonly ILogger<IncusSandboxProvider> _logger;

    public IncusSandboxProvider(IncusOptions options, ILoggerFactory loggerFactory)
    {
        _options = options;
        _logger = loggerFactory.CreateLogger<IncusSandboxProvider>();
        _cli = new IncusCliRunner(_logger);
    }

    public string Name => ProviderId;

    public async Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken cancellationToken = default)
    {
        var name = CreateInstanceName();
        var image = string.IsNullOrWhiteSpace(spec.ImageReference) ? _options.DefaultImage : spec.ImageReference;
        var bridge = string.IsNullOrWhiteSpace(spec.NetworkBridge) ? _options.Bridge : spec.NetworkBridge!;

        _logger.LogInformation("Provisioning Incus sandbox {Name} ({Image})", name, image);
        await _cli.RunCheckedAsync("init", IncusCommandBuilder.BuildInit(_options, image, name, spec.Limits), cancellationToken: cancellationToken).ConfigureAwait(false);
        await _cli.RunCheckedAsync("nic add", IncusCommandBuilder.BuildNicAdd(_options, name, bridge), cancellationToken: cancellationToken).ConfigureAwait(false);
        await _cli.RunCheckedAsync("cloud-init", IncusCommandBuilder.BuildConfigSetStdin(_options, name, "user.user-data"), IncusGuest.CloudInit(_options), cancellationToken).ConfigureAwait(false);

        // Optional bind mount of the host working directory.
        if (spec.HostWorkingDirectory is { Length: > 0 } hostDir && Directory.Exists(hostDir))
        {
            await _cli.RunCheckedAsync("mount workdir",
                IncusCommandBuilder.BuildDiskAdd(_options, name, "agnes-work", hostDir, spec.WorkingDirectory, readOnly: false),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        await _cli.RunCheckedAsync("start", IncusCommandBuilder.BuildStart(_options, name), cancellationToken: cancellationToken).ConfigureAwait(false);
        await WaitForGuestReadyAsync(name, cancellationToken).ConfigureAwait(false);

        return new IncusSandbox(name, _options, _cli, _logger);
    }

    public async Task<IReadOnlyList<SandboxInfo>> ListManagedAsync(CancellationToken cancellationToken = default)
    {
        var (code, stdout, _) = await _cli.RunAsync(IncusCommandBuilder.BuildListJson(_options), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (code != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            return [];
        }

        var result = new List<SandboxInfo>();
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var id = el.TryGetProperty("name", out var n) ? n.GetString() : null;
                var status = el.TryGetProperty("status", out var s) ? s.GetString() : null;
                if (id is not null)
                {
                    result.Add(new SandboxInfo(ProviderId, id, MapState(status)));
                }
            }
        }
        catch (JsonException)
        {
            // ignore malformed output
        }

        return result;
    }

    private async Task WaitForGuestReadyAsync(string name, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + _options.GuestReadyTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var probe = IncusCommandBuilder.BuildExec(_options, name, ["test", "-f", "/run/agnes/ready"], workingDirectory: null, asUser: false);
            var (code, _, _) = await _cli.RunAsync(probe, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (code == 0)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"Incus sandbox {name} did not become ready within {_options.GuestReadyTimeout}.");
    }

    private static SandboxState MapState(string? status) => status switch
    {
        "Running" => SandboxState.Running,
        "Frozen" => SandboxState.Paused,
        _ => SandboxState.Stopped,
    };

    internal static string CreateInstanceName()
    {
        Span<byte> bytes = stackalloc byte[10];
        RandomNumberGenerator.Fill(bytes);
        return "agnes-" + Convert.ToHexStringLower(bytes);
    }
}
