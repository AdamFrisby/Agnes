using Agnes.Abstractions;
using Agnes.Agents.Codex;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Agents.Codex.Tests;

/// <summary>
/// Exercises the adapter against the REAL <c>codex</c> binary when it's installed — launching
/// <c>codex app-server</c>, doing the initialize handshake, and starting a thread. None of this
/// calls the model, so it consumes no usage. Skips (passes as a no-op) where codex isn't on PATH,
/// so CI without codex stays green.
/// </summary>
public class CodexLiveSmokeTests
{
    private static bool CodexInstalled => AgentCommand.IsOnPath("codex");

    [Fact]
    public async Task Real_app_server_initializes_and_starts_a_thread()
    {
        if (!CodexInstalled)
        {
            return; // no codex on this machine — nothing to verify
        }

        var adapter = CodexAppServer.Create(NullLoggerFactory.Instance);
        Assert.True(adapter.IsAvailable());

        var options = new AgentSessionOptions { WorkingDirectory = Path.GetTempPath() };
        await using var session = await adapter.StartSessionAsync(options);

        // A real thread id came back from `thread/start` over the real wire.
        Assert.False(string.IsNullOrWhiteSpace(session.AgentSessionId));
    }
}
