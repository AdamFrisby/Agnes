using Agnes.App.Mobile.Services;
using Agnes.App.Mobile.ViewModels;
using Agnes.App.Mobile.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Agnes.Mobile.Tests;

/// <summary>
/// The status line as it is actually drawn — the card, the session header, and the "while you were
/// away" band.
///
/// A view-model test cannot catch what breaks here: a row bound to a property nobody raises, a resource
/// key that doesn't exist, a band whose visibility is wired to the wrong half of its condition. So this
/// lays out the real screens against the real theme.
/// </summary>
[Collection(AvaloniaCollection.Name)]
public sealed class StatusRenderTests : IDisposable
{
    private const string Line = "Found the leak in the tail cursor; rewriting the resume path so a reconnect can't replay.";
    private const string AwayLine = "Rebased onto main and re-ran the suite — two tests still red, both in the sandbox probe.";

    private readonly AvaloniaSession _avalonia;

    private readonly string _state = Path.Combine(
        Path.GetTempPath(), "agnes-status-render-" + Guid.NewGuid().ToString("n"));

    public StatusRenderTests(AvaloniaSession avalonia)
    {
        _avalonia = avalonia;
        JsonStore.UseDirectory(_state);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_state, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    [Fact]
    public async Task A_card_shows_the_agents_line_and_how_old_it_is()
    {
        await _avalonia.Run(
            () =>
            {
                var (shell, window) = OpenShell();
                var entry = Card(shell, latestStatus: Line, minutesAgo: 4);
                Pump();

                var texts = Texts(window);
                Assert.Contains(Line, texts);
                Assert.Contains("4m", texts);
                Assert.True(StatusRow(window, entry).IsVisible, "the card carries a status row");
            });
    }

    [Fact]
    public async Task A_card_without_a_status_grows_no_extra_row()
    {
        await _avalonia.Run(
            () =>
            {
                var (shell, window) = OpenShell();
                var entry = Card(shell, latestStatus: null, minutesAgo: 0);
                Pump();

                Assert.False(StatusRow(window, entry).IsVisible, "silence is not a state worth a line");
            });
    }

    [Fact]
    public async Task A_working_agent_that_has_gone_quiet_says_so_on_the_card()
    {
        await _avalonia.Run(
            () =>
            {
                var (shell, window) = OpenShell();
                var entry = Card(shell, latestStatus: Line, minutesAgo: 12);
                Pump();

                // Not live, so the card's own activity is Idle; the stale wording is the running case, and
                // the entry is what decides it. Assert the rule at the entry and the render around it.
                Assert.Equal("12m", entry.StatusAge);
                Assert.Equal(
                    "no update for 12 min",
                    StatusLine.Age(entry.AgentStatus, working: true));
                Assert.Contains("12m", Texts(window));
            });
    }

    [Fact]
    public async Task The_session_header_carries_the_line_too()
    {
        await _avalonia.Run(
            () =>
            {
                var (shell, window) = OpenShell();
                var entry = Card(shell, latestStatus: null, minutesAgo: 0);

                var page = new SessionPageViewModel(shell, shell.Sessions, entry, session: null)
                {
                    StatusSource = new StubStatus
                    {
                        Status = new AgentStatus(Line, DateTimeOffset.Now.AddMinutes(-2)),
                    },
                };
                shell.Push(page);
                Pump();

                var texts = Texts(window);
                Assert.True(page.HasStatus);
                Assert.Contains(Line, texts);
                Assert.Contains("2m", texts);
                Assert.False(page.ShowAwayBand, "nobody was away; this is just the header line");
            });
    }

    [Fact]
    public async Task The_away_band_appears_for_a_session_nobody_was_watching()
    {
        await _avalonia.Run(
            () =>
            {
                var (shell, window) = OpenShell();
                var entry = Card(shell, latestStatus: null, minutesAgo: 0);
                var stub = new StubStatus
                {
                    Status = new AgentStatus(AwayLine, DateTimeOffset.Now.AddMinutes(-40)),
                    IsUnattended = true,
                    AwayStatus = AwayLine,
                };

                var page = new SessionPageViewModel(shell, shell.Sessions, entry, session: null)
                {
                    StatusSource = stub,
                };
                shell.Push(page);
                Pump();

                Assert.True(page.ShowAwayBand);
                Assert.Contains("While you were away", Texts(window));
                Assert.Contains(AwayLine, Texts(window));

                // Arriving is itself the end of being away: the session is marked attended, but the band
                // survives it — otherwise it would be a flicker nobody ever read.
                Assert.True(stub.Interactions > 0, "opening the page marks the session attended");

                // The first thing a person does takes it down.
                page.NoteUserInteraction();
                Pump();
                Assert.False(page.ShowAwayBand);
                Assert.DoesNotContain("While you were away", Texts(window));
            });
    }

    // ---- harness ----

    private sealed class StubStatus : IAgentStatusSource
    {
        public AgentStatus Status { get; init; } = AgentStatus.None;

        public bool IsUnattended { get; init; }

        public string? AwayStatus { get; init; }

        public int Interactions { get; private set; }

        public void NoteUserInteraction() => Interactions++;
    }

    private static (ShellViewModel Shell, Window Window) OpenShell()
    {
        var shell = new ShellViewModel(
            new MobileConnector(), new MobileDispatcher(), new MobileSettings(), "Status test");

        var window = new Window
        {
            Width = 412,
            Height = 915,
            Content = new ShellView { DataContext = shell },
        };
        window.Show();
        Pump();
        return (shell, window);
    }

    /// <summary>Puts one card in the list, straight from a saved pointer — which is the state the list is
    /// actually read in: cold, before any host has answered.</summary>
    private static SessionEntry Card(ShellViewModel shell, string? latestStatus, int minutesAgo)
    {
        var link = shell.Hosts.Links[0];
        var saved = new SavedSession(
            link.Name, link.Url, string.Empty, "s-" + Guid.NewGuid().ToString("n"), "claude-code",
            "Agnes", "/home/you/projects/agnes",
            LatestStatus: latestStatus,
            LatestStatusAt: latestStatus is null ? null : DateTimeOffset.Now.AddMinutes(-minutesAgo));

        var entry = new SessionEntry(saved, link);
        shell.Sessions.All.Add(entry);
        shell.SelectTab(ShellTab.Sessions);
        return entry;
    }

    private static Grid StatusRow(Visual root, SessionEntry entry)
        => root.GetVisualDescendants()
            .OfType<Grid>()
            .First(g => g.Name == "StatusRow" && ReferenceEquals(g.DataContext, entry));

    private static IReadOnlyList<string> Texts(Visual root)
        => [.. root.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible)
            .Select(t => t.Text ?? string.Empty)];

    private static void Pump()
    {
        for (var n = 0; n < 20; n++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }

        Dispatcher.UIThread.RunJobs();
    }
}
