using Agnes.Wrap;

// agnes <cli> [args...] — run a coding CLI in a real terminal on this machine while teeing its I/O into an
// Agnes session so remote clients can watch it and it can be handed off. This file wires real dependencies
// and the interactive tee loop; the testable core lives in LocalWrapperHost / WrappedCliAdapter /
// WrappedCliSession. See .ideas/sessions/07-local-cli-wrapper-and-handoff.md.

if (args.Length == 0)
{
    await Console.Error.WriteLineAsync("usage: agnes <cli> [args...]   e.g. agnes claude");
    return 2;
}

var command = args[0];
var passthrough = args[1..];

await using var host = await LocalWrapperHost.StartAsync(command, passthrough, Directory.GetCurrentDirectory());
var terminal = host.Terminal;

await Console.Error.WriteLineAsync(
    $"[agnes] wrapped '{command}' as session {host.Session.SessionId} — watchable and handoff-capable while it runs.");

// Tee the PTY's output straight through to the local terminal so the user's experience is byte-identical to
// the bare CLI, while WrappedCliSession simultaneously captures it into the session log.
using var stdout = Console.OpenStandardOutput();
terminal.OutputReceived += chunk =>
{
    stdout.Write(chunk.Span);
    stdout.Flush();
};

// Forward the user's keystrokes into the wrapped CLI. (Local/remote control-handoff arming — the two-step
// confirm and the "remote is in control" input gate from the spec — is the interactive outer loop layered on
// top of this and is not part of the testable core.)
using var forwarding = new CancellationTokenSource();
_ = Task.Run(async () =>
{
    using var stdin = Console.OpenStandardInput();
    var buffer = new byte[4096];
    try
    {
        while (!forwarding.IsCancellationRequested)
        {
            var read = await stdin.ReadAsync(buffer, forwarding.Token).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await terminal.WriteInputAsync(buffer.AsMemory(0, read), forwarding.Token).ConfigureAwait(false);
        }
    }
    catch (OperationCanceledException)
    {
        // The CLI exited and we cancelled input forwarding — expected.
    }
});

var exitCode = await terminal.WaitForExitAsync().ConfigureAwait(false);
await forwarding.CancelAsync().ConfigureAwait(false);
await host.Manager.StopSessionAsync(host.Session.SessionId).ConfigureAwait(false);
return exitCode;
