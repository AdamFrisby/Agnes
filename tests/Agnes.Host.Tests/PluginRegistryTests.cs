using Agnes.Abstractions;

namespace Agnes.Host.Tests;

public class PluginRegistryTests
{
    [Fact]
    public void All_lists_every_registered_provider_in_registration_order()
    {
        var a = new ScriptedAgentAdapter("a");
        var b = new ScriptedAgentAdapter("b");
        var registry = new PluginRegistry<IAgentAdapter>([a, b], p => p.Descriptor.Id);

        Assert.Equal([a, b], registry.All);
    }

    [Fact]
    public void Find_returns_the_provider_registered_under_that_id()
    {
        var a = new ScriptedAgentAdapter("a");
        var b = new ScriptedAgentAdapter("b");
        var registry = new PluginRegistry<IAgentAdapter>([a, b], p => p.Descriptor.Id);

        Assert.Same(b, registry.Find("b"));
    }

    [Fact]
    public void Find_returns_null_for_an_unknown_id()
    {
        var registry = new PluginRegistry<IAgentAdapter>([new ScriptedAgentAdapter("a")], p => p.Descriptor.Id);

        Assert.Null(registry.Find("missing"));
    }

    [Fact]
    public void Empty_registry_has_no_entries_and_never_finds_anything()
    {
        var registry = new PluginRegistry<IAgentAdapter>([], p => p.Descriptor.Id);

        Assert.Empty(registry.All);
        Assert.Null(registry.Find("anything"));
    }

    [Fact]
    public void A_later_provider_with_a_duplicate_id_wins_the_lookup_but_both_still_appear_in_All()
    {
        var first = new ScriptedAgentAdapter("dup");
        var second = new ScriptedAgentAdapter("dup");
        var registry = new PluginRegistry<IAgentAdapter>([first, second], p => p.Descriptor.Id);

        Assert.Equal([first, second], registry.All);
        Assert.Same(second, registry.Find("dup"));
    }
}
