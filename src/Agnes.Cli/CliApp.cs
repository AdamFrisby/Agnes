using System.Globalization;
using Agnes.Abstractions;
using Agnes.Client;
using Agnes.Protocol;

namespace Agnes.Cli;

/// <summary>Pairs a machine with a host and returns the durable device token. Injected so tests don't hit
/// the network; the default binding is <see cref="DevicePairing.PairAsync"/>.</summary>
public delegate Task<PairResponse> PairFunc(string hostUrl, string code, string deviceName, CancellationToken cancellationToken);

/// <summary>
/// The testable core of <c>agnes-agent</c>: dispatches a parsed command line to a thin wrapper over
/// <see cref="Agnes.Client"/>. Every collaborator (connector, console, registries, clock, pairing) is a
/// constructor input so the exit-code and output behaviour can be exercised offline against the simulated
/// host with no real network or CLI.
/// </summary>
internal sealed class CliApp
{
    private static readonly IReadOnlySet<string> BooleanFlags =
        new HashSet<string>(StringComparer.Ordinal) { "json", "wait", "skip-permissions", "create-dir" };

    private readonly IAgnesConnector _connector;
    private readonly IConsole _console;
    private readonly IHostRegistry _hosts;
    private readonly ISessionRegistry _sessions;
    private readonly TimeProvider _time;
    private readonly PairFunc _pair;

    public CliApp(
        IAgnesConnector connector,
        IConsole console,
        IHostRegistry hosts,
        ISessionRegistry sessions,
        TimeProvider time,
        PairFunc? pair = null)
    {
        _connector = connector;
        _console = console;
        _hosts = hosts;
        _sessions = sessions;
        _time = time;
        _pair = pair ?? ((url, code, name, ct) => DevicePairing.PairAsync(url, code, name, cancellationToken: ct));
    }

    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        if (args.Count == 0)
        {
            return Usage();
        }

        // "auth login" is the one two-word command; everything else is a single verb.
        var verb = args[0];
        var rest = args.Skip(1).ToArray();
        if (string.Equals(verb, "auth", StringComparison.Ordinal))
        {
            if (rest.Length == 0 || !string.Equals(rest[0], "login", StringComparison.Ordinal))
            {
                _console.Error("usage: agnes-agent auth login --host <url> [--code <code>] [--name <deviceName>]");
                return ExitCodes.Failure;
            }

            return await AuthLoginAsync(Parse(rest.Skip(1).ToArray()), cancellationToken).ConfigureAwait(false);
        }

        var cmd = Parse(rest);
        try
        {
            return verb switch
            {
                "machines" => await MachinesAsync(cmd, cancellationToken).ConfigureAwait(false),
                "spawn" => await SpawnAsync(cmd, cancellationToken).ConfigureAwait(false),
                "send" => await SendAsync(cmd, cancellationToken).ConfigureAwait(false),
                "status" => await StatusAsync(cmd, cancellationToken).ConfigureAwait(false),
                "wait" => await WaitAsync(cmd, cancellationToken).ConfigureAwait(false),
                "stop" => await StopAsync(cmd, cancellationToken).ConfigureAwait(false),
                _ => Usage(),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _console.Error($"error: {ex.Message}");
            return ExitCodes.Failure;
        }
    }

    private static CommandLine Parse(IReadOnlyList<string> args) => CommandLine.Parse(args, BooleanFlags);

    private int Usage()
    {
        _console.Error(
            "usage: agnes-agent <command>\n" +
            "  auth login --host <url> [--code <code>] [--name <deviceName>]\n" +
            "  machines [--json]\n" +
            "  spawn --host <id> --path <dir> --agent <agentId> [--create-dir] [--json]\n" +
            "  send <session-id> \"<prompt>\" [--skip-permissions] [--wait] [--timeout <seconds>]\n" +
            "  status <session-id> [--json]\n" +
            "  wait <session-id> [--timeout <seconds>]\n" +
            "  stop <session-id>");
        return ExitCodes.Failure;
    }

    // ---- auth ----

