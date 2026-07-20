using Agnes.Sandbox.Credentials;
using Microsoft.Extensions.Logging;

namespace Agnes.Sandbox.Incus;

/// <summary>
/// A live Incus VM. The agent runs inside via <see cref="WrapCommand"/> (an <c>incus exec</c> of the
/// run-wrapper); credentials are materialised via <see cref="ExecAsync"/>. Agnes persists the VM:
/// <see cref="DisposeAsync"/> does NOT delete — only <see cref="DeleteAsync"/> destroys it.
/// </summary>
internal sealed class IncusSandbox : ISandbox, IPausableSandbox
{
    private readonly IncusOptions _options;
    private readonly IncusCliRunner _cli;
    private readonly ILogger _logger;
    private SandboxState _state = SandboxState.Running;

    public IncusSandbox(string id, IncusOptions options, IncusCliRunner cli, ILogger logger)
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
    private async Task WriteAgentEnvAsync(IReadOnlyDictionary<string, string> env, CancellationToken cancellationToken = default)
    {
        if (env.Count == 0)
        {
            return;
        }

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

    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        await _cli.RunCheckedAsync("delete", IncusCommandBuilder.BuildDelete(_options, Id), cancellationToken: cancellationToken).ConfigureAwait(false);
        _state = SandboxState.Stopped;
    }

    // Agnes persists VMs — dispose does NOT delete. The VM keeps running for reconnect.
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
