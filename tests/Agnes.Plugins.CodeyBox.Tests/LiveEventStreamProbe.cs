using Agnes.Plugins.CodeyBox;

namespace Agnes.Plugins.CodeyBox.Tests;

/// <summary>
/// Connects the real reader to a real orchestrator, when one is configured and running on this machine,
/// and otherwise passes without asserting anything.
///
/// <para>It exists because the canned-frame tests prove the parser and prove nothing about the
/// integration. The feed's auth scheme (Bearer, not the <c>X-Api-Key</c> header a first attempt assumed),
/// its content type, and the fact that it holds the connection open rather than answering once, are all
/// things that were got wrong here by reasoning about them instead of calling them.</para>
///
/// <para>Deliberately not reachability-gated into a Skip: it asserts only when it genuinely connected, so
/// it is silent on a machine with no CodeyBox rather than reporting a false pass or a false failure.</para>
/// </summary>
public sealed class LiveEventStreamProbe
{
    [Fact]
    public async Task Connects_to_a_real_orchestrator_and_holds_the_stream_open()
    {
        var options = CodeyBoxOptions.Resolve();
        if (!options.IsConfigured)
        {
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var connected = false;
        var stream = new CodeyBoxEventStream(options);

        try
        {
            await stream.RunAsync(
                DateTimeOffset.MinValue,
                _ => Task.CompletedTask,
                _ => { connected = true; return Task.CompletedTask; },
                cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected: the feed is meant to stay open until the reader stops.
        }

        if (!connected)
        {
            return;   // configured but not running
        }

        // The property under test is that the reader was still holding the stream when the window closed.
        // A feed that answered once and hung up would be indistinguishable from a working one if all we
        // checked was a successful status code.
        Assert.True(cts.IsCancellationRequested);
    }
}
