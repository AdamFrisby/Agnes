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
/// Renders the runway to PNGs — from the canned board, and from the live orchestrator where there is one.
/// </summary>
/// <remarks>
/// <para>The board's remaining failure modes are dimensional: a title squeezed to nothing by a long pip
/// strip, a reason clipped at the pane boundary, a section that is technically present and off the bottom
/// of the screen. None of that throws, none of it fails an assertion about a control's existence, and
/// none of it is visible from reading the markup — it is only visible by looking. So this renders at the
/// two widths the pane is actually used at and writes the frames out.</para>
///
/// <para>It is the one place in this project with a real rasteriser: <see cref="BoardViewTests"/> and
/// <see cref="OverviewViewTests"/> stay on headless drawing, where capture is a documented no-op, and
/// this class asks for Skia so there are pixels to look at. Set <c>AGNES_BOARD_SHOTS</c> to choose where
/// they land.</para>
///
/// <para>The live half is GET-only, and silent where no CodeyBox is configured or the one configured is
/// not running — the same contract as <see cref="LiveOverviewProbe"/>, for the same reason: the host it
/// runs against is doing real work.</para>
/// </remarks>
[Collection("avalonia-headless")]
public sealed class BoardShotTests
{
    /// <summary>
    /// Just enough theme for the roles to resolve — the desktop's <c>Themes/Tokens.axaml</c> is not
    /// reachable from a test project. Stated again here rather than shared with
    /// <see cref="BoardViewTests"/> because that class's app is deliberately left on headless drawing;
    /// the two differ in their platform, not in their palette.
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
                ["StatusDone"] = new SolidColorBrush(Color.FromRgb(0x5A, 0xD6, 0xA8)),
                ["StatusError"] = new SolidColorBrush(Color.FromRgb(0xF4, 0x72, 0x9B)),
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

    private static readonly string ShotDir =
        Environment.GetEnvironmentVariable("AGNES_BOARD_SHOTS")
        ?? Path.Combine(Path.GetTempPath(), "board-shots");

    /// <summary>Draws one control at one size and saves the frame. Returns the file, for the log line.</summary>
    private static string Shot(Func<Control> build, string name, double width, double height)
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

    private static BoardView Runway(BoardStub stub) => new()
    {
        DataContext = stub,
        RunNextCommand = stub.RunNextCommand,
        BeginRunAfterCommand = stub.BeginRunAfterCommand,
        RunAfterCommand = stub.RunAfterCommand,
    };

    [Fact]
    public void The_canned_runway_renders_at_both_widths()
    {
        var stub = new BoardStub { HistoryMatches = BoardSamples.History() };

        // 1000 is a comfortable pane; 700 is the width the runway has when the tab is on half a laptop
        // screen and the item pane has taken its two fifths. Nothing may clip at either.
        Assert.True(File.Exists(Shot(() => Runway(stub), "board-wide", 1000, 1000)));
        Assert.True(File.Exists(Shot(() => Runway(stub), "board-narrow", 700, 1000)));

        // And the whole tab, which is where the 3:2 split and the item pane's wrapping live.
        var tab = new BoardStub
        {
            Relations = BoardSamples.Held(),
            HistoryMatches = BoardSamples.History(),
        };
        tab.Selected = tab.Relations!.Item;

        Assert.True(File.Exists(Shot(() => new CodeyBoxQueueView { DataContext = tab }, "board-tab", 1400, 1000)));
        Assert.True(File.Exists(Shot(() => new CodeyBoxQueueView { DataContext = tab }, "board-tab-narrow", 1000, 900)));
    }

    [Fact]
    public void A_busy_runway_folds_its_queue_and_caps_its_longest_strip()
    {
        var stub = new BoardStub { Board = BoardSamples.Busy() };

        Assert.True(File.Exists(Shot(() => Runway(stub), "board-busy-wide", 1000, 1000)));
        Assert.True(File.Exists(Shot(() => Runway(stub), "board-busy-narrow", 700, 1000)));
    }

    /// <summary>
    /// The premise, not the wiring: a runway built from the real queue, drawn at the two widths.
    /// </summary>
    /// <remarks>
    /// Every layout problem this pass fixed came from a shape the canned board does not have — a chain of
    /// 32 steps, sixteen queued rows all saying the same sentence, an ordinal running under an agent name.
    /// A sample that contains none of those proves the markup parses, which was never in doubt.
    /// </remarks>
    [Fact]
    public async Task The_live_runway_renders_at_both_widths()
    {
        var options = CodeyBoxOptions.Resolve();
        if (!options.IsConfigured)
        {
            return;
        }

        await using var client = new CodeyBoxClient(options);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

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
        var stub = new BoardStub { Board = board };

        var wide = Shot(() => Runway(stub), "live-board-wide", 1000, 1000);
        var narrow = Shot(() => Runway(stub), "live-board-narrow", 700, 1000);

        // Printed on purpose: a probe that can legitimately do nothing has to say when it did something.
        Console.WriteLine(
            $"[runway] {items.Count} items → {board.NowCount} now, {board.NextCount} next " +
            $"({board.Waiting.Count} waiting group(s), {board.WaitingCount} items), {board.LandedCount} landed. " +
            $"Longest chain {board.Now.Concat(board.Next).Concat(board.Waiting.SelectMany(g => g.Chains)).DefaultIfEmpty(null!).Max(ch => ch?.Count ?? 0)} steps.");
        Console.WriteLine($"[runway] {board.NextHeader}");
        Console.WriteLine($"[runway] {board.WaitingHeader}");
        foreach (var group in board.Waiting)
        {
            Console.WriteLine($"[runway]   {group.Header}  (open: {group.OpenByDefault})");
        }

        Console.WriteLine($"[runway] {board.LandedHeader}");
        Console.WriteLine($"[runway] shots: {wide}, {narrow}");

        Assert.True(File.Exists(wide));
        Assert.True(File.Exists(narrow));
    }
}
