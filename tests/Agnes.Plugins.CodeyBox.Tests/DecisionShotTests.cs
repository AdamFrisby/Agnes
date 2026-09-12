using Agnes.Plugins.CodeyBox.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace Agnes.Plugins.CodeyBox.Tests;

/// <summary>
/// Renders the decision card to PNGs, one per blocked state, at the width the pane actually has.
/// </summary>
/// <remarks>
/// <para>The failure this guards against is not an exception. A card can be present, bound and green in
/// every assertion and still be unreadable: a consequence line clipped at the pane edge, three choices
/// whose buttons are all different widths so the column cannot be read down, an error box tall enough to
/// push the choices off the bottom. None of that throws. So the frames get written and looked at.</para>
///
/// <para>660 px is the number that matters — the pane is two fifths of the tab, so that is what it has on
/// a tab a little over 1600 px wide, and it is the narrowest width the card has to stay legible at.</para>
///
/// <para><b>Run this class on its own when you want to look at the frames</b>
/// (<c>--filter FullyQualifiedName~DecisionShotTests</c>). A headless session is process-global and the
/// suite's other render classes leave the text stack without glyphs once they have started and disposed
/// theirs — a property of the harness, not of the card. Set <c>AGNES_BOARD_SHOTS</c> to choose where they
/// land.</para>
/// </remarks>
[Collection("avalonia-headless")]
public sealed class DecisionShotTests
{
    /// <summary>
    /// Just enough theme for the roles to resolve. Same set as <see cref="BoardShotTests"/> plus the two
    /// soft status fills the card uses for its ground — amber for blocked-on-you, pink for failed.
    /// </summary>
    private sealed class ShotApp : Application
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
                ["StatusAttentionSoft"] = new SolidColorBrush(Color.FromRgb(0x2C, 0x24, 0x12)),
                ["StatusDone"] = new SolidColorBrush(Color.FromRgb(0x5A, 0xD6, 0xA8)),
                ["StatusError"] = new SolidColorBrush(Color.FromRgb(0xF4, 0x72, 0x9B)),
                ["StatusErrorSoft"] = new SolidColorBrush(Color.FromRgb(0x2E, 0x16, 0x20)),
                ["StatusIdle"] = new SolidColorBrush(Color.FromRgb(0x76, 0x76, 0x86)),
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

    /// <summary>
    /// The width the item pane has: two fifths of the tab, so roughly this on a full-width window and a
    /// little less on the 1650 px tab the whole-tab shots below are taken at. The card has to hold at
    /// both, which is why one set of frames is drawn at exactly this and the other in situ.
    /// </summary>
    internal const int PaneWidth = 660;

