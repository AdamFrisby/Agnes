using Agnes.Abstractions;
using Agnes.App.Mobile.Services;
using Agnes.App.Mobile.ViewModels;
using Agnes.App.Mobile.Views;
using Agnes.Protocol;
using Agnes.Ui.Core.Transcript;
using Agnes.Ui.Core.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Agnes.App.Mobile.Preview;

/// <summary>
/// Renders the Android client's real views offscreen at phone dimensions and writes PNGs.
///
/// This exists because the mobile head can otherwise only be exercised on a device: a missing resource,
/// an unresolvable binding or a font that fails to load are all silent at build time and fatal at run
/// time. Driving the simulated host through the same event pipeline the real one uses means what's
/// captured is what the app actually does, not a mock-up of it.
/// </summary>
public static class Program
{
    /// <summary>A common Android phone in device-independent pixels (≈ Pixel 7).</summary>
    internal const int PhoneWidth = 412;
    internal const int PhoneHeight = 915;

    private static string _outDir = "screenshots/mobile";

    public static void Main(string[] args)
    {
        _outDir = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal)
            ? args[0]
            : Path.Combine(Directory.GetCurrentDirectory(), "screenshots", "mobile");
        Directory.CreateDirectory(_outDir);

        // Never touch real device state from a render.
        var state = Path.Combine(Path.GetTempPath(), "agnes-mobile-preview");
        if (Directory.Exists(state))
        {
            Directory.Delete(state, recursive: true);
        }

        JsonStore.UseDirectory(state);

        // Live mode (--host …) renders the same shell against a REAL host and a session it is really
        // running; see LivePreview for why a fake display channel cannot stand in for that.
        if (LivePreview.TryParse(args) is { } live)
        {
            _outDir = live.OutDir;
            Directory.CreateDirectory(_outDir);
            using var liveSession = HeadlessUnitTestSession.StartNew(typeof(PreviewAppBuilder));
            liveSession.Dispatch(() => LivePreview.Run(live), CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine($"Done. {Directory.GetFiles(_outDir, "*.png").Length} screens in {_outDir}");
            return;
        }

        using var session = HeadlessUnitTestSession.StartNew(typeof(PreviewAppBuilder));
        session.Dispatch(Capture, CancellationToken.None).GetAwaiter().GetResult();

        Console.WriteLine($"Done. {Directory.GetFiles(_outDir, "*.png").Length} screens in {_outDir}");
    }

