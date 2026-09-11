using System.Windows.Input;
using Agnes.Plugins.CodeyBox.Views;
using Agnes.Plugins.CodeyBox.Views.Controls;
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
/// A whole overview, hand-built: five trace shapes, a row of each motion, vitals with and without a
/// control band, thirty days of flow and three quota windows.
/// </summary>
/// <remarks>
/// It lives in the test project on purpose. The view renders an <see cref="Overview"/> record and nothing
/// else, so anyone wiring the real data layer up can point at this to see the screen fully populated
/// before a single request goes out — and every awkward case (a trace with one point, an item that has
/// never audited, a quota window with no history) is here rather than waiting to be discovered on a live
/// host.
/// </remarks>
public static class OverviewSamples
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 14, 30, 0, TimeSpan.Zero);

    public static Overview Fleet() => new(
        Sentence: "Quota-bound. Codex gated until 18:10, Claude carrying 3 of 3 slots. 2 items wedged.",
        Verdict: TileTone.Attention,
        Vitals: Vitals(),
        Attention: Attention(),
        Healthy: Healthy(),
        Flow: Flow(),
        Quota: Quota(),
        Sample: Sample());

    /// <summary>The five headline numbers, in reading order.</summary>
    private static IReadOnlyList<Vital> Vitals() =>
    [
        // A band and a rising trend: the ordinary, fully-populated case.
        new Vital("LANDED THIS WEEK", "34", "up from 27 last week", TileTone.Active,
            [21, 24, 22, 27, 29, 26, 31, 34], Median: 27, BandLow: 22, BandHigh: 32, Current: 34, Trend.Up),

        // Flat inside its band: the case that must not read as a problem.
        new Vital("IN MOTION", "7 of 12", "5 stopped: 2 parked, 1 blocked, 2 wedged", TileTone.Neutral,
            [8, 7, 7, 6, 7, 8, 7, 7], Median: 7, BandLow: 5, BandHigh: 9, Current: 7, Trend.Flat),

        // Falling out of the bottom of its band — the shape that earns a tone.
        new Vital("ELIGIBLE CAPACITY", "1 of 3 agents", "codex gated, pi paused", TileTone.Attention,
            [3, 3, 3, 2, 3, 2, 1, 1], Median: 3, BandLow: 2, BandHigh: 3, Current: 1, Trend.Down),

        // History but no band yet: sparkline draws, the control band does not.
        new Vital("INFRA FAILURE RATE", "12%", "3 of 25 turns lost to the sandbox", TileTone.Bad,
            [0.04, 0.06, 0.05, 0.09, 0.11, 0.12], Median: null, BandLow: null, BandHigh: null,
            Current: 0.12, Trend.Unknown),

        // Neither: a fresh install, or a number this host cannot trend.
        new Vital("BLOCKED ON YOU", "2", "1 question, 1 failed item awaiting a decision", TileTone.Attention,
            [], Median: null, BandLow: null, BandHigh: null, Current: 2, Trend.Unknown),
    ];

    /// <summary>Live items needing a look, worst first — one of every shape the trace can take.</summary>
    private static IReadOnlyList<ItemTrace> Attention() =>
    [
        // Long, then silent. The trace looks healthy; the freshness dot is what gives it away.
        Trace("Rewrite the credential broker", "Working", "claude", Descending(11),
            ceiling: 25, Motion.Wedged, Convergence.Converging,
            why: "No audit progress and no output for 4h 12m.", sinceMoved: TimeSpan.FromHours(4.2), rank: 0),

        // Sawtooth on the same gate: iteration without progress.
        Trace("Harden the Incus volume lifecycle", "Working", "codex", Sawtooth(),
            ceiling: 25, Motion.Moving, Convergence.Oscillating,
            why: "completeness:llm-review has blocked 4 of the last 5 iterations.",
            sinceMoved: TimeSpan.FromMinutes(6), rank: 1),

        // Converging into its ceiling: the one row where extending the cap preserves work.
        Trace("Port the mobile inbox to sheets", "Working", "claude", Descending(22),
            ceiling: 25, Motion.Moving, Convergence.Converging,
            why: "3 iterations from the cap with 2 findings left.",
            sinceMoved: TimeSpan.FromMinutes(2), rank: 2, nearCeiling: true),

        // Flat and non-zero, and past its budget: twelve iterations against a cap of ten, which is the
        // only case where the trace draws a ceiling tick with bars still running past it.
        Trace("Retire the Uno desktop head", "Working", "codex", Flat(),
            ceiling: 10, Motion.Moving, Convergence.Stuck,
            why: "Same 4 findings since iteration 9.", sinceMoved: TimeSpan.FromMinutes(21), rank: 3),

        // Blocked on a person, which is the amber case.
        Trace("Decide the plugin signing story", "Failed", "claude", Descending(4),
            ceiling: 25, Motion.Blocked, Convergence.Converging,
            why: "Waiting on you: the item failed twice and needs a call.",
            sinceMoved: TimeSpan.FromHours(19), rank: 4, needsPerson: true),

        // Parked with a known resume: not a problem, and the resume time is the information.
        Trace("Bake the sandbox image", "Queued", "codex", [],
            ceiling: 25, Motion.Parked, Convergence.New,
            why: "Quota window reopens at 18:10.", sinceMoved: TimeSpan.FromMinutes(48), rank: 5),

        // One iteration, still reporting: the trailing bar is outlined rather than filled.
        Trace("Add a Pi smoke test", "Working", "pi", [new TracePoint(1, 3, false, false)],
            ceiling: 12, Motion.Moving, Convergence.New,
            why: "First audit still reporting.", sinceMoved: TimeSpan.FromMinutes(1), rank: 6),
    ];

    /// <summary>Moving and converging: the rows that collapse behind a count.</summary>
    private static IReadOnlyList<ItemTrace> Healthy() =>
    [
        Trace("Tidy the release workflow", "Working", "claude", Descending(6), 25, Motion.Moving,
            Convergence.Converging, why: string.Empty, sinceMoved: TimeSpan.FromMinutes(3), rank: 90),
        Trace("Document the event spine", "Working", "codex", Descending(4), 25, Motion.Moving,
            Convergence.Converging, why: string.Empty, sinceMoved: TimeSpan.FromMinutes(9), rank: 91),
        Trace("Land the Copilot effort flag", "Working", "claude", Passed(), 25, Motion.Moving,
            Convergence.Passed, why: string.Empty, sinceMoved: TimeSpan.FromMinutes(11), rank: 92),
    ];

    /// <summary>A month of cumulative flow: created pulling away, landed following, a little cancelled.</summary>
    private static FlowSeries Flow()
    {
        var start = DateOnly.FromDateTime(Now.UtcDateTime).AddDays(-29);
        var days = new List<FlowPoint>(30);
        int created = 120, landed = 96, cancelled = 8;

        for (var i = 0; i < 30; i++)
        {
            created += 3 + (i % 4);
            // Landing lags creation, and stalls for a stretch in the middle — the gap that widens is the
            // whole reason this chart is here.
            landed += i is > 11 and < 19 ? 0 : 2 + (i % 3);
            cancelled += i % 7 == 0 ? 1 : 0;
            days.Add(new FlowPoint(start.AddDays(i), created, landed, cancelled));
        }

        return new FlowSeries(days);
    }

    /// <summary>Three quota windows: one burning with a projection, one ineligible, one with no history.</summary>
    private static IReadOnlyList<QuotaBurn> Quota() =>
    [
        new QuotaBurn("claude", "five_hour", Burn(78, 55, 8), Now.AddHours(2), NowPct: 55,
            ProjectedUnspentPct: 12, Eligible: true),
        new QuotaBurn("codex", "five_hour", Burn(40, 3, 8), Now.AddHours(3.7), NowPct: 3,
            ProjectedUnspentPct: null, Eligible: false),
        new QuotaBurn("pi", null, [], null, NowPct: null, ProjectedUnspentPct: null, Eligible: false),
    ];

    private static IReadOnlyList<BurnSample> Burn(double from, double to, int count)
        => Enumerable.Range(0, count)
            .Select(i => new BurnSample(
                Now.AddMinutes(-15 * (count - 1 - i)),
                from + ((to - from) * i / (count - 1))))
            .ToList();

    private static OverviewSample Sample() => new(
        Now, Landed7d: 34, InMotion: 7, Parked: 2, Blocked: 1, Wedged: 2, EligibleAgents: 1,
        SlotsBusy: 3, SlotsTotal: 3, InfraFailureRate: 0.12, BlockedOnYou: 2);

    // ---- trace shapes ----

    /// <summary>Findings walking down to one or two: the loop doing its job.</summary>
    private static IReadOnlyList<TracePoint> Descending(int count)
        => Enumerable.Range(1, count)
            .Select(i => new TracePoint(i, Math.Max(1, 12 - i), true, false))
            .ToList();

    /// <summary>Down, then back up on the same gate, repeatedly. The one repetition that is waste.</summary>
    private static IReadOnlyList<TracePoint> Sawtooth()
    {
        int[] findings = [7, 4, 6, 3, 7, 2, 8, 3, 6];
        return findings
            .Select((f, i) => new TracePoint(i + 1, f, true, i > 0 && f > findings[i - 1]))
            .ToList();
    }

    /// <summary>The same count, iteration after iteration.</summary>
    private static IReadOnlyList<TracePoint> Flat()
        => Enumerable.Range(1, 12).Select(i => new TracePoint(i, i < 8 ? 6 : 4, true, i >= 8)).ToList();

    /// <summary>Ends on a clean audit: the last bar is the baseline pip.</summary>
    private static IReadOnlyList<TracePoint> Passed()
        => [.. Descending(5), new TracePoint(6, 0, true, false)];

    private static ItemTrace Trace(
        string title,
        string state,
        string agent,
        IReadOnlyList<TracePoint> points,
        int ceiling,
        Motion motion,
        Convergence shape,
        string why,
        TimeSpan sinceMoved,
        int rank,
        bool nearCeiling = false,
        bool needsPerson = false)
        => new(
            new WorkItemRow(
                Id: $"{Math.Abs(title.GetHashCode(StringComparison.Ordinal)):x8}", Title: title, State: state,
                Agent: agent, ProjectId: "codeybox-self", QueuePosition: rank, UpdatedAt: Now - sinceMoved,
                LastError: null),
            points, ceiling, motion, shape, why, nearCeiling, needsPerson, sinceMoved, rank);
}

