using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agnes.Host.Hosting;

/// <summary>Result of running the <c>tailscale</c> CLI once.</summary>
public sealed record TailscaleCommandResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
/// Runs the <c>tailscale</c> CLI. Injected into <see cref="TailscaleTransportProvider"/> so the provider is
/// unit-testable without the real binary (tests supply a stub runner). The real implementation is
/// <see cref="TailscaleCli"/>.
/// </summary>
public interface ITailscaleCli
{
    /// <summary>Runs <c>tailscale</c> with <paramref name="args"/> and returns its exit code and streams.
    /// Implementations should surface "binary not found" as a non-zero result (or throw), never a fake success.</summary>
    Task<TailscaleCommandResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct = default);
}

/// <summary>Default <see cref="ITailscaleCli"/>: shells out to the real <c>tailscale</c> executable.</summary>
public sealed class TailscaleCli : ITailscaleCli
{
    private readonly string _executable;

    /// <param name="executable">CLI name or path; defaults to <c>tailscale</c> on PATH.</param>
    public TailscaleCli(string executable = "tailscale") => _executable = executable;

    /// <inheritdoc />
    public async Task<TailscaleCommandResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(_executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return new TailscaleCommandResult(-1, string.Empty, "Could not start the 'tailscale' process.");
            }

            var stdOut = await process.StandardOutput.ReadToEndAsync(ct);
            var stdErr = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            return new TailscaleCommandResult(process.ExitCode, stdOut, stdErr);
        }
        catch (Exception ex)
        {
            // The binary being absent surfaces here (Win32Exception / file-not-found) — report it as a
            // failure the provider can turn into a clear, actionable error, never a silent success.
            return new TailscaleCommandResult(-1, string.Empty, ex.Message);
        }
    }
}

/// <summary>Configuration for <see cref="TailscaleTransportProvider"/> (bound from <c>Agnes:Transport:Tailscale</c>).</summary>
public sealed record TailscaleTransportOptions
{
    /// <summary>When true, expose publicly via <c>tailscale funnel</c>; otherwise tailnet-only <c>tailscale serve</c>
    /// (the default — no public listener, satisfying AC3).</summary>
    public bool Funnel { get; init; }

    /// <summary>The tailnet-facing HTTPS port Tailscale terminates on. Defaults to 443, so the advertised
    /// client URL carries no explicit port.</summary>
    public int HttpsPort { get; init; } = 443;

    /// <summary>Overrides the local hub port to proxy to. When null, it is derived from the host's bound
    /// address(es).</summary>
    public int? HubPort { get; init; }
}

/// <summary>
/// Exposes the host on the operator's own Tailscale mesh with one config switch instead of hand-run
/// <c>tailscale</c> commands (<c>.ideas/connectivity/01-relay-and-tunneling.md</c>, "Tailscale: YES").
/// <para>
/// Default mode is <c>tailscale serve</c> — reachable only from the tailnet, with no public listener (AC3).
/// An opt-in <c>Agnes:Transport:Tailscale:Funnel=true</c> switches to <c>tailscale funnel</c> for public
/// exposure. Either way this is outbound-only (<see cref="RequiresOutboundOnly"/> is true) and needs no
/// inbound port opened, and the advertised address is the host's MagicDNS <c>*.ts.net</c> name (a reachable
/// tailnet address, never a LAN IP — AC5). Tailscale auto-provisions the TLS certificate for that name, so
/// this transport needs no certificate wiring of its own.
/// </para>
/// If the CLI is missing or the node is not logged in, <see cref="ExposeAsync"/> throws a clear, actionable
/// error rather than silently falling back to an unintended transport (AC6).
/// </summary>
public sealed class TailscaleTransportProvider : ITransportProvider
{
    private readonly ITailscaleCli _cli;
    private readonly TailscaleTransportOptions _options;
    private TransportEndpoint? _lastEndpoint;
    private int? _exposedPort;

    public TailscaleTransportProvider(ITailscaleCli cli, TailscaleTransportOptions? options = null)
    {
        _cli = cli;
        _options = options ?? new TailscaleTransportOptions();
    }

    /// <inheritdoc />
    public string Id => "tailscale";

    /// <inheritdoc />
    public string DisplayName => _options.Funnel ? "Tailscale Funnel" : "Tailscale";

    /// <inheritdoc />
    public bool RequiresOutboundOnly => true;

    /// <inheritdoc />
    public TransportEndpoint Describe(HostExposureContext context)
        // Before ExposeAsync has run the tailnet name is unknown; advertise nothing rather than a LAN address
        // that would be unreachable from off-LAN (AC5). Program's startup path calls ExposeAsync, not this.
        => _lastEndpoint ?? new TransportEndpoint([], "Tailscale transport is not exposed yet — call ExposeAsync.");

