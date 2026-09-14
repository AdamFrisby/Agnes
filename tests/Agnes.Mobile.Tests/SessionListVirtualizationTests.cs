using Agnes.App.Mobile.Services;
using Agnes.App.Mobile.ViewModels;
using Agnes.App.Mobile.Views;
using Agnes.Ui.Core;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Agnes.Mobile.Tests;

/// <summary>
/// The sessions list builds the cards you can see, not the cards you have.
///
/// <para>This is a cold-start cost, not a scrolling one: a phone that has had a busy week has dozens of
/// saved sessions, and before the list virtualized, every one of them was a full card tree — measured,
/// arranged and rasterized — before the app's first frame could go up. The guard is here because the
/// thing that makes it work is one line of structure (the ScrollViewer sits *inside* the ItemsControl's
/// template, so the panel is measured against a viewport instead of against infinity), and that is
/// exactly the kind of thing a later tidy-up moves back out without noticing.</para>
/// </summary>
[Collection(AvaloniaCollection.Name)]
public sealed class SessionListVirtualizationTests(AvaloniaSession avalonia) : IDisposable
{
    private readonly string _state = Path.Combine(Path.GetTempPath(), "agnes-mobile-tests-" + Guid.NewGuid().ToString("n"));

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
    public async Task A_long_list_realizes_only_the_cards_that_fit_on_the_phone()
    {
        await avalonia.Run(() =>
        {
            JsonStore.UseDirectory(_state);

            // Forty saved sessions is an ordinary week, and far more than a phone screen holds.
            const int Saved = 40;
            SessionRegistry.Save(Enumerable.Range(0, Saved).Select(i => new SavedSession(
                HostName: "host",
                HostUrl: "https://10.0.0.1:5081",
                Token: "t",
                SessionId: $"s{i}",
                AdapterId: "claude-code",
                Title: $"Session {i}")));

            var shell = new ShellViewModel(
                new MobileConnector(), ImmediateDispatcher.Instance, new MobileSettings(), "Virtualization");
            var window = new Window
            {
                Width = 411,
                Height = 891,
                Content = new ShellView { DataContext = shell },
            };
            window.Show();

            // Only the list half of a restore: the reattach would try to reach a host that isn't there.
            _ = shell.Sessions.ListAsync();
            Pump();

            Assert.Equal(Saved, shell.Sessions.All.Count);

            var cards = window.GetVisualDescendants()
                .OfType<Button>()
                .Count(b => b.DataContext is SessionEntry);

            Assert.True(cards > 0, "the list renders the cards that are on screen");
            Assert.True(
                cards < Saved,
                $"the list built {cards} of {Saved} cards — the panel is not virtualizing, so a cold start pays "
                + "for every saved session before it can draw anything");

            window.Close();
        });
    }

    private static void Pump()
    {
        for (var i = 0; i < 8; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }
}
