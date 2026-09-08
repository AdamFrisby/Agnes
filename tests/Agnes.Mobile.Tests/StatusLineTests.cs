using Agnes.App.Mobile.Services;
using Agnes.App.Mobile.ViewModels;
using Agnes.Client;
using System.Text.Json;
using Agnes.Ui.Core;

namespace Agnes.Mobile.Tests;

/// <summary>
/// The agent's one-line status, as the sessions list words it.
///
/// The wording is the feature: "12m" beside a running agent reads as progress, and the whole reason the
/// line exists is to tell you when there hasn't been any.
/// </summary>
public sealed class StatusLineTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    private static AgentStatus Reported(int minutesAgo)
        => new("Rewriting the session store's cursor handling", Now.AddMinutes(-minutesAgo));

    [Fact]
    public void A_fresh_status_reads_as_a_plain_age()
    {
        Assert.Equal("4m", StatusLine.Age(Reported(4), working: true, Now));
        Assert.Equal("now", StatusLine.Age(Reported(0), working: true, Now));
    }

    [Fact]
    public void A_working_agent_that_has_gone_quiet_says_so_in_words()
    {
        // The point of the feature: a running session whose agent stopped narrating is not the same as a
        // running session, and an age alone cannot carry that difference.
        Assert.True(StatusLine.IsStale(Reported(12), working: true, Now));
        Assert.Equal("no update for 12 min", StatusLine.Age(Reported(12), working: true, Now));
    }

    [Fact]
    public void A_long_silence_collapses_to_hours()
    {
        Assert.Equal("no update for 3 h", StatusLine.Age(Reported(190), working: true, Now));
    }

    [Fact]
    public void An_idle_session_is_never_stale()
    {
        // Nothing is overdue on a session that isn't running — the status is simply the last thing that
        // happened, and "no update for 3 h" would read as a fault where there is none.
        Assert.False(StatusLine.IsStale(Reported(180), working: false, Now));
        Assert.Equal("3h", StatusLine.Age(Reported(180), working: false, Now));
    }

    [Fact]
    public void Silence_is_not_a_status()
    {
        Assert.False(AgentStatus.None.HasLine);
        Assert.False(new AgentStatus("   ", Now).HasLine);
        Assert.Equal(string.Empty, StatusLine.Age(AgentStatus.None, working: true, Now));
    }

    [Fact]
    public void The_saved_pointer_carries_the_status_across_a_relaunch()
    {
        // Why it is persisted at all: the list is opened cold, on a phone that has been in a pocket, and
        // the status is the one line that has to be right in that first second — before any host answers.
        //
        // Serialized directly rather than through SessionRegistry, whose store is a process-global
        // directory: two test classes writing it at once is a race, and what is under test here is the
        // record's shape, not the file.
        var at = Now.AddMinutes(-3);
        var saved = new SavedSession("Host", "https://host.example", "token", "s1", "claude-code", "Agnes",
            "/home/you/projects/agnes", LatestStatus: "Bisecting the flaky reconnect test", LatestStatusAt: at);

        var json = JsonSerializer.Serialize<IReadOnlyList<SavedSession>>([saved], StoreOptions);
        var restored = Assert.Single(
            JsonSerializer.Deserialize<List<SavedSession>>(json, StoreOptions) ?? []);

        Assert.Equal("Bisecting the flaky reconnect test", restored.LatestStatus);
        Assert.Equal(at, restored.LatestStatusAt);
    }

    /// <summary>The options <see cref="JsonStore"/> writes device state with.</summary>
    private static readonly JsonSerializerOptions StoreOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [Fact]
    public void A_card_prefers_what_it_remembered_to_nothing_at_all()
    {
        var saved = new SavedSession("Host", "sim://demo", "", "s1", "claude-code", "Agnes",
            "/home/you/projects/agnes",
            LatestStatus: "Waiting on the sandbox image to bake", LatestStatusAt: DateTimeOffset.Now.AddMinutes(-2));
        var hosts = new HostBook(new MobileConnector(), ImmediateDispatcher.Instance);
        var entry = new SessionEntry(saved, hosts.Links[0]);

        Assert.True(entry.HasStatus);
        Assert.Equal("Waiting on the sandbox image to bake", entry.LatestStatus);
        Assert.Equal("2m", entry.StatusAge);
    }

    [Fact]
    public void A_card_whose_agent_never_reported_has_no_status_row()
    {
        var saved = new SavedSession("Host", "sim://demo", "", "s1", "claude-code", "Agnes",
            "/home/you/projects/agnes");
        var hosts = new HostBook(new MobileConnector(), ImmediateDispatcher.Instance);
        var entry = new SessionEntry(saved, hosts.Links[0]);

        Assert.False(entry.HasStatus);
        Assert.Null(entry.LatestStatus);
        Assert.Equal(string.Empty, entry.StatusAge);
    }
}
