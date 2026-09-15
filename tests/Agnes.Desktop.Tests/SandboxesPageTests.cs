using Agnes.Abstractions;
using Agnes.App.Desktop.ViewModels;
using Agnes.Protocol;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.Desktop.Tests;

/// <summary>
/// What the Sandboxes page says about a list of VMs, and how a row shortens itself to one line — the
/// parts of the redesign that are logic rather than markup.
/// </summary>
public class SandboxesPageTests
{
    private static SandboxRecordDto Record(string title, string state, string dir, DateTimeOffset created, DateTimeOffset used)
        => new("s-" + Guid.NewGuid().ToString("n"), "agnes-" + title, "incus", "opencode", dir, title, title, state, created, used, Live: state == "running");

    [Fact]
    public void The_summary_counts_by_state_in_words()
    {
        var now = DateTimeOffset.UtcNow;
        var list = new[]
        {
            Record("a", "running", "/w", now, now),
            Record("b", "stopped", "/w", now, now),
            Record("c", "stopped", "/w", now, now),
            Record("d", "paused", "/w", now, now),
        };

        Assert.Equal("4 sandboxes on AIPC25 · 1 running · 1 paused · 2 stopped", MainWindowViewModel.SandboxesSummary(list, "AIPC25"));
        Assert.Equal("One sandbox on AIPC25 · 1 running", MainWindowViewModel.SandboxesSummary([list[0]], "AIPC25"));
        Assert.StartsWith("No sandboxes on AIPC25 yet.", MainWindowViewModel.SandboxesSummary([], "AIPC25"));
    }

    [Fact]
    public void A_row_shortens_its_directory_and_ages_itself()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal("~/Projects/dawn2", SandboxRowVm.Shorten(Path.Combine(home, "Projects", "dawn2")));
        Assert.Equal("…/status-proof/work", SandboxRowVm.Shorten("/tmp/claude-1000/some-long/scratchpad/status-proof/work"));
        Assert.Equal("/tmp/work", SandboxRowVm.Shorten("/tmp/work"));

        Assert.Equal("just now", SandboxRowVm.Ago(TimeSpan.FromSeconds(30)));
        Assert.Equal("16 h ago", SandboxRowVm.Ago(TimeSpan.FromHours(16.5)));
        Assert.Equal("21 d ago", SandboxRowVm.Ago(TimeSpan.FromDays(21.2)));

        var now = DateTimeOffset.UtcNow;
        var row = new SandboxRowVm(Record("Dawn2", "running", "/w", now.AddDays(-24), now.AddDays(-19)));
        Assert.True(row.IsRunning);
        Assert.Equal("created 24 d ago · used 19 d ago", row.Age);
        Assert.Equal("Open", row.OpenLabel);
        row.IsOpenHere = true;
        Assert.Equal("Go to tab", row.OpenLabel);
    }

    [Fact]
    public void A_collaborator_row_turns_the_answer_into_a_fact_the_tag_can_colour()
    {
        var row = new CollaboratorRowVm(new Collaborator("octocat", null, DateTimeOffset.UtcNow, CollaboratorSource.Explicit));
        Assert.False(row.IsKnownEligible);
        Assert.False(row.IsKnownIneligible);

        row.IsEligible = true;
        Assert.True(row.IsKnownEligible);

        row.IsEligible = false;
        Assert.True(row.IsKnownIneligible);
        Assert.False(row.IsKnownEligible);

        row.IsEligible = null;
        Assert.False(row.IsKnownIneligible);
    }
}
