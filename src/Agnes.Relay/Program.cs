using System.Text.Json;

namespace Agnes.Relay;

// Explicit entrypoint (not top-level statements): the relay references the ASP.NET Core shared framework for the
// OPTIONAL LettuceEncrypt endpoint, and the SDK would otherwise emit a PUBLIC top-level `Program` that collides
// with Agnes.Host's `Program` in the integration test assembly. A named internal class avoids that entirely.
// Everything testable lives in RelayServer and friends; this file just wires them up.
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        string? configPath = ResolveConfigPath(args);
        RelayOptions options = LoadOptions(configPath);

        IRelayLog log = new ConsoleRelayLog();
        IAuthorizedHostKeys keys = new FileAuthorizedHostKeys(options.AuthorizedHostKeysFile, log);
        var rateLimiter = new RelayRateLimiter(options.RateLimit);

        // Config-gated (Agnes:Relay:PublicDomain, off by default): obtain a real Let's Encrypt cert for the relay's
        // own public endpoint via LettuceEncrypt and TLS-wrap the broker port. With no public domain the relay
        // serves plain TCP exactly as before — no ASP.NET Core host, no ACME.
        RelayTlsEndpoint? tlsEndpoint = RelayEndpointTls.IsEnabled(options)
            ? await RelayTlsEndpoint.StartAsync(options, log)
            : null;

        await using var relay = new RelayServer(options, keys, rateLimiter, log, security: tlsEndpoint?.Security);
        relay.Start();

        if (string.IsNullOrWhiteSpace(options.AuthorizedHostKeysFile))
        {
            log.Warn("No AuthorizedHostKeysFile configured — no host will be able to register.");
        }

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            shutdown.Cancel();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => shutdown.Cancel();

        try
        {
            await Task.Delay(Timeout.Infinite, shutdown.Token);
        }
        catch (OperationCanceledException)
        {
            log.Info("Relay shutting down.");
        }

        if (tlsEndpoint is not null)
        {
            await tlsEndpoint.DisposeAsync();
        }

        return 0;
    }

    private static string? ResolveConfigPath(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] is "--config" or "-c")
            {
                return args[i + 1];
            }
        }

        string? fromEnv = Environment.GetEnvironmentVariable("AGNES_RELAY_CONFIG");
        return string.IsNullOrWhiteSpace(fromEnv) ? null : fromEnv;
    }

    private static RelayOptions LoadOptions(string? configPath)
    {
        RelayOptions options = new();
        if (!string.IsNullOrWhiteSpace(configPath) && File.Exists(configPath))
        {
            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            options = JsonSerializer.Deserialize<RelayOptions>(File.ReadAllText(configPath), json) ?? options;
        }

        // Env overrides (handy in containers).
        string? port = Environment.GetEnvironmentVariable("AGNES_RELAY_PORT");
        if (int.TryParse(port, System.Globalization.CultureInfo.InvariantCulture, out int p))
        {
            options = options with { Port = p };
        }

        string? listen = Environment.GetEnvironmentVariable("AGNES_RELAY_LISTEN");
        if (!string.IsNullOrWhiteSpace(listen))
        {
            options = options with { ListenAddress = listen };
        }

        string? authKeys = Environment.GetEnvironmentVariable("AGNES_RELAY_AUTHORIZED_KEYS");
        if (!string.IsNullOrWhiteSpace(authKeys))
        {
            options = options with { AuthorizedHostKeysFile = authKeys };
        }

        string? publicDomain = Environment.GetEnvironmentVariable("AGNES_RELAY_PUBLIC_DOMAIN");
        if (!string.IsNullOrWhiteSpace(publicDomain))
        {
            options = options with { PublicDomain = publicDomain };
        }

        string? acmeEmail = Environment.GetEnvironmentVariable("AGNES_RELAY_ACME_EMAIL");
        if (!string.IsNullOrWhiteSpace(acmeEmail))
        {
            options = options with { AcmeEmailAddress = acmeEmail };
        }

        return options;
    }
}
