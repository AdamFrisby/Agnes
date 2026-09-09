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
public sealed class IncusSandboxProvider : ISandboxProvider, ISandboxImageBuilder, ISandboxCloner
{
    public const string ProviderId = "incus";

    private readonly IncusOptions _options;
    private readonly IIncusCliRunner _cli;
    private readonly ILogger<IncusSandboxProvider> _logger;
    private readonly Graphical.DisplayBus _displayBus;

    public IncusSandboxProvider(IncusOptions options, ILoggerFactory loggerFactory)
        : this(options, loggerFactory, null)
    {
    }

    // Test seam: inject a fake runner to exercise orchestration (bake sequence) without real incus.
    internal IncusSandboxProvider(IncusOptions options, ILoggerFactory loggerFactory, IIncusCliRunner? cli)
    {
        _options = options;
        _logger = loggerFactory.CreateLogger<IncusSandboxProvider>();
        _cli = cli ?? new IncusCliRunner(_logger);
        _displayBus = new Graphical.DisplayBus(options, _logger);
    }

    public string Name => ProviderId;

    public SandboxIsolationLevel IsolationLevel => SandboxIsolationLevel.DedicatedKernel;

    public async Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var name = CreateInstanceName();
        var display = spec.Display;
        var image = string.IsNullOrWhiteSpace(spec.ImageReference)
            ? (display is null ? _options.DefaultImage : _options.GraphicalImage)
            : spec.ImageReference;
        var bridge = _options.ResolveBridge(spec.NetworkBridge, spec.NetworkProfile);

        // Resolve resource caps: the host-configured defaults, the graphical floor if a display was asked
        // for (a bigger image needs a bigger volume — Incus refuses to launch otherwise), then any
        // per-session override on top, which stays the last word.
        var limits = _options.DefaultLimits
            .With(display is null ? null : _options.GraphicalResourceOverride)
            .With(spec.ResourceOverride);

        _logger.LogInformation("Provisioning Incus sandbox {Name} ({Image}) — {Cpu} CPU / {MemGiB} GiB / {DiskGiB} GiB",
            name, image, limits.CpuCount, limits.MemoryBytes / (1024 * 1024 * 1024), limits.DiskBytes / (1024 * 1024 * 1024));
        await _cli.RunCheckedAsync("init", IncusCommandBuilder.BuildInit(_options, image, name, limits), cancellationToken: cancellationToken).ConfigureAwait(false);
        await _cli.RunCheckedAsync("nic add", IncusCommandBuilder.BuildNicAdd(_options, name, bridge), cancellationToken: cancellationToken).ConfigureAwait(false);
        await _cli.RunCheckedAsync("cloud-init", IncusCommandBuilder.BuildConfigSetStdin(_options, name, "user.user-data"), IncusGuest.CloudInit(_options, display), cancellationToken).ConfigureAwait(false);
        if (display is not null)
        {
            await ConfigureDisplayAsync(name, cancellationToken).ConfigureAwait(false);
        }

