using Agnes.Abstractions;
using Agnes.Client;
using Agnes.Client.Simulation;
using Agnes.Protocol;
using Agnes.Ui.Core;
using Agnes.Ui.Core.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentIcons.Avalonia;

namespace Agnes.Desktop.Tests;

/// <summary>
/// The transcript card for a file the agent sent, rendered for real. What only rendering can catch is what
/// is asserted here: that the template parses and binds (a missing property throws on attach, not at build
/// time), that an image card actually decodes and shows the bytes it fetched, and that the button row
/// follows what the head's handler says it can do rather than offering verbs that would do nothing.
/// </summary>
[Collection("desktop-headless")]
public class SharedFileCardTests
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

    /// <summary>The one file the simulated host's workspace actually serves — a real PNG, so it decodes.</summary>
    private const string SimulatedImagePath = ".agnes/shared/shot-1/gradient-preview.png";

    [Fact]
    public async Task An_image_card_renders_and_shows_the_bytes_it_fetched()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));
        await session.Dispatch(async () =>
        {
            var scene = Show(new Handler { CanSaveIt = true, CanOpenIt = true });
            scene.Emit(new FileSharedEvent("shot-1", "gradient-preview.png", SimulatedImagePath, 1223, "image/png",
                "before vs after"));
            Dispatcher.UIThread.RunJobs();

            var image = Assert.Single(Images(scene.Window));
            // The preview is fetched lazily on attach — pump until the bitmap lands.
            for (var i = 0; i < 60 && image.Source is null; i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(10);
            }

            Assert.NotNull(image.Source);
            var texts = Texts(scene.Window);
            Assert.Contains("gradient-preview.png", texts);
            Assert.Contains("before vs after", texts);
            Assert.Contains("PNG", texts);
            Assert.Contains("1.2 KB", texts);

            scene.Window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task A_non_image_card_renders_with_no_preview_frame()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));
        await session.Dispatch(() =>
        {
            var scene = Show(new Handler { CanSaveIt = true, CanOpenIt = true });
            scene.Emit(new FileSharedEvent("f2", "report.pdf", ".agnes/shared/f2/report.pdf", 4096,
                "application/pdf", null));
            Dispatcher.UIThread.RunJobs();

            // The image element exists in the template but stays hidden, and nothing is fetched for it.
            Assert.DoesNotContain(Images(scene.Window), i => i.IsEffectivelyVisible);
            Assert.Contains("report.pdf", Texts(scene.Window));

            scene.Window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task The_button_row_offers_exactly_the_verbs_the_handler_claims()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));
        await session.Dispatch(() =>
        {
            // The desktop's own stance: save and open, never share (there is no share sheet to open).
            var scene = Show(new Handler { CanSaveIt = true, CanOpenIt = true, CanShareIt = false });
            scene.Emit(new FileSharedEvent("f3", "notes.txt", ".agnes/shared/f3/notes.txt", 120, "text/plain", null));
            Dispatcher.UIThread.RunJobs();

            var labels = CardButtonLabels(scene.Window);
            Assert.Contains("Open", labels);
            Assert.Contains("Save…", labels);
            Assert.DoesNotContain("Share", labels);

            scene.Window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task A_head_that_can_do_nothing_with_a_file_shows_no_buttons_rather_than_dead_ones()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));
        await session.Dispatch(() =>
        {
            var scene = Show(NullReceivedFileHandler.Instance);
            scene.Emit(new FileSharedEvent("f4", "build.zip", ".agnes/shared/f4/build.zip", 900_000,
                "application/zip", null));
            Dispatcher.UIThread.RunJobs();

            Assert.Empty(CardButtonLabels(scene.Window));
            Assert.Contains("build.zip", Texts(scene.Window));

            scene.Window.Close();
        }, CancellationToken.None);
    }

    // ---- helpers ----

    private sealed class Scene(Window window, SessionView view)
    {
        private long _seq;

        public Window Window => window;

        public void Emit(SessionEvent @event) => view.Apply(@event with { Sequence = ++_seq });
    }

    private static Scene Show(IReceivedFileHandler handler)
    {
        var view = new SessionView("s1");
        view.ApplySnapshot(new SessionSnapshot(new SessionInfo("s1", "opencode", string.Empty, 0), [], 0));
        var session = new SessionViewModel(
            new SimulatedHost(), view, ImmediateDispatcher.Instance, "OpenCode", receivedFiles: handler);

        // The card reaches its commands through the ItemsControl's DataContext, so the list is given the
        // session view model exactly as the real tab gives it one.
        var list = new ListBox { DataContext = session, ItemsSource = session.Items };
        foreach (var template in TranscriptTemplates())
        {
            list.DataTemplates.Add(template);
        }

        var window = new Window { Width = 900, Height = 700, Content = list };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return new Scene(window, view);
    }

    // The templates under test live in SessionTabView. Borrow them from a real instance rather than
    // restating them here, so this test fails when that file's card breaks.
    private static IReadOnlyList<IDataTemplate> TranscriptTemplates()
    {
        var tab = new Agnes.App.Desktop.Views.SessionTabView();
        var list = tab.FindControl<ListBox>("Transcript");
        return list is null ? [] : [.. list.DataTemplates];
    }

    private static List<Image> Images(Visual root)
        => [.. root.GetVisualDescendants().OfType<Image>().Where(i => i.Name == "SharedImage")];

    private static List<string> Texts(Visual root)
        => [.. root.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty)];

    // The card's buttons are the ones carrying a SymbolIcon beside their label, which tells them apart from
    // any chrome (scrollbar repeat buttons and the like) the window brings with it.
    private static List<string> CardButtonLabels(Visual root)
        => [.. root.GetVisualDescendants().OfType<Button>()
            .Where(b => b.IsEffectivelyVisible && b.GetVisualDescendants().OfType<SymbolIcon>().Any())
            .SelectMany(b => b.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty))];

    private sealed class Handler : IReceivedFileHandler
    {
        public bool CanSaveIt { get; init; }
        public bool CanOpenIt { get; init; }
        public bool CanShareIt { get; init; }

        public bool CanSave => CanSaveIt;
        public bool CanOpen => CanOpenIt;
        public bool CanShare => CanShareIt;

        public Task SaveAsync(ReceivedFile file, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task OpenAsync(ReceivedFile file, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ShareAsync(ReceivedFile file, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
