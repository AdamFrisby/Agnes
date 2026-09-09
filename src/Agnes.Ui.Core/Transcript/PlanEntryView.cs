using Agnes.Abstractions;
using FluentIcons.Common;

namespace Agnes.Ui.Core.Transcript;

/// <summary>
/// One plan entry as a panel should render it.
/// </summary>
/// <remarks>
/// An adapter hands us the plan status verbatim off the wire — ACP's <c>pending</c> /
/// <c>in_progress</c> / <c>completed</c> — and a view that prints it puts <c>in_progress</c> in a
/// sidebar next to prose, which is a protocol value leaking through the UI rather than a label anyone
/// chose. The mapping happens once, here, onto the two things a view actually needs: a named icon and
/// which status hue it wears. The raw <see cref="Status"/> stays for anything that still wants it (a
/// tooltip, a test), and an unrecognized value reads as pending rather than throwing — the set is the
/// agent's to extend, not ours.
/// </remarks>
public sealed record PlanEntryView(string Content, string Status)
{
    public static PlanEntryView Of(PlanEntry entry) => new(entry.Content, entry.Status);

    public bool IsDone => Is("completed") || Is("done");
    public bool IsRunning => Is("in_progress") || Is("in-progress") || Is("running");
    public bool IsCancelled => Is("cancelled") || Is("canceled");

    /// <summary>Anything we don't recognize reads as "not started yet", which is the safe way to be wrong.</summary>
    public bool IsPending => !IsDone && !IsRunning && !IsCancelled;

    /// <summary>
    /// Which glyph the entry wears. Filled marks a state that is *on* — running, finished, abandoned —
    /// and the outline dot marks work that hasn't started, per the icon rules in CLAUDE.md.
    /// </summary>
    public Symbol Symbol => IsDone ? Symbol.CheckmarkCircle
        : IsRunning ? Symbol.CircleHalfFill
        : IsCancelled ? Symbol.DismissCircle
        : Symbol.CircleSmall;

    public IconVariant Variant => IsPending ? IconVariant.Regular : IconVariant.Filled;

    /// <summary>
    /// What the icon means, in words: the tooltip, and what a reader falls back to when a glyph alone
    /// isn't enough. This is the label the sidebar used to print as a raw enum.
    /// </summary>
    public string StatusLabel => IsDone ? "Completed"
        : IsRunning ? "In progress"
        : IsCancelled ? "Cancelled"
        : "Pending";

    private bool Is(string value) => string.Equals(Status, value, StringComparison.OrdinalIgnoreCase);
}
