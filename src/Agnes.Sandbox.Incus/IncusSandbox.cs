using Agnes.Sandbox.Credentials;
using Microsoft.Extensions.Logging;

namespace Agnes.Sandbox.Incus;

/// <summary>
/// A live Incus VM. The agent runs inside via <see cref="WrapCommand"/> (an <c>incus exec</c> of the
/// run-wrapper); credentials are materialised via <see cref="ExecAsync"/>. Agnes persists the VM:
/// <see cref="DisposeAsync"/> does NOT delete — only <see cref="DeleteAsync"/> destroys it.
/// </summary>
internal sealed class IncusSandbox : ISandbox, IPausableSandbox, IStoppableSandbox, Agnes.Abstractions.IPortForwardingSandbox
{
    private readonly IncusOptions _options;
    private readonly IIncusCliRunner _cli;
    private readonly ILogger _logger;
    private SandboxState _state = SandboxState.Running;

    public IncusSandbox(string id, IncusOptions options, IIncusCliRunner cli, ILogger logger)
    {
        Id = id;
        _options = options;
        _cli = cli;
        _logger = logger;
    }

    public string Id { get; }
    public string HomeDirectory => _options.GuestHome;
    public bool IsPaused => _state == SandboxState.Paused;
    public SandboxInfo Info => new(IncusSandboxProvider.ProviderId, Id, _state);

    public (string Command, IReadOnlyList<string> Arguments) WrapCommand(
        string command, IReadOnlyList<string> arguments, string workingDirectory)
    {
        // incus --project agnes exec <id> --cwd <wd> -- agnes-run <command> <args...>
        var agentArgv = new List<string> { IncusGuest.RunWrapperPath, command };
        agentArgv.AddRange(arguments);
        var argv = IncusCommandBuilder.BuildExec(_options, Id, agentArgv, workingDirectory, asUser: false);
        return (argv[0], argv.Skip(1).ToList());
    }

    /// <inheritdoc />
    public async Task<Uri> ForwardPortAsync(int guestPort, CancellationToken cancellationToken = default)
    {
        // A free host port is found by binding one and letting it go: Incus needs the number up front, and
        // racing another listener for it is far less likely than colliding with a hard-coded choice.
        var hostPort = FreeLoopbackPort();
        var device = $"agnes-fwd-{guestPort}";

        await _cli.RunCheckedAsync(
            "add proxy device",
            IncusCommandBuilder.BuildAddProxyDevice(_options, Id, device, hostPort, guestPort),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Forwarding 127.0.0.1:{HostPort} to {Instance}:{GuestPort}", hostPort, Id, guestPort);
        return new Uri($"http://127.0.0.1:{hostPort}");
    }

    private static int FreeLoopbackPort()
    {
        using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken cancellationToken = default)
    {
        var argv = IncusCommandBuilder.BuildExec(_options, Id, exec.Argv, exec.WorkingDirectory, asUser: true);
        var (code, stdout, stderr) = await _cli.RunAsync(
            argv, exec.Stdin, exec.StdoutChunkCallback, exec.StderrChunkCallback, cancellationToken).ConfigureAwait(false);
        return new SandboxExecResult(code, stdout, stderr);
    }

    public async Task MaterializeCredentialAsync(SandboxCredential credential, CancellationToken cancellationToken = default)
    {
        await WriteAgentEnvAsync(credential.EnvironmentVariables, cancellationToken).ConfigureAwait(false);
        foreach (var file in credential.Files)
        {
            await WriteCredentialFileAsync(file, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Materialises a credential file inside the guest under the agent user's $HOME (0600).</summary>
    private async Task WriteCredentialFileAsync(SandboxCredentialFile file, CancellationToken cancellationToken = default)
    {
        var exec = new SandboxExec
        {
            Argv = ["env", $"HOME={_options.GuestHome}", "python3", "-c", IncusGuest.CredentialWriterPython, file.HomeRelativePath],
            Stdin = file.Contents,
            EnvironmentContainsSecrets = true,
        };
        var result = await ExecAsync(exec, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException($"Credential materialisation failed: {result.Stderr.Trim()}");
        }
    }

    /// <summary>Writes the credential env vars to the root-owned tmpfs env file (NUL-delimited).</summary>
    /// <summary>Replaces the root-owned env file with exactly what this provision computed. An empty set
    /// writes an empty file rather than skipping: re-provisioning is a re-stamp, so a variable that is no
    /// longer set must actually disappear from the guest (e.g. a session switched back to its default model,
    /// whose model env would otherwise linger and keep overriding it).</summary>
    private async Task WriteAgentEnvAsync(IReadOnlyDictionary<string, string> env, CancellationToken cancellationToken = default)
    {
        var payload = string.Concat(env.Select(kv => $"{kv.Key}={kv.Value}\0"));
        var argv = IncusCommandBuilder.BuildFilePush(_options, Id, IncusGuest.AgentEnvFile, "0600", 0, 0);
        await _cli.RunCheckedAsync("push agent env", argv, payload, cancellationToken).ConfigureAwait(false);
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        await _cli.RunCheckedAsync("pause", IncusCommandBuilder.BuildPause(_options, Id), cancellationToken: cancellationToken).ConfigureAwait(false);
        _state = SandboxState.Paused;
    }

    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        await _cli.RunCheckedAsync("resume", IncusCommandBuilder.BuildStart(_options, Id), cancellationToken: cancellationToken).ConfigureAwait(false);
        _state = SandboxState.Running;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _cli.RunCheckedAsync("stop", IncusCommandBuilder.BuildStop(_options, Id, timeoutSeconds: 30, stateful: false), cancellationToken: cancellationToken).ConfigureAwait(false);
        _state = SandboxState.Stopped;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _cli.RunCheckedAsync("start", IncusCommandBuilder.BuildStart(_options, Id), cancellationToken: cancellationToken).ConfigureAwait(false);
        _state = SandboxState.Running;
    }

    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        await _cli.RunCheckedAsync("delete", IncusCommandBuilder.BuildDelete(_options, Id), cancellationToken: cancellationToken).ConfigureAwait(false);
        _state = SandboxState.Stopped;
    }

    // Agnes persists VMs — dispose does NOT delete. The VM keeps running for reconnect.
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
