using Agnes.Abstractions;
using Agnes.Client;
using Agnes.Client.Cache;
using Agnes.Protocol;

namespace Agnes.Client.Tests;

/// <summary>
/// The client-side event cache: a session's log is append-only, so what a client fetched once it can
/// keep, and the next open needs only what arrived since. These tests pin the range arithmetic in the
/// SQLite store and the replay policy over it — where the events come from, and when the cache is
/// distrusted and dropped.
/// </summary>
public sealed class SessionEventCacheTests : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
    private const string Host = "https://host.example:5081";
    private const string Session = "s1";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "agnes-cache-tests", Guid.NewGuid().ToString("N"));
    private SqliteSessionEventCache _cache = null!;

    public Task InitializeAsync()
    {
        _cache = SqliteSessionEventCache.Open(Path.Combine(_dir, "events.db"));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _cache.DisposeAsync();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static SessionEvent Event(long seq)
        => (seq % 3) switch
        {
            0 => new NoticeEvent($"notice {seq}") { Sequence = seq, Timestamp = T0.AddSeconds(seq) },
            1 => new MessageChunkEvent(MessageRole.Assistant, new TextContent($"chunk {seq}")) { Sequence = seq, Timestamp = T0.AddSeconds(seq) },
            _ => new TurnEndedEvent(StopReason.EndTurn) { Sequence = seq, Timestamp = T0.AddSeconds(seq) },
        };

    private static List<SessionEvent> Events(long from, long to)
        => Enumerable.Range(0, (int)(to - from + 1)).Select(i => Event(from + i)).ToList();

    private static SessionInfo Info(long head) => new(Session, "claude-code", "/work", head);

    /// <summary>A host whose log is <paramref name="log"/>: answers Subscribe(since) with everything after it.</summary>
    private sealed class FakeHost(List<SessionEvent> log)
    {
        public List<long> Asked { get; } = [];

        public Task<SessionSnapshot> Fetch(long since)
        {
            Asked.Add(since);
            var events = log.Where(e => e.Sequence > since).OrderBy(e => e.Sequence).ToList();
            var head = log.Count == 0 ? 0 : log.Max(e => e.Sequence);
            return Task.FromResult(new SessionSnapshot(Info(head), events, head));
        }
    }

    [Fact]
    public async Task Events_round_trip_through_the_store_as_their_own_types()
    {
        await _cache.WriteAsync(Host, Session, 0, Events(1, 6));

        var back = await _cache.ReadAsync(Host, Session, 0);

        Assert.Equal(6, back.Count);
        Assert.IsType<MessageChunkEvent>(back[0]);
        Assert.IsType<TurnEndedEvent>(back[1]);
        Assert.IsType<NoticeEvent>(back[2]);
        Assert.Equal("chunk 4", ((MessageChunkEvent)back[3]).Content is TextContent t ? t.Text : null);
        Assert.Equal(T0.AddSeconds(5), back[4].Timestamp);
        var range = await _cache.RangeAsync(Host, Session);
        Assert.Equal(new CachedRange(0, 6, nameof(NoticeEvent), T0.AddSeconds(6)), range);
    }

    [Fact]
    public async Task A_batch_that_touches_the_range_extends_it_and_one_that_covers_it_lowers_the_floor()
    {
        // A tail-first open: everything after 100.
        await _cache.WriteAsync(Host, Session, 100, Events(101, 120));
        Assert.Equal(new Span(100, 120), Range());

        // Live events, one at a time, each answering for the stretch after the previous head.
        await _cache.WriteAsync(Host, Session, 120, [Event(121)]);
        await _cache.WriteAsync(Host, Session, 121, [Event(122)]);
        Assert.Equal(new Span(100, 122), Range());

        // "Load everything": the host's answer to Subscribe(0) covers the lot, so the floor drops.
        await _cache.WriteAsync(Host, Session, 0, Events(1, 122));
        Assert.Equal(new Span(0, 122), Range());
        Assert.Equal(122, (await _cache.ReadAsync(Host, Session, 0)).Count);
        Assert.Equal(2, (await _cache.ReadAsync(Host, Session, 120)).Count);

        Span Range() => RangeAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task A_disjoint_batch_replaces_the_range_only_when_it_spans_more()
    {
        await _cache.WriteAsync(Host, Session, 0, Events(1, 50));

        // A lone event that lost touch (a write failed in between) must not evict fifty.
        await _cache.WriteAsync(Host, Session, 60, [Event(61)]);
        Assert.Equal(new Span(0, 50), await RangeAsync());
        Assert.Equal(50, (await _cache.ReadAsync(Host, Session, 0)).Count);

        // A bigger island wins, and the old rows go with the old range.
        await _cache.WriteAsync(Host, Session, 100, Events(101, 200));
        Assert.Equal(new Span(100, 200), await RangeAsync());
        Assert.DoesNotContain(await _cache.ReadAsync(Host, Session, 0), e => e.Sequence <= 50);
    }

    [Fact]
    public async Task Sessions_and_hosts_do_not_bleed_into_each_other()
    {
        await _cache.WriteAsync(Host, "a", 0, Events(1, 3));
        await _cache.WriteAsync(Host, "b", 0, Events(1, 5));
        await _cache.WriteAsync("https://other", "a", 0, Events(1, 7));

        Assert.Equal(3, (await _cache.ReadAsync(Host, "a", 0)).Count);
        Assert.Equal(5, (await _cache.ReadAsync(Host, "b", 0)).Count);
        Assert.Equal(7, (await _cache.ReadAsync("https://other", "a", 0)).Count);
        await _cache.ForgetAsync(Host, "a");
        Assert.Null(await _cache.RangeAsync(Host, "a"));
        Assert.Equal(5, (await _cache.ReadAsync(Host, "b", 0)).Count);
        Assert.Equal(2, (await _cache.ListAsync()).Count);
    }

    [Fact]
    public async Task The_second_open_reads_from_disk_and_asks_the_host_only_for_the_delta()
    {
        var log = Events(1, 1000);
        var host = new FakeHost(log);
        var replay = new CachedReplay(_cache, Host);
        var reports = new List<ReplayReport>();
        replay.Replayed += reports.Add;

        var first = await replay.FetchAsync(Session, 0, host.Fetch);
        Assert.Equal(1000, first.Events.Count);
        Assert.Equal([0L], host.Asked);

        log.AddRange(Events(1001, 1010));
        var second = await replay.FetchAsync(Session, 0, host.Fetch);

        // Asked for everything after 999, so the first event back is the cached head, 1000 — the probe.
        Assert.Equal([0L, 999L], host.Asked);
        Assert.Equal(1010, second.Events.Count);
        Assert.Equal(Enumerable.Range(1, 1010).Select(i => (long)i), second.Events.Select(e => e.Sequence));
        Assert.Equal(1010, second.HeadSequence);
        Assert.Equal(1000, reports[1].FromCache);
        Assert.Equal(10, reports[1].FromHost);
        Assert.Equal(new Span(0, 1010), await RangeAsync());
    }

    [Fact]
    public async Task A_host_whose_head_event_differs_is_a_different_log_and_the_cache_is_dropped()
    {
        var host = new FakeHost(Events(1, 20));
        var replay = new CachedReplay(_cache, Host);
        await replay.FetchAsync(Session, 0, host.Fetch);

        // The host's log was rebuilt: same sequences, different moments.
        var rebuilt = new FakeHost(Events(1, 25).Select(e => e with { Timestamp = e.Timestamp.AddDays(1) }).ToList());
        var reports = new List<ReplayReport>();
        replay.Replayed += reports.Add;
        var snapshot = await replay.FetchAsync(Session, 0, rebuilt.Fetch);

        Assert.Equal([19L, 0L], rebuilt.Asked);
        Assert.Equal(25, snapshot.Events.Count);
        Assert.All(snapshot.Events, e => Assert.Equal(T0.AddDays(1).AddSeconds(e.Sequence), e.Timestamp));
        Assert.True(reports.Single().Invalidated);
        // And what is now cached is the rebuilt log.
        var cached = await _cache.ReadAsync(Host, Session, 0);
        Assert.Equal(25, cached.Count);
        Assert.Equal(T0.AddDays(1).AddSeconds(1), cached[0].Timestamp);
    }

    [Fact]
    public async Task A_host_whose_head_fell_below_the_cached_one_is_refetched_from_scratch()
    {
        var host = new FakeHost(Events(1, 40));
        var replay = new CachedReplay(_cache, Host);
        await replay.FetchAsync(Session, 0, host.Fetch);

        var shorter = new FakeHost(Events(1, 30));
        var snapshot = await replay.FetchAsync(Session, 0, shorter.Fetch);

        Assert.Equal([39L, 0L], shorter.Asked);
        Assert.Equal(30, snapshot.Events.Count);
        Assert.Equal(new Span(0, 30), await RangeAsync());
    }

    [Fact]
    public async Task A_request_below_the_floor_or_at_the_head_goes_straight_to_the_host()
    {
        var host = new FakeHost(Events(1, 500));
        var replay = new CachedReplay(_cache, Host);

        // Tail-first: only the last hundred are cached.
        await replay.FetchAsync(Session, 400, host.Fetch);
        Assert.Equal(new Span(400, 500), await RangeAsync());

        // Everything: below the floor, so the host answers in full, and the floor drops to cover it.
        var all = await replay.FetchAsync(Session, 0, host.Fetch);
        Assert.Equal([400L, 0L], host.Asked);
        Assert.Equal(500, all.Events.Count);
        Assert.Equal(new Span(0, 500), await RangeAsync());

        // A reconnect from the head has nothing to replay; it is a plain delta fetch that extends the range.
        host.Asked.Clear();
        var log = Events(501, 503);
        var later = new FakeHost(Events(1, 503));
        var delta = await replay.FetchAsync(Session, 500, later.Fetch);
        Assert.Equal([500L], later.Asked);
        Assert.Equal(log.Select(e => e.Sequence), delta.Events.Select(e => e.Sequence));
        Assert.Equal(new Span(0, 503), await RangeAsync());
    }

    [Fact]
    public async Task Live_events_applied_to_a_view_land_in_the_cache_with_their_predecessor()
    {
        var host = new FakeHost(Events(1, 10));
        var replay = new CachedReplay(_cache, Host);
        var view = new SessionView(Session);
        var recorded = new List<Span>();
        view.LiveApplied += (previous, e) =>
        {
            recorded.Add(new Span(previous, e.Sequence));
            replay.RecordLive(Session, previous, e);
        };

        // One live event arrives before the snapshot lands; it is buffered and applied after it.
        view.Apply(Event(11));
        view.ApplySnapshot(await replay.FetchAsync(Session, 0, host.Fetch));
        view.Apply(Event(12));
        view.Apply(Event(12)); // a duplicate is not live twice

        Assert.Equal([new Span(10, 11), new Span(11, 12)], recorded);
        // The writes are fire-and-forget; a read queued behind them sees them.
        Assert.Equal(new Span(0, 12), await RangeAsync());
        Assert.Equal(12, (await _cache.ReadAsync(Host, Session, 0)).Count);
    }

    [Fact]
    public async Task A_broken_cache_never_stops_a_session_opening()
    {
        var host = new FakeHost(Events(1, 5));
        var replay = new CachedReplay(new BrokenCache(), Host);

        var snapshot = await replay.FetchAsync(Session, 0, host.Fetch);

        Assert.Equal(5, snapshot.Events.Count);
        Assert.Equal([0L], host.Asked);
    }

    private sealed record Span(long Floor, long Head);

    private async Task<Span> RangeAsync()
    {
        var r = await _cache.RangeAsync(Host, Session);
        return new Span(r!.Floor, r.Head);
    }

    private sealed class BrokenCache : ISessionEventCache
    {
        public Task<CachedRange?> RangeAsync(string hostId, string sessionId, CancellationToken cancellationToken = default)
            => throw new IOException("disk on fire");

        public Task<IReadOnlyList<SessionEvent>> ReadAsync(string hostId, string sessionId, long since, CancellationToken cancellationToken = default)
            => throw new IOException("disk on fire");

        public Task WriteAsync(string hostId, string sessionId, long since, IReadOnlyList<SessionEvent> events, CancellationToken cancellationToken = default)
            => throw new IOException("disk on fire");

        public Task ForgetAsync(string hostId, string sessionId, CancellationToken cancellationToken = default)
            => throw new IOException("disk on fire");
    }
}
