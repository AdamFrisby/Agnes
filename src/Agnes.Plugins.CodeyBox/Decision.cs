using System.Windows.Input;
using FluentIcons.Common;

namespace Agnes.Plugins.CodeyBox;

/// <summary>
/// What the operator is being asked to decide about one work item, and what the choices would do.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> The pane already showed everything the orchestrator knows about a
/// blocked item — a state name, a truncated error, eight lifecycle buttons and a view switch — and none
/// of it said what the decision <i>was</i>. An item reading "MergeConflictResolutionFailed · failureKind
/// other" beside buttons labelled Retry, Promote and More… asks the reader to already know that Promote
/// is refused outside Queued, that Retry re-runs from the failed phase, and that a question blocks a
/// retry outright. That is domain knowledge the screen was supposed to supply.</para>
///
/// <para>So a decision is modelled rather than drawn: one sentence saying what is being asked
/// (<see cref="Situation"/>), the things worth reading before answering (<see cref="Evidence"/>) and one
/// click to each place the rest of it lives (<see cref="Lookups"/>), then the actual choices, each with
/// the consequence spelled out (<see cref="Choices"/>). It is a pure function of the row, its open
/// questions and the commands that already exist — see <see cref="For"/> — so what the card says per
/// state is pinned by unit tests rather than by looking at it.</para>
///
/// <para>Null is a first-class answer: an item nobody is blocked on gets no card at all.</para>
/// </remarks>
/// <param name="Situation">One sentence naming what is being asked of the reader. Never empty.</param>
/// <param name="Glyph">The icon beside the situation. It varies with the kind of block, so it is model
/// state rather than markup — named Glyph rather than Symbol so it does not shadow the FluentIcons type
/// it is declared as.</param>
/// <param name="Tone">Amber when the item is merely waiting on a person, pink when it failed.</param>
/// <param name="Evidence">What to read before choosing: the question verbatim, the whole error, the
/// branch a merge failed on.</param>
/// <param name="Lookups">One click each to the pane's existing views. These CHANGE WHAT YOU LOOK AT and
/// nothing else — they never duplicate the view's content into the card.</param>
/// <param name="Choices">The actual decision. Each carries what it does next, and destructive ones say
/// so.</param>
public sealed record Decision(
    string Situation,
    Symbol Glyph,
    DecisionTone Tone,
    IReadOnlyList<DecisionEvidence> Evidence,
    IReadOnlyList<DecisionLookup> Lookups,
    IReadOnlyList<DecisionChoice> Choices)
{
    /// <summary>Whether the card wears the failure edge. One meaning per hue: pink is failed, amber is
    /// blocked on you, and a parked-but-healthy item is not a failure.</summary>
    public bool IsFailure => Tone == DecisionTone.Failure;

    public bool IsAttention => Tone == DecisionTone.Attention;

    /// <summary>
    /// The evidence that is prose — a question, a failure message — rendered as blocks to be read.
    /// </summary>
    public IReadOnlyList<DecisionEvidence> Passages => [.. Evidence.Where(e => !e.Monospace)];

    /// <summary>
    /// The evidence that is an identifier — a branch name, a base branch. Rendered inline beside each
    /// other in the pane's own labelled-fact convention: a one-word value in a full-width box costs four
    /// times the height it needs, and at 660px the card cannot spend that.
    /// </summary>
    public IReadOnlyList<DecisionEvidence> Facts => [.. Evidence.Where(e => e.Monospace)];

    public bool HasPassages => Passages.Count > 0;

    public bool HasFacts => Facts.Count > 0;

    public bool HasLookups => Lookups.Count > 0;

    /// <summary>How much headroom "raise the ceiling" buys. The same step the overview's own
    /// <c>ExtendCeiling</c> uses, and small for the same reason: a nudge, not a decision to stop
    /// measuring.</summary>
    internal const int CeilingStep = 5;

    /// <summary>
    /// The decision for one item, or null when nobody is blocked on it.
    /// </summary>
    /// <remarks>
    /// <para>Pure: everything it needs is an argument, including the commands a choice runs, so the same
    /// function that feeds the card feeds the tests.</para>
    ///
    /// <para>"Blocked on a person" is deliberately narrower than <see cref="WorkItemRow.NeedsAttention"/>.
    /// A Queued item with an unsatisfied dependency needs attention but has no decision — the thing to do
    /// is wait, or fix the parent, which the relations band already offers. An item backing off for a
    /// quota window (<see cref="WorkItemRow.IsWaiting"/>) is stopped but not stuck and will resume itself.
    /// Only the parked and the failed are asked about here.</para>
    /// </remarks>
    /// <param name="item">The selected row. Null yields null.</param>
    /// <param name="questions">The item's questions; only the open ones matter.</param>
    /// <param name="actions">The commands the choices run. All of them already exist on the queue view
    /// model — a choice that cannot be wired to one is a choice this card must not offer.</param>
    /// <param name="baseBranch">The branch a merge was attempted into, where the project is known. The
    /// row does not carry it, and a merge failure that cannot name what it failed against is half a
    /// sentence.</param>
    /// <param name="auditCeiling">The item's audit-iteration budget, where it is known. Zero means the
    /// "raise the ceiling" choice is not offered, because it would have no number to raise.</param>
    public static Decision? For(
        WorkItemRow? item,
        IReadOnlyList<WorkItemQuestion> questions,
        DecisionActions actions,
        string? baseBranch = null,
        int auditCeiling = 0)
    {
        if (item is null)
        {
            return null;
        }

        var open = questions.Where(q => q.IsOpen).ToList();

        // Questions win over the state: the orchestrator parks an item in NeedsOperatorInput when it
        // records one, but a question that outlives a state change is still a person being waited on.
        if (open.Count > 0 || item.State == "NeedsOperatorInput")
        {
            return Parked(item, open, actions);
        }

        return item.IsFailed ? Failed(item, actions, baseBranch, auditCeiling) : null;
    }

    // ---- parked on a person ------------------------------------------------------------------------

    private static Decision Parked(WorkItemRow item, IReadOnlyList<WorkItemQuestion> open, DecisionActions actions)
        => open.Count > 0 ? Asked(item, open, actions) : ParkedSilently(item, actions);

    /// <summary>
    /// The common case: the agent hit real ambiguity, wrote a question down and the item parked.
    /// </summary>
    /// <remarks>
    /// Retry is deliberately absent. <c>POST /workitems/{id}/retry</c> refuses an item with open
    /// questions — it answers 409 and hands back the open set — so offering it here would be offering a
    /// button that cannot work. Answering or dismissing every question is what un-parks the item; the
    /// orchestrator then moves it to WorkComplete on its own.
    /// </remarks>
    private static Decision Asked(WorkItemRow item, IReadOnlyList<WorkItemQuestion> open, DecisionActions actions)
    {
        var one = open.Count == 1;
        var situation = one
            ? "The agent asked a question and is waiting for your answer."
            : Inv($"The agent asked {open.Count} questions and is waiting for your answers.");

        var evidence = open
            .Select((q, i) => new DecisionEvidence(
                one ? "THE QUESTION" : Inv($"QUESTION {i + 1}"),
                q.QuestionText,
                Inv($"asked {q.Age}")))
            .ToList();

        // The agent did not stop to ask: its contract is to carry on with its best guess and produce a
        // usable diff anyway. So what it has already written IS the evidence for choosing, and both are
        // one click away rather than described here.
        var lookups = new List<DecisionLookup>
        {
            new("Show what the agent did anyway", actions.ShowOutput),
            new("Show the diff it produced", actions.ShowDiff),
        };

        var choices = new List<DecisionChoice>();
        for (var i = 0; i < open.Count; i++)
        {
            var q = open[i];
            choices.Add(new DecisionChoice(
                one ? "Answer" : Inv($"Answer question {i + 1}"),
                "Opens a reply box here. Your answer reaches the agent on its next run; once every "
                + "question is resolved the item re-enters the pipeline on its own.",
                actions.Answer,
                q,
                IsPrimary: i == 0));
            choices.Add(new DecisionChoice(
                one ? "Dismiss the question" : Inv($"Dismiss question {i + 1}"),
                "The agent continues without an answer, keeping the default it already chose.",
                actions.Dismiss,
                q));
        }

        choices.Add(Stop(item, actions));

        return new Decision(situation, Symbol.PersonQuestionMark, DecisionTone.Attention, evidence, lookups, choices);
    }

    /// <summary>
    /// Parked for an operator, with nothing to answer — every question has been resolved, or the state
    /// was reached without one. Rare, and previously indistinguishable from the case above.
    /// </summary>
    private static Decision ParkedSilently(WorkItemRow item, DecisionActions actions)
    {
        var evidence = new List<DecisionEvidence>();
        AddError(evidence, item, "WHAT THE ORCHESTRATOR RECORDED");

        // Deliberately not "every question has been answered". The live instance parks items here for a
        // second reason entirely: a rework pass that produced no changes while the audit had not yet shown
        // convergence is parked for operator review rather than hard-failed, and it never asked anything.
        // The sentence has to be true of both, and the recorded reason below says which.
        return new Decision(
            "The item is parked for an operator decision and there is nothing to answer — the orchestrator "
            + "stopped it here and is waiting for a person to release it.",
            Symbol.QuestionCircle,
            DecisionTone.Attention,
            evidence,
            [
                new DecisionLookup("Show the agent's last output", actions.ShowOutput),
                new DecisionLookup("Show the timeline", actions.ShowTimeline),
                new DecisionLookup("Show the diff", actions.ShowDiff),
            ],
            [
                new DecisionChoice(
                    "Put it back in the pipeline",
                    "Resumes from the phase it stopped at. Allowed here precisely because nothing is "
                    + "waiting on an answer.",
                    actions.Retry,
                    item,
                    IsPrimary: true),
                Stop(item, actions),
            ]);
    }

    // ---- failed ------------------------------------------------------------------------------------

    private static Decision Failed(WorkItemRow item, DecisionActions actions, string? baseBranch, int auditCeiling)
        => item.State switch
        {
            "MergeConflictResolutionFailed" => MergeFailed(item, actions, baseBranch),
            "AuditFailed" => AuditFailed(item, actions, auditCeiling),
            "AbandonedAfterRecoveryAttempts" => Abandoned(item, actions),
            _ => WorkFailed(item, actions),
        };

    private static Decision MergeFailed(WorkItemRow item, DecisionActions actions, string? baseBranch)
    {
        var evidence = new List<DecisionEvidence>();
        AddError(evidence, item, "WHY THE MERGE STOPPED");
        if (item.WorkBranch is { Length: > 0 } branch)
        {
            evidence.Add(new DecisionEvidence("WORK BRANCH", branch, Monospace: true));
        }

        if (baseBranch is { Length: > 0 } target)
        {
            evidence.Add(new DecisionEvidence("MERGED INTO", target, Monospace: true));
        }

        return new Decision(
            "The merge" + (baseBranch is { Length: > 0 } into ? " into " + into : string.Empty)
            + " hit a conflict the agent could not resolve inside the conflicted lines, so the item "
            + "stopped.",
            Symbol.ErrorCircle,
            DecisionTone.Failure,
            evidence,
            [
                new DecisionLookup("Show the diff", actions.ShowDiff),
                new DecisionLookup("Show the timeline", actions.ShowTimeline),
                new DecisionLookup("Show the agent's last output", actions.ShowOutput),
            ],
            [
                new DecisionChoice(
                    "Retry the merge",
                    "Re-runs from the failed phase against a refreshed base. Resolve the conflict on the "
                    + "work branch first, or it stops the same way.",
                    actions.Retry,
                    item,
                    IsPrimary: true),
                Replay(actions, item),
                Stop(item, actions),
            ]);
    }

    /// <summary>
    /// The audit never converged.
    /// </summary>
    /// <remarks>
    /// The ceiling choice is offered only when a ceiling is actually known, and it is a retry-then-patch
    /// rather than a patch: <c>PATCH /workitems/{id}</c> refuses an audit-budget change on a terminal
    /// item, and AuditFailed is terminal. See <see cref="CodeyBoxQueueViewModel.RaiseAuditCeilingCommand"/>.
    /// </remarks>
    private static Decision AuditFailed(WorkItemRow item, DecisionActions actions, int auditCeiling)
    {
        var evidence = new List<DecisionEvidence>();
        AddError(evidence, item, "WHAT THE AUDIT SAID");
        if (auditCeiling > 0)
        {
            evidence.Add(new DecisionEvidence(
                "ITERATION BUDGET", Inv($"{auditCeiling} audit iterations"), "spent in full"));
        }

        var choices = new List<DecisionChoice>();
        if (auditCeiling > 0)
        {
            choices.Add(new DecisionChoice(
                "Raise the audit ceiling and retry",
                Inv($"Re-runs the item and gives it {CeilingStep} more audit iterations ({auditCeiling} → {auditCeiling + CeilingStep}). Worth it when the findings were still shrinking."),
                actions.RaiseCeiling,
                item,
                IsPrimary: true));
        }

        choices.Add(new DecisionChoice(
            "Retry as it is",
            "Re-runs from the audit phase on the same budget. Worth it only if you changed something the "
            + "auditors read.",
            actions.Retry,
            item,
            IsPrimary: auditCeiling <= 0));
        choices.Add(Stop(item, actions));

        return new Decision(
            "The audit never passed: the item spent its whole iteration budget and still had blocking "
            + "findings.",
            Symbol.ErrorCircle,
            DecisionTone.Failure,
            evidence,
            [
                new DecisionLookup("Show the audit findings", actions.ShowTimeline),
                new DecisionLookup("Show the diff", actions.ShowDiff),
            ],
            choices);
    }

    private static Decision Abandoned(WorkItemRow item, DecisionActions actions)
    {
        var evidence = new List<DecisionEvidence>();
        AddError(evidence, item, "WHY IT WAS GIVEN UP ON");

        return new Decision(
            "The orchestrator recovered this item over and over and it never got through the phase, so it "
            + "stopped trying.",
            Symbol.ErrorCircle,
            DecisionTone.Failure,
            evidence,
            [
                new DecisionLookup("Show the timeline", actions.ShowTimeline),
                new DecisionLookup("Show the agent's last output", actions.ShowOutput),
            ],
            [
                new DecisionChoice(
                    "Retry it by hand",
                    "Resumes from the phase it kept dying in. Find out why it kept dying first, or it is "
                    + "abandoned again.",
                    actions.Retry,
                    item,
                    IsPrimary: true),
                Replay(actions, item),
                Stop(item, actions),
            ]);
    }

    /// <summary>
    /// A plain <c>Failed</c>, told apart by the kind the orchestrator classified it as. The kinds come
    /// from <c>WorkItemFailureKinds</c> and the retry schedulers: an infrastructure failure and an agent
    /// that could not do the work are the same state and completely different decisions.
    /// </summary>
    private static Decision WorkFailed(WorkItemRow item, DecisionActions actions)
    {
        var kind = item.FailureKind ?? string.Empty;
        var (situation, retryLabel, retryWhy, offerReplay) = kind.ToLowerInvariant() switch
        {
            "auth_required" => (
                "The item failed because the agent's credentials were refused — this is a login, not the "
                + "work.",
                "Retry",
                "Re-runs from the failed phase. Fix the credential first: the same one fails the same way.",
                false),
            "infrastructure" or "agent_unavailable" or "agent_routing_unavailable" => (
                "The item failed before the agent's work really began — the orchestrator classified this "
                + "as infrastructure.",
                "Retry",
                "Re-runs from the failed phase. Infrastructure failures are usually transient, so this is "
                + "the first thing to try.",
                true),
            "quota" => (
                "The item ran out of provider quota and has stopped retrying on its own.",
                "Retry",
                "Re-runs from the failed phase. Do it once the provider window has reopened, or it fails "
                + "again immediately.",
                false),
            "transient" or "transient-exhausted" or "cancelled" => (
                "The item was interrupted again and again and used up its automatic retries.",
                "Retry",
                "Re-runs from the failed phase and resets the automatic retry count.",
                true),
            "timeout" => (
                "The item hit its configured time limit and was stopped part-way through.",
                "Retry",
                "Re-runs from the failed phase with a fresh time budget. Raise the phase timeout first if "
                + "it was simply too small.",
                true),
            "build" => (
                "The build gate failed, so nothing reached the audit.",
                "Retry",
                "Re-runs from the failed phase. Read the diff first: a build that failed once fails again "
                + "unchanged.",
                true),
            _ => (
                "The item failed while the agent was working on it.",
                "Retry",
                "Re-runs from the failed phase, keeping the work branch and everything already committed "
                + "on it.",
                true),
        };

        var evidence = new List<DecisionEvidence>();
        AddError(evidence, item, "WHY IT STOPPED");

        var choices = new List<DecisionChoice>
        {
            new(retryLabel, retryWhy, actions.Retry, item, IsPrimary: true),
        };

        if (offerReplay)
        {
            choices.Add(Replay(actions, item));
        }

        choices.Add(Stop(item, actions));

        return new Decision(
            situation,
            Symbol.ErrorCircle,
            DecisionTone.Failure,
            evidence,
            [
                new DecisionLookup("Show the agent's last output", actions.ShowOutput),
                new DecisionLookup("Show the timeline", actions.ShowTimeline),
                new DecisionLookup("Show the diff", actions.ShowDiff),
            ],
            choices);
    }

    // ---- the two choices every failure shares ------------------------------------------------------

    private static DecisionChoice Replay(DecisionActions actions, WorkItemRow item) => new(
        "Start over on a fresh branch",
        "Creates a NEW item with the same task and its own work branch. This one stays failed.",
        actions.Replay,
        item);

    /// <summary>The way out. Destructive, and armed rather than fired — <c>CancelCommand</c> puts the
    /// named item in the confirmation bar first.</summary>
    private static DecisionChoice Stop(WorkItemRow item, DecisionActions actions) => new(
        "Cancel the item",
        "Stops it for good, after a confirmation. The work branch and every commit on it stay on disk.",
        actions.Cancel,
        item,
        IsDestructive: true);

    /// <summary>
    /// The failure text in full, and the kind beside it.
    /// </summary>
    /// <remarks>
    /// <see cref="WorkItemRow.ErrorSummary"/> folds the two into one line for a header; here they are
    /// separate, because the kind is the classification an operator acts on and the message is the
    /// sentence they read. Nothing is truncated: the whole point of the card is to stop the reader
    /// hunting for the rest of it.
    /// </remarks>
    private static void AddError(ICollection<DecisionEvidence> into, WorkItemRow item, string label)
    {
        if (item.LastError is { Length: > 0 } error)
        {
            into.Add(new DecisionEvidence(
                label,
                error,
                item.FailureKind is { Length: > 0 } kind ? "classified " + kind : string.Empty));
        }
        else if (item.FailureKind is { Length: > 0 } only)
        {
            into.Add(new DecisionEvidence(label, "classified " + only));
        }
    }

    private static string Inv(FormattableString text) => FormattableString.Invariant(text);
}

