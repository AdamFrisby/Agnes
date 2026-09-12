using Agnes.Plugins.CodeyBox;

namespace Agnes.Plugins.CodeyBox.Tests;

/// <summary>
/// What the pane says when an item is blocked on a person, and what each choice would do.
/// </summary>
/// <remarks>
/// <para>The finding this answers: "when an item has 'Needs your answer' or a similar status, the options
/// which should be picked from when you select the item are non-obvious." The fix is a sentence, the
/// evidence, and two to four choices that each state their consequence — so the sentence and the choice
/// set per state are the contract, and they are pinned here rather than left to whatever the markup
/// happens to render.</para>
///
/// <para>Every choice is checked to be wired to a command that already exists. A choice whose endpoint
/// the orchestrator would refuse — Promote outside Queued, Retry with a question still open — is worse
/// than no choice at all, so several of these tests are about what the card must NOT offer.</para>
/// </remarks>
public class DecisionTests
{
    // ---- when there is a decision at all -----------------------------------------------------------

    [Theory]
    [InlineData("Queued")]
    [InlineData("Working")]
    [InlineData("Auditing")]
    [InlineData("Reworking")]
    [InlineData("Merging")]
    [InlineData("WorkComplete")]
    [InlineData("AuditPassed")]
    [InlineData("Merged")]
    [InlineData("Done")]
    public void An_item_nobody_is_blocked_on_gets_no_card(string state)
        => Assert.Null(Decision.For(DecisionSamples.Row(state), [], DecisionSamples.Recording()));

    [Fact]
    public void Nothing_selected_is_not_a_decision()
        => Assert.Null(Decision.For(null, [], DecisionSamples.Recording()));

    [Fact]
    public void A_cancelled_item_is_a_decision_already_made()
    {
        // Cancelled is not IsFailed and must not be: 45 of the 67 items carrying an error on the live
        // instance are cancellations, and heading those "you must decide something" would put a card on
        // two thirds of the history.
        Assert.Null(Decision.For(
            DecisionSamples.Row("Cancelled", "cancelled by an operator"), [], DecisionSamples.Recording()));
    }

    [Fact]
    public void A_backing_off_item_is_stopped_but_not_stuck()
    {
        // The orchestrator resumes quota and transient waits by itself. A card here would ask the operator
        // to retry something that was already going to retry itself — which is the exact mistake the
        // pane's "waiting to resume" box was added to prevent.
        var waiting = DecisionSamples.Row("Working") with
        {
            QuotaRetryAttempts = 2,
            NextQuotaRetryAt = DateTimeOffset.UtcNow.AddHours(3),
        };

        Assert.True(waiting.IsWaiting);
        Assert.Null(Decision.For(waiting, [], DecisionSamples.Recording()));
    }

    [Theory]
    [InlineData("NeedsOperatorInput")]
    [InlineData("Failed")]
    [InlineData("AuditFailed")]
    [InlineData("MergeConflictResolutionFailed")]
    [InlineData("AbandonedAfterRecoveryAttempts")]
    public void Every_blocked_state_produces_a_card_that_says_something(string state)
    {
        var decision = Decision.For(
            DecisionSamples.Row(state, "boom"), [], DecisionSamples.Recording(), "main", 28);

        Assert.NotNull(decision);
        Assert.False(string.IsNullOrWhiteSpace(decision!.Situation));
        Assert.EndsWith(".", decision.Situation, StringComparison.Ordinal);
        Assert.NotEmpty(decision.Choices);
        Assert.All(decision.Choices, c => Assert.False(string.IsNullOrWhiteSpace(c.Consequence)));
    }

    // ---- an open question --------------------------------------------------------------------------

