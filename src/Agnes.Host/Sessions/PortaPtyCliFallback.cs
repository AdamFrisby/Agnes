using System.Text;
using Agnes.Abstractions;
using Microsoft.Extensions.Logging;
using Porta.Pty;

namespace Agnes.Host.Sessions;

/// <summary>
/// The real host-side <see cref="ICliFallback"/>: opens an actual pseudo-terminal (via the cross-platform
/// <c>Porta.Pty</c> library) for commands ACP cannot express — the embedded terminal (platform/03) and
/// provider-login flows. Each opened terminal spawns the requested command in a PTY; its output surfaces
/// through the <see cref="ITerminalOutputSource"/> seam (which the <see cref="SessionManager"/> turns into
/// <see cref="TerminalOutputEvent"/>s) and its process exit through <see cref="ITerminalExitSource"/>.
/// </summary>
public sealed class PortaPtyCliFallback : ICliFallback
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<PortaPtyCliFallback> _logger;

    public PortaPtyCliFallback(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<PortaPtyCliFallback>();
    }

    /// <inheritdoc />
    public async Task<ITerminalHandle> OpenTerminalAsync(TerminalOptions options, CancellationToken cancellationToken = default)
    {
        // Pre-flight: resolve the executable ourselves so a missing command surfaces a clear error synchronously.
        // (A bare PtyProvider.SpawnAsync of a nonexistent command does NOT throw — the child spawns and exits
        // nonzero from the failed exec, producing no output; that would be an opaque "terminal that closed at once".)
        var app = ResolveExecutable(options.Command)
            ?? throw new FileNotFoundException(
                $"Cannot open a terminal: command '{options.Command}' was not found on PATH or as an existing path.",
                options.Command);

        var environment = BuildEnvironment();

        var ptyOptions = new PtyOptions
        {
            App = app,
            // Porta.Pty prepends App as argv[0]; CommandLine carries only the remaining arguments.
            CommandLine = [.. options.Arguments],
            Cwd = string.IsNullOrWhiteSpace(options.WorkingDirectory) ? Environment.CurrentDirectory : options.WorkingDirectory,
            Cols = NormalizeDimension(options.Columns, 120),
            Rows = NormalizeDimension(options.Rows, 30),
            Name = environment.TryGetValue("TERM", out var term) ? term : "xterm-256color",
            Environment = environment,
        };

        IPtyConnection connection;
        try
        {
            connection = await PtyProvider.SpawnAsync(ptyOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // PTY allocation genuinely failed (e.g. /dev/ptmx unavailable, or an unsupported platform) — surface a
            // clear error, not a crash. The caller (SessionManager) already reports a friendly message upstream.
            throw new InvalidOperationException(
                $"Failed to allocate a pseudo-terminal for '{options.Command}': {ex.Message}", ex);
        }

        _logger.LogDebug("Opened PTY for {App} (pid {Pid})", app, connection.Pid);
        return new PortaPtyTerminalHandle(connection, _loggerFactory.CreateLogger<PortaPtyTerminalHandle>());
    }

    // Resolve a command to a concrete executable path: a value with a directory separator (or rooted) is used
    // as-is and must exist; otherwise we search PATH (honouring PATHEXT on Windows). Returns null when nothing
    // matches, so the caller can raise a clear "command not found".
    private static string? ResolveExecutable(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        if (Path.IsPathRooted(command)
            || command.Contains(Path.DirectorySeparatorChar)
            || command.Contains(Path.AltDirectorySeparatorChar))
        {
            return File.Exists(command) ? Path.GetFullPath(command) : null;
        }

        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar))
        {
            return null;
        }

        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [string.Empty];

        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir, command + ext);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    // The child PTY inherits the host's environment (so PATH, HOME, etc. behave normally), with TERM defaulted
    // when absent. Porta.Pty requires a non-null Environment.
    private static Dictionary<string, string> BuildEnvironment()
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var env = new Dictionary<string, string>(comparer);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                env[key] = value;
            }
        }

        if (!env.ContainsKey("TERM"))
        {
            env["TERM"] = "xterm-256color";
        }

        return env;
    }

    private static int NormalizeDimension(int value, int fallback) => value > 0 ? value : fallback;
}
