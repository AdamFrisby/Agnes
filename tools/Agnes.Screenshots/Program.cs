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
        var hostsPath = Path.Combine(_outDir, "hosts.json");
        File.Delete(statePath);
        File.Delete(hostsPath);
        var store = new SessionStateStore(statePath);

        var vm = new MainWindowViewModel(new SimulatedConnector(), new AvaloniaDispatcher(), store, new HostRegistryStore(hostsPath));
        var window = new MainWindow { DataContext = vm };
        window.Show();
        vm.RestoreAsync(); // empty → one fresh host-picker tab; also enables persistence
        Settle(300);

        // 1) New-tab host picker (host is a per-tab choice)
        var first = LastTab(vm)!;
        Pump(() => first.Hosts is { Count: > 0 });
        Capture(window, "01-host-picker.png");

        // 2) Agent picker on the chosen host
        first.Hosts!.First().Select.Execute(null);
        Pump(() => first.Agents is { Count: > 0 });
        Capture(window, "02-agent-picker.png");

        // 3) A live conversation (note the status bar: connection + usage)
        first.Agents!.First(a => a.AdapterId == "opencode").Open.Execute(null);
        Pump(() => first.Session is not null);
        Prompt(first, "In one sentence, what is the Agent Client Protocol?");
        Pump(() => first.Session!.Items.OfType<MessageBubbleItem>().Count(m => !m.IsUser && !m.IsThought) >= 2);
        Capture(window, "03-conversation.png");

        // 4) Tool call + plan
        var tools = OpenSession(vm, "opencode");
        Prompt(tools, "Create a file called notes.txt with a short plan.");
        Pump(() => tools.Session!.Items.OfType<ToolCallItem>().Any()
                   && tools.Session!.Items.OfType<PlanItemView>().Any());
        Capture(window, "04-tools-and-plan.png");

        // 5) Permission request
        var perm = OpenSession(vm, "opencode");
        Prompt(perm, "Delete the build folder please.");
        Pump(() => perm.Session!.PendingPermission is not null);
        Capture(window, "05-permission.png");

        // 6) The browser-style tab strip (several tabs now open)
        Capture(window, "06-tabs.png");

        // 7) Auto-reconnect on relaunch: a fresh instance restoring the saved tabs
        var vm2 = new MainWindowViewModel(new SimulatedConnector(), new AvaloniaDispatcher(), store, new HostRegistryStore(hostsPath));
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

    private static SessionDocument OpenSession(MainWindowViewModel vm, string adapterId)
    {
        vm.NewTabCommand.Execute(null);
        var doc = LastTab(vm)!;
        Pump(() => doc.Hosts is { Count: > 0 });
        doc.Hosts!.First().Select.Execute(null); // simulated host
        Pump(() => doc.Agents is { Count: > 0 });
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
