using Agnes.Abstractions;

namespace Agnes.Host.Events;

/// <summary>
/// A selectable event-store backend, exposed as a plugin-point so the Sqlite-vs-in-memory choice flows
/// through the same <see cref="IPluginRegistry{TProvider}"/> machinery as agents and sandboxes, rather
/// than a hardcoded <c>if</c> (AC13). Built-in providers are <see cref="SqliteEventStoreProvider"/> and
/// <see cref="InMemoryEventStoreProvider"/>; a host selects one by name via <c>Agnes:EventStore:Provider</c>.
/// Selection happens once at startup (the event log can't be hot-swapped under a running host), so this
/// registry is populated from built-ins only — unlike the runtime-pluggable points, it has no merger.
/// </summary>
public interface IEventStoreProvider
{
    /// <summary>Stable id used to select this backend (e.g. <c>sqlite</c>, <c>in-memory</c>).</summary>
    string Name { get; }

    /// <summary>Constructs the backing store. Called once, at host startup.</summary>
    IEventStore Create();
}

/// <summary>Built-in provider for the durable SQLite event store.</summary>
public sealed class SqliteEventStoreProvider(string databasePath) : IEventStoreProvider
{
    public string Name => "sqlite";

    public IEventStore Create() => new SqliteEventStore(databasePath);
}

/// <summary>Built-in provider for the in-memory event store (default when no database path is configured;
/// also used by tests).</summary>
public sealed class InMemoryEventStoreProvider : IEventStoreProvider
{
    public string Name => "in-memory";

    public IEventStore Create() => new InMemoryEventStore();
}
