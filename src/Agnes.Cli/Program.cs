using Agnes.Cli;
using Agnes.Client;

// agnes-agent: a thin, scriptable control surface over Agnes.Client for scripts and CI.
// See .ideas/extensibility/05-scriptable-agent-cli.md. This file only wires real dependencies;
// all behaviour (and its exit codes) lives in the testable CliApp.

var configDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "agnes-agent");

var protector = new SecureTokenProtector();
var hosts = new FileHostRegistry(Path.Combine(configDir, "hosts.json"), protector);
var sessions = new FileSessionRegistry(Path.Combine(configDir, "sessions.json"));

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

var app = new CliApp(new SignalRConnector(), new SystemConsole(), hosts, sessions, TimeProvider.System);
try
{
    return await app.RunAsync(args, cancellation.Token);
}
catch (OperationCanceledException)
{
    return 130; // conventional exit code for SIGINT
}
