using Agnes.Abstractions;
using Agnes.App.Mobile.Preview;
using Agnes.App.Mobile.Services;
using Agnes.App.Mobile.ViewModels;
using Agnes.App.Mobile.Views;
using Agnes.Ui.Core;
using Agnes.Ui.Core.Transcript;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Agnes.Mobile.Tests;

/// <summary>
/// The received-file card and sheet, actually laid out.
///
/// The rest of the mobile tests exercise view models, which cannot catch the failures these views are
/// most likely to have: a resource key that doesn't exist, a converter reached from XAML that isn't
/// public, a binding to a property that was renamed. Those are silent at build time and fatal on a phone,
/// so this renders the real controls against the real theme — the same thing
/// <c>tools/Agnes.MobilePreview</c> does, minus the PNGs.
/// </summary>
[Collection(AvaloniaCollection.Name)]
public sealed class ReceivedFileRenderTests : IDisposable
{
    private readonly AvaloniaSession _avalonia;

    private readonly string _state = Path.Combine(Path.GetTempPath(), "agnes-mobile-tests-" + Guid.NewGuid().ToString("n"));

    public ReceivedFileRenderTests(AvaloniaSession avalonia)
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
    public async Task An_image_and_a_text_file_both_render_as_cards_and_as_the_sheet()
    {
        await _avalonia.Run(
            () =>
            {
                var shell = new ShellViewModel(
                    new MobileConnector(), new MobileDispatcher(), new MobileSettings(), "Render test",
                    receivedFiles: NullReceivedFileHandler.Instance);

                var window = new Window
                {
                    Width = 412,
                    Height = 915,
                    Content = new ShellView { DataContext = shell },
                };
                window.Show();
                Pump();

                var link = shell.Hosts.Links.First();
                var host = link.ConnectAsync().GetAwaiter().GetResult()!;
                var info = host.OpenSessionAsync("claude-code-native", "/home/you/projects/agnes")
                    .GetAwaiter().GetResult();
                var view = host.SubscribeAsync(info.SessionId).GetAwaiter().GetResult();

                var session = shell.Sessions.Build(host, view, "Render test");
                var saved = new SavedSession(link.Name, link.Url, string.Empty, info.SessionId,
                    "claude-code-native", "Render test", info.WorkingDirectory);
                var entry = shell.Sessions.Adopt(link, session, saved, open: false);

                var image = File("shot.png", "image/png", "The dashboard after the fix.");
                var text = File("coverage.txt", "text/plain", caption: null);
                // Bytes the card's inline preview will decode, standing in for a real fetch — the point
                // being that the image path lays out with a bitmap in it, not just with a glyph.
                SharedFilePreviews.Seed(session, image, OnePixelPng);
                session.Items.Add(image);
                session.Items.Add(text);

                shell.Sessions.Open(entry);
                Pump();

                var page = Assert.IsType<SessionPageViewModel>(shell.CurrentPage);
                Assert.Contains("shot.png", Texts(window));
                Assert.Contains("coverage.txt", Texts(window));

                // …and the sheet over it.
                page.OpenSharedFileCommand.Execute(image);
                Pump();

                Assert.IsType<ReceivedFileSheetViewModel>(shell.CurrentSheet);
                Assert.Contains("The dashboard after the fix.", Texts(window));
            });
    }

    private static SharedFileItem File(string name, string mime, string? caption)
        => new(new FileSharedEvent(name, name, "shared/" + name, 1_200, mime, caption))
        {
            Sequence = 7,
            Timestamp = DateTimeOffset.Now,
        };

    /// <summary>Every string the window is currently showing.</summary>
    private static IReadOnlyList<string> Texts(Visual root)
        => [.. root.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty)];

    /// <summary>Runs the dispatcher and layout until things settle. Sheets present themselves over two
    /// frames (off-screen, then animated in), so one pass isn't enough.</summary>
    private static void Pump()
    {
        for (var n = 0; n < 40; n++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }

        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>A 1×1 PNG — enough for <c>SharedImage</c> to decode and lay out.</summary>
    private static byte[] OnePixelPng => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
}
