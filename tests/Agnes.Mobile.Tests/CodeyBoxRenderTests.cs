using Agnes.App.Mobile.Services;
using Agnes.App.Mobile.ViewModels;
using Agnes.App.Mobile.Views;
using Agnes.Plugins.CodeyBox;
using Agnes.Plugins.CodeyBox.Tests;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Agnes.Mobile.Tests;

/// <summary>
/// The fleet screens as pixels, at both sizes they are actually read at.
/// </summary>
/// <remarks>
/// <para>What is left to go wrong on these screens is dimensional. A vitals card whose figure is clipped
/// by its own sparkline, a decision whose choices are below the fold, a chain strip that takes the
/// title's width, a section heading with nothing under it — none of that throws, none of it fails an
/// assertion about a control existing, and none of it is visible from reading the markup. So the frames
/// get written and looked at, and the assertions cover the things a picture cannot check: that the words
/// that decide something are on screen, and that a phone-sized window has not simply produced an empty
/// one.</para>
///
/// <para>The fleet is the plugin's own canned samples, which its tests already exercise. The operator's
/// real orchestrator is running real work and is never touched.</para>
///
/// <para>Set <c>AGNES_MOBILE_SHOTS</c> to choose where the frames land.</para>
/// </remarks>
[Collection(AvaloniaCollection.Name)]
public sealed class CodeyBoxRenderTests : IDisposable
{
    /// <summary>A common Android phone in device-independent pixels.</summary>
    private const int PhoneWidth = 411;
    private const int PhoneHeight = 891;

    /// <summary>The Lenovo TB310FU this was verified on, in the same units.</summary>
    private const int TabletWidth = 800;
    private const int TabletHeight = 1340;

    private static readonly string ShotDir =
        Environment.GetEnvironmentVariable("AGNES_MOBILE_SHOTS")
        ?? Path.Combine(Path.GetTempPath(), "agnes-mobile-shots");

    private readonly AvaloniaSession _avalonia;

    private readonly string _state = Path.Combine(
        Path.GetTempPath(), "agnes-codeybox-render-" + Guid.NewGuid().ToString("n"));

    public CodeyBoxRenderTests(AvaloniaSession avalonia)
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

    [Theory]
    [InlineData(PhoneWidth, PhoneHeight, "phone")]
    [InlineData(TabletWidth, TabletHeight, "tablet")]
    public async Task The_three_segments_draw(int width, int height, string size)
    {
        await _avalonia.Run(() =>
        {
            var (shell, window) = Open(width, height);
            shell.SelectTab(ShellTab.CodeyBox);
            shell.CodeyBox.OnHidden();       // nothing is behind it; the canned fleet goes in instead
            shell.CodeyBox.Show(Fleet.Gathered(), BoardSamples.Fleet());
            Pump();

            Shot(window, $"codeybox-overview-{size}");
            var overview = Texts(window);
            Assert.Contains(overview, t => t == "LANDED THIS WEEK");
            Assert.Contains(overview, t => t.Contains("NEEDS A LOOK", StringComparison.Ordinal));
            // The sentence names the constraint, and it is the first thing on the screen.
            Assert.Contains(overview, t => t.Contains(OverviewSamples.Fleet().Sentence[..20], StringComparison.Ordinal));

            shell.CodeyBox.ShowNowWorkingCommand.Execute(null);
            Pump();
            Shot(window, $"codeybox-now-working-{size}");
            Assert.Contains(Texts(window), t => t.Contains("slots busy", StringComparison.Ordinal));

            shell.CodeyBox.ShowQueueCommand.Execute(null);
            Pump();
            Shot(window, $"codeybox-queue-{size}");
            Assert.Contains(Texts(window), t => t.StartsWith("Now", StringComparison.Ordinal));

            window.Close();
        });
    }

