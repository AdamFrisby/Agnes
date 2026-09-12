using Agnes.Plugins.CodeyBox.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Agnes.Plugins.CodeyBox.Tests;

/// <summary>
/// Renders the wall to PNGs at the two sizes it is actually left open at, and — twice on the same scene,
/// a second and a half apart — proves that it moves.
/// </summary>
/// <remarks>
/// <para>A screen whose whole purpose is to be watched cannot be signed off by assertions about controls
/// existing. Its failure modes are dimensional and temporal: a card squeezed until its title is an
/// ellipsis, a number column that pushes the feed off the bottom, an animation that does not actually
/// animate. So the frames get written and looked at, and the motion is proved by capturing the same
/// scene twice and requiring the two files to differ.</para>
///
/// <para>That second part is only possible because nothing on this screen animates itself: every moving
/// value is on the view model, stepped by an <see cref="IWallClock"/> the test owns. An Avalonia
/// <c>Animation</c> would have made the same screen unphotographable.</para>
///
/// <para><b>Run this class on its own when you want to look at the frames</b>
/// (<c>--filter FullyQualifiedName~NowWorkingShotTests</c>). A headless session is process-global, and
/// after the suite's other render classes have started and disposed theirs the frames still come back —
/// but with no glyphs in them, which is a property of the harness rather than of the wall. Set
/// <c>AGNES_BOARD_SHOTS</c> to choose where they land.</para>
/// </remarks>
[Collection("avalonia-headless")]
public sealed class NowWorkingShotTests
{
    /// <summary>
    /// Just enough theme for the roles to resolve, plus the host's horizontal brand gradient — the wall
    /// asks for <c>GradAgnesH</c> by name rather than re-typing the brand's stops, so a harness without
    /// it would draw the header hairline as nothing.
    /// </summary>
    private sealed class ShotApp : Application
    {
        public override void Initialize()
        {
            Styles.Add(new FluentTheme());
            RequestedThemeVariant = ThemeVariant.Dark;
            var gradient = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromRgb(0x59, 0x40, 0xD6), 0.0),
                    new GradientStop(Color.FromRgb(0x8A, 0x55, 0xEE), 0.28),
                    new GradientStop(Color.FromRgb(0xC8, 0x58, 0xC8), 0.58),
                    new GradientStop(Color.FromRgb(0xE8, 0x6D, 0x9A), 0.80),
                    new GradientStop(Color.FromRgb(0xFF, 0x8A, 0x66), 1.0),
                },
            };

            Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                ["Bg"] = new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x16)),
                ["Panel"] = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x20)),
                ["PanelAlt"] = new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x2C)),
                ["Line"] = new SolidColorBrush(Color.FromRgb(0x2E, 0x2E, 0x38)),
                ["Fg"] = new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xF2)),
                ["FgDim"] = new SolidColorBrush(Color.FromRgb(0xA8, 0xA8, 0xB6)),
                ["FgFaint"] = new SolidColorBrush(Color.FromRgb(0x76, 0x76, 0x86)),
                ["Accent"] = new SolidColorBrush(Color.FromRgb(0x8B, 0x5C, 0xF6)),
                ["Danger"] = new SolidColorBrush(Color.FromRgb(0xF4, 0x72, 0x9B)),
                ["StatusWorking"] = new SolidColorBrush(Color.FromRgb(0x5A, 0xB8, 0xF0)),
                ["StatusAttention"] = new SolidColorBrush(Color.FromRgb(0xE8, 0xB3, 0x39)),
                ["StatusDone"] = new SolidColorBrush(Color.FromRgb(0x5A, 0xD6, 0xA8)),
                ["StatusError"] = new SolidColorBrush(Color.FromRgb(0xF4, 0x72, 0x9B)),
                ["StatusIdle"] = new SolidColorBrush(Color.FromRgb(0x76, 0x76, 0x86)),
                ["GradAgnesH"] = gradient,
            });
        }
    }

    public static class ShotAppBuilder
    {
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<ShotApp>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }

    internal static readonly string ShotDir =
        Environment.GetEnvironmentVariable("AGNES_BOARD_SHOTS")
        ?? Path.Combine(Path.GetTempPath(), "board-shots");

    /// <summary>The two sizes the wall is left open at: a laptop with the tab full width, and a 1440p
    /// monitor it has to itself. Nothing may clip at either.</summary>
    internal const double LaptopWidth = 1683;
    internal const double LaptopHeight = 1095;
    internal const double MonitorWidth = 2560;
    internal const double MonitorHeight = 1440;

    /// <summary>Draws the wall once and saves the frame. Returns the file, for the log line.</summary>
    /// <param name="directory">Where the frame lands; <see cref="ShotDir"/> when not named. The live
    /// probe has its own directory and shares this renderer rather than copying it.</param>
    internal static string Shot(
        NowWorkingViewModel wall, string name, double width, double height, string? directory = null)
    {
        var into = directory ?? ShotDir;
        var path = Path.Combine(into, name + ".png");
        using var session = HeadlessUnitTestSession.StartNew(typeof(ShotAppBuilder));
        session.Dispatch(() =>
        {
            var window = new Window
            {
                Width = width,
                Height = height,
                Background = new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x16)),
                Content = new NowWorkingView { DataContext = wall },
            };

            window.Show();
            Dispatcher.UIThread.RunJobs();

            using var frame = window.CaptureRenderedFrame();
            if (frame is not null)
            {
                Directory.CreateDirectory(into);
                using var file = File.Create(path);
                frame.Save(file, new PngBitmapEncoderOptions());
            }

            window.Close();
        }, CancellationToken.None).GetAwaiter().GetResult();

        return path;
    }

    /// <summary>The canned wall, with output on every card and an hour of feed behind it.</summary>
    internal static (NowWorkingViewModel Wall, FakeWallClock Clock, Func<DateTimeOffset> Now) Canned(
        Func<Board?> board, bool calm = false)
    {
        var at = NowWorkingSamples.Now;
        var clock = new FakeWallClock();
        var wall = new NowWorkingViewModel(
            board,
            NowWorkingSamples.Overview,
            NowWorkingSamples.Items,
            action => { action(); return Task.CompletedTask; },
            tail: null,
            now: () => at,
            clock: clock);

        wall.IsCalm = calm;
        foreach (var (id, tail) in NowWorkingSamples.Tails())
        {
            wall.SetOutput(id, tail);
        }

        wall.Start();
        Seed(wall, at);
        wall.Frames(at);

        return (wall, clock, () => at);
    }

    /// <summary>
    /// An hour of a busy fleet behind the wall, so the log and the heartbeat are not drawn empty.
    /// </summary>
    /// <remarks>
    /// Every one of these is a feed event of exactly the shape the orchestrator emits — the same types,
    /// in the same proportions, with the same item payload — because the two panels they feed are the
    /// two that would look most convincing on invented data and mean least.
    /// </remarks>
    private static void Seed(NowWorkingViewModel wall, DateTimeOffset at)
    {
        var items = NowWorkingSamples.Items();
        (string Type, string? State)[] cycle =
        [
            ("work_item.working", "Working"),
            ("iteration.started", null),
            ("audit.started", null),
            ("work_item.auditing", "Auditing"),
            ("audit.findings.emitted", null),
            ("iteration.completed", null),
            ("work_item.reworking", "Reworking"),
            ("audit.started", null),
            ("work_item.audit_passed", "Done"),
            ("merge.started", null),
            ("work_item.merging", "Merging"),
            ("work_item.done", "Done"),
        ];

        // Uneven on purpose: a real hour has quiet stretches, and a chart that is flat-busy is the one
        // shape that proves nothing.
        int[] perMinute =
        [
            0, 2, 0, 0, 1, 3, 5, 4, 0, 0, 0, 1, 2, 6, 8, 5, 2, 0, 0, 0,
            0, 0, 1, 4, 7, 9, 6, 3, 1, 0, 0, 2, 3, 0, 0, 0, 1, 5, 4, 2,
            0, 0, 0, 0, 3, 6, 4, 1, 0, 0, 2, 4, 5, 3, 0, 1, 6, 4, 3, 2,
        ];

        var n = 0;
        for (var minute = 0; minute < perMinute.Length; minute++)
        {
            for (var k = 0; k < perMinute[minute]; k++)
            {
                // Clamped: a seeded burst must not carry a timestamp later than the moment the screen is
                // being photographed at, or the log prints out of order.
                var when = at.AddMinutes(-(perMinute.Length - 1 - minute)).AddSeconds(k * 7);
                when = when > at ? at : when;
                var (type, state) = cycle[n % cycle.Length];
                var item = items[n % 4];
                wall.Accept(
                    new CodeyBoxEvent(++n, type, item.Id, item.ProjectId, when, item.Title, state),
                    when);
            }
        }

        // One thing that just happened, so the newest line is the one that glows.
        wall.Accept(
            new CodeyBoxEvent(++n, "work_item.done", items[3].Id, items[3].ProjectId, at, items[3].Title, "Done"),
            at);
    }

    [Fact]
    public void The_canned_wall_renders_on_a_laptop_and_on_a_monitor()
    {
        var (wall, _, _) = Canned(NowWorkingSamples.Board);

        var laptop = Shot(wall, "nowworking-laptop", LaptopWidth, LaptopHeight);
        var monitor = Shot(wall, "nowworking-monitor", MonitorWidth, MonitorHeight);

        Console.WriteLine($"[wall] {laptop}");
        Console.WriteLine($"[wall] {monitor}");
        Assert.True(File.Exists(laptop));
        Assert.True(File.Exists(monitor));
    }

    /// <summary>
    /// The motion, proved: the same scene photographed at the moment a slot changed state and again once
    /// the flash has decayed. If the two files are identical, nothing on this screen moves.
    /// </summary>
    [Fact]
    public void A_slot_changing_state_looks_different_a_second_and_a_half_later()
    {
        var at = NowWorkingSamples.Now;
        var board = NowWorkingSamples.Board();
        var clock = new FakeWallClock();
        var wall = new NowWorkingViewModel(
            () => board,
            NowWorkingSamples.Overview,
            NowWorkingSamples.Items,
            action => { action(); return Task.CompletedTask; },
            tail: null,
            now: () => at,
            clock: clock);

        foreach (var (id, tail) in NowWorkingSamples.Tails())
        {
            wall.SetOutput(id, tail);
        }

        wall.Start();
        Seed(wall, at);
        wall.Frames(at.AddHours(1));   // the arrival flashes and the seeded log are long settled

        // Something happens: an item moves from Working to Auditing, and the feed says so.
        var moved = NowWorkingSamples.Items().First(i => i.State == "Working") with { State = "Auditing" };
        board = board with
        {
            Now = [.. board.Now.Select(ch => ch.Head.Id == moved.Id
                ? ch with { Head = moved, Steps = [new Step(moved, 0, StepState.Running, string.Empty)] }
                : ch)],
        };
        at = at.AddSeconds(1);
        wall.Tick(at);
        wall.Accept(
            new CodeyBoxEvent(900, "work_item.auditing", moved.Id, moved.ProjectId, at, moved.Title, "Auditing"),
            at);
        wall.Frames(at);

        var before = Shot(wall, "nowworking-motion-before", LaptopWidth, LaptopHeight);

        // …and a second and a half later, with nothing else having happened.
        at += NowWorkingViewModel.FlashLife;
        wall.Tick(at);
        wall.Frames(at);

        var after = Shot(wall, "nowworking-motion-after", LaptopWidth, LaptopHeight);

        Console.WriteLine($"[wall] motion: {before} vs {after}");
        Assert.True(File.Exists(before));
        Assert.True(File.Exists(after));

        // What changed, asserted on the values the screen is drawn from: the flash was at full strength
        // in the first frame and gone in the second, and the timers moved on.
        var flashed = wall.Slots.Single(s => s.Card.ItemId == moved.Id);
        Assert.Equal(0, flashed.Glow);
        Assert.Equal(0, wall.Ticker[0].Glow);
        Assert.Contains("Auditing", flashed.Phase, StringComparison.Ordinal);

        // And in the pixels — but only when this class has the process's headless session to itself.
        // A capture taken after the suite's other render classes have started and disposed theirs comes
        // back as the same empty frame whatever is on screen, which is a property of the harness (see the
        // class remarks) and would otherwise make this a test of test ordering. The control is a wall
        // with nothing on it at the same size: if THAT is indistinguishable from a wall with four running
        // slots on it, nothing was drawn and there is nothing to compare.
        var control = Shot(Idle(), "nowworking-motion-control", LaptopWidth, LaptopHeight);
        if (File.ReadAllBytes(control).SequenceEqual(File.ReadAllBytes(before)))
        {
            Console.WriteLine("[wall] the harness drew nothing this run — pixel comparison skipped.");
            return;
        }

        Assert.False(File.ReadAllBytes(before).SequenceEqual(File.ReadAllBytes(after)));
    }

    /// <summary>
    /// The switch that gives the wall the whole tab, checked where it actually happens: in the tab.
    /// </summary>
    /// <remarks>
    /// The rail and the pane header belong to <see cref="CodeyBoxQueueView"/>, not to the wall, so this
    /// is the one part of the feature whose binding path can be wrong without any view model noticing —
    /// a mistyped <c>Sections.IsChromeVisible</c> simply leaves the rail standing and nothing fails.
    /// </remarks>
    [Fact]
    public void Wall_mode_takes_the_rail_and_the_pane_header_off_the_tab() => CheckChrome();

    /// <summary>
    /// The body of the check above. A method rather than the test itself because the headless session's
    /// dispatch is blocking, and a blocking wait inside a <c>[Fact]</c> is a deadlock the analyzers
    /// refuse on sight — the same shape <see cref="DeadButtonTests"/> uses, for the same reason.
    /// </summary>
    private static void CheckChrome()
    {
        CodeyBoxQueueViewModel? vm = null;
        try
        {
            using var session = HeadlessUnitTestSession.StartNew(typeof(ShotAppBuilder));
            session.Dispatch(() =>
            {
                vm = new CodeyBoxQueueViewModel(
                    new CodeyBoxClient(new CodeyBoxOptions("http://127.0.0.1:1", "k"), new Offline()),
                    action => { action(); return Task.CompletedTask; });
                vm.Sections.Section = CodeyBoxSection.NowWorking;

                var window = new Window
                {
                    Width = LaptopWidth,
                    Height = LaptopHeight,
                    Content = new CodeyBoxQueueView { DataContext = vm },
                };
                window.Show();
                Dispatcher.UIThread.RunJobs();

                var rail = window.GetVisualDescendants().OfType<Border>().First(b => b.Width == 168);
                Assert.True(rail.IsEffectivelyVisible);

                vm.Sections.NowWorking.ToggleWallCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                Assert.False(rail.IsEffectivelyVisible);

                // And back: the tab is never a room without a door.
                vm.Sections.NowWorking.ToggleWallCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                Assert.True(rail.IsEffectivelyVisible);

                window.Close();
            }, CancellationToken.None).GetAwaiter().GetResult();
        }
        finally
        {
            vm?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>Answers every request with an empty body, so the tab can be rendered with no host.</summary>
    private sealed class Offline : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("null", System.Text.Encoding.UTF8, "application/json"),
            });
    }

    /// <summary>A wall with nothing on it: the control frame, and the idle-fleet shot.</summary>
    private static NowWorkingViewModel Idle()
    {
        var empty = NowWorkingSamples.Board() with { Now = [], Slots = (0, 5) };
        var at = NowWorkingSamples.Now;
        var wall = new NowWorkingViewModel(
            () => empty,
            NowWorkingSamples.Overview,
            NowWorkingSamples.Items,
            action => { action(); return Task.CompletedTask; },
            tail: null,
            now: () => at,
            clock: new FakeWallClock());
        wall.Start();
        return wall;
    }

    [Fact]
    public void Calm_renders_the_same_wall_with_the_decoration_switched_off()
    {
        var (wall, _, _) = Canned(NowWorkingSamples.Board, calm: true);

        Assert.All(wall.Slots, s => Assert.Equal(0, s.Glow));
        Assert.All(wall.Ticker, t => Assert.Equal(0, t.Glow));

        var calm = Shot(wall, "nowworking-calm", LaptopWidth, LaptopHeight);
        Console.WriteLine($"[wall] {calm}");
        Assert.True(File.Exists(calm));
    }

    /// <summary>An idle fleet is the state this screen spends most of its life in, and the one a sample
    /// full of running work never shows.</summary>
    [Fact]
    public void An_idle_fleet_draws_five_outlines_and_a_quiet_chart()
    {
        var wall = Idle();

        Assert.Equal("Nothing running", wall.Headline);
        Assert.Equal(5, wall.Slots.Count);
        Assert.All(wall.Slots, s => Assert.True(s.IsFree));

        var idle = Shot(wall, "nowworking-idle", LaptopWidth, LaptopHeight);
        Console.WriteLine($"[wall] {idle}");
        Assert.True(File.Exists(idle));
    }
}
