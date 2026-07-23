using System.Text.Json;
using Agnes.Relay;

// Minimal entrypoint: load config (JSON file + a couple of env overrides), start the relay, run
// until Ctrl-C / SIGTERM. Everything testable lives in RelayServer and friends; this file just wires
// them up, so the relay pulls in ZERO third-party packages (BCL only).

string? configPath = ResolveConfigPath(args);
RelayOptions options = LoadOptions(configPath);

IRelayLog log = new ConsoleRelayLog();
IAuthorizedHostKeys keys = new FileAuthorizedHostKeys(options.AuthorizedHostKeysFile, log);
var rateLimiter = new RelayRateLimiter(options.RateLimit);

await using var relay = new RelayServer(options, keys, rateLimiter, log);
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

return 0;

static string? ResolveConfigPath(string[] args)
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

static RelayOptions LoadOptions(string? configPath)
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

    return options;
}