    private async Task<int> AuthLoginAsync(CommandLine cmd, CancellationToken cancellationToken)
    {
        var url = cmd.Option("host");
        if (string.IsNullOrWhiteSpace(url))
        {
            _console.Error("auth login: --host <url> is required.");
            return ExitCodes.Failure;
        }

        var code = cmd.Option("code");
        if (string.IsNullOrWhiteSpace(code))
        {
            _console.Error("Enter the pairing code shown on the host:");
            code = _console.ReadLine();
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            _console.Error("auth login: a pairing code is required.");
            return ExitCodes.Failure;
        }

        var deviceName = cmd.Option("name") ?? $"agnes-agent@{Environment.MachineName}";
        var response = await _pair(url, code, deviceName, cancellationToken).ConfigureAwait(false);

        // The device token is sealed at rest by the registry — never written in the clear (see SecureTokenProtector).
        _hosts.Upsert(new HostEntry(deviceName, url, response.Token));
        _console.Error($"Paired '{deviceName}' with {url} (device {response.DeviceId}). Revoke it any time from the host's device list.");
        _console.Out(deviceName);
        return ExitCodes.Success;
    }

    // ---- machines ----

    private async Task<int> MachinesAsync(CommandLine cmd, CancellationToken cancellationToken)
    {
        var machines = new List<MachineJson>();
        foreach (var entry in _hosts.Hosts.OrderBy(h => h.Name, StringComparer.Ordinal))
        {
            var machine = await ProbeAsync(entry, cancellationToken).ConfigureAwait(false);
            machines.Add(machine);
        }

        if (cmd.Flag("json"))
        {
            _console.Out(JsonOutput.Render(machines));
        }
        else if (machines.Count == 0)
        {
            _console.Error("No paired hosts. Run: agnes-agent auth login --host <url>");
        }
        else
        {
            foreach (var m in machines)
            {
                var reach = m.Reachable ? "reachable" : "unreachable";
                var detail = m.Reachable ? $"  {m.DisplayName} {m.Version}".TrimEnd() : string.Empty;
                _console.Out($"{m.Id}\t{m.Url}\t{reach}{detail}");
            }
        }

        return ExitCodes.Success;
    }

