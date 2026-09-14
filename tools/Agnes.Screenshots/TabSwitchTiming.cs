using System.Diagnostics;
using System.Text.Json;
using Agnes.App.Desktop;
using Agnes.App.Desktop.Persistence;
using Agnes.App.Desktop.ViewModels;
using Agnes.Client.Cache;
using Agnes.Ui.Core.Onboarding;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Model.Controls;

namespace Agnes.Screenshots;

/// <summary>
/// Measures what a tab switch costs in the real window, against a real host, with the sessions this
/// desktop actually has open — the number behind "switching to the long session takes seconds".
/// </summary>
/// <remarks>
/// The window is built the way the desktop builds it (cache included, so opening is quick), the saved
/// tabs are restored, and then the active document is flipped back and forth while three phases are
/// timed: the activation itself (Dock swapping the content, the view attaching), the first layout pass
/// (the list realising its rows), and the queued work that follows (the scroll-to-end, the overlays).
///
/// <code>
/// dotnet run --project tools/Agnes.Screenshots -- --switch-timing --out shots   # reads the desktop's own hosts.json + tabs
/// </code>
/// </remarks>
public static class TabSwitchTiming
{
    public sealed record Options
    {
        public string OutDir { get; init; } = "screenshots/timing";
        public int Rounds { get; init; } = 4;

        /// <summary>Keep switching for this many seconds after the measured rounds, so a sampler outside
        /// the process (dotnet-stack) can catch the UI thread in the act.</summary>
        public int LoopSeconds { get; init; }

        /// <summary>Force the hot host down to this many attached views before the rounds and the loop, so
        /// every switch is a warm re-attach (1) rather than a visibility flip — the cost to profile next.</summary>
        public int? Capacity { get; init; }

        /// <summary>Also forget each document's view in the recycler before activating it, so every switch is
        /// a cold rebuild.</summary>
        public bool Cold { get; init; }

        /// <summary>Bisection aids for a leak: run without client plugins, or without the event cache.</summary>
        public bool NoPlugins { get; init; }
        public bool NoCache { get; init; }

        /// <summary>Write a full process dump right after the sleep (for --roots), to this path.</summary>
        public string? DumpPath { get; init; }
        public int Width { get; init; } = 1700;
        public int Height { get; init; } = 1000;
    }