    /// <inheritdoc />
    public async Task<TransportEndpoint> ExposeAsync(HostExposureContext context, CancellationToken ct = default)
    {
        var hubPort = _options.HubPort ?? DeriveHubPort(context.BoundAddresses)
            ?? throw new InvalidOperationException(
                "Tailscale transport could not determine the local hub port to proxy. Set Agnes:Transport:Tailscale:HubPort.");

        var self = await ReadSelfAsync(ct);
        var httpsPort = _options.HttpsPort;

        // Bring the proxy up. serve = tailnet-only; funnel = public. `https+insecure` targets the host's own
        // TLS listener without Tailscale re-validating the local self-signed cert.
        var verb = _options.Funnel ? "funnel" : "serve";
        var exposeArgs = new[]
        {
            verb,
            "--bg",
            "--https=" + httpsPort.ToString(CultureInfo.InvariantCulture),
            "https+insecure://localhost:" + hubPort.ToString(CultureInfo.InvariantCulture),
        };
        var exposeResult = await _cli.RunAsync(exposeArgs, ct);
        if (exposeResult.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"'tailscale {verb}' failed (exit {exposeResult.ExitCode}): {Describe(exposeResult)}. " +
                "Verify Tailscale is installed, this node is logged in, and " +
                (_options.Funnel ? "Funnel is enabled for this tailnet." : "serve is permitted for this node."));
        }

        _exposedPort = httpsPort;
        var url = "https://" + self;
        if (httpsPort != 443)
        {
            url += ":" + httpsPort.ToString(CultureInfo.InvariantCulture);
        }

        var hint = _options.Funnel
            ? "public via Tailscale Funnel"
            : "tailnet-only";
        _lastEndpoint = new TransportEndpoint([url], hint);
        return _lastEndpoint;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken ct = default)
    {
        var port = _exposedPort;
        if (port is null)
        {
            return;
        }

        var verb = _options.Funnel ? "funnel" : "serve";
        var offArgs = new[] { verb, "--https=" + port.Value.ToString(CultureInfo.InvariantCulture), "off" };
        // Best-effort teardown: a non-zero here shouldn't crash shutdown, but surface it for diagnosis.
        await _cli.RunAsync(offArgs, ct);
        _exposedPort = null;
        _lastEndpoint = null;
    }

    /// <summary>Queries <c>tailscale status --json</c> and returns this node's MagicDNS name (trailing dot
    /// stripped). Throws a clear, actionable error if the CLI is absent, errored, or the node is not usable.</summary>
    private async Task<string> ReadSelfAsync(CancellationToken ct)
    {
        var status = await _cli.RunAsync(["status", "--json"], ct);
        if (status.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Could not query 'tailscale status --json' (exit {status.ExitCode}): {Describe(status)}. " +
                "Install Tailscale and run 'tailscale up' to log this host in before selecting the tailscale transport.");
        }

        TailscaleStatus? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<TailscaleStatus>(status.StandardOutput, TailscaleJson.Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "Could not parse 'tailscale status --json' output: " + ex.Message, ex);
        }

        var backend = parsed?.BackendState;
        if (!string.Equals(backend, "Running", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Tailscale is not connected (BackendState='{backend ?? "unknown"}'). " +
                "Run 'tailscale up' to log this host into your tailnet before selecting the tailscale transport.");
        }

        var dnsName = parsed?.Self?.DnsName;
        if (string.IsNullOrWhiteSpace(dnsName))
        {
            throw new InvalidOperationException(
                "'tailscale status --json' did not report this node's MagicDNS name; cannot advertise a reachable " +
                "tailnet address. Ensure MagicDNS is enabled for your tailnet.");
        }

        return dnsName.TrimEnd('.');
    }

    private static int? DeriveHubPort(IReadOnlyList<string> boundAddresses)
    {
        foreach (var address in boundAddresses)
        {
            if (Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Port > 0)
            {
                return uri.Port;
            }
        }

        return null;
    }

    private static string Describe(TailscaleCommandResult result)
    {
        var message = result.StandardError.Trim();
        if (message.Length == 0)
        {
            message = result.StandardOutput.Trim();
        }

        return message.Length == 0 ? "(no output)" : message;
    }
}

/// <summary>Typed projection of the subset of <c>tailscale status --json</c> Agnes reads. Kept at the boundary
/// only (schema owned by Tailscale, not us) and deserialized immediately into these records.</summary>
public sealed record TailscaleStatus
{
    [JsonPropertyName("BackendState")]
    public string? BackendState { get; init; }

    [JsonPropertyName("Self")]
    public TailscalePeer? Self { get; init; }
}

/// <summary>A node in <c>tailscale status --json</c>.</summary>
public sealed record TailscalePeer
{
    [JsonPropertyName("DNSName")]
    public string? DnsName { get; init; }
}

/// <summary>Deserialization options for the Tailscale status boundary.</summary>
internal static class TailscaleJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };
}
