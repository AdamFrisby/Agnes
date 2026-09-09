using Agnes.Abstractions;
using Agnes.App.Desktop.ViewModels;
using Agnes.App.Desktop.Views;
using Agnes.Client;
using Agnes.Client.Simulation;
using Agnes.Protocol;
using Agnes.Ui.Core;
using Agnes.Ui.Core.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Agnes.Desktop.Tests;

/// <summary>
/// The desktop's two status surfaces, rendered for real: the one band above the transcript, and the
/// dashboard row. Rendering is the point — a binding to a property that does not exist throws on attach,
/// not at build time, and the band's whole contract is a visual one: it is present whenever the agent has
/// said anything, and it *raises* rather than appears when nobody has been looking.
///
/// There used to be a third surface: a faint duplicate of the same sentence under the tab header. It is
/// gone, and <see cref="The_header_no_longer_repeats_the_status_under_the_toolbar"/> keeps it gone.
/// </summary>
[Collection("desktop-headless")]
public class AgentStatusSurfaceTests
{
    private sealed class TestApp : Application
    {
        public override void Initialize() => Styles.Add(new FluentTheme());
    }

    public static class TestAppBuilder
    {
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<TestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
    }

    private const string Reported =
        "Found the config default is wrong; fixing it and adding a regression test, which is the last step of the plan.";

    // A clock the test moves by hand — the away band is three minutes of not looking, and a test must not
    // take three minutes to say so.
    private sealed class Clock(DateTimeOffset start)
    {
        public DateTimeOffset Now { get; set; } = start;

        public void Advance(TimeSpan by) => Now += by;

        public Func<DateTimeOffset> Read => () => Now;
    }