    private static void Capture()
    {
        var shell = new ShellViewModel(
            new MobileConnector(),
            new MobileDispatcher(),
            new MobileSettings(),
            deviceName: "Preview (headless)",
            // Android's handler is a platform type this harness can't link, and the null one reports it
            // can do nothing — which would render the received-file sheet with no buttons at all and hide
            // the very thing worth screenshotting. This one says yes and writes into the harness's own
            // scratch directory.
            receivedFiles: new PreviewReceivedFileHandler());

        var window = new Window
        {
            Width = PhoneWidth,
            Height = PhoneHeight,
            Content = new ShellView { DataContext = shell },
        };
        window.Show();
        Settle(200);

        // 1) First launch, before anything is seeded: the empty state that teaches the model.
        Shot(window, "01-empty");

        // Seed the demo exactly as a first launch would.
        shell.StartAsync().GetAwaiter().GetResult();
        Pump(() => shell.Sessions.All.Count > 0, 4000);
        Settle(1400); // let the scripted turn stream in (plan, tool calls, a diff)

        // 1b) Two more cards carrying the agent's own status line — the thing the list is read for.
        //     One fresh, one from an agent that has gone quiet mid-run, which is the case the wording
        //     exists to make visible. Saved pointers rather than live sessions, because that is exactly
        //     the state the list is opened in: cold, before any host has answered.
        SeedStatusCards(shell);
        Settle(300);
        Shot(window, "02-sessions");

        var entry = shell.Sessions.All.First(e => e.Session is not null);

        // 2) The session screen with a real transcript.
        shell.Sessions.Open(entry);
        Pump(() => shell.CurrentPage is SessionPageViewModel, 2000);
        Settle(700);
        Shot(window, "03-session");

        // 2b) Coming back to a session that ran while the phone was in a pocket: the header carries the
        //     agent's own line, and the band above the transcript carries what it said while nobody was
        //     looking. Handed in rather than derived, because "you weren't here" is a state a live
        //     simulated session will not enter on request.
        if (shell.CurrentPage is SessionPageViewModel away)
        {
            away.StatusSource = new PreviewAgentStatus(
                new AgentStatus(
                    "Terminal panel is reflowing correctly at 80 cols; wiring the resize handler next.",
                    DateTimeOffset.Now.AddMinutes(-3)),
                isUnattended: true,
                awayStatus: "Rebased onto main and re-ran the suite — the reflow tests pass, two sandbox probes still red.");
            away.OnAppearing();
            Settle(500);
            Shot(window, "03b-session-away");
        }

        // 3) A sheet over it: the files the agent changed.
        if (shell.CurrentPage is SessionPageViewModel page)
        {
            page.ShowFilesCommand.Execute(null);
            Settle(500);
            Shot(window, "04-files-sheet");

            // 4) A diff, rendered line by line.
            if (entry.Session?.ModifiedFiles.FirstOrDefault() is { } file)
            {
                shell.ShowSheet(new DetailSheetViewModel(shell, file.KindLabel, file.Detail, command: file.Name));
                Settle(500);
                Shot(window, "05-diff");
            }

            shell.CloseSheet();
            Settle(200);

            // 5) The pinned approval card — the app's whole reason for existing on a phone. The
            //    simulated agent raises a real permission request for a destructive tool.
            if (entry.Session is { } live)
            {
                live.PromptText = "Delete the build directory and start clean.";
                live.SendCommand.Execute(null);
                Pump(() => live.PendingPermission is not null, 4000);
                Settle(600);
                Shot(window, "06-approval");
            }
        }

        // 5b) A file the agent sent: the transcript card (with the picture inline), then the sheet over
        //     it with the three verbs. The simulated host serves no file bytes, so the image is seeded
        //     into the same cache a real fetch would fill — the card and the sheet are the thing under
        //     test, not the download.
        if (shell.CurrentPage is SessionPageViewModel filePage && filePage.Session is { } withFiles)
        {
            var shot = SamplePng(720, 380);
            var image = Shared(withFiles, "screenshot.png", "shared/screenshot.png", shot.Length, "image/png",
                "The dashboard after the fix — the p95 line is the one that moved.");
            SharedFilePreviews.Seed(withFiles, image, shot);
            withFiles.Items.Add(image);

            withFiles.Items.Add(Shared(withFiles, "coverage.txt", "shared/coverage.txt", 4_812, "text/plain",
                "Coverage for the touched files, if you want the numbers."));

            Settle(400);
            Shot(window, "06b-shared-file");

            filePage.OpenSharedFileCommand.Execute(image);
            Settle(700);
            Shot(window, "06c-received-file");
            shell.CloseSheet();
            Settle(250);
        }

        // 5c) A graphical session: the Screen segment, with a desktop in it and the trackpad's rules on
        //     the strip below. The simulated host has no sandbox, so the display channel is faked — what
        //     is under test is the surface (decode, letterbox, cursor, chrome), not the transport.
        CaptureScreen(shell, window);

        // 6) The inbox, with that same request waiting in it — plus a device asking to join, which the
        //    simulated host can't produce, so it's injected directly into the collection the view binds.
        shell.SelectTab(ShellTab.Inbox);
        Settle(500); // the tab's own refresh runs first and would otherwise clear the injected row
        shell.Inbox.PendingDevices.Add(new PendingDeviceRow(
            shell.Hosts.Links[0],
            new PendingPairApproval("req-preview", "Ada's laptop", "418302",
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(10))));
        Settle(300);
        Shot(window, "07-inbox");

        // 6b) The same tab with the join request answered, so the "Sent to you" section is above the fold.
        shell.Inbox.PendingDevices.Clear();
        Settle(300);
        Shot(window, "07b-inbox-files");

        // 7) Starting a session.
        shell.SelectTab(ShellTab.Sessions);
        Settle(200);
        shell.Sessions.StartNew();
        Pump(() => shell.CurrentPage is NewSessionPageViewModel p && p.Agents.Count > 0, 3000);
        Settle(400);
        Shot(window, "08-new-session");

        // 8) Pairing a host.
        shell.PopToRoot();
        shell.Push(new ConnectPageViewModel(shell, shell.Hosts, shell.Sessions));
        Settle(400);
        Shot(window, "09-connect");

        // 8b) The same screen pointed at an address with nothing behind it. Port 1 on loopback is
        //     reliably refused, so this exercises the real probe rather than a mocked failure.
        if (shell.CurrentPage is ConnectPageViewModel connect)
        {
            connect.Address = "http://127.0.0.1:1";
            Pump(() => connect.IsUnreachable, 6000);
            Settle(400);
            Shot(window, "09b-connect-unreachable");

            // 8c) Waiting for a device you already use to approve this one. Reaching this state for real
            //     needs a live host, so the state itself is set — the view is the thing under test.
            connect.IsAwaitingApproval = true;
            connect.VerificationCode = "418302";
            Settle(400);
            Shot(window, "09c-connect-awaiting");
            connect.CancelApprovalCommand.Execute(null);
        }

        // 8d) What's already running on a host — the screen a freshly paired device lands on, and the one
        //     that turns "I paired a phone" into "I picked up the run I left on the desktop".
        shell.PopToRoot();
        if (shell.Hosts.Links.FirstOrDefault() is { } link)
        {
            var browse = new HostSessionsPageViewModel(shell, shell.Hosts, shell.Sessions, link);
            shell.Push(browse);
            Pump(() => browse.Catalog.HasSessions, 3000);
            Settle(400);
            Shot(window, "09d-host-sessions");
        }

        // 9) Settings, and the light theme (the brand's default surface treatment).
        shell.PopToRoot();
        shell.SelectTab(ShellTab.More);
        Settle(300);
        Shot(window, "10-more");

        // 9b) Appearance, which is where the graphical session's data-saving switch lives.
        shell.Push(new AppearancePageViewModel(shell));
        Settle(400);
        Shot(window, "10b-appearance");
        shell.PopToRoot();
        shell.SelectTab(ShellTab.More);
        Settle(200);

        ThemeApplier.Apply("Light");
        Settle(400);
        Shot(window, "11-more-light");

        shell.SelectTab(ShellTab.Sessions);
        Settle(400);
        Shot(window, "12-sessions-light");

        ThemeApplier.Apply("Dark");
        Settle(200);
    }

