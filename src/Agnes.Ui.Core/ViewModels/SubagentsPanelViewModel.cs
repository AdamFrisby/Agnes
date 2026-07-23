using System.Collections.ObjectModel;
using Agnes.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Agnes.Ui.Core.ViewModels;

/// <summary>
/// The session's participant roster (sessions/04, Tier 1 — visibility only): the lead agent as the root
/// participant plus a row per subagent the adapter reports via <see cref="SubagentStartedEvent"/>,
/// de-duplicated by id. A pure client-side projection of events Agnes already normalizes — no host or
/// protocol change. Subagent rows are observe-only (<see cref="ParticipantRow.Controllable"/> is false);
/// true addressed routing/stop is deferred until an adapter exposes a controllable-subagent capability
/// (sessions/04 Tier 2), at which point <see cref="Add"/> can mark those rows controllable.
/// </summary>
public sealed class SubagentsPanelViewModel : ObservableObject
{
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    public SubagentsPanelViewModel(string leadName)
    {
        // The lead/main agent is always the root participant — present even before any subagent appears.
        Participants.Add(new ParticipantRow(Id: null, leadName, ParticipantKind.Lead, Controllable: true));
    }

    /// <summary>The roster: the lead first (root), then each subagent in the order it was reported.</summary>
    public ObservableCollection<ParticipantRow> Participants { get; } = [];

    /// <summary>Whether any subagent has been reported yet — drives the panel's visibility.</summary>
    public bool HasSubagents => Participants.Any(p => p.Kind == ParticipantKind.Subagent);

    /// <summary>Adds a reported subagent as an observe-only roster row; a repeated id is ignored.</summary>
    public void Add(SubagentStartedEvent sub)
    {
        if (!_seen.Add(sub.SubagentId))
        {
            return;
        }

        // Tier 1: reported-only. No adapter exposes addressed send/stop yet, so the row is observe-only
        // (Controllable: false) — flip this when an ISubagentCapableSession-style capability lands.
        Participants.Add(new ParticipantRow(sub.SubagentId, sub.Name, ParticipantKind.Subagent, Controllable: false));
        OnPropertyChanged(nameof(HasSubagents));
    }
}
