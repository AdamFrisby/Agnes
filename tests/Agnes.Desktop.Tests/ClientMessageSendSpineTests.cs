using Agnes.Abstractions.Events;
using Agnes.Client;
using Agnes.Protocol;
using Agnes.Ui.Core;
using Agnes.Ui.Core.Plugins;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.Desktop.Tests;

/// <summary>A client plugin bound to the client spine can rewrite or veto an outgoing message before it
/// leaves this client (see .ideas/00d-event-spine-and-ui-extensibility.md).</summary>
public class ClientMessageSendSpineTests
{
    private sealed class VetoSend : IEventInterceptor<BeforeMessageSendEvent>
    {
        public ValueTask InterceptAsync(BeforeMessageSendEvent evt, CancellationToken ct = default) { evt.Cancel("do-not-disturb"); return ValueTask.CompletedTask; }
    }

    private sealed class RewriteSend : IEventInterceptor<BeforeMessageSendEvent>
    {
        public ValueTask InterceptAsync(BeforeMessageSendEvent evt, CancellationToken ct = default) { evt.Text = "REWRITTEN"; return ValueTask.CompletedTask; }
    }

    private static SessionView Live(string id = "s1")
    {
        var view = new SessionView(id);
        view.ApplySnapshot(new SessionSnapshot(new SessionInfo(id, "opencode", string.Empty, 0), [], 0));
        return view;
    }

    [Fact]
    public void A_client_plugin_can_veto_an_outgoing_message()
    {
        var host = new FakeHost();
        var bus = new EventBus();
        var vm = new SessionViewModel(host, Live(), ImmediateDispatcher.Instance, "OpenCode", eventBus: bus);
        bus.Intercept(new VetoSend());

        vm.PromptText = "hello";
        vm.SendCommand.Execute(null);

        Assert.Empty(host.Prompts); // never left the client
    }

    [Fact]
    public void A_client_plugin_can_rewrite_an_outgoing_message()
    {
        var host = new FakeHost();
        var bus = new EventBus();
        var vm = new SessionViewModel(host, Live(), ImmediateDispatcher.Instance, "OpenCode", eventBus: bus);
        bus.Intercept(new RewriteSend());

        vm.PromptText = "hello";
        vm.SendCommand.Execute(null);

        Assert.Equal("REWRITTEN", host.Prompts.Single());
    }

    [Fact]
    public void With_no_interceptors_the_message_is_sent_unchanged()
    {
        var host = new FakeHost();
        var vm = new SessionViewModel(host, Live(), ImmediateDispatcher.Instance, "OpenCode", eventBus: new EventBus());

        vm.PromptText = "hello";
        vm.SendCommand.Execute(null);

        Assert.Equal("hello", host.Prompts.Single());
    }
}
