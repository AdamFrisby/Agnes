using Agnes.App.Desktop.Persistence;
using Agnes.App.Desktop.ViewModels;
using Agnes.Client.Simulation;
using Agnes.Ui.Core;
using Dock.Model.Controls;

namespace Agnes.Desktop.Tests;

/// <summary>
/// The cold tier: a tab nobody has activated for a while releases its session and keeps its place;
/// bringing it to the front reloads it. A tab whose agent is mid-turn, and the active tab, never sleep.
/// </summary>
public class IdleTabSleepTests
{
    private static MainWindowViewModel NewVm()
    {
        var id = Guid.NewGuid().ToString("n");
        return new MainWindowViewModel(new SimulatedConnector(), ImmediateDispatcher.Instance,
            new SessionStateStore(Path.Combine(Path.GetTempPath(), $"agnes-tabs-{id}.json")),
            new HostRegistryStore(Path.Combine(Path.GetTempPath(), $"agnes-hosts-{id}.json")),
            new NullPromptStore(),
            new SessionStateStore(Path.Combine(Path.GetTempPath(), $"agnes-arch-{id}.json")));
    }

    private static IEnumerable<SessionDocument> Tabs(MainWindowViewModel vm)
        => ((IDocumentDock)vm.Layout.VisibleDockables![0]).VisibleDockables!.OfType<SessionDocument>();

    private static async Task WaitAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(20, cts.Token);
        }
    }

    private static async Task<SessionDocument> OpenSessionAsync(MainWindowViewModel vm)
    {
        var tab = Tabs(vm).Last();
        await WaitAsync(() => tab.Hosts is { Count: > 0 });
        tab.Hosts!.First().Select.Execute(null);
        await WaitAsync(() => tab.Agents is { Count: > 0 });
        tab.WorkingDirectory = "/tmp/agnes";
        tab.SelectAgentChoiceCommand.Execute(tab.Agents!.First(a => a.AdapterId == "opencode"));
        tab.StartSessionCommand.Execute(null);
        await WaitAsync(() => tab.Session is not null && tab.Descriptor is not null);
        return tab;
    }

    // Sleeps and hands back a weak reference to what was released, without leaving a strong one on the
    // caller's stack (a local in the same frame keeps the object alive through the GC that follows).
    private static WeakReference Sleep(MainWindowViewModel vm, DateTimeOffset now, SessionDocument doc)
    {
        var weak = new WeakReference(doc.Session!);
        Assert.Equal(1, vm.SweepIdleTabs(now));
        return weak;
    }

    [Fact]
    public async Task An_idle_background_tab_sleeps_and_the_active_one_does_not()
    {
        var vm = NewVm();
        await vm.RestoreAsync();
        var first = await OpenSessionAsync(vm);
        vm.NewTabCommand.Execute(null);
        var second = await OpenSessionAsync(vm);
        await WaitAsync(() => !first.Session!.IsTurnActive && !second.Session!.IsTurnActive);
        Assert.Same(second, ((IDocumentDock)vm.Layout.VisibleDockables![0]).ActiveDockable);

        var later = DateTimeOffset.UtcNow + vm.SleepAfter + TimeSpan.FromMinutes(1);
        var released = Sleep(vm, later, first);

        Assert.True(first.IsSleeping);
        Assert.Null(first.Session);
        // The point of sleeping is the memory: nothing in the window may still hold the session.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(released.IsAlive, "the slept session view model is still reachable");
        Assert.Equal("agnes", first.Title);
        Assert.False(second.IsSleeping);
        Assert.NotNull(second.Session);

        // Nothing to do twice.
        Assert.Equal(0, vm.SweepIdleTabs(later));
    }

    [Fact]
    public async Task A_sleeping_tab_wakes_with_its_session_when_activated()
    {
        var vm = NewVm();
        await vm.RestoreAsync();
        var first = await OpenSessionAsync(vm);
        var sessionId = first.Session!.SessionId;
        vm.NewTabCommand.Execute(null);
        await OpenSessionAsync(vm);
        await WaitAsync(() => !first.Session!.IsTurnActive);
        vm.SweepIdleTabs(DateTimeOffset.UtcNow + vm.SleepAfter + TimeSpan.FromMinutes(1));
        Assert.True(first.IsSleeping);

        vm.ActivateSessionCommand.Execute(first);
        await WaitAsync(() => first.Session is not null);

        Assert.False(first.IsSleeping);
        Assert.Equal(sessionId, first.Session!.SessionId);
        Assert.NotEmpty(first.Session.Items);
    }

    [Fact]
    public async Task A_tab_whose_agent_is_working_never_sleeps()
    {
        var vm = NewVm();
        await vm.RestoreAsync();
        var first = await OpenSessionAsync(vm);
        vm.NewTabCommand.Execute(null);
        await OpenSessionAsync(vm);
        await WaitAsync(() => !first.Session!.IsTurnActive);

        // A prompt puts the simulated agent to work on the background tab.
        first.Session!.PromptText = "keep going";
        await first.Session!.SendCommand.ExecuteAsync(null);
        await WaitAsync(() => first.Session!.IsTurnActive);

        Assert.Equal(0, vm.SweepIdleTabs(DateTimeOffset.UtcNow + vm.SleepAfter + TimeSpan.FromMinutes(1)));
        Assert.False(first.IsSleeping);

        vm.SleepAfter = TimeSpan.Zero;
        await WaitAsync(() => !first.Session!.IsTurnActive);
        Assert.Equal(0, vm.SweepIdleTabs(DateTimeOffset.UtcNow.AddDays(1)));
    }
}
