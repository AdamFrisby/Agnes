using Agnes.Plugins.CodeyBox;

namespace Agnes.Plugins.CodeyBox.Tests;

/// <summary>
/// Renders the real wall — a live board, a live overview, and a few seconds of the orchestrator's own
/// event feed — to PNGs, so it can be judged against the fleet it will actually be watching rather than
/// against a hand-built sample.
/// </summary>
/// <remarks>
/// <para>Silent where no CodeyBox is configured or the one configured is not running, and read-only
/// against one that is: every call here is a GET or the read-only SSE subscription, because the host it
/// runs against is doing real work. Writes only when <c>AGNES_NOWWORKING_LIVE_SHOTS</c> names a
/// directory, which is also what keeps it out of an ordinary test run.</para>
///
/// <para>The feed is watched rather than replayed. The stream's buffer carries events from days ago on
/// connect, so the cutoff is the moment the subscription opens: what lands after that is what the fleet
/// is doing right now, which is the only thing this screen claims to show.</para>
/// </remarks>
public sealed class LiveNowWorkingProbe
{
    /// <summary>How long to listen. Long enough for a busy fleet to say something, short enough that a
    /// probe is a probe.</summary>
    private static readonly TimeSpan Listen = TimeSpan.FromSeconds(25);

    [Fact]
    public async Task Renders_the_live_wall_at_both_screen_sizes()
    {
        var shotDir = Environment.GetEnvironmentVariable("AGNES_NOWWORKING_LIVE_SHOTS");
        var options = CodeyBoxOptions.Resolve();
        if (string.IsNullOrEmpty(shotDir) || !options.IsConfigured)
        {
            return;
        }

        await using var client = new CodeyBoxClient(options);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));

        IReadOnlyList<WorkItemRow> items;
        IReadOnlyList<Project> projects;
        Concurrency? concurrency;
        try
        {
            items = await client.ListWorkItemsAsync(cts.Token);
            projects = await client.GetProjectsAsync(cts.Token);
            concurrency = await client.GetConcurrencyAsync(cts.Token);
        }
        catch (Exception)
        {
            return;   // configured but not running
        }

        if (items.Count == 0)
        {
            return;
        }

        var slots = concurrency is { } c ? (c.CurrentlyRunningTotal, c.GlobalMaxConcurrent) : (0, 0);
        var board = BoardModel.Build(items, projects, slots.Item1, slots.Item2, DateTimeOffset.Now);

        // The same gather the overview section makes, through the same model: the wall reads its traces,
        // flow, quota and drain estimate out of exactly that record in the real tab too.
        var inputs = await LiveOverviewInputs.GatherAsync(options);
        var overview = inputs is null ? null : OverviewModel.Build(inputs);

        // No timers: the probe drives the wall by hand (a fake clock, an explicit Rebuild) because
        // there is no dispatcher out here and because a probe wants one frame, not a frame loop.
        var wall = new NowWorkingViewModel(
            () => board,
            () => overview,
            () => items,
            action => { action(); return Task.CompletedTask; },
            tail: null,
            clock: new FakeWallClock());

        // The real agents' real output, for the running items only.
        foreach (var chain in board.Now)
        {
            try
            {
                wall.SetOutput(chain.Head.Id, await client.GetStdoutTailAsync(chain.Head.Id, cts.Token));
            }
            catch (Exception)
            {
                // An item whose stdout this host does not keep simply has no lines.
            }
        }

        // And a real minute of the feed. Read-only, and dropped on the floor if the host has nothing to
        // say in the window — a quiet fleet drawing a quiet chart is the correct outcome, not a failure.
        var seen = 0;
        var stream = client.CreateEventStream();
        using var listen = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        listen.CancelAfter(Listen);
        var since = DateTimeOffset.UtcNow;
        try
        {
            await stream.RunAsync(
                since,
                evt =>
                {
                    seen++;
                    wall.Accept(evt, DateTimeOffset.Now);
                    return Task.CompletedTask;
                },
                null,
                listen.Token);
        }
        catch (OperationCanceledException)
        {
            // the window closed, which is how this always ends
        }

        wall.Rebuild();

        Directory.CreateDirectory(shotDir);
        var laptop = NowWorkingShotTests.Shot(
            wall, "live-nowworking-laptop",
            NowWorkingShotTests.LaptopWidth, NowWorkingShotTests.LaptopHeight, shotDir);
        var monitor = NowWorkingShotTests.Shot(
            wall, "live-nowworking-monitor",
            NowWorkingShotTests.MonitorWidth, NowWorkingShotTests.MonitorHeight, shotDir);

        // Printed on purpose: a probe that can legitimately do nothing has to say when it did something.
        Console.WriteLine(
            $"[wall] {wall.Headline}  ·  {board.NowCount} running, {board.WaitingCount} waiting  ·  " +
            $"{seen} feed event(s) in {Listen.TotalSeconds:0}s  ·  {wall.Ticker.Count} log line(s)");
        foreach (var number in wall.Numbers)
        {
            Console.WriteLine($"[wall]   {number.Label}: {number.Text}  ({number.Caption})");
        }

        foreach (var row in wall.Ticker)
        {
            Console.WriteLine($"[wall]   {row.Line.Text}");
        }

        Console.WriteLine($"[wall] shots: {laptop}, {monitor}");
        Assert.True(File.Exists(laptop));
        Assert.True(File.Exists(monitor));
    }
}