    [Fact]
    public void An_open_question_says_so_and_quotes_it_whole()
    {
        var question = DecisionSamples.Question();
        var decision = Decision.For(
            DecisionSamples.Row("NeedsOperatorInput"), [question], DecisionSamples.Recording());

        Assert.Equal("The agent asked a question and is waiting for your answer.", decision!.Situation);
        Assert.Equal(DecisionTone.Attention, decision.Tone);

        // Verbatim, with its age beside it rather than mixed into it.
        var evidence = Assert.Single(decision.Evidence);
        Assert.Equal("THE QUESTION", evidence.Label);
        Assert.Equal(question.QuestionText, evidence.Text);
        Assert.Contains("asked", evidence.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Several_questions_are_counted_and_numbered()
    {
        var decision = Decision.For(
            DecisionSamples.Row("NeedsOperatorInput"),
            [DecisionSamples.Question(id: "a"), DecisionSamples.Question(text: "And the retry budget?", id: "b")],
            DecisionSamples.Recording());

        Assert.Equal("The agent asked 2 questions and is waiting for your answers.", decision!.Situation);
        Assert.Equal(["QUESTION 1", "QUESTION 2"], decision.Evidence.Select(e => e.Label));
        Assert.Contains(decision.Choices, c => c.Label == "Answer question 1");
        Assert.Contains(decision.Choices, c => c.Label == "Answer question 2");
    }

    [Fact]
    public void An_answered_question_is_not_a_block()
    {
        // Only the open ones matter; a resolved one is history and the item has left the parked state.
        var answered = DecisionSamples.Question() with
        {
            AnsweredAt = DateTimeOffset.UtcNow,
            AnswerText = "per session",
        };

        Assert.Null(Decision.For(DecisionSamples.Row("Working"), [answered], DecisionSamples.Recording()));
    }

    [Fact]
    public void A_question_never_offers_a_retry()
    {
        // POST /workitems/{id}/retry answers 409 while a question is open and hands back the open set.
        // Offering it would be offering a button that cannot work.
        var decision = DecisionSamples.Asked();

        Assert.DoesNotContain(decision.Choices, c => c.Label.Contains("Retry", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["Answer", "Dismiss the question", "Cancel the item"], decision.Choices.Select(c => c.Label));
    }

    [Fact]
    public void Answering_and_dismissing_target_the_question_not_the_row()
    {
        // The command carries the question either way, so a list refreshing underneath can never redirect
        // an answer at a different one.
        var question = DecisionSamples.Question();
        var decision = Decision.For(
            DecisionSamples.Row("NeedsOperatorInput"), [question], DecisionSamples.Recording());

        var answer = decision!.Choices.Single(c => c.Label == "Answer");
        var dismiss = decision.Choices.Single(c => c.Label == "Dismiss the question");

        Assert.Same(question, answer.Parameter);
        Assert.Same(question, dismiss.Parameter);
    }

    [Fact]
    public void Dismissing_says_what_the_agent_does_next()
        => Assert.Contains(
            "continues without an answer",
            DecisionSamples.Asked().Choices.Single(c => c.Label == "Dismiss the question").Consequence,
            StringComparison.Ordinal);

    [Fact]
    public void A_question_points_at_what_the_agent_did_anyway()
    {
        // The agent's contract is to carry on with its best guess rather than block, so there IS output
        // and usually a diff — and that is the evidence for choosing between the options it asked about.
        var actions = DecisionSamples.Recording();
        var decision = DecisionSamples.Asked(actions);

        Assert.Equal(2, decision.Lookups.Count);
        Assert.Same(actions.ShowOutput, decision.Lookups[0].Command);
        Assert.Same(actions.ShowDiff, decision.Lookups[1].Command);
    }

    [Fact]
    public void Parked_with_nothing_open_is_a_different_sentence_and_a_different_choice()
    {
        var actions = DecisionSamples.Recording();
        var decision = DecisionSamples.ParkedSilently(actions);

        // True of both ways an item lands here: every question resolved, and — the one the live instance
        // actually produces — a blank rework parked for review rather than hard-failed.
        Assert.Contains("nothing to answer", decision.Situation, StringComparison.Ordinal);
        Assert.Equal(DecisionTone.Attention, decision.Tone);

        // Here a retry IS legal — nothing is waiting on an answer — and it is the point of the card.
        var resume = decision.Choices.First();
        Assert.Same(actions.Retry, resume.Command);
        Assert.True(resume.IsPrimary);
    }

    // ---- the failures ------------------------------------------------------------------------------

    [Fact]
    public void A_failed_merge_names_the_branch_it_failed_against()
    {
        var actions = DecisionSamples.Recording();
        var decision = DecisionSamples.MergeFailed(actions);

        Assert.Contains("The merge into main", decision.Situation, StringComparison.Ordinal);
        Assert.Equal(DecisionTone.Failure, decision.Tone);
        Assert.Contains(decision.Evidence, e => e.Label == "WORK BRANCH" && e.Text == "codeybox/43c8ec28");
        Assert.Contains(decision.Evidence, e => e.Label == "MERGED INTO" && e.Text == "main");

        Assert.Equal(
            ["Retry the merge", "Start over on a fresh branch", "Cancel the item"],
            decision.Choices.Select(c => c.Label));
        Assert.Same(actions.Retry, decision.Choices[0].Command);
        Assert.Same(actions.Replay, decision.Choices[1].Command);
        Assert.Same(actions.Cancel, decision.Choices[2].Command);
    }

    [Fact]
    public void A_failed_merge_with_no_known_base_still_reads_as_a_sentence()
    {
        var decision = Decision.For(
            DecisionSamples.Row("MergeConflictResolutionFailed", "conflict"), [], DecisionSamples.Recording());

        Assert.StartsWith("The merge hit a conflict", decision!.Situation, StringComparison.Ordinal);
        Assert.DoesNotContain(decision.Evidence, e => e.Label == "MERGED INTO");
    }

    [Fact]
    public void A_failed_audit_offers_the_ceiling_only_when_there_is_one_to_raise()
    {
        var actions = DecisionSamples.Recording();
        var withCeiling = DecisionSamples.AuditFailed(actions);

        Assert.Contains("spent its whole iteration budget", withCeiling.Situation, StringComparison.Ordinal);
        var raise = withCeiling.Choices.First();
        Assert.Equal("Raise the audit ceiling and retry", raise.Label);
        Assert.Same(actions.RaiseCeiling, raise.Command);
        // The numbers, not "more": an operator deciding whether five more rounds is worth it needs both.
        Assert.Contains("28 → 33", raise.Consequence, StringComparison.Ordinal);

        var unknown = Decision.For(
            DecisionSamples.Row("AuditFailed", "did not converge"), [], DecisionSamples.Recording());
        Assert.DoesNotContain(unknown!.Choices, c => c.Label.Contains("ceiling", StringComparison.OrdinalIgnoreCase));
        Assert.True(unknown.Choices[0].IsPrimary);   // the plain retry leads instead
    }

    [Fact]
    public void A_failed_audit_sends_you_to_the_findings()
    {
        var actions = DecisionSamples.Recording();
        var decision = DecisionSamples.AuditFailed(actions);

        var findings = decision.Lookups.First();
        Assert.Equal("Show the audit findings", findings.Label);
        Assert.Same(actions.ShowTimeline, findings.Command);
    }

    [Fact]
    public void An_abandoned_item_says_the_orchestrator_gave_up()
    {
        var decision = DecisionSamples.Abandoned();

        Assert.Contains("stopped trying", decision.Situation, StringComparison.Ordinal);
        Assert.Contains("Find out why it kept dying first", decision.Choices[0].Consequence, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("infrastructure", "as infrastructure", true)]
    [InlineData("agent_unavailable", "as infrastructure", true)]
    [InlineData("agent_routing_unavailable", "as infrastructure", true)]
    [InlineData("auth_required", "credentials were refused", false)]
    [InlineData("quota", "ran out of provider quota", false)]
    [InlineData("transient", "used up its automatic retries", true)]
    [InlineData("transient-exhausted", "used up its automatic retries", true)]
    [InlineData("cancelled", "used up its automatic retries", true)]
    [InlineData("timeout", "hit its configured time limit", true)]
    [InlineData("build", "build gate failed", true)]
    [InlineData("other", "failed while the agent was working", true)]
    [InlineData(null, "failed while the agent was working", true)]
    public void A_plain_failure_is_told_apart_by_its_kind(string? kind, string expected, bool offersReplay)
    {
        // "Failed" is one state and several completely different decisions. An orchestrator that could not
        // provision a sandbox and an agent that could not do the work are the same word on the row.
        var decision = Decision.For(
            DecisionSamples.Row("Failed", "boom", kind), [], DecisionSamples.Recording());

        Assert.Contains(expected, decision!.Situation, StringComparison.Ordinal);
        Assert.Equal(DecisionTone.Failure, decision.Tone);
        Assert.Equal(
            offersReplay,
            decision.Choices.Any(c => c.Label == "Start over on a fresh branch"));
    }

    [Fact]
    public void An_infrastructure_failure_says_a_retry_is_the_first_thing_to_try()
        => Assert.Contains(
            "usually transient",
            DecisionSamples.InfraFailed().Choices[0].Consequence,
            StringComparison.Ordinal);

    [Fact]
    public void A_credential_failure_does_not_pretend_a_retry_will_help()
    {
        var decision = Decision.For(
            DecisionSamples.Row("Failed", "401 from the provider", "auth_required"),
            [],
            DecisionSamples.Recording());

        Assert.Contains("Fix the credential first", decision!.Choices[0].Consequence, StringComparison.Ordinal);
    }

    // ---- what every card owes the reader -----------------------------------------------------------

    [Fact]
    public void The_error_is_carried_whole()
    {
        // The pane's existing one-liner is a header. The card is where the rest of it goes, so nothing
        // here may abbreviate: chasing the remainder is what the card exists to stop.
        var error = new string('x', 4000) + " END";
        var decision = Decision.For(
            DecisionSamples.Row("Failed", error, "other"), [], DecisionSamples.Recording());

        var evidence = Assert.Single(decision!.Evidence);
        Assert.Equal(error, evidence.Text);
        Assert.Equal("classified other", evidence.Note);
    }

    [Fact]
    public void A_failure_with_no_message_still_reports_its_kind()
    {
        var evidence = Assert.Single(
            Decision.For(DecisionSamples.Row("Failed", null, "quota"), [], DecisionSamples.Recording())!.Evidence);

        Assert.Equal("classified quota", evidence.Text);
    }

    public static TheoryData<Decision> EveryCard => new(
    [
        DecisionSamples.Asked(),
        DecisionSamples.ParkedSilently(),
        DecisionSamples.MergeFailed(),
        DecisionSamples.AuditFailed(),
        DecisionSamples.InfraFailed(),
        DecisionSamples.Abandoned(),
    ]);

    [Theory]
    [MemberData(nameof(EveryCard))]
    public void Every_choice_is_wired_to_a_command_that_already_exists(Decision decision)
        => Assert.All(decision.Choices, c => Assert.NotNull(c.Command));

    [Theory]
    [MemberData(nameof(EveryCard))]
    public void Every_lookup_is_wired(Decision decision)
        => Assert.All(decision.Lookups, l => Assert.NotNull(l.Command));

    [Theory]
    [MemberData(nameof(EveryCard))]
    public void Exactly_one_choice_leads(Decision decision)
        => Assert.Single(decision.Choices, c => c.IsPrimary);

    [Theory]
    [MemberData(nameof(EveryCard))]
    public void Cancelling_is_the_only_destructive_choice_and_every_card_offers_it(Decision decision)
    {
        var destructive = Assert.Single(decision.Choices, c => c.IsDestructive);
        Assert.Equal("Cancel the item", destructive.Label);
        Assert.Contains("after a confirmation", destructive.Consequence, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(EveryCard))]
    public void A_card_never_asks_for_more_than_four_decisions(Decision decision)
        // Two to four. More than that is a menu, and a menu is what the pane already had.
        => Assert.InRange(decision.Choices.Count, 2, 4);

    [Theory]
    [MemberData(nameof(EveryCard))]
    public void Amber_is_waiting_on_you_and_pink_is_failed(Decision decision)
        => Assert.Equal(decision.Tone == DecisionTone.Failure, decision.IsFailure);

    // ---- the path from the runway to the card ------------------------------------------------------

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("null", System.Text.Encoding.UTF8, "application/json"),
            });
    }

    private static CodeyBoxQueueViewModel Offline()
        => new(new CodeyBoxClient(new CodeyBoxOptions("http://127.0.0.1:1", "k"), new OfflineHandler()),
               a => { a(); return Task.CompletedTask; });

    [Fact]
    public async Task Selecting_a_needs_you_row_on_the_runway_lands_on_a_card()
    {
        // The runway already groups these under "Needs you" and labels them "needs your answer" /
        // "failed: needs a decision". What that promised and could not deliver was somewhere to go: the
        // row selected the item and the pane showed the same generic surface as everything else.
        await using var vm = Offline();
        var rows = new[]
        {
            DecisionSamples.Row("NeedsOperatorInput", title: "Decide the plugin signing story"),
            DecisionSamples.Row("Working", title: "Something else entirely") with { Id = "bbbb2222" },
        };

        vm.Load(rows);
        await vm.BoardReady;

        var group = Assert.Single(vm.Board!.Waiting, g => g.Reason == WaitReason.Person);
        var chain = Assert.Single(group.Chains);

        vm.SelectChainCommand.Execute(chain);

        Assert.Same(rows[0], vm.Selected);
        Assert.NotNull(vm.Decision);
        Assert.Contains("parked for an operator decision", vm.Decision!.Situation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_ordinary_selection_leaves_the_pane_exactly_as_it_was()
    {
        await using var vm = Offline();
        var row = DecisionSamples.Row("Working");

        vm.Load([row]);
        vm.Selected = row;
        await vm.BoardReady;

        Assert.Null(vm.Decision);
        Assert.False(vm.HasDecision);
    }

    [Fact]
    public async Task The_transcripts_error_box_steps_aside_for_the_card()
    {
        // One failure, said once. The box keeps the case it was written for — a Cancelled item carrying an
        // error, which is two thirds of the errors on a real instance and asks nobody for anything.
        await using var vm = Offline();
        var failed = DecisionSamples.Row("Failed", "boom", "other");
        var cancelled = DecisionSamples.Row("Cancelled", "cancelled by an operator") with { Id = "cccc3333" };

        vm.Load([failed, cancelled]);

        vm.Selected = failed;
        Assert.NotNull(vm.Decision);
        Assert.False(vm.ShowErrorBox);

        vm.Selected = cancelled;
        Assert.Null(vm.Decision);
        Assert.True(vm.ShowErrorBox);
    }

    [Fact]
    public async Task Answering_a_question_that_lands_takes_the_card_down()
    {
        // The card is derived from the question list, not snapshotted beside it: whoever changes the list
        // changes the card. A card still saying "waiting for your answer" after the answer went through is
        // the failure mode that makes an operator answer twice.
        await using var vm = Offline();
        var row = DecisionSamples.Row("NeedsOperatorInput");
        vm.Load([row]);
        vm.Selected = row;
        vm.Questions.Add(DecisionSamples.Question());

        Assert.Contains("asked a question", vm.Decision!.Situation, StringComparison.Ordinal);

        vm.Questions.Clear();

        Assert.Contains("nothing to answer", vm.Decision!.Situation, StringComparison.Ordinal);
    }
}