    /// <summary>
    /// Opens a session marked as having a graphical sandbox, feeds its display a synthetic desktop, and
    /// shoots both segments: the Screen itself, and the conversation with the live thumbnail above it.
    /// </summary>
    private static void CaptureScreen(ShellViewModel shell, Window window)
    {
        const int GuestWidth = 1280;
        const int GuestHeight = 800;

        var link = shell.Hosts.Links[0];
        var host = link.ConnectAsync().GetAwaiter().GetResult();
        if (host is null)
        {
            return;
        }

        var info = host.OpenSessionAsync("claude-code-native", "/home/you/projects/atlas")
            .GetAwaiter().GetResult();
        var view = host.SubscribeAsync(info.SessionId).GetAwaiter().GetResult();
        var session = shell.Sessions.Build(host, view, "Atlas — browser check");
        var saved = new SavedSession(
            link.Name, link.Url, link.Saved.Token, info.SessionId, "claude-code-native",
            "Atlas — browser check", info.WorkingDirectory, HasDisplay: true);
        var entry = shell.Sessions.Adopt(link, session, saved, open: false);

        shell.PopToRoot();
        shell.Sessions.Open(entry);
        Pump(() => shell.CurrentPage is SessionPageViewModel p && p.Entry == entry, 3000);
        if (shell.CurrentPage is not SessionPageViewModel page)
        {
            return;
        }

        // The display is handed in rather than built from the session, so the frames come from the fake
        // channel below instead of a sandbox that doesn't exist here.
        var display = new FakeDisplayHost();
        page.Display = new DisplayViewModel(display, info.SessionId, new MobileDispatcher());
        page.ShowScreenCommand.ExecuteAsync(null);
        Pump(() => page.Display!.IsConnected, 2000);

        display.Channel.PushInfo(GuestWidth, GuestHeight, DisplayControlHolder.Agent);
        display.Channel.PushFull(GuestWidth, GuestHeight, FakeDesktop.Jpeg(GuestWidth, GuestHeight, "atlas — build log"));
        Settle(500);
        Shot(window, "13-screen-agent-driving");

        // Taking control is the whole point of the segment: the cursor appears, the chip turns amber and
        // the key strip lights up.
        display.Channel.Push(ControlNotice(DisplayControlHolder.User));
        // A tile: one region repainted over the picture already there.
        display.Channel.PushTile(120, 140, 520, 220, GuestWidth, GuestHeight,
            FakeDesktop.Jpeg(520, 220, "terminal", hue: 60));
        Settle(500);
        Shot(window, "13b-screen-you-driving");

        // Back to the conversation: the screen stays connected, and the thumbnail above the transcript is
        // why that matters.
        page.ShowTranscriptCommand.Execute(null);
        Settle(400);
        Shot(window, "13c-screen-thumbnail");

        shell.PopToRoot();
        Settle(200);
    }