    public static Options? TryParse(string[] args)
    {
        if (!args.Contains("--switch-timing", StringComparer.Ordinal))
        {
            return null;
        }

        string? Value(string name)
        {
            var i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.Ordinal));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        return new Options
        {
            OutDir = Value("--out") ?? Path.Combine(Directory.GetCurrentDirectory(), "screenshots", "timing"),
            Rounds = int.TryParse(Value("--rounds"), out var r) ? r : 4,
            LoopSeconds = int.TryParse(Value("--loop"), out var l) ? l : 0,
            Capacity = int.TryParse(Value("--capacity"), out var c) ? c : null,
            Cold = args.Contains("--cold", StringComparer.Ordinal),
            NoPlugins = args.Contains("--no-plugins", StringComparer.Ordinal),
            NoCache = args.Contains("--no-cache", StringComparer.Ordinal),
            DumpPath = Value("--dump"),
        };
    }

    public static void Run(Options options)
    {
        Directory.CreateDirectory(options.OutDir);
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Agnes");

        // The desktop's own hosts and tabs, copied beside the shots so nothing here writes back to them.
        var hosts = new HostRegistryStore(Path.Combine(appData, "hosts.json")).Load();
        var tabs = new SessionStateStore(Path.Combine(appData, "desktop-tabs.json")).Load().Where(t => !t.IsScreen).ToList();
        var hostsPath = Path.Combine(options.OutDir, "hosts.json");
        var statePath = Path.Combine(options.OutDir, "tabs-state.json");
        new HostRegistryStore(hostsPath).Save(hosts);
        new SessionStateStore(statePath).Save(tabs);
        Console.WriteLine($"{tabs.Count} session tab(s): {string.Join(", ", tabs.Select(t => t.Title))}");

        var cache = options.NoCache ? null : SqliteSessionEventCache.Open(Path.Combine(appData, "cache", "events.db"));
        var emptyPlugins = Path.Combine(options.OutDir, "no-plugins");
        Directory.CreateDirectory(emptyPlugins);
        var vm = new MainWindowViewModel(
            new RoutingConnector(Path.Combine(Directory.GetCurrentDirectory(), "recordings"), eventCache: cache),
            new AvaloniaDispatcher(),
            new SessionStateStore(statePath),
            new HostRegistryStore(hostsPath),
            onboarding: new InMemoryOnboardingStore(new OnboardingState(WizardCompleted: true)),
            clientPluginDirectory: options.NoPlugins ? emptyPlugins : null,
            eventCache: cache);
        Console.WriteLine($"plugins: {(options.NoPlugins ? "none" : "the desktop's")} · cache: {(cache is null ? "off" : "on")}");
        var window = new MainWindow { DataContext = vm, Width = options.Width, Height = options.Height };
        window.Show();
        MainWindowViewModel.ApplyTheme("Dark");
        vm.Showcase.Dismiss();
        var heapBefore = Heap();
        var opened = Stopwatch.StartNew();
        vm.RestoreAsync();

        var dock = (IDocumentDock)vm.Layout.VisibleDockables![0];
        List<SessionDocument> docs = [];
        Program.Pump(() => (docs = dock.VisibleDockables!.OfType<SessionDocument>().ToList()).Count == tabs.Count
            && docs.All(d => d.Session is not null && d.Session.Items.Count > 0), 180_000);
        Program.Settle(1500);
        Console.WriteLine($"all sessions open after {opened.Elapsed.TotalSeconds:0.0} s");
        var heapAfter = Heap();
        Console.WriteLine($"managed heap: {heapBefore / 1048576.0:0} MB before the tabs → {heapAfter / 1048576.0:0} MB with them open (+{(heapAfter - heapBefore) / 1048576.0:0} MB); working set {Environment.WorkingSet / 1048576.0:0} MB");
        Summarize(docs);

        if (docs.Count < 2)
        {
            Console.Error.WriteLine("Need at least two session tabs to switch between.");
            return;
        }

        var hotHost = window.GetVisualDescendants().OfType<HotDocumentHost>().FirstOrDefault();
        if (options.Capacity is { } cap && hotHost is not null)
        {
            hotHost.Capacity = cap;
            Console.WriteLine($"hot host capacity set to {cap}");
        }
        var recycler = Avalonia.Application.Current?.Resources.TryGetResource("DockRecycler", null, out var r) == true ? r as PerItemControlRecycling : null;
        void Chill(SessionDocument doc)
        {
            if (options.Cold)
            {
                recycler?.Forget(doc);
            }
        }

        for (var round = 0; round < options.Rounds; round++)
        {
            foreach (var target in docs)
            {
                if (ReferenceEquals(dock.ActiveDockable, target))
                {
                    continue;
                }

                Chill(target);
                var sw = Stopwatch.StartNew();
                vm.ActivateSessionCommand.Execute(target);
                var activate = sw.Elapsed;
                var targetView = window.GetVisualDescendants().OfType<Agnes.App.Desktop.Views.SessionTabView>()
                    .FirstOrDefault(v => ReferenceEquals(v.DataContext, target));
                var attachedAtOnce = targetView is not null;

                sw.Restart();
                window.UpdateLayout();
                var layout = sw.Elapsed;

                // The queue by priority: what Dock and the bindings post (Normal and above), then the
                // render-priority overlays, then the background scroll-to-end.
                sw.Restart();
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Normal);
                var normalJobs = sw.Elapsed;
                sw.Restart();
                window.UpdateLayout();
                var layoutAfterNormal = sw.Elapsed;
                sw.Restart();
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);
                window.UpdateLayout();
                var renderJobs = sw.Elapsed;
                sw.Restart();
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
                window.UpdateLayout();
                var queued1 = normalJobs + layoutAfterNormal + renderJobs + sw.Elapsed;
                var backgroundJobs = sw.Elapsed;
                Console.WriteLine($"      attached synchronously: {attachedAtOnce} · normal jobs {normalJobs.TotalMilliseconds:0} ms · layout {layoutAfterNormal.TotalMilliseconds:0} ms · render jobs {renderJobs.TotalMilliseconds:0} ms · background jobs {backgroundJobs.TotalMilliseconds:0} ms · view visuals {(targetView ?? window.GetVisualDescendants().OfType<Agnes.App.Desktop.Views.SessionTabView>().FirstOrDefault(v => ReferenceEquals(v.DataContext, target)))?.GetVisualDescendants().Count()}");

                sw.Restart();
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
                window.UpdateLayout();
                var queued2 = sw.Elapsed;

                var list = window.GetVisualDescendants().OfType<ListBox>().FirstOrDefault(l => l.Name == "Transcript" && l.IsEffectivelyVisible);
                var realized = list?.GetRealizedContainers().ToList() ?? [];
                Console.WriteLine(
                    $"round {round + 1} → {target.Title,-24} activate {activate.TotalMilliseconds,7:0.0} ms · first layout {layout.TotalMilliseconds,7:0.0} ms · " +
                    $"queued {queued1.TotalMilliseconds,7:0.0} ms · settle {queued2.TotalMilliseconds,7:0.0} ms · total {(activate + layout + queued1 + queued2).TotalMilliseconds,7:0.0} ms · realized rows {realized.Count}");
                if (round == 0)
                {
                    foreach (var row in realized)
                    {
                        var item = row.DataContext;
                        var size = item switch
                        {
                            Agnes.Ui.Core.Transcript.MessageBubbleItem m => $"text {m.Text.Length:N0} chars, thought={m.IsThought} user={m.IsUser}",
                            Agnes.Ui.Core.Transcript.ToolCallItem t => $"detail {t.Detail.Length:N0} chars, diff {(t.Diff?.Length ?? 0):N0} chars, {t.InlineDiffLines?.Count ?? 0} diff lines",
                            _ => string.Empty,
                        };
                        Console.WriteLine($"      row {list!.IndexFromContainer(row),6}: {item?.GetType().Name,-18} h={row.Bounds.Height,7:0} visuals={row.GetVisualDescendants().Count(),5}  {size}");
                    }

                    // Where the time goes: the top page versus the last page, each realized from cold.
                    if (list?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } sv)
                    {
                        sw.Restart();
                        sv.Offset = new Avalonia.Vector(0, 0);
                        window.UpdateLayout();
                        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
                        window.UpdateLayout();
                        var top = sw.Elapsed;
                        var topRows = list.GetRealizedContainers().Count();
                        sw.Restart();
                        sv.Offset = new Avalonia.Vector(0, sv.Extent.Height);
                        window.UpdateLayout();
                        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
                        window.UpdateLayout();
                        var end = sw.Elapsed;
                        Console.WriteLine($"      scroll to top: {top.TotalMilliseconds:0} ms ({topRows} rows) · back to end: {end.TotalMilliseconds:0} ms ({list.GetRealizedContainers().Count()} rows) · extent {sv.Extent.Height:0}");
                    }
                }
            }
        }

        foreach (var doc in docs)
        {
            vm.ActivateSessionCommand.Execute(doc);
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
            window.UpdateLayout();
            DumpTree(window, doc);
        }

        // The cold tier, live: put the background tab to sleep, weigh what that gave back, wake it and time it.
        if (docs.Count >= 2)
        {
            var background = docs.First(d => !ReferenceEquals(d, dock.ActiveDockable));
            // A real sleep comes half an hour after the open; give the open's own host calls (models, prompt
            // templates, git status) a moment to land so what is measured is the sleep, not the open.
            Program.Settle(4000);
            var heapAwake = Heap();
            vm.SleepAfter = TimeSpan.FromMinutes(1);
            var (weakSession, weakView) = WeakHandles(window, background);
            var slept = vm.SweepIdleTabs(DateTime.UtcNow.AddHours(1));
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
            var heapAsleep = Heap();
            Console.WriteLine($"sleep: {slept} tab(s) slept ({background.Title}); heap {heapAwake / 1048576.0:0} MB → {heapAsleep / 1048576.0:0} MB (−{(heapAwake - heapAsleep) / 1048576.0:0} MB); still alive: session={weakSession.IsAlive} view={weakView.IsAlive}");
            if (options.DumpPath is { Length: > 0 } dumpPath && weakSession.IsAlive)
            {
                HeapDump.WriteSelf(dumpPath);
            }

            var sw = Stopwatch.StartNew();
            vm.ActivateSessionCommand.Execute(background);
            Program.Pump(() => background.Session is not null && background.Session.Items.Count > 0, 120_000);
            var reloaded = sw.Elapsed;
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
            window.UpdateLayout();
            Console.WriteLine($"wake: {background.Title} back with {background.Session!.Items.Count:N0} items in {reloaded.TotalMilliseconds:0} ms; shown after {sw.ElapsedMilliseconds} ms; heap {Heap() / 1048576.0:0} MB");
        }

        if (options.LoopSeconds > 0)
        {
            Console.WriteLine($"looping switches for {options.LoopSeconds} s (pid {Environment.ProcessId}) — sample with: dotnet-stack report -p {Environment.ProcessId}");
            var until = DateTime.UtcNow.AddSeconds(options.LoopSeconds);
            var n = 0;
            while (DateTime.UtcNow < until)
            {
                var next = docs[n++ % docs.Count];
                Chill(next);
                vm.ActivateSessionCommand.Execute(next);
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
                window.UpdateLayout();
            }
            Console.WriteLine($"{n} switches in the loop");
        }

        window.Close();
        if (cache is not null)
        {
            cache.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    // In its own frame on purpose: a Debug-built method keeps every local alive until it returns, and a
    // local that names a session would keep the session alive through the sleep measured below.
    private static void Summarize(List<SessionDocument> docs)
    {
        foreach (var d in docs)
        {
            var s = d.Session!;
            Console.WriteLine($"  {d.Title}: {s.Items.Count:N0} transcript items ; subagents={s.HasSubagents}; display items={(s.DisplayItems as System.Collections.ICollection)?.Count.ToString("N0") ?? "projected"}");
        }
    }

    // Weak handles on what sleeping should release, taken in their own frame so no local keeps them alive.
    private static (WeakReference session, WeakReference view) WeakHandles(Window window, SessionDocument doc)
    {
        var view = window.GetVisualDescendants().OfType<Agnes.App.Desktop.Views.SessionTabView>().FirstOrDefault(v => ReferenceEquals(v.DataContext, doc));
        return (new WeakReference(doc.Session!), new WeakReference(view!));
    }

    /// <summary>The managed heap after a full, compacting collection — what the tabs actually hold.</summary>
    private static long Heap()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        return GC.GetTotalMemory(forceFullCollection: true);
    }

    /// <summary>What the visuals of the active tab are: by control type, by region, and how many of them
    /// are hidden — the anatomy behind "a simple design that takes a second to attach".</summary>
    private static void DumpTree(Window window, SessionDocument active)
    {
        var view = window.GetVisualDescendants().OfType<Agnes.App.Desktop.Views.SessionTabView>()
            .FirstOrDefault(v => ReferenceEquals(v.DataContext, active));
        if (view is null)
        {
            Console.WriteLine("(no view for " + active.Title + ")");
            return;
        }

        var all = view.GetVisualDescendants().OfType<Visual>().ToList();
        var hidden = all.Count(v => !v.IsEffectivelyVisible);
        var inList = view.GetVisualDescendants().OfType<ListBox>().Where(l => l.Name == "Transcript")
            .SelectMany(l => l.GetVisualDescendants()).Count();
        Console.WriteLine($"=== {active.Title}: {all.Count:N0} visuals; {hidden:N0} not effectively visible; {inList:N0} inside the transcript list");

        Console.WriteLine("--- by type");
        foreach (var g in all.GroupBy(v => v.GetType().Name).OrderByDescending(g => g.Count()).Take(28))
        {
            Console.WriteLine($"{g.Count(),7:N0}  {g.Key}");
        }

        // By region: the nearest named ancestor that is a direct-ish child of the tab, so the counts say
        // "the left panel", "the composer", "the transcript", "a hidden sheet".
        Console.WriteLine("--- by named region (nearest named ancestor)");
        var byRegion = new Dictionary<string, (int total, int hidden)>();
        foreach (var v in all)
        {
            var region = v.GetSelfAndVisualAncestors().OfType<Control>()
                .TakeWhile(c => !ReferenceEquals(c, view))
                .LastOrDefault(c => !string.IsNullOrEmpty(c.Name))?.Name ?? "(unnamed root children)";
            var t = byRegion.GetValueOrDefault(region);
            byRegion[region] = (t.total + 1, t.hidden + (v.IsEffectivelyVisible ? 0 : 1));
        }
        foreach (var kv in byRegion.OrderByDescending(kv => kv.Value.total).Take(30))
        {
            Console.WriteLine($"{kv.Value.total,7:N0}  ({kv.Value.hidden,6:N0} hidden)  {kv.Key}");
        }

        // The item hosts carrying the bulk: which ItemsControl, bound to what, how many items, shown or not.
        Console.WriteLine("--- heaviest item hosts");
        foreach (var host in all.OfType<ItemsControl>().Select(c => (c, count: c.GetVisualDescendants().Count()))
                     .OrderByDescending(t => t.count).Take(8))
        {
            var c = host.c;
            var first = c.Items.Cast<object?>().FirstOrDefault();
            var chain = string.Join(" < ", c.GetVisualAncestors().OfType<Control>().TakeWhile(a => !ReferenceEquals(a, view))
                .Where(a => !string.IsNullOrEmpty(a.Name)).Select(a => a.Name).Take(4));
            Console.WriteLine($"{host.count,7:N0}  {c.GetType().Name,-12} name='{c.Name}' items={c.ItemCount:N0} of {first?.GetType().Name ?? "?"} visible={c.IsEffectivelyVisible} dc={c.DataContext?.GetType().Name} in [{chain}]");
        }

        // The tab's own top-level regions: each direct child of the Workspace grid and of the chat viewport,
        // with what it costs and whether it shows — the map for building the collapsed ones lazily.
        Console.WriteLine("--- regions (direct children of Workspace / ChatViewport)");
        foreach (var grid in all.OfType<Grid>().Where(g => g.Name is "Workspace" or "ChatViewport"))
        {
            foreach (var child in grid.Children)
            {
                var count = child.GetVisualDescendants().Count();
                var col = Grid.GetColumn(child);
                var row = Grid.GetRow(child);
                var label = child.Name ?? child.GetType().Name;
                var first = (child as Panel)?.Children.FirstOrDefault()?.GetType().Name;
                Console.WriteLine($"{count,7:N0}  {grid.Name}[{row},{col}] {child.GetType().Name} '{label}' visible={child.IsEffectivelyVisible} size={child.Bounds.Width:0}x{child.Bounds.Height:0} first={first}");
            }
        }

        // Hidden subtrees, by their roots: an element that is not shown whose parent is. These are built and
        // styled for nothing until they show, so the biggest are the candidates for lazy construction.
        Console.WriteLine("--- hidden roots (largest first)");
        var hiddenRoots = all.OfType<Control>()
            .Where(c => !c.IsEffectivelyVisible && c.GetVisualParent() is Control parent && parent.IsEffectivelyVisible)
            .Select(c => (c, count: 1 + c.GetVisualDescendants().Count()))
            .OrderByDescending(t => t.count)
            .Take(14);
        foreach (var (c, count) in hiddenRoots)
        {
            var chain = string.Join(" < ", c.GetVisualAncestors().OfType<Control>().TakeWhile(a => !ReferenceEquals(a, view))
                .Where(a => !string.IsNullOrEmpty(a.Name)).Select(a => a.Name).Take(3));
            var dc = c.DataContext?.GetType().Name;
            Console.WriteLine($"{count,7:N0}  {c.GetType().Name,-16} '{c.Name}' classes=[{string.Join(' ', c.Classes.Where(k => !k.StartsWith(':')))}] dc={dc} in [{chain}]");
        }

        // The named element with the most descendants of its own, at any depth: what to look at first.
        Console.WriteLine("--- heaviest named elements (own descendants)");
        var named = all.OfType<Control>().Where(c => !string.IsNullOrEmpty(c.Name))
            .Select(c => (c.Name, c.GetType().Name, count: c.GetVisualDescendants().Count(), c.IsEffectivelyVisible))
            .OrderByDescending(t => t.count).Take(30);
        foreach (var (name, type, count, visible) in named)
        {
            Console.WriteLine($"{count,7:N0}  {type,-22} {name}{(visible ? string.Empty : "   [hidden]")}");
        }
    }
}
