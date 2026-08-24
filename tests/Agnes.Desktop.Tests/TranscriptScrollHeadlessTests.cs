using Agnes.Abstractions;
using Agnes.App.Desktop.Keymaps;
using Agnes.App.Desktop.ViewModels;
using Agnes.App.Desktop.Views;
using Agnes.Client;
using Agnes.Protocol;
using Agnes.Ui.Core;
using Agnes.Ui.Core.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Agnes.Desktop.Tests;

[Collection("Avalonia headless")]
public sealed class TranscriptScrollHeadlessTests
{
    [Fact]
    public async Task Rapid_wheel_and_far_thumb_jumps_settle_at_only_the_latest_row_without_snapback()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(TranscriptScrollTestApp));
        await session.Dispatch(() =>
        {
            using var state = CreateState();
            PumpLayout(state, passes: 8);

            var scroll = TranscriptScroll(state);
            var maximum = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);

            // Rapid direction changes replace one another and suppress following immediately. None of
            // the resulting extent corrections may snap the viewport back to the tail.
            state.View.SimulateWheelInputForTesting();
            scroll.Offset = new Vector(0, maximum * 0.35);
            state.View.SimulateWheelInputForTesting();
            scroll.Offset = new Vector(0, maximum * 0.28);
            state.View.SimulateWheelInputForTesting();
            scroll.Offset = new Vector(0, maximum * 0.31);
            PumpLayout(state, passes: 3);
            Assert.Equal(TranscriptScrollState.GestureScrolling, state.View.ScrollStateForTesting);
            Assert.InRange(Math.Abs(scroll.Offset.Y - maximum * 0.31), 0, 1);

            // All targets arrive before the render pass that consumes them. The frozen drag range and
            // generation token mean only row 650 survives to exact-position correction.
            state.View.SimulateIndexInputForTesting(40, beginDrag: true);
            state.View.SimulateIndexInputForTesting(760);
            state.View.SimulateIndexInputForTesting(120);
            state.View.SimulateIndexInputForTesting(650);
            state.View.SimulateIndexInputForTesting(650, endDrag: true);
            PumpLayout(state, passes: 24);

            Assert.Equal(650, state.View.FirstVisibleTranscriptIndexForTesting);
            Assert.InRange(Math.Abs(state.View.TranscriptRowTopForTesting(650)), 0, 1);
            Assert.Equal(TranscriptScrollState.ReadingHistory, state.View.ScrollStateForTesting);
        }, CancellationToken.None);
    }

    private static ScrollTestState CreateState()
    {
        var directory = Path.Combine(Path.GetTempPath(), "agnes-scroll-headless-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var keymap = KeymapService.CreateDefault(Path.Combine(directory, "keymap.json"), watch: false);
        var main = KeymapTests.NewVm(keymap, directory);

        var view = new SessionView("scroll-regression");
        view.ApplySnapshot(new SessionSnapshot(
            new SessionInfo("scroll-regression", "codex", string.Empty, 0), [], 0));
        var session = new SessionViewModel(new FakeHost(), view, ImmediateDispatcher.Instance, "Codex");
        var document = new SessionDocument(main, ImmediateDispatcher.Instance);
        document.AttachSession(session);

        var sessionView = new SessionTabView { DataContext = document };
        var window = new Window { Width = 1000, Height = 720, Content = sessionView };
        window.Show();
        var transcript = Assert.IsType<ListBox>(sessionView.FindControl<ListBox>("Transcript"));
        transcript.ItemTemplate = new FuncDataTemplate<ScrollRow>((row, _) => new Border
        {
            Height = row?.Height ?? 24,
            Child = new TextBlock { Text = row?.Label },
        }, supportsRecycling: true);
        transcript.ItemsSource = Enumerable.Range(0, 801)
            .Select(index => new ScrollRow($"row {index}", index % 2 == 0 ? 24 : 144))
            .ToArray();
        window.UpdateLayout();
        sessionView.RefreshTranscriptScrollForTesting();
        return new ScrollTestState(directory, keymap, window, sessionView);
    }

    private static ScrollViewer TranscriptScroll(ScrollTestState state)
    {
        var transcript = Assert.IsType<ListBox>(state.View.FindControl<ListBox>("Transcript"));
        return Assert.Single(transcript.GetVisualDescendants().OfType<ScrollViewer>());
    }

    private static void PumpLayout(ScrollTestState state, int passes)
    {
        for (var pass = 0; pass < passes; pass++)
        {
            state.Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private sealed record ScrollTestState(
        string Directory,
        KeymapService Keymap,
        Window Window,
        SessionTabView View) : IDisposable
    {
        public void Dispose()
        {
            Window.Close();
            Keymap.Dispose();
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }

    private sealed record ScrollRow(string Label, double Height);
}

public static class TranscriptScrollTestApp
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<Agnes.App.Desktop.App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia();
}