    /// <summary>
    /// Two cards whose agents have reported: the running demo session, which last said anything twelve
    /// minutes ago — where the age stops being a timestamp and starts reading "no update for 12 min" —
    /// and a second, cold from its saved pointer, that spoke three minutes back.
    /// </summary>
    private static void SeedStatusCards(ShellViewModel shell)
    {
        var link = shell.Hosts.Links[0];

        // The stale one has to be the *running* card: "no update for 12 min" is a claim about an agent
        // that is still going, and on an idle session it would be a complaint about nothing.
        shell.Sessions.All[0].AdoptStatus(new AgentStatus(
            "Narrowed the flake to the tail cursor after a reconnect; bisecting the last twelve commits.",
            DateTimeOffset.Now.AddMinutes(-12)));

        Add("Reflow the terminal panel", "/home/you/projects/agnes",
            "Terminal panel reflows correctly at 80 cols; wiring the resize handler next.", minutesAgo: 3);

        void Add(string title, string folder, string status, int minutesAgo)
        {
            var saved = new SavedSession(
                link.Name, link.Url, link.Saved.Token, "preview-" + Guid.NewGuid().ToString("n"),
                "claude-code", title, folder,
                LatestStatus: status,
                LatestStatusAt: DateTimeOffset.Now.AddMinutes(-minutesAgo));
            shell.Sessions.All.Add(new SessionEntry(saved, link));
        }
    }