    [Fact]
    public async Task The_band_carries_the_agents_latest_line_and_its_age()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));
        await session.Dispatch(() =>
        {
            var clock = new Clock(new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero));
            var scene = Show(clock);

            scene.Emit(new AgentStatusEvent(Reported));
            Dispatcher.UIThread.RunJobs();

            var band = Band(scene.Window);
            Assert.True(band.IsEffectivelyVisible);
            Assert.Contains(Reported, Texts(band));
            Assert.Contains("just now", Texts(band));

            // Quiet: no "while you were away", no dismiss — the person is right here.
            Assert.DoesNotContain("While you were away", Texts(band));
            Assert.DoesNotContain("raised", band.Classes);

            scene.Window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task A_session_whose_agent_has_said_nothing_shows_no_band_at_all()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));
        await session.Dispatch(() =>
        {
            var scene = Show(new Clock(new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero)));
            Dispatcher.UIThread.RunJobs();

            Assert.False(Band(scene.Window).IsEffectivelyVisible);

            scene.Window.Close();
        }, CancellationToken.None);
    }

    /// <summary>The band replaced a second copy of itself under the toolbar; one sentence, one place.</summary>
    [Fact]
    public async Task The_header_no_longer_repeats_the_status_under_the_toolbar()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));
        await session.Dispatch(() =>
        {
            var clock = new Clock(new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero));
            var scene = Show(clock);
            scene.Emit(new AgentStatusEvent(Reported));
            Dispatcher.UIThread.RunJobs();

            Assert.DoesNotContain(scene.Window.GetVisualDescendants().OfType<Grid>(), g => g.Name == "StatusLine");
            // And the sentence itself is rendered exactly once, not stacked twice.
            Assert.Single(Texts(scene.Window), t => t == Reported);

            scene.Window.Close();
        }, CancellationToken.None);
    }

    /// <summary>
    /// A working agent that has gone quiet swaps the age for how long it has been quiet — in the band, in
    /// FgFaint, and never in a status hue.
    /// </summary>
    [Fact]
    public async Task Silence_from_a_working_agent_is_reported_in_the_band()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));
        await session.Dispatch(() =>
        {
            var clock = new Clock(new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero));
            var scene = Show(clock);

            scene.Emit(new AgentStatusEvent(Reported));
            // Stale only counts while the agent is actually working, and "working" is derived from the
            // stream — a streamed chunk is what puts the session in a running turn.
            scene.Emit(new MessageChunkEvent(MessageRole.Assistant, new TextContent("thinking about it")));
            clock.Advance(TimeSpan.FromMinutes(12));
            scene.Session.RaiseStatusAge();
            Dispatcher.UIThread.RunJobs();

            Assert.Contains(Texts(Band(scene.Window)), t => t.StartsWith("no update for", StringComparison.Ordinal));

            scene.Window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task The_band_raises_when_nobody_has_looked_and_settles_the_moment_somebody_does()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));
        await session.Dispatch(() =>
        {
            var clock = new Clock(new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero));
            var scene = Show(clock);

            // Fresh on screen: the person is right here, so there is nothing to catch them up on.
            scene.Emit(new AgentStatusEvent(Reported));
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain("While you were away", Texts(scene.Window));

            // Four minutes later the agent does something more, and nobody has touched the tab.
            clock.Advance(TimeSpan.FromMinutes(4));
            scene.Emit(new AgentStatusEvent(Reported));
            Dispatcher.UIThread.RunJobs();

            Assert.Contains("While you were away", Texts(scene.Window));
            Assert.Contains(Reported, Texts(scene.Window));
            Assert.Contains("raised", Band(scene.Window).Classes);

            // Any sign of life settles it — here the same call every scroll, keystroke and click makes.
            scene.Session.NoteUserInteraction();
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain("While you were away", Texts(scene.Window));

            // ...but the band stays: only the emphasis changed.
            var band = Band(scene.Window);
            Assert.True(band.IsEffectivelyVisible);
            Assert.DoesNotContain("raised", band.Classes);
            Assert.Contains(Reported, Texts(band));

            scene.Window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task A_dashboard_row_shows_the_line_under_the_session_title()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));
        await session.Dispatch(() =>
        {
            var clock = new Clock(new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero));
            var scene = Show(clock);
            scene.Emit(new AgentStatusEvent(Reported));

            // The dashboard's own card template, driven by a row mirroring that tab — the same object the
            // real dashboard builds, rather than a restatement of it here.
            var rows = new ItemsControl { ItemsSource = new[] { new DashboardSessionRow(scene.Document) } };
            if (new DashboardView().FindControl<ItemsControl>("LiveRows")?.ItemTemplate is { } template)
            {
                rows.ItemTemplate = template;
            }

            var window = new Window { Width = 1100, Height = 700, Content = rows };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.Contains(Reported, Texts(window));
            Assert.Contains("just now", Texts(window));

            window.Close();
            scene.Window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public void The_tabs_tooltip_says_where_it_runs_and_what_the_agent_is_doing()
    {
        var clock = new Clock(new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero));
        var (doc, view) = Attach(clock);
        doc.WorkingDirectory = "/home/a/agnes";

        Assert.Equal("/home/a/agnes", doc.TabTooltip);

        view.Apply(new AgentStatusEvent(Reported) { Sequence = 1 });

        Assert.Contains("/home/a/agnes", doc.TabTooltip, StringComparison.Ordinal);
        Assert.Contains(Reported, doc.TabTooltip, StringComparison.Ordinal);
        Assert.Contains("just now", doc.TabTooltip, StringComparison.Ordinal);
    }

    // ---- helpers ----

    private sealed class Scene(Window window, SessionDocument document, SessionViewModel session, SessionView view)
    {
        private long _seq;

        public Window Window => window;
        public SessionDocument Document => document;
        public SessionViewModel Session => session;

        public void Emit(SessionEvent @event) => view.Apply(@event with { Sequence = ++_seq });
    }

    private static (SessionDocument Document, SessionView View) Attach(Clock clock)
    {
        var view = new SessionView("s1");
        view.ApplySnapshot(new SessionSnapshot(new SessionInfo("s1", "opencode", string.Empty, 0), [], 0));
        var vm = new SessionViewModel(new SimulatedHost(), view, ImmediateDispatcher.Instance, "OpenCode",
            now: clock.Read);
        var doc = new SessionDocument(new NullTabController(), ImmediateDispatcher.Instance) { Title = "agnes" };
        doc.AttachSession(vm);
        return (doc, view);
    }

    private static Scene Show(Clock clock)
    {
        var (doc, view) = Attach(clock);
        var window = new Window { Width = 1280, Height = 900, Content = new SessionTabView { DataContext = doc } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return new Scene(window, doc, doc.Session!, view);
    }

    private static Border Band(Visual root)
        => Assert.Single(root.GetVisualDescendants().OfType<Border>(), b => b.Name == "StatusBand");

    private static List<string> Texts(Visual root)
        => [.. root.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible)
            .Select(t => t.Text ?? string.Empty)];
}