/// <summary>Which hue the card wears. Two, because there are two meanings: blocked on you, and failed.</summary>
public enum DecisionTone
{
    /// <summary>Amber — the item is waiting on a person and nothing has gone wrong.</summary>
    Attention,

    /// <summary>Pink — the item failed.</summary>
    Failure,
}

/// <summary>One thing to read before deciding.</summary>
/// <param name="Label">The tiny signpost above it, in the pane's fact-label convention.</param>
/// <param name="Text">The content, verbatim and untruncated.</param>
/// <param name="Note">A fact about the content rather than part of it: when a question was asked, how the
/// orchestrator classified a failure.</param>
/// <param name="Monospace">Set for a value that is an identifier — a branch name — rather than prose.</param>
public sealed record DecisionEvidence(string Label, string Text, string Note = "", bool Monospace = false)
{
    public bool HasNote => Note.Length > 0;
}

/// <summary>
/// One place the rest of the evidence already lives, reached in a click.
/// </summary>
/// <remarks>
/// These drive the pane's existing view switch. The card must never copy a view's content into itself:
/// two renderings of one diff is how they start disagreeing.
/// </remarks>
public sealed record DecisionLookup(string Label, ICommand? Command);

/// <summary>
/// One thing the operator can decide, and what happens if they do.
/// </summary>
/// <param name="Label">The verb, in the operator's words.</param>
/// <param name="Consequence">What it does and what happens next. One line, always present — a choice
/// whose effect cannot be stated is a choice that should not be offered.</param>
/// <param name="Command">The command that already exists for it. Never invented: a decision the API
/// cannot carry out is not on the card.</param>
/// <param name="Parameter">What the command acts on — the row, or the question being answered.</param>
/// <param name="IsDestructive">Wears the destructive treatment and confirms before acting.</param>
/// <param name="IsPrimary">The one the card leads with. Exactly one per decision.</param>
public sealed record DecisionChoice(
    string Label,
    string Consequence,
    ICommand? Command,
    object? Parameter = null,
    bool IsDestructive = false,
    bool IsPrimary = false);

/// <summary>
/// The commands a decision's choices run, handed in rather than reached for.
/// </summary>
/// <remarks>
/// Every one of these already exists on <see cref="CodeyBoxQueueViewModel"/> and is bound elsewhere in
/// the pane, which is the point: the card is a better arrangement of the actions the tab already has,
/// not a second set of them. Passing them in is also what keeps <see cref="Decision.For"/> a function —
/// a test hands it recording commands and asserts which choice got which.
/// </remarks>
public sealed record DecisionActions(
    ICommand? Answer,
    ICommand? Dismiss,
    ICommand? Retry,
    ICommand? RaiseCeiling,
    ICommand? Replay,
    ICommand? Cancel,
    ICommand? ShowOutput,
    ICommand? ShowTimeline,
    ICommand? ShowDiff);
