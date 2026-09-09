using Agnes.Abstractions;
using Agnes.App.Mobile.Controls;
using Agnes.App.Mobile.Preview;
using Agnes.App.Mobile.Services;
using Agnes.App.Mobile.ViewModels;
using Agnes.App.Mobile.Views;
using Agnes.Protocol;
using Agnes.Ui.Core.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Agnes.Mobile.Tests;

/// <summary>
/// The Screen segment, actually laid out, with real JPEGs decoded into it.
///
/// A view model test cannot catch what breaks here: a resource key that doesn't exist, a binding to a
/// renamed property, a frame whose header says one size and whose payload says another. So this renders
/// the real session page against the real theme and pushes synthetic frames through a fake display
/// channel — the same path the phone takes, minus the sandbox.
/// </summary>
[Collection(AvaloniaCollection.Name)]
public sealed class DisplayRenderTests : IDisposable
{
    private readonly AvaloniaSession _avalonia;

    private const int GuestWidth = 800;
    private const int GuestHeight = 500;

    private readonly string _state = Path.Combine(
        Path.GetTempPath(), "agnes-display-tests-" + Guid.NewGuid().ToString("n"));

    public DisplayRenderTests(AvaloniaSession avalonia)
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
    public async Task The_screen_segment_paints_a_full_frame_then_a_tile_over_it()
    {
        await _avalonia.Run(
            () =>
            {
                var (shell, window, page, display) = OpenGraphicalSession();

                // Entering the segment connects and states what this phone can use.
                page.ShowScreenCommand.ExecuteAsync(null);
                Pump(() => page.Display!.IsConnected);

                var quality = Assert.IsType<DisplayQuality>(Assert.Single(display.Channel.Sent));
                Assert.Equal(15, quality.MaxFps);
                Assert.Equal(65, quality.JpegQuality);

                display.Channel.PushInfo(GuestWidth, GuestHeight);
                display.Channel.PushFull(
                    GuestWidth, GuestHeight, FakeDesktop.Jpeg(GuestWidth, GuestHeight, "build"));
                Pump(() => Surface(window).HasPicture);

                var surface = Surface(window);
                Assert.True(surface.HasPicture);
                Assert.True(surface.Bounds.Width > 0, "the surface got laid out");

                // A tile repaints one rectangle of the picture already there. It must not be refused and
                // must not reallocate the canvas.
                display.Channel.PushTile(
                    40, 40, 200, 120, GuestWidth, GuestHeight, FakeDesktop.Jpeg(200, 120, "term"));
                Pump(() => true);
                Assert.True(Surface(window).HasPicture);

                // The header strip says who is driving.
                Assert.Contains("Agent is driving", Texts(window));

                GC.KeepAlive(shell);
            });
    }

    [Fact]
    public async Task Watching_sends_nothing_and_taking_control_is_what_lets_input_through()
    {
        await _avalonia.Run(
            () =>
            {
                var (_, window, page, display) = OpenGraphicalSession();
                page.ShowScreenCommand.ExecuteAsync(null);
                Pump(() => page.Display!.IsConnected);
                display.Channel.PushInfo(GuestWidth, GuestHeight);
                display.Channel.PushFull(
                    GuestWidth, GuestHeight, FakeDesktop.Jpeg(GuestWidth, GuestHeight, "build"));
                Pump(() => Surface(window).HasPicture);

                var surface = Surface(window);
                display.Channel.Sent.Clear();

                // The agent holds control: typing at the screen must not reach the guest at all.
                surface.TypeText("ls");
                surface.PressKey(DisplayKeysyms.Return);
                Pump(() => true);
                Assert.Empty(display.Channel.Sent);

                page.Display!.TakeControlCommand.Execute(null);
                Pump(() => true);
                Assert.Contains(display.Channel.Sent, m => m is DisplayControlRequest { Take: true });

                // The host answers by saying who holds it now; only then does input flow.
                display.Channel.Push(Notice(DisplayControlHolder.User));
                Pump(() => page.Display!.IsUserDriving);

                display.Channel.Sent.Clear();
                surface.TypeText("ls");
                surface.PressKey(DisplayKeysyms.Return);
                Pump(() => display.Channel.Sent.Count >= 6);

                var keys = display.Channel.Sent.OfType<DisplayKey>().ToList();
                Assert.Equal(["l", "l", "s", "s", "Return", "Return"], keys.Select(k => k.Key));
                Assert.Contains("You have control", Texts(window));
            });
    }

    // ---- harness ----

    private static (ShellViewModel Shell, Window Window, SessionPageViewModel Page, FakeDisplayHost Display)
        OpenGraphicalSession()
    {
        var shell = new ShellViewModel(
            new MobileConnector(), new MobileDispatcher(), new MobileSettings(), "Display test");

        var window = new Window
        {
            Width = 412,
            Height = 915,
            Content = new ShellView { DataContext = shell },
        };
        window.Show();
        Pump(() => true);

        var link = shell.Hosts.Links.First();
        var host = link.ConnectAsync().GetAwaiter().GetResult()!;
        var info = host.OpenSessionAsync("claude-code-native", "/home/you/projects/atlas")
            .GetAwaiter().GetResult();
        var view = host.SubscribeAsync(info.SessionId).GetAwaiter().GetResult();

        var session = shell.Sessions.Build(host, view, "Atlas");
        var saved = new SavedSession(
            link.Name, link.Url, string.Empty, info.SessionId, "claude-code-native", "Atlas",
            info.WorkingDirectory, HasDisplay: true);
        var entry = shell.Sessions.Adopt(link, session, saved, open: false);

        shell.Sessions.Open(entry);
        Pump(() => shell.CurrentPage is SessionPageViewModel);

        var page = Assert.IsType<SessionPageViewModel>(shell.CurrentPage);
        Assert.True(page.HasDisplay, "the saved pointer says this session has a screen");

        var display = new FakeDisplayHost();
        page.Display = new DisplayViewModel(display, info.SessionId, new MobileDispatcher());
        return (shell, window, page, display);
    }

    private static DisplaySurface Surface(Visual root)
        => root.GetVisualDescendants().OfType<DisplaySurface>().First();

    private static Agnes.Client.DisplayFrame Notice(DisplayControlHolder holder)
    {
        var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new DisplayControlNotice(holder, "test-device"), DisplayWire.Json);
        return new Agnes.Client.DisplayFrame(
            new DisplayFrameHeader(DisplayFrameKind.Control, 9, 0, 0, 0, 0, 0, 0, (uint)json.Length), json);
    }

    private static IReadOnlyList<string> Texts(Visual root)
        => [.. root.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty)];

    /// <summary>Runs the dispatcher and layout until the condition holds, or long enough to be sure it
    /// won't. Frames arrive on a background pump and are posted back, so one pass is never enough.</summary>
    private static void Pump(Func<bool> until)
    {
        for (var n = 0; n < 120 && !until(); n++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }

        Dispatcher.UIThread.RunJobs();
    }
}
