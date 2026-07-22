using Agnes.Abstractions.Events;
using Agnes.Client;
using Agnes.Protocol;
using Agnes.Ui.Core;
using Agnes.Ui.Core.Plugins;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.Desktop.Tests;

/// <summary>The client command tail runs through the client spine: interrupt (cancel) is vetoable, and
/// retry/attachment are observe-only facts a client plugin can react to (see
/// .ideas/00d-event-spine-and-ui-extensibility.md, Pass 3).</summary>
public class ClientCommandSpineTests
{
    private sealed class Veto<T>() : IEventInterceptor<T> where T : CancelableEvent
    {
        public ValueTask InterceptAsync(T evt, CancellationToken ct = default) { evt.Cancel("test"); return ValueTask.CompletedTask; }
    }

    private sealed class Record<T>(Action<T> onSeen) : IEventObserver<T> where T : IAgnesEvent
    {
        public ValueTask ObserveAsync(T evt, CancellationToken ct = default) { onSeen(evt); return ValueTask.CompletedTask; }
    }

    private static SessionView Live(string id = "s1")
    {
        var view = new SessionView(id);
        view.ApplySnapshot(new SessionSnapshot(new SessionInfo(id, "opencode", string.Empty, 0), [], 0));
        return view;
    }

    private static SessionViewModel NewVm(FakeHost host, EventBus bus)
        => new(host, Live(), ImmediateDispatcher.Instance, "OpenCode", eventBus: bus);

    [Fact]
    public void A_client_plugin_can_veto_an_interrupt_so_the_turn_keeps_running()
    {
        var host = new FakeHost();
        var bus = new EventBus();
        var vm = NewVm(host, bus);
        bus.Intercept(new Veto<BeforeTurnCancelEvent>());

        vm.CancelCommand.Execute(null);

        Assert.Equal(0, host.Cancels); // the veto kept the turn running
    }

    [Fact]
    public void With_no_interceptor_an_interrupt_reaches_the_host()
    {
        var host = new FakeHost();
        var vm = NewVm(host, new EventBus());

        vm.CancelCommand.Execute(null);

        Assert.Equal(1, host.Cancels);
    }

    [Fact]
    public void Adding_an_attachment_dispatches_an_observe_event()
    {
        var host = new FakeHost();
        var bus = new EventBus();
        string? seen = null;
        bus.Observe(new Record<AttachmentAddedEvent>(e => seen = e.SessionId));
        var vm = NewVm(host, bus);

        vm.Attach(PromptAttachment.Reference("README.md"));

        Assert.Equal("s1", seen);
    }
}