    [Theory]
    [InlineData(PhoneWidth, PhoneHeight, "phone")]
    [InlineData(TabletWidth, TabletHeight, "tablet")]
    public async Task An_item_leads_with_its_decision(int width, int height, string size)
    {
        await _avalonia.Run(() =>
        {
            var handler = new FleetHandler();
            var (shell, window) = Open(width, height, handler);
            shell.CodeyBox.Show(Fleet.Gathered(), BoardSamples.Fleet());

            var client = new CodeyBoxClient(Fleet.Config.ToOptions(), handler);
            var page = new CodeyBoxItemPageViewModel(
                shell, shell.CodeyBox, client, DecisionSamples.Row("AuditFailed", "never converged"));
            shell.Push(page);
            page.LoadAsync().GetAwaiter().GetResult();
            Pump();

            Shot(window, $"codeybox-item-failed-{size}");

            var texts = Texts(window);
            // The situation, the evidence and every choice's consequence: the three things that decide it.
            Assert.Contains(texts, t => t.Contains("The audit never passed", StringComparison.Ordinal));
            Assert.Contains(texts, t => t.Contains("never converged", StringComparison.Ordinal));
            Assert.Contains(texts, t => t.Contains("Raise the audit ceiling", StringComparison.Ordinal));
            Assert.Contains(texts, t => t.Contains("Cancel the item", StringComparison.Ordinal));

            window.Close();
        });
    }

    [Theory]
    [InlineData(PhoneWidth, PhoneHeight, "phone")]
    [InlineData(TabletWidth, TabletHeight, "tablet")]
    public async Task A_parked_item_shows_the_question_verbatim(int width, int height, string size)
    {
        await _avalonia.Run(() =>
        {
            var question = DecisionSamples.Question();
            var handler = new FleetHandler
            {
                QuestionsBody = $$"""
                    [{"id":"aa11","workItemId":"{{DecisionSamples.Row("NeedsOperatorInput").Id}}",
                      "questionId":"q-token-scope","questionText":{{System.Text.Json.JsonSerializer.Serialize(question.QuestionText)}},
                      "state":"open","askedAt":"2026-09-12T11:30:00+00:00","answeredAt":null,
                      "answerText":null,"answeredBy":null,"dismissedAt":null}]
                    """,
            };

            var (shell, window) = Open(width, height, handler);
            shell.CodeyBox.Show(Fleet.Gathered(), BoardSamples.Fleet());

            var client = new CodeyBoxClient(Fleet.Config.ToOptions(), handler);
            var page = new CodeyBoxItemPageViewModel(
                shell, shell.CodeyBox, client, DecisionSamples.Row("NeedsOperatorInput"));
            shell.Push(page);
            page.LoadAsync().GetAwaiter().GetResult();
            Pump();

            Shot(window, $"codeybox-item-parked-{size}");

            // Verbatim and untruncated is the whole point of the card.
            Assert.Contains(Texts(window), t => t.Contains(question.QuestionText, StringComparison.Ordinal));
            Assert.Contains(Texts(window), t => t == "Answer");

            window.Close();
        });
    }

    [Theory]
    [InlineData(PhoneWidth, PhoneHeight, "phone")]
    [InlineData(TabletWidth, TabletHeight, "tablet")]
    public async Task Setup_is_two_fields_and_a_test(int width, int height, string size)
    {
        await _avalonia.Run(() =>
        {
            var (shell, window) = Open(width, height);
            shell.SelectTab(ShellTab.More);
            shell.Push(new CodeyBoxSetupPageViewModel(shell, shell.CodeyBox));
            Pump();

            Shot(window, $"codeybox-setup-{size}");

            var texts = Texts(window);
            Assert.Contains(texts, t => t == "ADDRESS");
            Assert.Contains(texts, t => t == "API KEY");
            Assert.Contains(texts, t => t.Contains("Test the connection", StringComparison.Ordinal));

            window.Close();
        });
    }

