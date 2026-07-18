using Agnes.App.Desktop;
using Agnes.App.Desktop.Persistence;
using Agnes.App.Desktop.ViewModels;
using Agnes.Client;
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
        var recordingsDir = Path.Combine(Directory.GetCurrentDirectory(), "recordings");

        var vm = new MainWindowViewModel(NewConnector(recordingsDir), new AvaloniaDispatcher(), store, new HostRegistryStore(hostsPath));
        var window = new MainWindow { DataContext = vm };
        window.Show();
        vm.Notifier = new AvaloniaNotifier(window); // in-app toasts for blockers/completions
        vm.WindowActive = false; // simulate a background window so completion toasts also show
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

        // 4) Multi-column workspace: plan + files + tools (left), chat (middle), full diff (right)
        var tools = OpenSession(vm, "opencode");
        Prompt(tools, "Create a file called notes.txt with a short plan.");
        Pump(() => tools.Session!.HasFiles && tools.Session!.HasTools
                   && tools.Session!.Items.OfType<PlanItemView>().Any());
        // Open the modified file to show its full diff in the right preview column.
        tools.Session!.ShowFilePreviewCommand.Execute(tools.Session!.ModifiedFiles[0]);
        Pump(() => tools.Session!.ShowRightPanel);
        Capture(window, "04-multicolumn.png");

        // 4c) Full-screen review: the diff fills the tab (chat + panels collapse away).
        tools.Session!.ToggleFullScreenCommand.Execute(null);
        Pump(() => tools.Session!.IsPreviewFullScreen && !tools.Session!.ShowChat);
        Capture(window, "04c-fullscreen-review.png");
        tools.Session!.ToggleFullScreenCommand.Execute(null);
        tools.Session!.ClosePreviewCommand.Execute(null);

        // 4d) Clear session-state banner (offline / reconnecting / interrupted / stale).
        tools.Session!.MarkStale();
        Pump(() => tools.Session!.ShowBanner);
        Capture(window, "04d-state-banner.png");
        tools.Session!.DismissBannerCommand.Execute(null);

        // 4e) Session management: pin, tag and the inline rename/tag editor.
        tools.TogglePinCommand.Execute(null);
        tools.TagInput = "backend";
        tools.AddTagCommand.Execute(null);
        tools.TagInput = "review";
        tools.AddTagCommand.Execute(null);
        tools.BeginRenameCommand.Execute(null);
        tools.RenameText = "Config refactor";
        Pump(() => tools.IsRenaming && tools.HasTags);
        Capture(window, "04e-session-management.png");
        tools.CommitRenameCommand.Execute(null); // rename commits → the tab at the top updates
        Pump(() => tools.Title == "Config refactor");

        // 4f) In-session search (Ctrl+F): matches list, count, deep-link to each hit.
        tools.Session!.OpenSearchCommand.Execute(null);
        tools.Session!.SearchQuery = "config";
        Pump(() => tools.Session!.Matches.Count > 0);
        Capture(window, "04f-search.png");
        tools.Session!.CloseSearchCommand.Execute(null);

        // 4g) Stop button: shown while a turn is streaming (Send is replaced by Stop).
        var running = OpenSession(vm, "opencode");
        Prompt(running, "Explain the Agent Client Protocol in detail.");
        Pump(() => running.Session!.IsTurnActive
                   && running.Session!.Items.OfType<MessageBubbleItem>().Any(m => !m.IsUser && !m.IsThought));
        Capture(window, "04g-stop-button.png");

        // 4h) Queue prompts while the turn runs (Queue / Send now / Stop actions).
        running.Session!.PromptText = "Then add unit tests for the parser.";
        running.Session!.SendCommand.Execute(null);
        running.Session!.PromptText = "Finally, update the README.";
        running.Session!.SendCommand.Execute(null);
        Pump(() => running.Session!.PendingPrompts.Count >= 2 && running.Session!.IsTurnActive);
        Capture(window, "04h-prompt-queue.png");
        running.Session!.CancelCommand.Execute(null);

        // 4b) Long assistant message → condensed in chat, full text in the preview
        var longChat = OpenSession(vm, "opencode");
        Prompt(longChat, "Explain the Agent Client Protocol in detail.");
        Pump(() => longChat.Session!.Items.OfType<MessageBubbleItem>().Any(m => m.IsLong));
        longChat.Session!.ShowMessagePreviewCommand.Execute(
            longChat.Session!.Items.OfType<MessageBubbleItem>().First(m => m.IsLong));
        Pump(() => longChat.Session!.ShowRightPanel);
        Capture(window, "04b-long-message.png");

        // 5) Permission request
        var perm = OpenSession(vm, "opencode");
        Prompt(perm, "Delete the build folder please.");
        Pump(() => perm.Session!.PendingPermission is not null);
        Capture(window, "05-permission.png");

        // 6) Recorded session — real captured OpenCode data replayed as an agent
        vm.NewTabCommand.Execute(null);
        var rec = LastTab(vm)!;
        Pump(() => rec.Hosts is { Count: > 1 });
        rec.Hosts!.First(h => h.Url.StartsWith("rec:")).Select.Execute(null);
        Pump(() => rec.Agents is { Count: > 0 });
        rec.Agents!.First(a => a.DisplayName.Contains("file")).Open.Execute(null);
        Pump(() => rec.Session is not null);
        Pump(() => rec.Session!.Items.OfType<ToolCallItem>().Any()
                   && rec.Session!.Items.OfType<MessageBubbleItem>().Any(m => !m.IsUser && !m.IsThought), 15000);
        Capture(window, "06-recorded.png");

        // 7) The browser-style tab strip (several tabs now open)
        Capture(window, "07-tabs.png");

        // 8) Auto-reconnect on relaunch: a fresh instance restoring the saved tabs
        var vm2 = new MainWindowViewModel(NewConnector(recordingsDir), new AvaloniaDispatcher(), store, new HostRegistryStore(hostsPath));
        var window2 = new MainWindow { DataContext = vm2 };
        window2.Show();
        vm2.RestoreAsync();
        Pump(() => Tabs(vm2).Any() && Tabs(vm2).All(d => d.Session is not null));
        Capture(window2, "08-restore-on-relaunch.png");
    }

    private static IAgnesConnector NewConnector(string recordingsDir)
        => new RoutingConnector(recordingsDir, recordingSpeed: 8.0);

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