/// <summary>
/// Renders the overview and each of its drawn controls, and captures a PNG of every one.
/// </summary>
/// <remarks>
/// A drawn control cannot be reviewed by reading it — a chart that throws nothing and shows nothing looks
/// exactly like a chart that works — so these render for real and write the frames out. Capture needs a
/// rendering backend the test project does not carry, so it is best-effort here: with headless drawing
/// the frame comes back null and the render assertions still hold. <c>tools/</c>-style harnesses (and the
/// scratchpad preview used while building this) get pixels by adding <c>Avalonia.Skia</c> and
/// <c>UseHeadlessDrawing = false</c>.
/// </remarks>
[Collection("avalonia-headless")]
public class OverviewViewTests
{
    /// <summary>
    /// Just enough theme for the roles to resolve. The desktop's <c>Themes/Tokens.axaml</c> is not
    /// reachable from a test project, so these stand in for it — which is itself worth having, because
    /// it proves the drawn controls find their hues by ROLE NAME rather than by carrying colours of
    /// their own.
    /// </summary>
    private sealed class TestApp : Application
    {
        public override void Initialize()
        {
            Styles.Add(new FluentTheme());
            RequestedThemeVariant = ThemeVariant.Dark;
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
            });
        }
    }

    public static class TestAppBuilder
    {
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<TestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
    }

    private static readonly string ShotDir =
        Environment.GetEnvironmentVariable("AGNES_OVERVIEW_SHOTS")
        ?? Path.Combine(Path.GetTempPath(), "overview-preview");

    /// <summary>Shows a control in a real window — the step that forces every binding to be evaluated —
    /// and hands the window back so a test can look inside it.</summary>
    private static void Render(Func<Control> build, string shot, double width, double height,
                               Action<Window>? inspect = null)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));
        session.Dispatch(() =>
        {
            var window = new Window
            {
                Width = width,
                Height = height,
                Background = new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x16)),
                Padding = new Thickness(16),
                Content = build(),
            };

            window.Show();
            Dispatcher.UIThread.RunJobs();
            Capture(window, shot);
            inspect?.Invoke(window);
            window.Close();
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static void Capture(Window window, string name)
    {
        WriteableBitmap? frame;
        try
        {
            frame = window.CaptureRenderedFrame();
        }
        catch (NotSupportedException)
        {
            // Headless drawing: nothing to capture, and nothing wrong.
            return;
        }

        if (frame is null)
        {
            return;
        }

        Directory.CreateDirectory(ShotDir);
        using (frame)
        using (var file = File.Create(Path.Combine(ShotDir, name + ".png")))
        {
            frame.Save(file, new PngBitmapEncoderOptions());
        }
    }

    [Fact]
    public void The_whole_overview_renders_from_a_hand_built_record()
        => Render(() => new OverviewView { DataContext = OverviewSamples.Fleet() },
                  "overview-page", 1180, 1000);

    [Fact]
    public void A_trace_renders_for_every_shape_the_loop_can_take()
    {
        var traces = OverviewSamples.Fleet().Attention.Concat(OverviewSamples.Fleet().Healthy).ToList();
        Render(() =>
        {
            var stack = new StackPanel { Spacing = 6 };
            foreach (var trace in traces)
            {
                stack.Children.Add(new TraceGlyph { Trace = trace, Width = 200, Height = 22 });
            }

            // And the empty case: no trace at all must draw nothing rather than throw.
            stack.Children.Add(new TraceGlyph { Width = 200, Height = 22 });
            return stack;
        }, "trace-glyphs", 260, 320);
    }

    [Fact]
    public void A_sparkline_renders_with_and_without_a_band()
        => Render(() =>
        {
            var stack = new StackPanel { Spacing = 10 };
            foreach (var vital in OverviewSamples.Fleet().Vitals)
            {
                stack.Children.Add(new Sparkline
                {
                    Values = vital.Spark,
                    BandLow = vital.BandLow,
                    BandHigh = vital.BandHigh,
                    Median = vital.Median,
                    Trend = vital.Trend,
                    Tone = vital.Tone,
                    Width = 180,
                    Height = 26,
                });
            }

            stack.Children.Add(new Sparkline { Width = 180, Height = 26 });
            return stack;
        }, "sparklines", 230, 260);

    [Fact]
    public void The_flow_chart_renders_a_month_of_days()
        => Render(() => new FlowChart { Series = OverviewSamples.Fleet().Flow, Height = 160 },
                  "flow-chart", 560, 220);

    [Fact]
    public void The_flow_chart_survives_having_nothing_to_draw()
        => Render(() => new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new FlowChart { Height = 80 },
                new FlowChart { Series = new FlowSeries([]), Height = 80 },
            },
        }, "flow-chart-empty", 320, 220);

    [Fact]
    public void A_burn_down_renders_for_each_quota_window()
        => Render(() =>
        {
            var stack = new StackPanel { Spacing = 12 };
            foreach (var burn in OverviewSamples.Fleet().Quota)
            {
                stack.Children.Add(new BurnDown { Burn = burn, Width = 240, Height = 44 });
            }

            stack.Children.Add(new BurnDown { Width = 240, Height = 44 });
            return stack;
        }, "burn-downs", 290, 260);

    [Fact]
    public void The_tone_edge_and_motion_dot_render_every_value_they_have()
        => Render(() =>
        {
            var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12 };
            foreach (var tone in Enum.GetValues<TileTone>())
            {
                row.Children.Add(new ToneEdge { Tone = tone, Width = 3, Height = 40 });
            }

            foreach (var motion in Enum.GetValues<Motion>())
            {
                row.Children.Add(new MotionDot { Motion = motion, Width = 10, Height = 10 });
            }

            return row;
        }, "tone-and-motion", 300, 90);

    [Fact]
    public void A_row_hands_the_open_command_the_trace_it_names()
    {
        // The one binding in this screen that reaches outside its own DataContext. If
        // $parent[v:OverviewView] ever stops resolving, every row silently becomes unclickable — which is
        // exactly the class of failure a render-only test cannot see.
        var overview = OverviewSamples.Fleet();
        var opened = new List<ItemTrace>();
        var extended = new List<ItemTrace>();

        Render(() => new OverviewView
        {
            DataContext = overview,
            OpenItemCommand = new Relay(t => opened.Add((ItemTrace)t!)),
            ExtendCeilingCommand = new Relay(t => extended.Add((ItemTrace)t!)),
        }, "overview-commands", 1180, 1000, window =>
        {
            var rows = window.GetVisualDescendants().OfType<Button>()
                .Where(b => b.Classes.Contains("tracerow")).ToList();

            Assert.NotEmpty(rows);
            Assert.All(rows, row => Assert.IsType<Relay>(row.Command));

            foreach (var row in rows)
            {
                row.Command!.Execute(row.CommandParameter);
            }

            // Extending a ceiling is offered only where it changes an outcome.
            var extend = window.GetVisualDescendants().OfType<Button>()
                .Where(b => b.Classes.Contains("secondary") && b.IsEffectivelyVisible).ToList();

            Assert.Single(extend);
            extend[0].Command!.Execute(extend[0].CommandParameter);
        });

        Assert.Equal(overview.Attention.Count, opened.Count(overview.Attention.Contains));
        Assert.Contains(overview.Attention[2], opened);
        Assert.Same(overview.Attention[2], Assert.Single(extended));
    }

    [Fact]
    public void The_overview_shows_its_empty_states_in_words()
    {
        // An overview with nothing behind it yet: no traces, no bands, no quota history. Every one of
        // those is a sentence, not a blank panel.
        var bare = new Overview(
            "Idle. Nothing is queued and no agent is working.",
            TileTone.Neutral,
            [new Vital("LANDED THIS WEEK", "0", "nothing landed", TileTone.Neutral, [], null, null, null, 0, Trend.Unknown)],
            [],
            [],
            new FlowSeries([]),
            [],
            new OverviewSample(DateTimeOffset.UnixEpoch, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));

        Render(() => new OverviewView { DataContext = bare }, "overview-empty", 900, 420, window =>
        {
            var said = window.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.IsEffectivelyVisible)
                .Select(t => t.Text ?? string.Empty)
                .ToList();

            Assert.Contains("Nothing needs a look.", said);
            Assert.Contains("Sparklines cover the time this tab has been open; a norm appears after a day of history.", said);
            Assert.Contains("No quota history on this host.", said);
        });
    }

    [Fact]
    public void The_overview_view_tolerates_having_no_record_at_all()
        // The section renders before the first build lands, and a null DataContext must draw an empty
        // pane rather than take the tab down.
        => Render(() => new OverviewView { DataContext = null }, "overview-null", 900, 300);

    private sealed class Relay(Action<object?> run) : ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => run(parameter);

        public void Raise() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
