using Microsoft.Extensions.Configuration;

namespace Agnes.Host.Events;

/// <summary>
/// Resolves the event-store deployment topology from configuration (ops/03). Kept as a pure helper — a function
/// of <see cref="IConfiguration"/> — so the store choice is unit-testable without standing up the host, and so
/// there is a single source of truth shared by <c>Program.cs</c> and the tests.
/// </summary>
/// <remarks>
/// Store choice IS topology choice: <c>sqlite</c>/<c>in-memory</c> for a single-node host, the optional
/// <c>postgres</c> backend for a scaled/shared-database deployment. The selection is per-store, leaving a clean
/// seam for other durable stores (e.g. the memory index) to gain a Postgres backing the same way later.
/// </remarks>
internal static class EventStoreSelection
{
    /// <summary>
    /// The selected store name. Precedence: the neutral <c>Agnes:Storage:EventStore</c>, then the legacy
    /// <c>Agnes:EventStore:Provider</c>, then the implicit default (<c>sqlite</c> when a database path is
    /// configured, else <c>in-memory</c>). Default behavior is unchanged from before ops/03.
    /// </summary>
    public static string ResolveName(IConfiguration configuration)
    {
        var databasePath = configuration["Agnes:Database"];
        return configuration["Agnes:Storage:EventStore"]
            ?? configuration["Agnes:EventStore:Provider"]
            ?? (string.IsNullOrWhiteSpace(databasePath) ? "in-memory" : "sqlite");
    }

    /// <summary>
    /// The set of built-in providers to register for the given configuration. In-memory is always available;
    /// SQLite is added when a database path is set; Postgres is added only when a connection string is present,
    /// so the Npgsql driver is never touched by a default deployment.
    /// </summary>
    public static IReadOnlyList<IEventStoreProvider> BuildProviders(IConfiguration configuration)
    {
        var providers = new List<IEventStoreProvider> { new InMemoryEventStoreProvider() };

        var databasePath = configuration["Agnes:Database"];
        if (!string.IsNullOrWhiteSpace(databasePath))
        {
            providers.Add(new SqliteEventStoreProvider(databasePath));
        }

        var postgresConnectionString = configuration["Agnes:Storage:Postgres:ConnectionString"];
        if (!string.IsNullOrWhiteSpace(postgresConnectionString))
        {
            providers.Add(new PostgresEventStoreProvider(postgresConnectionString));
        }

        return providers;
    }
}
