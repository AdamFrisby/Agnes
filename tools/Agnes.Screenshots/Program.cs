using Agnes.App.Desktop;
using Agnes.App.Desktop.Persistence;
using Agnes.App.Desktop.ViewModels;
using Agnes.Client.Simulation;
using Agnes.Ui.Core.Transcript;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Dock.Model.Controls;

namespace Agnes.Screenshots;

/// <summary>
/// Renders the real Avalonia desktop UI (against the simulated server) to PNGs using Avalonia's
/// headless Skia backend — no display, no screen capture. Driving is synchronous: we pump the
/// dispatcher with RunJobs()/render ticks and sleep the wall clock so the simulator's background
/// streaming completes and its UI posts are flushed before each capture.
/// </summary>
public static class Program
{
    private static string _outDir = "screenshots";

    public static void Main(string[] args)
    {
        _outDir = args.Length > 0 ? args[0] : Path.Combine(Directory.GetCurrentDirectory(), "screenshots");
        Directory.CreateDirectory(_outDir);

        using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        session.Dispatch(CaptureAll, CancellationToken.None).GetAwaiter().GetResult();

        Console.WriteLine($"Done. Screenshots in {_outDir}");
    }

    private static void CaptureAll()
    {
        var statePath = Path.Combine(_outDir, "tabs-state.json");
        File.Delete(statePath);
        var store = new SessionStateStore(statePath);

        var vm = new MainWindowViewModel(new SimulatedConnector(), new AvaloniaDispatcher(), store);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        vm.RestoreAsync(); // empty state → one fresh picker tab; also enables persistence
        Settle(300);

        // 1) New-tab agent picker
        Pump(() => LastTab(vm)?.Agents is { Count: > 0 });
        Capture(window, "01-picker.png");

        // 2) A live conversation
        var chat = OpenAgent(vm, "opencode");
        Prompt(chat, "In one sentence, what is the Agent Client Protocol?");
        Pump(() => chat.Session!.Items.OfType<MessageBubbleItem>().Count(m => !m.IsUser && !m.IsThought) >= 2);
        Capture(window, "02-conversation.png");

        // 3) Tool call + plan
        var tools = OpenAgent(vm, "opencode");
        Prompt(tools, "Create a file called notes.txt with a short plan.");
        Pump(() => tools.Session!.Items.OfType<ToolCallItem>().Any()
                   && tools.Session!.Items.OfType<PlanItemView>().Any());
        Capture(window, "03-tools-and-plan.png");

        // 4) Permission request + resolution
        var perm = OpenAgent(vm, "opencode");
        Prompt(perm, "Delete the build folder please.");
        Pump(() => perm.Session!.PendingPermission is not null);
        Capture(window, "04-permission.png");
        perm.Session!.AllowCommand.Execute(null);
        Pump(() => perm.Session!.PendingPermission is null
                   && perm.Session!.Items.OfType<PermissionItem>().Any(p => p.Resolved));
        Capture(window, "05-permission-allowed.png");

        // 6) The browser-style tab strip (several tabs now open)
        Capture(window, "06-tabs.png");

        // 7) Auto-reconnect on relaunch: a fresh instance restoring the saved tabs
        var vm2 = new MainWindowViewModel(new SimulatedConnector(), new AvaloniaDispatcher(), store);
        var window2 = new MainWindow { DataContext = vm2 };
        window2.Show();
        vm2.RestoreAsync();
        Pump(() => Tabs(vm2).Any() && Tabs(vm2).All(d => d.Session is not null));
        Capture(window2, "07-restore-on-relaunch.png");
    }

    // ---- driving through the real public model ----

    private static IDocumentDock Dock(MainWindowViewModel vm) => (IDocumentDock)vm.Layout.VisibleDockables![0];

    private static IEnumerable<SessionDocument> Tabs(MainWindowViewModel vm)
        => Dock(vm).VisibleDockables!.OfType<SessionDocument>();

    private static SessionDocument? LastTab(MainWindowViewModel vm) => Tabs(vm).LastOrDefault();

    private static SessionDocument OpenAgent(MainWindowViewModel vm, string adapterId)
    {
        vm.NewTabCommand.Execute(null);
        Pump(() => LastTab(vm)?.Agents is { Count: > 0 });
        var doc = LastTab(vm)!;
        doc.Agents!.First(a => a.AdapterId == adapterId).Open.Execute(null);
        Pump(() => doc.Session is not null);
        return doc;
    }

    private static void Prompt(SessionDocument doc, string text)
    {
        doc.Session!.PromptText = text;
        doc.Session!.SendCommand.Execute(null);
        Pump(() => doc.Session!.Items.OfType<MessageBubbleItem>().Any(m => m.IsUser));
    }

    private static void Pump(Func<bool> condition, int timeoutMs = 10000)
    {
        var start = DateTime.UtcNow;
        while (!condition() && (DateTime.UtcNow - start).TotalMilliseconds < timeoutMs)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
            Thread.Sleep(12);
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static void Settle(int ms)
    {
        var start = DateTime.UtcNow;
        while ((DateTime.UtcNow - start).TotalMilliseconds < ms)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
            Thread.Sleep(12);
        }
    }

    private static void Capture(Window window, string name)
    {
        Settle(250);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
        var frame = window.CaptureRenderedFrame();
        var path = Path.Combine(_outDir, name);
        frame.Save(path);
        Console.WriteLine($"captured {name} ({frame.PixelSize.Width}x{frame.PixelSize.Height})");
    }
}
