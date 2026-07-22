using Agnes.Abstractions;
using Agnes.Host.Events;

namespace Agnes.Host.Tests;

/// <summary>The event-store backend is chosen from an <see cref="IPluginRegistry{TProvider}"/> of built-in
/// providers (AC13) rather than a hardcoded branch.</summary>
public class EventStoreProviderTests
{
    [Fact]
    public void In_memory_provider_creates_an_in_memory_store()
    {
        var provider = new InMemoryEventStoreProvider();
        Assert.Equal("in-memory", provider.Name);
        Assert.IsType<InMemoryEventStore>(provider.Create());
    }

    [Fact]
    public void Sqlite_provider_creates_a_sqlite_store()
    {
        var path = Path.Combine(Path.GetTempPath(), "agnes-esp-" + Guid.NewGuid().ToString("n") + ".db");
        try
        {
            var provider = new SqliteEventStoreProvider(path);
            Assert.Equal("sqlite", provider.Name);
            using var store = Assert.IsType<SqliteEventStore>(provider.Create());
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Registry_selects_a_backend_by_name()
    {
        IReadOnlyList<IEventStoreProvider> providers = [new InMemoryEventStoreProvider(), new SqliteEventStoreProvider("ignored.db")];
        var registry = new PluginRegistry<IEventStoreProvider>(providers, p => p.Name);

        Assert.Equal("in-memory", registry.Find("in-memory")!.Name);
        Assert.Equal("sqlite", registry.Find("sqlite")!.Name);
        Assert.Null(registry.Find("nope"));
        Assert.Equal(2, registry.All.Count);
    }
}
