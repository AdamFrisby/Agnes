using Agnes.Abstractions;
using Agnes.Client;
using Agnes.Protocol;

namespace Agnes.Client.Tests;

/// <summary>
/// A session view that is backed by a durable cache need not keep the whole log in memory: once the
/// transcript has been built from it, the head can go, and the view stays a bounded tail from then on.
/// A view without a cache keeps everything, as it always did.
/// </summary>
public class SessionViewResidencyTests
{
    private static SessionEvent Event(long seq) => new NoticeEvent($"n{seq}") { Sequence = seq };

    private static SessionView Loaded(int count, bool durable)
    {
        var view = new SessionView("s1") { HistoryIsDurable = durable };
        view.ApplySnapshot(new SessionSnapshot(new SessionInfo("s1", "opencode", string.Empty, count),
            Enumerable.Range(1, count).Select(i => Event(i)).ToList(), count));
        return view;
    }

    [Fact]
    public void A_durable_view_lets_its_history_go_and_keeps_a_tail()
    {
        var view = Loaded(10_000, durable: true);

        Assert.Equal(9_500, view.TrimTo());

        Assert.Equal(500, view.Events.Count);
        Assert.Equal(9_501, view.HeldFrom);
        Assert.Equal(1, view.FirstSequence);   // what was loaded, still the answer to "have I the start?"
        Assert.Equal(10_000, view.LastSequence); // the resume cursor is untouched
    }

    [Fact]
    public void A_trimmed_view_stays_bounded_as_events_stream_in()
    {
        var view = Loaded(1_000, durable: true);
        view.TrimTo();
        var seen = new List<long>();
        view.EventAppended += e => seen.Add(e.Sequence);

        for (var i = 1_001; i <= 3_000; i++)
        {
            view.Apply(Event(i));
        }

        Assert.Equal(2_000, seen.Count);                       // every live event still reaches the consumer
        Assert.InRange(view.Events.Count, 500, 1_000);         // but the list never grows back to the log
        Assert.Equal(3_000, view.LastSequence);
        Assert.Equal(3_000, view.Events[^1].Sequence);
    }

    [Fact]
    public void A_view_without_a_cache_keeps_everything()
    {
        var view = Loaded(2_000, durable: false);

        Assert.Equal(0, view.TrimTo());
        Assert.Equal(2_000, view.Events.Count);

        view.Apply(Event(2_001));
        Assert.Equal(2_001, view.Events.Count);
    }
}
