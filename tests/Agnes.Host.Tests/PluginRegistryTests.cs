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
    public void A_later_provider_with_a_duplicate_id_wins_both_All_and_Find()
    {
        var first = new ScriptedAgentAdapter("dup");
        var second = new ScriptedAgentAdapter("dup");
        var registry = new PluginRegistry<IAgentAdapter>([first, second], p => p.Descriptor.Id);

        // All and Find agree — a mutable, id-keyed registry never shows an entry Find can't return.
        Assert.Equal([second], registry.All);
        Assert.Same(second, registry.Find("dup"));
    }

    [Fact]
    public void Register_adds_a_new_provider_visible_in_All_and_Find()
    {
        var a = new ScriptedAgentAdapter("a");
        var registry = new PluginRegistry<IAgentAdapter>([a], p => p.Descriptor.Id);

        var plugin = new ScriptedAgentAdapter("from-plugin");
        registry.Register("from-plugin", plugin);

        Assert.Equal([a, plugin], registry.All);
        Assert.Same(plugin, registry.Find("from-plugin"));
    }

    [Fact]
    public void Unregister_removes_a_provider_from_All_and_Find()
    {
        var a = new ScriptedAgentAdapter("a");
        var plugin = new ScriptedAgentAdapter("from-plugin");
        var registry = new PluginRegistry<IAgentAdapter>([a], p => p.Descriptor.Id);
        registry.Register("from-plugin", plugin);

        registry.Unregister("from-plugin");

        Assert.Equal([a], registry.All);
        Assert.Null(registry.Find("from-plugin"));
    }

    [Fact]
    public void Unregister_of_an_unknown_id_is_a_harmless_no_op()
    {
        var registry = new PluginRegistry<IAgentAdapter>([new ScriptedAgentAdapter("a")], p => p.Descriptor.Id);

        registry.Unregister("missing");

        Assert.Single(registry.All);
    }
}
