using Agnes.Abstractions;
using Agnes.App.Mobile.ViewModels;
using Agnes.Client;
using Agnes.Protocol;

namespace Agnes.Mobile.Tests;

/// <summary>
/// A phone takes the tail of a session's log and reaches back on request. The whole log of one live
/// session was 338,000 events; the tablet spent minutes downloading it and never finished, on every
/// card, because every card subscribed from zero.
/// </summary>
public class TailFirstTests
{
    private static SessionEvent Ev(long seq)
        => new MessageChunkEvent(MessageRole.Assistant, new TextContent($"e{seq}")) { Sequence = seq, Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(seq) };

    [Fact]
    public void The_tail_window_is_the_last_few_hundred_events_or_everything_for_a_short_log()
    {
        Assert.Equal(0, SessionsViewModel.TailSince(0));
        Assert.Equal(0, SessionsViewModel.TailSince(SessionsViewModel.TailWindow));
        Assert.Equal(338_146 - SessionsViewModel.TailWindow, SessionsViewModel.TailSince(338_146));
    }

    [Fact]
    public void Older_events_go_in_front_and_the_view_knows_where_it_now_starts()
    {
        var view = new SessionView("s");
        view.ApplySnapshot(new SessionSnapshot(null!, [Ev(900), Ev(901), Ev(902)], 902));
        Assert.Equal(900, view.FirstSequence);
        Assert.Equal(902, view.LastSequence);

        var raised = 0;
        view.HistoryPrepended += () => raised++;
        // A re-subscribe from zero answers with everything, including what is already here.
        var added = view.Prepend([Ev(1), Ev(2), Ev(900), Ev(901), Ev(2)]);

        Assert.Equal(2, added);
        Assert.Equal(1, raised);
        Assert.Equal([1L, 2L, 900L, 901L, 902L], view.Events.Select(e => e.Sequence));
        Assert.Equal(1, view.FirstSequence);
        Assert.Equal(902, view.LastSequence);
    }

    [Fact]
    public void A_listing_names_the_session_by_its_folder_not_its_path()
    {
        SessionSummary Summary(string? title, string dir) => new("id-1", "claude-code-native", dir, title, SessionRunState.Idle, 42);
        Assert.Equal("dawn2", SessionsViewModel.TitleFor(Summary(null, "/home/adam/Projects/dawn2")));
        Assert.Equal("dawn2", SessionsViewModel.TitleFor(Summary("", "/home/adam/Projects/dawn2/")));
        Assert.Equal("Fix the broker", SessionsViewModel.TitleFor(Summary("Fix the broker", "/home/adam/Projects/dawn2")));
        Assert.Equal("id-1", SessionsViewModel.TitleFor(Summary(null, "")));
    }
}
