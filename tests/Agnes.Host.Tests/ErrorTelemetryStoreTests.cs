using Agnes.Abstractions;
using Agnes.Host.Ops;

namespace Agnes.Host.Tests;

public class ErrorTelemetryStoreTests
{
    [Fact]
    public async Task Observes_agent_error_events_from_the_spine()
    {
        var store = new ErrorTelemetryStore(10);
        await store.ObserveAsync(new AgentErrorEvent("boom"));

        var entry = Assert.Single(store.Snapshot());
        Assert.Equal("agent", entry.Source);
        Assert.Equal("boom", entry.Message);
    }

    [Fact]
    public void Records_process_level_errors()
    {
        var store = new ErrorTelemetryStore(10);
        store.Record("unhandled", "kaboom");

        var entry = Assert.Single(store.Snapshot());
        Assert.Equal("unhandled", entry.Source);
        Assert.Equal("kaboom", entry.Message);
    }

    [Fact]
    public void Is_bounded_dropping_the_oldest_entries()
    {
        var store = new ErrorTelemetryStore(3);
        for (var i = 0; i < 6; i++)
        {
            store.Record("test", $"error-{i}");
        }

        var entries = store.Snapshot();
        Assert.Equal(3, entries.Count);
        // Oldest (error-0..2) dropped; the last three, oldest-first, remain.
        Assert.Equal(["error-3", "error-4", "error-5"], entries.Select(e => e.Message));
    }
}