        // Optional bind mount of the host working directory.
        if (spec.HostWorkingDirectory is { Length: > 0 } hostDir && Directory.Exists(hostDir))
        {
            await _cli.RunCheckedAsync("mount workdir",
                IncusCommandBuilder.BuildDiskAdd(_options, name, "agnes-work", hostDir, spec.WorkingDirectory, readOnly: false),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        await _cli.RunCheckedAsync("start", IncusCommandBuilder.BuildStart(_options, name), cancellationToken: cancellationToken).ConfigureAwait(false);
        await WaitForGuestReadyAsync(name, cancellationToken).ConfigureAwait(false);

        return CreateHandle(name, display);
    }

    /// <summary>
    /// Points this instance's QEMU at its own private D-Bus display, and widens the AppArmor profile
    /// Incus generates just enough to let it get there. Both keys are applied while the VM is stopped:
    /// <c>raw.qemu</c> is only read at start, and a profile change needs a reload anyway.
    /// </summary>
    private async Task ConfigureDisplayAsync(string name, CancellationToken cancellationToken)
    {
        await _displayBus.EnsureRunningAsync(name, cancellationToken).ConfigureAwait(false);
        await _cli.RunCheckedAsync("display raw.qemu",
            IncusCommandBuilder.BuildConfigSet(_options, name, "raw.qemu", _displayBus.RawQemuFor(name)),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await _cli.RunCheckedAsync("display raw.apparmor",
            IncusCommandBuilder.BuildConfigSet(_options, name, "raw.apparmor", _displayBus.RawAppArmor()),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private ISandbox CreateHandle(string name, GraphicalDisplay? display)
        => display is null
            ? new IncusSandbox(name, _options, _cli, _logger)
            : new Graphical.GraphicalIncusSandbox(name, _options, _cli, _logger, _displayBus, display);

    public async Task<ISandbox> CloneAsync(string sourceVmName, string newHostWorkingDirectory, SandboxSpec spec, CancellationToken cancellationToken = default)
    {
        var name = CreateInstanceName();
        // A short snapshot name on the source: point-in-time disk to copy from (CoW on ZFS/btrfs).
        var snapshot = "fork" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        _logger.LogInformation("Cloning Incus sandbox {Source} -> {Name} (snapshot {Snapshot})", sourceVmName, name, snapshot);

        await _cli.RunCheckedAsync("snapshot", IncusCommandBuilder.BuildSnapshotCreate(_options, sourceVmName, snapshot), cancellationToken: cancellationToken).ConfigureAwait(false);
        try
        {
            await _cli.RunCheckedAsync("cow copy", IncusCommandBuilder.BuildCopyFromSnapshot(_options, sourceVmName, snapshot, name), cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // The snapshot has served its purpose whether or not the copy succeeded; don't leak it.
            await _cli.RunAsync(IncusCommandBuilder.BuildSnapshotDelete(_options, sourceVmName, snapshot), cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        // The copy inherited the source's work mount (pointing at the OLD host dir). Swap it for the
        // forked working folder before boot (VM disk devices are set while stopped).
        await _cli.RunAsync(IncusCommandBuilder.BuildDeviceRemove(_options, name, "agnes-work"), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (newHostWorkingDirectory is { Length: > 0 } hostDir && Directory.Exists(hostDir))
        {
            await _cli.RunCheckedAsync("mount workdir",
                IncusCommandBuilder.BuildDiskAdd(_options, name, "agnes-work", hostDir, spec.WorkingDirectory, readOnly: false),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        // A graphical clone inherited raw.qemu pointing at the *source's* bus socket, which belongs to a
        // different VM. Re-point it at its own before it ever starts.
        if (spec.Display is not null)
        {
            await ConfigureDisplayAsync(name, cancellationToken).ConfigureAwait(false);
        }

        await _cli.RunCheckedAsync("start", IncusCommandBuilder.BuildStart(_options, name), cancellationToken: cancellationToken).ConfigureAwait(false);

        // Cloud-init already ran on the source (once per instance-id), so it won't re-run on the clone —
        // and /run is tmpfs, so the /run/agnes/ready marker is gone after this fresh boot. The rootfs
        // (agnes user, run wrapper, /work, packages) persisted, so once the guest agent answers we just
        // recreate the runtime dir + marker ourselves rather than waiting on cloud-init.
        await WaitForGuestAgentAsync(name, cancellationToken).ConfigureAwait(false);
        await _cli.RunCheckedAsync("ready marker",
            IncusCommandBuilder.BuildExec(_options, name, ["sh", "-c", "mkdir -p /run/agnes && chmod 0755 /run/agnes && touch /run/agnes/ready"], workingDirectory: null, asUser: false),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return CreateHandle(name, spec.Display);
    }

    public async Task<ISandbox> AttachAsync(string vmName, SandboxSpec spec, bool start, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var sandbox = CreateHandle(vmName, spec.Display);
        if (start)
        {
            if (spec.Display is not null)
            {
                // The bus has to be listening before QEMU starts: QEMU connects to it during display
                // setup and the VM fails to boot at all if the socket is not there.
                await _displayBus.EnsureRunningAsync(vmName, cancellationToken).ConfigureAwait(false);
            }

            // Tolerant start (RunAsync, not RunChecked) so an already-running VM doesn't throw; then wait
            // for the guest to come up before we re-attach the agent.
            _logger.LogInformation("Reconnecting to Incus sandbox {Name} (start)", vmName);
            await _cli.RunAsync(IncusCommandBuilder.BuildStart(_options, vmName), cancellationToken: cancellationToken).ConfigureAwait(false);
            await WaitForGuestReadyAsync(vmName, cancellationToken).ConfigureAwait(false);
        }

        return sandbox;
    }

    // ---- ISandboxImageBuilder: bake a baseline image ----

    public async Task<bool> ImageExistsAsync(string alias, CancellationToken cancellationToken = default)
    {
        var (code, _, _) = await _cli.RunAsync(
            IncusCommandBuilder.BuildImageInfo(_options, alias), cancellationToken: cancellationToken).ConfigureAwait(false);
        return code == 0;
    }

    public async Task BuildImageAsync(SandboxImageManifest manifest, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var name = CreateInstanceName();
        _logger.LogInformation("Baking sandbox image {Alias} in {Name} from {Base}", manifest.Alias, name, manifest.BaseImage);
        try
        {
            progress?.Report($"Provisioning bake VM from {manifest.BaseImage}…");
            await _cli.RunCheckedAsync("bake init", IncusCommandBuilder.BuildInit(_options, manifest.BaseImage, name, _options.DefaultLimits), cancellationToken: cancellationToken).ConfigureAwait(false);
            await _cli.RunCheckedAsync("bake nic", IncusCommandBuilder.BuildNicAdd(_options, name, _options.Bridge), cancellationToken: cancellationToken).ConfigureAwait(false);
            await _cli.RunCheckedAsync("bake cloud-init", IncusCommandBuilder.BuildConfigSetStdin(_options, name, "user.user-data"), IncusGuest.CloudInit(_options), cancellationToken).ConfigureAwait(false);
            await _cli.RunCheckedAsync("bake start", IncusCommandBuilder.BuildStart(_options, name), cancellationToken: cancellationToken).ConfigureAwait(false);
            await WaitForGuestReadyAsync(name, cancellationToken).ConfigureAwait(false);

            // apt (+ node, + python3-pip when pip packages are requested)
            var apt = new List<string>(manifest.AptPackages.Where(IsSafePackage));
            if (manifest.Node)
            {
                apt.Add("nodejs");
                apt.Add("npm");
            }

            if (manifest.PipPackages.Count > 0)
            {
                apt.Add("python3-pip");
            }

            if (apt.Count > 0)
            {
                await RunStepAsync(name, "apt update", ["apt-get", "update"], progress, cancellationToken).ConfigureAwait(false);
                await RunStepAsync(name, "apt install", ["env", "DEBIAN_FRONTEND=noninteractive", "apt-get", "install", "-y", .. apt], progress, cancellationToken).ConfigureAwait(false);
            }

            var npm = manifest.NpmGlobals.Where(IsSafePackage).ToList();
            npm.AddRange(manifest.Agents.Where(a => a.Source.StartsWith("npm:", StringComparison.Ordinal))
                .Select(a => a.Source["npm:".Length..]).Where(IsSafePackage));
            if (npm.Count > 0)
            {
                await RunStepAsync(name, "npm install", ["npm", "install", "-g", .. npm], progress, cancellationToken).ConfigureAwait(false);
            }

            var pip = manifest.PipPackages.Where(IsSafePackage).ToList();
            if (pip.Count > 0)
            {
                await RunStepAsync(name, "pip install", ["pip3", "install", "--break-system-packages", .. pip], progress, cancellationToken).ConfigureAwait(false);
            }

            // Copy self-contained agent binaries from the host into the guest PATH.
            foreach (var agent in manifest.Agents.Where(a => a.Source.StartsWith("copy:", StringComparison.Ordinal)))
            {
                var binary = agent.Source["copy:".Length..];
                if (!IsSafeBinaryName(binary))
                {
                    progress?.Report($"Skipping agent {agent.AdapterId}: unsupported binary name '{binary}'");
                    continue;
                }

                var hostPath = ResolveHostBinary(binary);
                if (hostPath is null)
                {
                    progress?.Report($"Skipping agent {agent.AdapterId}: '{binary}' not found on the host PATH");
                    continue;
                }

                progress?.Report($"Copying {binary} into the image…");
                await _cli.RunCheckedAsync($"push {binary}",
                    IncusCommandBuilder.BuildFilePushFile(_options, name, hostPath, $"/usr/local/bin/{binary}", "0755"),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            // Reset cloud-init so it re-runs per launch on clones, then publish the stopped VM.
            await RunStepAsync(name, "cloud-init clean", ["cloud-init", "clean", "--logs"], progress, cancellationToken).ConfigureAwait(false);
            progress?.Report("Stopping and publishing the image…");
            await _cli.RunCheckedAsync("bake stop", IncusCommandBuilder.BuildStop(_options, name, (int)_options.VmStopTimeout.TotalSeconds, stateful: false), cancellationToken: cancellationToken).ConfigureAwait(false);
            await _cli.RunCheckedAsync("bake publish", IncusCommandBuilder.BuildPublish(_options, name, manifest.Alias), cancellationToken: cancellationToken).ConfigureAwait(false);
            progress?.Report($"Image {manifest.Alias} is ready.");
            _logger.LogInformation("Baked sandbox image {Alias}", manifest.Alias);
        }
        finally
        {
            // Always remove the throwaway bake VM (best-effort).
            try
            {
                await _cli.RunAsync(IncusCommandBuilder.BuildDelete(_options, name), cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not delete bake VM {Name}", name);
            }
        }
    }

    private async Task RunStepAsync(string instance, string what, IReadOnlyList<string> command, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(what + "…");
        void Sink(string line) => progress?.Report(line);
        var (code, _, stderr) = await _cli.RunAsync(
            IncusCommandBuilder.BuildExec(_options, instance, command, workingDirectory: null, asUser: false),
            stdoutChunk: Sink, stderrChunk: Sink, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (code != 0)
        {
            throw new InvalidOperationException($"Bake step '{what}' failed ({code}): {stderr.Trim()}");
        }
    }

    // A package name must not look like a flag or carry control chars (argv is never shell-parsed,
    // but a leading '-' would be read as an apt/npm option).
    private static bool IsSafePackage(string name)
        => !string.IsNullOrWhiteSpace(name) && !name.StartsWith('-') && !name.Any(char.IsControl);

    private static bool IsSafeBinaryName(string name)
        => name.Length is > 0 and <= 64 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    private static string? ResolveHostBinary(string binary)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, binary);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
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

    /// <summary>Waits until the guest agent answers <c>exec</c> (rootfs up + incus-agent connected), used
    /// for clones where cloud-init won't re-run — we then recreate the tmpfs ready marker ourselves.</summary>
    private async Task WaitForGuestAgentAsync(string name, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + _options.GuestReadyTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var probe = IncusCommandBuilder.BuildExec(_options, name, ["true"], workingDirectory: null, asUser: false);
            var (code, _, _) = await _cli.RunAsync(probe, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (code == 0)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"Incus sandbox {name} guest agent did not answer within {_options.GuestReadyTimeout}.");
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

    internal string CreateInstanceName()
    {
        Span<byte> bytes = stackalloc byte[10];
        RandomNumberGenerator.Fill(bytes);
        var name = _options.InstancePrefix + Convert.ToHexStringLower(bytes);
        // Validated here, at the one place names are minted, rather than in whichever incus command is
        // handed the bad name first: a prefix that can't make a legal instance name is a configuration
        // error, and it should say so before a VM is half-created.
        IncusInputValidation.ValidateInstanceName(name);
        return name;
    }
}
