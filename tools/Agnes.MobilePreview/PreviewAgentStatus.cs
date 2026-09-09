using Agnes.App.Mobile.ViewModels;

namespace Agnes.App.Mobile.Preview;

/// <summary>
/// A fixed set of agent-status facts for a render.
///
/// The "while you were away" band's condition is a state — nobody has been looking at this session for
/// a while — that a simulated session driven for four seconds will never reach. The band is also the
/// part of this feature most worth having a picture of, so the harness hands the page its facts rather
/// than waiting for a state it cannot produce.
/// </summary>
public sealed class PreviewAgentStatus(AgentStatus status, bool isUnattended, string? awayStatus)
    : IAgentStatusSource
{
    public AgentStatus Status { get; } = status;

    public bool IsUnattended { get; } = isUnattended;

    public string? AwayStatus { get; } = awayStatus;

    public void NoteUserInteraction()
    {
        // Nothing to record: a render has no person in it.
    }
}
