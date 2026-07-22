using Agnes.Ui.Core.Plugins;

namespace Agnes.Ui.Core.Tests;

/// <summary>Client plugins can contribute into UI slots, override conversation-item rendering, and register
/// custom screens (see .ideas/00d-event-spine-and-ui-extensibility.md, AC6-AC8).</summary>
public class ClientUiExtensionTests
{
    private sealed record Contribution(string SlotId, int Order, object Content) : IUiContribution
    {
        public object CreateContent() => Content;
    }

    private sealed record Renderer(string ItemKind, int Order, object? View) : IConversationItemRenderer
    {
        public object? CreateView(ConversationItemContext context) => View;
    }

    private sealed record Screen(string ScreenId, string Title, string? Icon, object Vm) : ICustomScreenProvider
    {
        public object CreateViewModel() => Vm;
    }

    private sealed class Module(Action<ClientPluginCollector> register) : IClientPluginModule
    {
        public void Register(ClientPluginCollector collector) => register(collector);
    }

    [Fact]
    public void Slot_contributions_are_grouped_by_slot_in_order()
    {
        var plugins = ClientPluginHost.FromModules(
        [
            new Module(c =>
            {
                c.AddUiContribution(new Contribution(UiSlots.ComposerActions, Order: 2, "b"));
                c.AddUiContribution(new Contribution(UiSlots.ComposerActions, Order: 1, "a"));
                c.AddUiContribution(new Contribution(UiSlots.ConversationBanner, Order: 0, "banner"));
            }),
        ]);

        var composer = plugins.SlotContributions(UiSlots.ComposerActions);
        Assert.Equal(["a", "b"], composer.Select(c => c.CreateContent()));               // ordered
        Assert.Single(plugins.SlotContributions(UiSlots.ConversationBanner));
        Assert.Empty(plugins.SlotContributions("no.such.slot"));                          // unknown slot → empty, not an error
    }

    [Fact]
    public void The_lowest_order_renderer_wins_for_a_kind_and_others_fall_back()
    {
        var plugins = ClientPluginHost.FromModules(
        [
            new Module(c =>
            {
                c.AddConversationRenderer(new Renderer("Bash", Order: 5, "low-priority"));
                c.AddConversationRenderer(new Renderer("Bash", Order: 1, "high-priority"));
            }),
        ]);

        var bash = plugins.RendererFor("Bash");
        Assert.NotNull(bash);
        Assert.Equal("high-priority", bash!.CreateView(new ConversationItemContext("Bash", new object())));
        Assert.Null(plugins.RendererFor("Diff")); // no plugin renderer for this kind → built-in
    }

    [Fact]
    public void Custom_screens_are_listed_for_the_head_to_open()
    {
        var vm = new object();
        var plugins = ClientPluginHost.FromModules(
        [
            new Module(c => c.AddCustomScreen(new Screen("myplugin.dashboard", "Dashboard", "📊", vm))),
        ]);

        var screen = Assert.Single(plugins.CustomScreens);
        Assert.Equal("myplugin.dashboard", screen.ScreenId);
        Assert.Equal("Dashboard", screen.Title);
        Assert.Same(vm, screen.CreateViewModel());
    }

    [Fact]
    public void An_empty_set_has_no_ui_extensions()
    {
        Assert.Empty(ClientPluginSet.Empty.Contributions);
        Assert.Empty(ClientPluginSet.Empty.CustomScreens);
        Assert.Null(ClientPluginSet.Empty.RendererFor("Bash"));
    }
}