    /// <summary>Draws one control at one size and saves the frame. Returns the file, for the log line.</summary>
    internal static string Shot(Func<Control> build, string name, double width, double height)
    {
        var path = Path.Combine(ShotDir, name + ".png");
        using var session = HeadlessUnitTestSession.StartNew(typeof(ShotAppBuilder));
        session.Dispatch(() =>
        {
            var window = new Window
            {
                Width = width,
                Height = height,
                Background = new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x16)),
                Content = build(),
            };

            window.Show();
            Dispatcher.UIThread.RunJobs();

            using var frame = window.CaptureRenderedFrame();
            if (frame is not null)
            {
                Directory.CreateDirectory(ShotDir);
                using var file = File.Create(path);
                frame.Save(file, new PngBitmapEncoderOptions());
            }

            window.Close();
        }, CancellationToken.None).GetAwaiter().GetResult();

        return path;
    }

    /// <summary>The card alone, on the pane's ground, with the pane's own 10px gutters.</summary>
    internal static Control Card(Decision decision, WorkItemQuestion? answering = null) => new Border
    {
        Background = new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x16)),
        Padding = new Thickness(10),
        Child = new DecisionView
        {
            DataContext = new BoardStub
            {
                Decision = decision,
                AnsweringQuestion = answering,
                AnswerText = answering is null ? string.Empty : "One token per session is fine — the "
                    + "push window is short enough that a leak is bounded.",
            },
        },
    };

    public static TheoryData<string, Func<Decision>> Cards => new()
    {
        { "decision-question", () => DecisionSamples.Asked() },
        { "decision-parked", () => DecisionSamples.ParkedSilently() },
        { "decision-merge-failed", () => DecisionSamples.MergeFailed() },
        { "decision-audit-failed", () => DecisionSamples.AuditFailed() },
        { "decision-infra-failed", () => DecisionSamples.InfraFailed() },
        { "decision-abandoned", () => DecisionSamples.Abandoned() },
    };

    [Theory]
    [MemberData(nameof(Cards))]
    public void Every_blocked_state_draws_a_card_at_pane_width(string name, Func<Decision> decision)
    {
        var path = Shot(() => Card(decision()), name, PaneWidth, 560);
        Console.WriteLine($"[decision] {path}");
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void The_reply_box_opens_inside_the_card()
    {
        // Not a second box below it. The old question panel and the old reply panel were two separate
        // surfaces both headed "the agent is waiting on you", which is most of why the decision read as
        // optional.
        var question = DecisionSamples.Question();
        var path = Shot(
            () => Card(DecisionSamples.Asked(), question), "decision-question-answering", PaneWidth, 640);

        Console.WriteLine($"[decision] {path}");
        Assert.True(File.Exists(path));
    }

    /// <summary>An orchestrator that answers everything with null, so the real view model can be driven
    /// without one. Same shape as the one in <see cref="DeadButtonTests"/>.</summary>
    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("null", System.Text.Encoding.UTF8, "application/json"),
            });
    }

    /// <summary>
    /// The card where it lives: top of the item pane, above the relations band and the action row.
    /// </summary>
    /// <remarks>
    /// <para>The cards above prove it is legible. This proves it is FIRST — that the identity block has
    /// not pushed it below the fold, and that it has not shoved the agent's live output off the bottom.
    /// </para>
    ///
    /// <para>Driven by the REAL view model rather than a stub, because what is being checked is where the
    /// card lands among controls whose visibility the view model decides. A stub missing
    /// <c>ShowStory</c> or <c>IsDetailVisible</c> leaves those bindings unresolved, so every pane view
    /// renders at once on top of the others — which looks exactly like a layout fault and is not one.
    /// </para>
    ///
    /// <para>1650 px wide is a full-width tab; the item pane's two fifths of that is the 660 the cards
    /// above are drawn at.</para>
    /// </remarks>
    [Theory]
    [InlineData("NeedsOperatorInput", true, "decision-tab-question")]
    [InlineData("MergeConflictResolutionFailed", false, "decision-tab-merge")]
    public void The_card_is_the_first_thing_in_the_pane(string state, bool asked, string name)
    {
        var path = LiveViewModelShot(state, asked, name);
        Console.WriteLine($"[decision] {path}");
        Assert.True(File.Exists(path));
    }

    /// <summary>Renders the whole tab from a real view model. A method rather than inline because the
    /// headless session is driven synchronously and xunit's analyzer forbids that in a test body.</summary>
    private static string LiveViewModelShot(string state, bool asked, string name)
    {
        CodeyBoxQueueViewModel? vm = null;
        var path = Path.Combine(ShotDir, name + ".png");
        try
        {
            using var session = HeadlessUnitTestSession.StartNew(typeof(ShotAppBuilder));
            session.Dispatch(() =>
            {
                vm = new CodeyBoxQueueViewModel(
                    new CodeyBoxClient(new CodeyBoxOptions("http://127.0.0.1:1", "k"), new OfflineHandler()),
                    action => { action(); return Task.CompletedTask; });
                vm.Sections.Section = CodeyBoxSection.Queue;

                var row = DecisionSamples.Row(
                    state,
                    state == "MergeConflictResolutionFailed"
                        ? "merge resolution rejected by the scope fence: src/CodeyBox.Core/WorkItem.cs "
                          + "line 812 is outside every conflict span (+8 lines of context)"
                        : null);
                vm.Load([row]);
                vm.Selected = row;
                if (asked)
                {
                    vm.Questions.Add(DecisionSamples.Question());
                }

                Assert.NotNull(vm.Decision);

                var window = new Window
                {
                    Width = 1650,
                    Height = 950,
                    Background = new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x16)),
                    Content = new CodeyBoxQueueView { DataContext = vm },
                };

                window.Show();
                Dispatcher.UIThread.RunJobs();

                using var frame = window.CaptureRenderedFrame();
                if (frame is not null)
                {
                    Directory.CreateDirectory(ShotDir);
                    using var file = File.Create(path);
                    frame.Save(file, new PngBitmapEncoderOptions());
                }

                window.Close();
            }, CancellationToken.None).GetAwaiter().GetResult();
        }
        finally
        {
            vm?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        return path;
    }
}
