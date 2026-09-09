using System.Reflection;
using Agnes.Ui.Core;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.App.Mobile.ViewModels;

/// <summary>
/// What an agent last said it was doing, and when it said it.
///
/// One or two sentences the agent volunteers through the host's status tool — "found problem X, working
/// on Y, it fits the plan because Z". Rare by design, which is exactly why it is worth carrying onto a
/// card: it is the only line in the app that is the agent's own account of itself rather than a
/// derivative of its output.
/// </summary>
/// <param name="Line">The sentence, or null when the agent has never reported one.</param>
/// <param name="At">When it was reported.</param>
public readonly record struct AgentStatus(string? Line, DateTimeOffset? At)
{
    /// <summary>An agent that has never said anything about itself.</summary>
    public static readonly AgentStatus None = new(null, null);

    /// <summary>Whether there is anything to show. A blank line is treated as no line, not as a status
    /// that happens to be empty — a card must not sprout an empty row.</summary>
    public bool HasLine => !string.IsNullOrWhiteSpace(Line);
}

/// <summary>
/// How a status line is worded on screen. Pure, so both heads' rules are one test away rather than
/// three views away.
/// </summary>
public static class StatusLine
{
    /// <summary>How long a status may go unrefreshed, while the agent is working, before the age stops
    /// being a timestamp and starts being a complaint. Ten minutes: long enough that a normal quiet
    /// stretch of a turn doesn't trip it, short enough that a wedged agent is visibly wedged.</summary>
    public static readonly TimeSpan Stale = TimeSpan.FromMinutes(10);

    /// <summary>Whether this status is old enough to be worth saying so. Only while the agent is
    /// working: a status on an idle session isn't stale, it's just the last thing that happened.</summary>
    public static bool IsStale(AgentStatus status, bool working, DateTimeOffset? now = null)
        => working && status.At is { } at && (now ?? DateTimeOffset.Now) - at >= Stale;

    /// <summary>
    /// The age, as it reads at the end of the line: "now" / "4m" / "2h" while things are moving as
    /// expected, and "no update for 12 min" once a working agent has gone quiet past <see cref="Stale"/>.
    /// The second wording is the point — "12m" beside a running session looks like progress, and it isn't.
    /// </summary>
    public static string Age(AgentStatus status, bool working, DateTimeOffset? now = null)
    {
        if (status.At is not { } at)
        {
            return string.Empty;
        }

        var span = (now ?? DateTimeOffset.Now) - at;
        if (!IsStale(status, working, now))
        {
            return RelativeTime.Format(at, now);
        }

        var minutes = (int)span.TotalMinutes;
        return minutes < 120 ? $"no update for {minutes} min" : $"no update for {(int)span.TotalHours} h";
    }
}

/// <summary>
/// Where a screen gets the agent-status facts from. An interface rather than a direct read of the
/// session so the away band can be rendered against a stub in a test — the band's whole condition is a
/// state ("you weren't here") that a live simulated session will not reproduce on demand.
/// </summary>
public interface IAgentStatusSource
{
    /// <summary>The latest status, or <see cref="AgentStatus.None"/>.</summary>
    AgentStatus Status { get; }

    /// <summary>Whether nobody has been looking at this session lately.</summary>
    bool IsUnattended { get; }

    /// <summary>What the agent said while nobody was looking, if anything.</summary>
    string? AwayStatus { get; }

    /// <summary>Records that a human just did something here, which ends the unattended stretch.</summary>
    void NoteUserInteraction();
}

/// <summary>A source with nothing in it (no session attached yet).</summary>
public sealed class NoAgentStatus : IAgentStatusSource
{
    public static readonly NoAgentStatus Instance = new();

    private NoAgentStatus()
    {
    }

    public AgentStatus Status => AgentStatus.None;

    public bool IsUnattended => false;

    public string? AwayStatus => null;

    public void NoteUserInteraction()
    {
    }
}

/// <summary>
/// The status members of a live <see cref="SessionViewModel"/>.
///
/// Read by name rather than called directly, because this head shipped its side of the feature while
/// the shared view model was still growing <c>LatestStatus</c> / <c>IsUnattended</c> / <c>AwayStatus</c>
/// / <c>NoteUserInteraction</c> in <c>Agnes.Ui.Core</c> — a project this head is not allowed to edit.
/// The lookups are resolved once against the type, so this costs a delegate call, not a search; and the
/// day those members land, every body here collapses to <c>session.LatestStatus</c> and this comment
/// goes with them. Nothing else in the head reflects: the seam is deliberately one file wide.
/// </summary>
public sealed class LiveAgentStatus(SessionViewModel? session) : IAgentStatusSource
{
    private static readonly PropertyInfo? LineProperty = Find("LatestStatus");
    private static readonly PropertyInfo? AtProperty = Find("LatestStatusAt");
    private static readonly PropertyInfo? UnattendedProperty = Find("IsUnattended");
    private static readonly PropertyInfo? AwayProperty = Find("AwayStatus");
    private static readonly MethodInfo? NoteMethod =
        typeof(SessionViewModel).GetMethod("NoteUserInteraction", BindingFlags.Instance | BindingFlags.Public, []);

    public AgentStatus Status => session is null
        ? AgentStatus.None
        : new AgentStatus(Read<string>(LineProperty), Read<DateTimeOffset?>(AtProperty));

    public bool IsUnattended => session is not null && Read<bool>(UnattendedProperty);

    public string? AwayStatus => session is null ? null : Read<string>(AwayProperty);

    public void NoteUserInteraction()
    {
        if (session is not null)
        {
            NoteMethod?.Invoke(session, null);
        }
    }

    private static PropertyInfo? Find(string name)
        => typeof(SessionViewModel).GetProperty(name, BindingFlags.Instance | BindingFlags.Public);

    private T? Read<T>(PropertyInfo? property)
        => property?.GetValue(session) is T value ? value : default;
}