    [Theory]
    [InlineData(PhoneWidth, PhoneHeight, "phone")]
    [InlineData(TabletWidth, TabletHeight, "tablet")]
    public async Task The_inbox_carries_the_fleet_under_the_agent_approvals(int width, int height, string size)
    {
        await _avalonia.Run(() =>
        {
            var (shell, window) = Open(width, height);
            shell.SelectTab(ShellTab.Inbox);
            shell.Inbox.CodeyBoxRows.Add(new CodeyBoxNeedsRow(
                DecisionSamples.Row("NeedsOperatorInput"), DecisionSamples.Question()));
            shell.Inbox.CodeyBoxRows.Add(new CodeyBoxNeedsRow(
                DecisionSamples.Row("AuditFailed", "never converged"), null));
            Pump();

            Shot(window, $"codeybox-inbox-{size}");

            var texts = Texts(window);
            Assert.Contains(texts, t => t == "CODEYBOX NEEDS YOU");
            Assert.Contains(texts, t => t == "Answer");
            Assert.Contains(texts, t => t == "Dismiss");

            window.Close();
        });
    }

    [Fact]
    public async Task The_fifth_tab_appears_only_for_a_device_that_watches_a_fleet()
    {
        await _avalonia.Run(() =>
        {
            new CodeyBoxConfig().Save();
            var shell = new ShellViewModel(
                new MobileConnector(), new MobileDispatcher(), new MobileSettings(), "Fleet render",
                codeyBoxClient: _ => null);
            var window = new Window
            {
                Width = PhoneWidth,
                Height = PhoneHeight,
                Content = new ShellView { DataContext = shell },
            };
            window.Show();
            Pump();

            // Four destinations, sharing the bar exactly as they did before CodeyBox existed.
            Assert.DoesNotContain(Texts(window), t => t == "Fleet");
            Assert.Equal(4, NavLabels(window).Count);
            var four = NavTargets(window)[0].Bounds.Width;

            shell.CodeyBox.Save(Fleet.Config);
            Pump();

            Assert.Contains(Texts(window), t => t == "Fleet");
            Assert.Equal(5, NavLabels(window).Count);

            // Five equal targets across 411 dp is 82 dp each — comfortably over the 48 dp floor, and the
            // four that were there are narrower than they were, not overlapping.
            var five = NavTargets(window)[0].Bounds.Width;
            Assert.True(five < four, "the bar re-divides rather than pushing a tab off it");
            Assert.True(NavTargets(window).All(b => b.Bounds.Width >= 48), "nothing tappable under 48dp");

            Shot(window, "codeybox-nav-five-tabs");
            window.Close();
        });
    }

    // ---- harness ----

    private static (ShellViewModel Shell, Window Window) Open(int width, int height, FleetHandler? handler = null)
    {
        var shell = Fleet.Shell(handler);
        var window = new Window
        {
            Width = width,
            Height = height,
            Content = new ShellView { DataContext = shell },
        };
        window.Show();
        Pump();
        return (shell, window);
    }

    /// <summary>The bottom navigation's labels, in order. The one place the tab count is observable.</summary>
    private static IReadOnlyList<TextBlock> NavLabels(Visual root)
        => [.. root.GetVisualDescendants()
            .OfType<RadioButton>()
            .Where(r => r.IsEffectivelyVisible && r.GroupName == "tabs")
            .Select(r => r.GetVisualDescendants().OfType<TextBlock>().First())];

    private static IReadOnlyList<RadioButton> NavTargets(Visual root)
        => [.. root.GetVisualDescendants()
            .OfType<RadioButton>()
            .Where(r => r.IsEffectivelyVisible && r.GroupName == "tabs")];

    private static IReadOnlyList<string> Texts(Visual root)
        => [.. root.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible)
            .Select(t => t.Text ?? string.Empty)
            .Where(t => t.Length > 0)];

    private static void Pump()
    {
        for (var i = 0; i < 8; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void Shot(Window window, string name)
    {
        Pump();
        using var frame = window.CaptureRenderedFrame();
        if (frame is null)
        {
            return;
        }

        Directory.CreateDirectory(ShotDir);
        using var file = File.Create(Path.Combine(ShotDir, name + ".png"));
        frame.Save(file, new PngBitmapEncoderOptions());
    }
}
