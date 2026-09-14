using Agnes.Abstractions;
using Agnes.Client;
using Agnes.Client.Cache;
using Microsoft.AspNetCore.Http.Connections;

namespace Agnes.Integration.Tests;

/// <summary>
/// The client-side event cache over the real SignalR wire: a second connection with the same cache file
/// replays the session from disk and asks the host only for what it has not seen, and what it replays is
/// indistinguishable from what the wire carried — same types, same payloads, same order.
/// </summary>
public class EventCacheEndToEndTests : IClassFixture<EndToEndTests.HostFactory>
{
    private const string Token = "test-token";
    private readonly EndToEndTests.HostFactory _factory;

    public EventCacheEndToEndTests(EndToEndTests.HostFactory factory) => _factory = factory;

    private Action<Microsoft.AspNetCore.Http.Connections.Client.HttpConnectionOptions> UseTestServer()
        => options =>
        {
            options.Transports = HttpTransportType.LongPolling;
            options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
        };

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(20, cts.Token);
        }
    }

    [Fact]
    public async Task A_second_connection_replays_from_the_cache_and_fetches_only_the_delta()
    {
        _factory.Adapter.Session.OnPrompt = (_, s) =>
        {
            s.Emit(new MessageChunkEvent(MessageRole.Assistant, new TextContent("Working on it")));
            s.Emit(new ToolCallEvent("tc1", "Run tests", ToolKind.Execute, ToolCallStatus.Completed,
                [new TextContent("all green"), new DiffContent("/work/tests.cs", "old", "new")]));
            s.Emit(new TurnEndedEvent(StopReason.EndTurn));
            return Task.FromResult(StopReason.EndTurn);
        };

        var cachePath = Path.Combine(Path.GetTempPath(), $"agnes-cache-it-{Guid.NewGuid():n}", "events.db");
        string sessionId;
        IReadOnlyList<SessionEvent> firstSeen;

        // First client: everything comes from the host and lands in the cache as it streams.
        await using (var cache = SqliteSessionEventCache.Open(cachePath))
        await using (var client = new AgnesClient(cache))
        {
            var host = await client.AddHostAsync("http://localhost", Token, UseTestServer());
            var session = await host.OpenSessionAsync("scripted", ".");
            sessionId = session.SessionId;
            var view = await host.SubscribeAsync(sessionId);
            await host.PromptAsync(sessionId, [new TextContent("hello")]);
            await WaitForAsync(() => view.Events.OfType<TurnEndedEvent>().Any());
            firstSeen = view.Events;

            // Live events were recorded one by one; the cache's head is the view's.
            await WaitForAsync(() => cache.RangeAsync(host.HostId, sessionId).GetAwaiter().GetResult()?.Head == view.LastSequence);
        }

        // Second client, same cache file: the log is read from disk, the host is asked from the cached head.
        await using (var cache = SqliteSessionEventCache.Open(cachePath))
        await using (var client = new AgnesClient(cache))
        {
            var host = await client.AddHostAsync("http://localhost", Token, UseTestServer());
            ReplayReport? report = null;
            host.Replayed += r => report = r;

            var view = await host.SubscribeAsync(sessionId);

            Assert.NotNull(report);
            Assert.Equal(firstSeen.Count, report!.FromCache);
            Assert.Equal(0, report.FromHost);
            Assert.False(report.Invalidated);
            Assert.Equal(firstSeen.Select(e => e.Sequence), view.Events.Select(e => e.Sequence));
            Assert.Equal(firstSeen.Select(e => e.GetType()), view.Events.Select(e => e.GetType()));

            // A structured event survives the round trip with its payload intact.
            var tool = view.Events.OfType<ToolCallEvent>().Single();
            var original = firstSeen.OfType<ToolCallEvent>().Single();
            Assert.Equal((original.ToolCallId, original.Title, original.Kind, original.Status, original.Timestamp),
                (tool.ToolCallId, tool.Title, tool.Kind, tool.Status, tool.Timestamp));
            Assert.Equal(original.Content, tool.Content);

            // And the connection is live: a new turn streams in on top of the replayed history.
            await host.PromptAsync(sessionId, [new TextContent("again")]);
            await WaitForAsync(() => view.Events.OfType<TurnEndedEvent>().Count() == 2);
        }

        Directory.Delete(Path.GetDirectoryName(cachePath)!, recursive: true);
    }
}
