using Agnes.App.Mobile.Services;
using Agnes.App.Mobile.ViewModels;
using Agnes.App.Mobile.Views;
using Agnes.Ui.Core.Transcript;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
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
    private const int PhoneWidth = 412;
    private const int PhoneHeight = 915;

    private static string _outDir = "screenshots/mobile";

    public static void Main(string[] args)
    {
        _outDir = args.Length > 0 ? args[0] : Path.Combine(Directory.GetCurrentDirectory(), "screenshots", "mobile");
        Directory.CreateDirectory(_outDir);

        // Never touch real device state from a render.
        var state = Path.Combine(Path.GetTempPath(), "agnes-mobile-preview");
        if (Directory.Exists(state))
        {
            Directory.Delete(state, recursive: true);
        }

        JsonStore.UseDirectory(state);

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
            deviceName: "Preview (headless)");

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
        Shot(window, "02-sessions");

        var entry = shell.Sessions.All[0];

        // 2) The session screen with a real transcript.
        shell.Sessions.Open(entry);
        Pump(() => shell.CurrentPage is SessionPageViewModel, 2000);
        Settle(700);
        Shot(window, "03-session");

        // 3) A sheet over it: the files the agent changed.
        if (shell.CurrentPage is SessionPageViewModel page)
        {
            page.ShowFilesCommand.Execute(null);
            Settle(500);
            Shot(window, "04-files-sheet");

            // 4) A diff, rendered line by line.
            if (entry.Session?.ModifiedFiles.FirstOrDefault() is { } file)
            {
                shell.ShowSheet(new DetailSheetViewModel(shell, file.Name, file.Detail));
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

        // 6) The inbox, with that same request waiting in it.
        shell.SelectTab(ShellTab.Inbox);
        Settle(500);
        Shot(window, "07-inbox");

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
        }

        // 9) Settings, and the light theme (the brand's default surface treatment).
        shell.PopToRoot();
        shell.SelectTab(ShellTab.More);
        Settle(300);
        Shot(window, "10-more");

        ThemeApplier.Apply("Light");
        Settle(400);
        Shot(window, "11-more-light");

        shell.SelectTab(ShellTab.Sessions);
        Settle(400);
        Shot(window, "12-sessions-light");

        ThemeApplier.Apply("Dark");
        Settle(200);
    }

    // ---- headless plumbing ----

    private static void Shot(Window window, string name)
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
    private static void Settle(int milliseconds)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(15);
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static void Pump(Func<bool> until, int timeoutMs)
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

/// <summary>AppBuilder for the headless session: real Skia drawing, so the frames have pixels.</summary>
public static class PreviewAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<PreviewApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia();
}