    private async Task<MachineJson> ProbeAsync(HostEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            var host = await _connector.ConnectAsync(entry.Url, entry.Token, cancellationToken).ConfigureAwait(false);
            var info = await host.GetHostInfoAsync().ConfigureAwait(false);
            return new MachineJson(entry.Name, entry.Url, Reachable: true, info.DisplayName, info.Version);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new MachineJson(entry.Name, entry.Url, Reachable: false, null, null);
        }
    }

    // ---- spawn ----

    private async Task<int> SpawnAsync(CommandLine cmd, CancellationToken cancellationToken)
    {
        var host = ResolveHost(cmd.Option("host"));
        if (host is null)
        {
            return ExitCodes.Failure;
        }

        var path = cmd.Option("path");
        var agent = cmd.Option("agent");
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(agent))
        {
            _console.Error("spawn: --path <dir> and --agent <agentId> are required.");
            return ExitCodes.Failure;
        }

        if (cmd.Flag("create-dir"))
        {
            Directory.CreateDirectory(path);
        }

        var connection = await _connector.ConnectAsync(host.Url, host.Token, cancellationToken).ConfigureAwait(false);
        var session = await connection.OpenSessionAsync(agent, path).ConfigureAwait(false);
        _sessions.Add(new SessionEntry(session.SessionId, host.Url, agent));

        if (cmd.Flag("json"))
        {
            _console.Out(JsonOutput.Render(new SpawnJson(session.SessionId, agent, host.Name)));
        }
        else
        {
            _console.Out(session.SessionId);
        }

        return ExitCodes.Success;
    }

    // ---- send ----

    private async Task<int> SendAsync(CommandLine cmd, CancellationToken cancellationToken)
    {
        if (cmd.Positionals.Count < 2)
        {
            _console.Error("send: usage: agnes-agent send <session-id> \"<prompt>\" [--skip-permissions] [--wait]");
            return ExitCodes.Failure;
        }

        var resolved = ResolveSession(cmd.Positionals[0]);
        if (resolved is null)
        {
            return ExitCodes.Failure;
        }

        var (session, host) = resolved.Value;
        var prompt = cmd.Positionals[1];
        var connection = await _connector.ConnectAsync(host.Url, host.Token, cancellationToken).ConfigureAwait(false);

        var view = await connection.SubscribeAsync(session.SessionId).ConfigureAwait(false);
        var baseline = view.LastSequence;

        // With --skip-permissions, auto-approve any permission the agent asks for during the wait so an
        // unattended send can actually complete (there's no per-send host toggle; this is the client-side
        // equivalent). Only meaningful together with --wait.
        Action<SessionEvent>? approver = null;
        if (cmd.Flag("skip-permissions"))
        {
            approver = e => AutoApprove(connection, session.SessionId, e);
            view.EventAppended += approver;
        }

        try
        {
            await connection.PromptAsync(session.SessionId, [new TextContent(prompt)]).ConfigureAwait(false);

            if (!cmd.Flag("wait"))
            {
                _console.Error("Prompt accepted.");
                return ExitCodes.Success;
            }

            var timeout = ParseTimeout(cmd.Option("timeout"));
            var outcome = await IdleWaiter.WaitAsync(view, timeout, _time, cancellationToken).ConfigureAwait(false);

            var reply = string.Concat(view.Events
                .Where(e => e.Sequence > baseline && e.AgentId is null)
                .OfType<MessageChunkEvent>()
                .Where(m => m.Role == MessageRole.Assistant)
                .Select(m => (m.Content as TextContent)?.Text ?? string.Empty));

            if (reply.Length > 0)
            {
                _console.Out(reply.Trim());
            }

            if (outcome == WaitOutcome.AgentError)
            {
                _console.Error("send: the agent turn ended in an error.");
            }
            else if (outcome == WaitOutcome.Timeout)
            {
                _console.Error("send: timed out waiting for the turn to complete.");
            }

            return ExitCodes.ForWait(outcome);
        }
        finally
        {
            if (approver is not null)
            {
                view.EventAppended -= approver;
            }
        }
    }

    private static void AutoApprove(IAgnesHost host, string sessionId, SessionEvent e)
    {
        if (e is not PermissionRequestedEvent permission)
        {
            return;
        }

        var option = permission.Options.FirstOrDefault(o =>
                         o.Kind is PermissionOptionKind.AllowOnce or PermissionOptionKind.AllowAlways)
                     ?? permission.Options.FirstOrDefault();
        if (option is not null)
        {
            // Fire-and-forget: the resolution flows back as its own events the wait already observes.
            _ = host.RespondPermissionAsync(sessionId, permission.RequestId, option.OptionId);
        }
    }

    // ---- status ----

    private async Task<int> StatusAsync(CommandLine cmd, CancellationToken cancellationToken)
    {
        if (cmd.Positionals.Count < 1)
        {
            _console.Error("status: usage: agnes-agent status <session-id> [--json]");
            return ExitCodes.Failure;
        }

        var resolved = ResolveSession(cmd.Positionals[0]);
        if (resolved is null)
        {
            return ExitCodes.Failure;
        }

        var (session, host) = resolved.Value;
        var connection = await _connector.ConnectAsync(host.Url, host.Token, cancellationToken).ConfigureAwait(false);
        var view = await connection.SubscribeAsync(session.SessionId).ConfigureAwait(false);
        var state = StateName(SessionActivity.Evaluate(view.Events));
        var info = view.Info;
        var status = new StatusJson(
            session.SessionId,
            info?.AdapterId ?? session.Adapter,
            info?.WorkingDirectory ?? string.Empty,
            state,
            view.LastSequence);

        if (cmd.Flag("json"))
        {
            _console.Out(JsonOutput.Render(status));
        }
        else
        {
            _console.Out($"{status.SessionId}\t{status.State}\t{status.Adapter}\t{status.WorkingDirectory}\thead={status.HeadSequence}");
        }

        return ExitCodes.Success;
    }

    // ---- wait ----

    private async Task<int> WaitAsync(CommandLine cmd, CancellationToken cancellationToken)
    {
        if (cmd.Positionals.Count < 1)
        {
            _console.Error("wait: usage: agnes-agent wait <session-id> [--timeout <seconds>]");
            return ExitCodes.Failure;
        }

        var resolved = ResolveSession(cmd.Positionals[0]);
        if (resolved is null)
        {
            return ExitCodes.Failure;
        }

        var (session, host) = resolved.Value;
        var connection = await _connector.ConnectAsync(host.Url, host.Token, cancellationToken).ConfigureAwait(false);
        var view = await connection.SubscribeAsync(session.SessionId).ConfigureAwait(false);

        var timeout = ParseTimeout(cmd.Option("timeout"));
        var outcome = await IdleWaiter.WaitAsync(view, timeout, _time, cancellationToken).ConfigureAwait(false);

        _console.Error(outcome switch
        {
            WaitOutcome.Idle => "idle",
            WaitOutcome.Timeout => "timeout (session left running, untouched)",
            WaitOutcome.AgentError => "agent error",
            _ => "unknown",
        });

        return ExitCodes.ForWait(outcome);
    }

    // ---- stop ----

    private async Task<int> StopAsync(CommandLine cmd, CancellationToken cancellationToken)
    {
        if (cmd.Positionals.Count < 1)
        {
            _console.Error("stop: usage: agnes-agent stop <session-id>");
            return ExitCodes.Failure;
        }

        var resolved = ResolveSession(cmd.Positionals[0]);
        if (resolved is null)
        {
            return ExitCodes.Failure;
        }

        var (session, host) = resolved.Value;
        var connection = await _connector.ConnectAsync(host.Url, host.Token, cancellationToken).ConfigureAwait(false);
        await connection.CancelAsync(session.SessionId).ConfigureAwait(false);
        _console.Error($"Cancelled the current turn on {session.SessionId}.");
        return ExitCodes.Success;
    }

    // ---- resolution helpers ----

    private HostEntry? ResolveHost(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            _console.Error("--host <id|prefix> is required.");
            return null;
        }

        var result = PrefixResolver.Resolve(_hosts.Hosts, h => h.Name, prefix);
        switch (result.Kind)
        {
            case PrefixMatchKind.Unique:
                return result.Value;
            case PrefixMatchKind.Ambiguous:
                _console.Error($"ambiguous host prefix '{prefix}' matches: {string.Join(", ", result.Candidates)}");
                return null;
            default:
                _console.Error($"no paired host matches '{prefix}'. Run: agnes-agent machines");
                return null;
        }
    }

    private (SessionEntry Session, HostEntry Host)? ResolveSession(string prefix)
    {
        var result = PrefixResolver.Resolve(_sessions.Sessions, s => s.SessionId, prefix);
        switch (result.Kind)
        {
            case PrefixMatchKind.Ambiguous:
                _console.Error($"ambiguous session prefix '{prefix}' matches: {string.Join(", ", result.Candidates)}");
                return null;
            case PrefixMatchKind.None:
                _console.Error($"no known session matches '{prefix}'. Sessions are remembered when you spawn them.");
                return null;
            default:
                break;
        }

        var session = result.Value!;
        var host = _hosts.Hosts.FirstOrDefault(h => UrlKey(h.Url) == UrlKey(session.HostUrl));
        if (host is null)
        {
            _console.Error($"session '{session.SessionId}' lives on {session.HostUrl}, which has no paired token. Run: agnes-agent auth login --host {session.HostUrl}");
            return null;
        }

        return (session, host);
    }

    private static string UrlKey(string url) => url.TrimEnd('/');

    private static TimeSpan? ParseTimeout(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }

    private static string StateName(SessionState state) => state switch
    {
        SessionState.Running => "running",
        SessionState.Errored => "error",
        _ => "idle",
    };
}
