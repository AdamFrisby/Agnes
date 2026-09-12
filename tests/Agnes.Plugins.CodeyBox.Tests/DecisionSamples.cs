using System.Windows.Input;
using Agnes.Plugins.CodeyBox;

namespace Agnes.Plugins.CodeyBox.Tests;

/// <summary>
/// The blocked items the decision card exists for, hand-built.
/// </summary>
/// <remarks>
/// Separate from <see cref="BoardSamples"/> because these are not runway shapes: a board sample is a
/// chain on a horizon, and every one of these is a single row in a state that a live orchestrator
/// produces perhaps twice a week. Having them here is the only way the four failure cases get rendered,
/// compared and looked at side by side rather than one at a time as the fleet happens to produce them.
/// </remarks>
public static class DecisionSamples
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 14, 30, 0, TimeSpan.Zero);

    public static WorkItemRow Row(
        string state,
        string? error = null,
        string? failureKind = null,
        string title = "Wire the credential broker into the sandbox",
        string branch = "codeybox/43c8ec28") => new(
            Id: "43c8ec28aa1140f0b3d9f1a27c5e0011",
            Title: title,
            State: state,
            Agent: "claude",
            ProjectId: "codeybox-self",
            QueuePosition: 3,
            UpdatedAt: Now.AddMinutes(-42),
            LastError: error,
            Prompt: "Wire the credential broker into the sandbox so a sandboxed agent can push without a "
                  + "long-lived token, then open a PR.",
            Priority: 40,
            CreatedAt: Now.AddHours(-9),
            FailureKind: failureKind,
            WorkBranch: branch,
            UsageTotal: new UsageTotal(12.44m, 840_000, 61_000));

    public static WorkItemQuestion Question(
        string text = "The broker can mint a token per push or one per session. Per push is safer but "
                    + "adds a round trip to every commit — which do you want?",
        string id = "q-token-scope",
        int minutesAgo = 42) => new(
            Id: "aa11",
            WorkItemId: "43c8ec28aa1140f0b3d9f1a27c5e0011",
            QuestionId: id,
            QuestionText: text,
            State: "open",
            AskedAt: DateTimeOffset.UtcNow.AddMinutes(-minutesAgo),
            AnsweredAt: null,
            AnswerText: null,
            AnsweredBy: null,
            DismissedAt: null);

    /// <summary>An agent waiting on one question — the case that occurs.</summary>
    public static Decision Asked(DecisionActions? actions = null)
        => Decision.For(Row("NeedsOperatorInput"), [Question()], actions ?? Recording(), "main", 28)!;

    /// <summary>Parked for an operator with nothing open to answer.</summary>
    public static Decision ParkedSilently(DecisionActions? actions = null)
        => Decision.For(
            Row("NeedsOperatorInput", "every question resolved; awaiting operator release"),
            [],
            actions ?? Recording(),
            "main",
            28)!;

    public static Decision MergeFailed(DecisionActions? actions = null)
        => Decision.For(
            Row("MergeConflictResolutionFailed",
                "merge resolution rejected by the scope fence: src/CodeyBox.Core/WorkItem.cs line 812 is "
                + "outside every conflict span (+8 lines of context); aborted before push"),
            [],
            actions ?? Recording(),
            "main",
            28)!;

    public static Decision AuditFailed(DecisionActions? actions = null)
        => Decision.For(
            Row("AuditFailed",
                "audit did not converge: iteration 28 of 28 still reported 2 blocking findings "
                + "(csharp:format-check, security:secrets-scan)"),
            [],
            actions ?? Recording(),
            "main",
            28)!;

    public static Decision InfraFailed(DecisionActions? actions = null)
        => Decision.For(
            Row("Failed", "sandbox provisioning failed: incus image codeybox/base-2026-08 not present",
                "infrastructure"),
            [],
            actions ?? Recording(),
            "main",
            28)!;

    public static Decision Abandoned(DecisionActions? actions = null)
        => Decision.For(
            Row("AbandonedAfterRecoveryAttempts",
                "recovery attempt 10 of 10 did not complete the work phase; giving up"),
            [],
            actions ?? Recording(),
            "main",
            28)!;

    /// <summary>
    /// A set of commands that record what they were handed, so a test can say which choice ran which.
    /// </summary>
    public static DecisionActions Recording() => new(
        Answer: new Fired(),
        Dismiss: new Fired(),
        Retry: new Fired(),
        RaiseCeiling: new Fired(),
        Replay: new Fired(),
        Cancel: new Fired(),
        ShowOutput: new Fired(),
        ShowTimeline: new Fired(),
        ShowDiff: new Fired());

    /// <summary>The <see cref="Fired"/> behind one of the actions, for asserting what a choice ran.</summary>
    public static Fired Recorded(ICommand? command) => (Fired)command!;
}