    private static Agnes.Client.DisplayFrame ControlNotice(DisplayControlHolder holder)
    {
        var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new DisplayControlNotice(holder, "preview-device"), DisplayWire.Json);
        return new Agnes.Client.DisplayFrame(
            new DisplayFrameHeader(DisplayFrameKind.Control, 3, 0, 0, 0, 0, 0, 0, (uint)json.Length), json);
    }

    /// <summary>Builds a shared-file transcript item and files it just after the newest event, so a link
    /// or an inbox row can address it the way a real one would.</summary>
    private static SharedFileItem Shared(
        SessionViewModel session, string name, string path, long size, string mime, string caption)
        => new(new FileSharedEvent(name, name, path, size, mime, caption))
        {
            Sequence = session.Items.LastOrDefault()?.Sequence + 1 ?? 1,
            Timestamp = DateTimeOffset.Now.AddMinutes(-2),
        };

    /// <summary>
    /// A PNG to stand in for whatever the agent actually produced — drawn rather than checked in, so the
    /// harness stays a single project with no binary fixtures. Bands of the brand ramp: enough for the
    /// card's inline preview and the sheet's full-width one to be legible in a screenshot.
    /// </summary>
    private static byte[] SamplePng(int width, int height)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

        using (var buffer = bitmap.Lock())
        {
            var row = new byte[buffer.RowBytes];
            for (var y = 0; y < height; y++)
            {
                var shade = 0.45 + (0.55 * (1 - (y / (double)height)));
                for (var x = 0; x < width; x++)
                {
                    // Violet → magenta → coral across x, darkened toward the bottom. BGRA byte order.
                    var t = x / (double)width;
                    row[(x * 4) + 0] = (byte)(((0xEE * (1 - t)) + (0x66 * t)) * shade);
                    row[(x * 4) + 1] = (byte)(((0x55 * (1 - t)) + (0x73 * t)) * shade);
                    row[(x * 4) + 2] = (byte)(((0x8A * (1 - t)) + (0xF9 * t)) * shade);
                    row[(x * 4) + 3] = 0xFF;
                }

                System.Runtime.InteropServices.Marshal.Copy(
                    row, 0, buffer.Address + (y * buffer.RowBytes), buffer.RowBytes);
            }
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, new PngBitmapEncoderOptions());
        return stream.ToArray();
    }

    // ---- headless plumbing ----

    internal static void Shot(Window window, string name)
    {
        Settle(120);
        var path = Path.Combine(_outDir, name + ".png");
        var frame = window.CaptureRenderedFrame();
        if (frame is null)
        {
            Console.Error.WriteLine($"!! {name}: no frame captured");
            return;
        }

        using (frame)
        using (var file = File.Create(path))
        {
            frame.Save(file, new PngBitmapEncoderOptions());
        }

        Console.WriteLine($"   {name}.png");
    }

    /// <summary>Runs the dispatcher and the render loop for a wall-clock interval, so background work in
    /// the simulated host (which streams on a timer) actually lands before a capture.</summary>
    internal static void Settle(int milliseconds)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(15);
        }

        Dispatcher.UIThread.RunJobs();
    }

    internal static void Pump(Func<bool> until, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline && !until())
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(15);
        }

        Dispatcher.UIThread.RunJobs();
    }
}

/// <summary>
/// Stands in for Android's Downloads / share sheet / open-with so the received-file sheet renders with
/// its three real buttons. Saving actually writes, into the harness's temp directory — a screenshot tool
/// that lies about what a button does is worse than one that has no buttons.
/// </summary>
public sealed class PreviewReceivedFileHandler : Agnes.Ui.Core.IReceivedFileHandler
{
    public bool CanSave => true;

    public bool CanOpen => true;

    public bool CanShare => true;

    public Task SaveAsync(Agnes.Ui.Core.ReceivedFile file, CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(Path.GetTempPath(), "agnes-mobile-preview", "downloads");
        Directory.CreateDirectory(directory);
        return File.WriteAllBytesAsync(
            Path.Combine(directory, ReceivedFileNaming.Sanitize(file.FileName)), file.Bytes, cancellationToken);
    }

    public Task OpenAsync(Agnes.Ui.Core.ReceivedFile file, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task ShareAsync(Agnes.Ui.Core.ReceivedFile file, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

/// <summary>AppBuilder for the headless session: real Skia drawing, so the frames have pixels.</summary>
public static class PreviewAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<PreviewApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia();
}
