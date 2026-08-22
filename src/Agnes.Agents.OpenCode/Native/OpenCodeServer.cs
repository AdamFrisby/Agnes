using System.Diagnostics;
using System.Text.RegularExpressions;
using Agnes.Abstractions;
using Microsoft.Extensions.Logging;

namespace Agnes.Agents.OpenCode.Native;

/// <summary>
/// Owns an <c>opencode serve</c> process and the address it ends up on.
/// </summary>
/// <remarks>
/// The port is asked for as 0 and read back from the startup banner rather than chosen by Agnes: picking a
/// port invites a collision with whatever else is on the box, and the server prints the one it bound. It
/// binds loopback, so nothing outside this machine can reach it — the server is unauthenticated unless
/// OPENCODE_SERVER_PASSWORD is set, and it warns about exactly that on startup.
/// </remarks>
public sealed partial class OpenCodeServer : IAsyncDisposable
{
    [GeneratedRegex(@"listening on (http://\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex ListeningLine();

    private readonly Process _process;
    private readonly ILogger _logger;

    private OpenCodeServer(Process process, Uri baseAddress, ILogger logger)
    {
        _process = process;
        BaseAddress = baseAddress;
        _logger = logger;
    }

    /// <summary>Where the server is listening.</summary>
    public Uri BaseAddress { get; }

    /// <summary>Starts the server and waits for it to announce its address.</summary>
    /// <param name="command">The opencode executable.</param>
    /// <param name="workingDirectory">The directory the agent should treat as the project.</param>
    /// <param name="environment">Extra environment (the inline config and provider auth).</param>
    /// <param name="startupTimeout">How long to wait for the banner before giving up.</param>
    /// <param name="sandbox">When set, the server runs inside the sandbox and its stdout is piped back
    /// through the wrapped exec, so the banner is read exactly as it is on the host.</param>
    public static async Task<OpenCodeServer> StartAsync(
        string command,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        ILogger logger,
        TimeSpan startupTimeout,
        ISandboxCommand? sandbox = null,
        CancellationToken cancellationToken = default)
    {
        // Port 0 lets the OS choose; --print-logs puts the banner on stderr where we can read it. Loopback
        // in both cases: in a sandbox the port is reached through a forward, never off the guest's bridge,
        // so an unauthenticated server is never visible to the other sandboxes sharing it.
        IReadOnlyList<string> arguments = ["serve", "--port", "0", "--hostname", "127.0.0.1", "--print-logs"];
        var executable = command;
        var hostWorkingDirectory = workingDirectory;

        if (sandbox is not null)
        {
            (executable, arguments) = sandbox.WrapCommand(command, arguments, workingDirectory);
            // The guest path travels inside the wrapped argv; the launcher itself needs a real host directory.
            hostWorkingDirectory = Environment.CurrentDirectory;
        }

        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = hostWorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        // Only meaningful on the host path: a sandboxed launch is scrubbed by the run wrapper, and its
        // environment is materialized into the guest's agent-env file instead.
        foreach (var (k, v) in environment ?? new Dictionary<string, string>())
        {
            startInfo.Environment[k] = v;
        }

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start '{command} serve'.");

        try
        {
            var address = await ReadAddressAsync(process, startupTimeout, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("opencode server listening on {Address} (pid {Pid})", address, process.Id);
            return new OpenCodeServer(process, address, logger);
        }
        catch
        {
            TryKill(process);
            throw;
        }
    }

    /// <summary>Reads the startup banner off stdout/stderr until the address appears.</summary>
    private static async Task<Uri> ReadAddressAsync(Process process, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        var stdout = ScanAsync(process.StandardOutput, deadline.Token);
        var stderr = ScanAsync(process.StandardError, deadline.Token);
        var winner = await Task.WhenAny(stdout, stderr).ConfigureAwait(false);

        return await winner.ConfigureAwait(false)
               ?? throw new InvalidOperationException(
                   "opencode serve exited before reporting an address. Run it by hand to see why.");
    }

    private static async Task<Uri?> ScanAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (ListeningLine().Match(line) is { Success: true } match
                && Uri.TryCreate(match.Groups[1].Value, UriKind.Absolute, out var uri))
            {
                return uri;
            }
        }

        return null;
    }

    public ValueTask DisposeAsync()
    {
        TryKill(_process);
        _process.Dispose();
        return ValueTask.CompletedTask;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // Already gone, or not ours to kill — nothing useful to do either way.
        }
    }
}
