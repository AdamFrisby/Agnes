using Agnes.App.Mobile.Services;
using Agnes.App.Mobile.ViewModels;
using Agnes.App.Mobile.Views;
using Avalonia.Controls;

namespace Agnes.App.Mobile.Preview;

/// <summary>
/// The Android shell's Screen segment against a <b>real</b> host and a real graphical sandbox, rather than
/// the <see cref="FakeDisplayHost"/> the scripted tour uses.
/// </summary>
/// <remarks>
/// The tour's <c>13-screen-*</c> shots push hand-drawn JPEGs into the view models, which proves the phone
/// *renders* a screen but says nothing about whether one ever arrives: a fake channel never fails a
/// handshake, never waits on a guest that has not mode-set, and never disagrees with the desktop about
/// who is driving. This mode joins a session a host is really running, so the pixels come off a VM's
/// virtio-gpu through the same WebSocket the desktop uses — and, run against a session the desktop is
/// also watching, it shows the two heads sharing one capture.
/// </remarks>
public static class LivePreview
{
    public sealed record Options
    {
        public required string HostUrl { get; init; }
        public required string Token { get; init; }
        public required string SessionId { get; init; }
        public string? Fingerprint { get; init; }
        public string OutDir { get; init; } = "screenshots/mobile-live";
    }

    /// <summary>Parses the live-mode arguments, or null when <c>--host</c> was not given.</summary>
    public static Options? TryParse(string[] args)
    {
        string? Value(string name)
        {
            var i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.Ordinal));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        if (Value("--host") is not { Length: > 0 } host)
        {
            return null;
        }

        return new Options
        {
            HostUrl = host,
            Token = Value("--token") ?? throw new ArgumentException("--host also needs --token."),
            SessionId = Value("--session") ?? throw new ArgumentException("--host also needs --session."),
            Fingerprint = Value("--fingerprint"),
            OutDir = Value("--out") ?? Path.Combine(Directory.GetCurrentDirectory(), "screenshots", "mobile-live"),
        };
    }

    public static void Run(Options options)
    {
        var shell = new ShellViewModel(
            new MobileConnector(),
            new MobileDispatcher(),
            new MobileSettings(),
            deviceName: "Live preview (headless)",
            receivedFiles: new PreviewReceivedFileHandler());

        var window = new Window
        {
            Width = Program.PhoneWidth,
            Height = Program.PhoneHeight,
            Content = new ShellView { DataContext = shell },
        };
        window.Show();
        Program.Settle(200);

        var link = shell.Hosts.Add(new SavedHost("Live host", options.HostUrl, options.Token, options.Fingerprint));
        var host = Await(link.ConnectAsync(), 60_000);
        if (host is null)
        {
            Console.Error.WriteLine($"Couldn't reach {options.HostUrl}: {link.Error}");
            return;
        }

        // The catalogue, not a guess: the adapter, folder and "does it have a screen" all come from what
        // the host says about the session, the same way a phone that discovered it would learn them.
        var summary = Await(host.ListSessionsAsync(), 30_000)
            ?.FirstOrDefault(s => s.SessionId == options.SessionId);
        if (summary is null)
        {
            Console.Error.WriteLine($"The host lists no session {options.SessionId}.");
            return;
        }

        Console.WriteLine($"joining {summary.AdapterId} session {summary.SessionId} (display: {summary.HasDisplay}).");
        var view = Await(host.SubscribeAsync(options.SessionId), 60_000);
        if (view is null)
        {
            Console.Error.WriteLine($"Subscribing to {options.SessionId} timed out.");
            return;
        }

        var title = summary.Title is { Length: > 0 } named ? named : Path.GetFileName(summary.WorkingDirectory);
        var session = shell.Sessions.Build(host, view, title);
        var entry = shell.Sessions.Adopt(
            link,
            session,
            new SavedSession(
                link.Name, link.Url, options.Token, summary.SessionId, summary.AdapterId,
                title, summary.WorkingDirectory, HasDisplay: summary.HasDisplay),
            open: false);

        shell.PopToRoot();
        shell.Sessions.Open(entry);
        Program.Pump(() => shell.CurrentPage is SessionPageViewModel p && p.Entry == entry, 5000);
        if (shell.CurrentPage is not SessionPageViewModel page)
        {
            Console.Error.WriteLine("The session page never opened.");
            return;
        }

        page.ShowScreenCommand.ExecuteAsync(null);
        Program.Pump(() => page.Display is { IsConnected: true }, 30_000);
        if (page.Display is not { } display)
        {
            Console.Error.WriteLine("The page offered no display — the host says this session has no screen.");
            return;
        }

        // The guest may have been idle for a while; the broker sends nothing until something on screen
        // changes, so allow generously and report rather than hang.
        Program.Pump(() => display.LastFrameAt is not null, 120_000);
        if (display.LastFrameAt is null)
        {
            Console.Error.WriteLine("No frame arrived. Display status: " + display.Status);
            return;
        }

        Console.WriteLine($"first frame — {display.Width}x{display.Height}, driver: {display.DriverLabel}");
        Program.Settle(400);
        Program.Shot(window, "live-mobile-01-screen");

        display.TakeControlCommand.Execute(null);
        Program.Pump(() => display.IsUserDriving, 15_000);
        Program.Settle(300);
        Program.Shot(window, "live-mobile-02-you-driving");

        // Back to the conversation: the screen stays connected, and the thumbnail above the transcript is
        // why that matters.
        page.ShowTranscriptCommand.Execute(null);
        Program.Settle(500);
        Program.Shot(window, "live-mobile-03-thumbnail");

        display.ReleaseControlCommand.Execute(null);
        Program.Pump(() => !display.IsUserDriving, 10_000);
    }

    /// <summary>
    /// Waits for a host call while still pumping the UI thread.
    /// <para>
    /// The scripted tour blocks on <c>GetAwaiter().GetResult()</c> and gets away with it because the
    /// simulated host answers in memory. A real one answers over SignalR, and its continuations are posted
    /// to the very dispatcher a blocking wait has stopped pumping — so the same line deadlocks the harness
    /// outright against a live host. Everything that crosses the network goes through here.
    /// </para>
    /// </summary>
    private static T? Await<T>(Task<T> task, int timeoutMs)
        where T : class
    {
        Program.Pump(() => task.IsCompleted, timeoutMs);
        return task.IsCompletedSuccessfully ? task.Result : null;
    }
}
