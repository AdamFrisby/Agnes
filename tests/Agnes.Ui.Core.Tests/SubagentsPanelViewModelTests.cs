using Agnes.Abstractions;
using Agnes.Ui.Core.Transcript;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.Ui.Core.Tests;

public class SubagentsPanelViewModelTests
{
    [Fact]
    public void Lead_participant_is_always_present_before_any_subagent()
    {
        var roster = new SubagentsPanelViewModel("Claude");

        var lead = Assert.Single(roster.Participants);
        Assert.Equal(ParticipantKind.Lead, lead.Kind);
        Assert.Equal("Claude", lead.Name);
        Assert.True(lead.IsLead);
        Assert.True(lead.Controllable);      // the lead is the composer's normal send target
        Assert.False(lead.IsObserveOnly);
        Assert.False(roster.HasSubagents);
    }

    [Fact]
    public void Two_distinct_subagents_yield_three_participants_and_a_repeated_id_does_not_double_add()
    {
        var roster = new SubagentsPanelViewModel("Claude");

        roster.Add(new SubagentStartedEvent("sub-1", "reviewer"));
        roster.Add(new SubagentStartedEvent("sub-2", "explorer"));
        roster.Add(new SubagentStartedEvent("sub-1", "reviewer"));   // same id again — must be ignored

        Assert.Equal(3, roster.Participants.Count);                  // lead + 2 distinct subagents
        Assert.True(roster.HasSubagents);
        Assert.Equal(ParticipantKind.Lead, roster.Participants[0].Kind);
        Assert.Equal(["reviewer", "explorer"], roster.Participants
            .Where(p => p.Kind == ParticipantKind.Subagent)
            .Select(p => p.Name));
    }

    [Fact]
    public void Subagents_are_observe_only_with_a_disabled_control_and_explaining_tooltip()
    {
        var roster = new SubagentsPanelViewModel("Claude");

        roster.Add(new SubagentStartedEvent("sub-1", "reviewer"));

        var sub = Assert.Single(roster.Participants, p => p.Kind == ParticipantKind.Subagent);
        Assert.False(sub.Controllable);       // no adapter exposes addressed send/stop yet (Tier 1)
        Assert.False(sub.CanControl);          // the route/stop affordance is disabled
        Assert.True(sub.IsObserveOnly);
        Assert.Contains("Observe-only", sub.ControlTooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void Roster_reflects_subagents_applied_through_the_transcript_builder()
    {
        // The roster is fed by TranscriptBuilder.SubagentAdded in the real VM; drive that same pipeline here.
        var roster = new SubagentsPanelViewModel("Claude");
        var builder = new TranscriptBuilder();
        builder.SubagentAdded += roster.Add;

        builder.Apply(new SubagentStartedEvent("sub-1", "reviewer"));
        // Claude's "Agent" tool also registers a subagent (name from the tool input).
        builder.Apply(new ToolCallEvent("tc-1", "Agent", ToolKind.Other, ToolCallStatus.InProgress,
            [new TextContent("{\"description\":\"Investigate the bug\"}")]));

        Assert.Equal(3, roster.Participants.Count);   // lead + the two reported subagents
        Assert.Contains(roster.Participants, p => p.Name == "reviewer");
        Assert.Contains(roster.Participants, p => p.Name == "Investigate the bug");
        Assert.All(roster.Participants.Where(p => p.Kind == ParticipantKind.Subagent), p => Assert.True(p.IsObserveOnly));
    }
}
