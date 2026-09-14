using Agnes.App.Mobile.Services;
using Agnes.App.Mobile.ViewModels;
using Agnes.Plugins.CodeyBox;
using Agnes.Plugins.CodeyBox.Tests;

namespace Agnes.Mobile.Tests;

/// <summary>
/// What a tap on the item page actually sends.
/// </summary>
/// <remarks>
/// <para>The decision card is a pure function of the row and its questions — the plugin's own tests pin
/// what it <em>says</em> per state. What this file pins is the other half: that the phone wires each
/// choice to the same endpoint the desktop does, that the ones with an order keep it, and that the
/// destructive one does not fire on the first tap.</para>
///
/// <para>Every test here runs against a recording handler. The operator's real orchestrator is running a
/// real fleet and is never touched.</para>
/// </remarks>
[Collection(AvaloniaCollection.Name)]
public sealed class CodeyBoxActionTests : IDisposable
{
    private readonly string _state = Path.Combine(
        Path.GetTempPath(), "agnes-codeybox-actions-" + Guid.NewGuid().ToString("n"));

    public CodeyBoxActionTests(AvaloniaSession avalonia)
    {
        _ = avalonia;
        JsonStore.UseDirectory(_state);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_state, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private const string ItemId = "43c8ec28aa1140f0b3d9f1a27c5e0011";

    /// <summary>An item page over a recording orchestrator, with the fleet's projects already read so the
    /// audit ceiling is known.</summary>
    private static (FleetHandler Handler, StubShell Shell, CodeyBoxItemPageViewModel Page) Page(
        WorkItemRow item, string questions = "[]")
    {
        var handler = new FleetHandler { QuestionsBody = questions };
        var (shell, fleet) = Fleet.Offline(handler);
        fleet.Show(Fleet.Gathered() with { Items = [item] });

        var client = new CodeyBoxClient(Fleet.Config.ToOptions(), handler);
        return (handler, shell, new CodeyBoxItemPageViewModel(shell, fleet, client, item));
    }

    /// <summary>
    /// Everything that CHANGED something, in order.
    /// </summary>
    /// <remarks>
    /// Filtered to the non-GET requests on purpose. Every action re-gathers afterwards so the card and the
    /// queue behind it cannot disagree about what just happened, and that gather is a dozen reads — real,
    /// wanted, and nothing to do with what the tap was supposed to send.
    /// </remarks>
    private static IReadOnlyList<string> Calls(FleetHandler handler)
        => [.. handler.Requests.Where(r => r.Method != "GET").Select(r => $"{r.Method} {r.Path}")];

    [Fact]
    public async Task Retry_is_one_post()
    {
        var (handler, _, page) = Page(DecisionSamples.Row("Failed", "it broke", "infrastructure"));
        await page.LoadAsync();

        await page.RetryCommand.ExecuteAsync(page.Item);

        Assert.Equal([$"POST /workitems/{ItemId}/retry"], Calls(handler));
    }

    [Fact]
    public async Task Raising_the_ceiling_retries_first_and_then_patches_the_budget()
    {
        // In that order, and it matters: PATCH /workitems/{id} is refused on a terminal item, and
        // AuditFailed — the only state that offers this choice — is terminal. The retry is what makes the
        // item patchable. Getting it the other way round is a 409 and a card that looks like it worked.
        var (handler, _, page) = Page(DecisionSamples.Row("AuditFailed", "never converged"));
        await page.LoadAsync();

        await page.RaiseCeilingCommand.ExecuteAsync(page.Item);

        Assert.Equal(
            [$"POST /workitems/{ItemId}/retry", $"PATCH /workitems/{ItemId}"],
            Calls(handler));

        // 25 is the sample project's ceiling and the step is the plugin's own five: a nudge, not a
        // decision to stop measuring.
        var patch = handler.Requests.Last(r => r.Method == "PATCH");
        Assert.Contains("\"auditMaxIterations\":30", patch.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_arms_before_it_fires()
    {
        // A phone has no hover and no undo, so the first tap names what it will stop and the second does
        // it — the same two-step More › Devices uses for its prune.
        var (handler, _, page) = Page(DecisionSamples.Row("Failed", "it broke"));
        await page.LoadAsync();

        await page.CancelItemCommand.ExecuteAsync(page.Item);
        Assert.True(page.IsConfirmingCancel);
        Assert.Empty(Calls(handler));
        Assert.Contains(page.Item.Title, page.ConfirmCancelText, StringComparison.Ordinal);

        await page.CancelItemCommand.ExecuteAsync(page.Item);
        Assert.False(page.IsConfirmingCancel);
        Assert.Equal([$"DELETE /workitems/{ItemId}"], Calls(handler));
    }

    [Fact]
    public async Task Keeping_it_disarms()
    {
        var (handler, _, page) = Page(DecisionSamples.Row("Failed", "it broke"));
        await page.LoadAsync();

        await page.CancelItemCommand.ExecuteAsync(page.Item);
        page.KeepItemCommand.Execute(null);

        Assert.False(page.IsConfirmingCancel);
        Assert.Empty(Calls(handler));
    }

    [Fact]
    public async Task Replay_starts_over_on_a_fresh_branch()
    {
        var (handler, _, page) = Page(DecisionSamples.Row("Failed", "it broke", "infrastructure"));
        await page.LoadAsync();

        await page.ReplayCommand.ExecuteAsync(page.Item);

        Assert.Equal([$"POST /workitems/{ItemId}/replay"], Calls(handler));
    }

    [Fact]
    public async Task Promote_is_offered_only_for_something_the_orchestrator_would_pick()
    {
        // The endpoint is refused outside Queued, and a button whose only outcome is a refusal teaches
        // nothing.
        var (handler, _, queued) = Page(DecisionSamples.Row("Queued"));
        Assert.True(queued.CanPromote);
        await queued.PromoteCommand.ExecuteAsync(queued.Item);
        Assert.Equal([$"POST /workitems/{ItemId}/promote"], Calls(handler));

        var (_, _, running) = Page(DecisionSamples.Row("Working"));
        Assert.False(running.CanPromote);
    }

    [Fact]
    public async Task Answering_a_question_opens_a_sheet_and_the_sheet_posts_the_answer()
    {
        var (handler, shell, page) = Page(
            DecisionSamples.Row("NeedsOperatorInput"), Questions());
        await page.LoadAsync();

        Assert.NotNull(page.Decision);
        var answer = page.Decision!.Choices.First(c => c.Label == "Answer");
        answer.Command!.Execute(answer.Parameter);

        var sheet = Assert.IsType<CodeyBoxAnswerSheetViewModel>(shell.Sheets.Single());
        Assert.False(sheet.CanSend);        // an empty answer un-parks the item having taught it nothing
        sheet.Answer = "per push";
        Assert.True(sheet.CanSend);

        await sheet.SendCommand.ExecuteAsync(null);

        var sent = handler.Requests.Single(r => r.Path.EndsWith("/answer", StringComparison.Ordinal));
        Assert.Equal("POST", sent.Method);
        Assert.Equal($"/workitems/{ItemId}/answer", sent.Path);
        Assert.Contains("\"questionId\":\"q-token-scope\"", sent.Body, StringComparison.Ordinal);
        Assert.Contains("\"answer\":\"per push\"", sent.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dismissing_a_question_sends_the_reason_the_orchestrator_requires()
    {
        // The orchestrator rejects an empty reason outright, so the card cannot offer a bare dismiss.
        var (handler, _, page) = Page(DecisionSamples.Row("NeedsOperatorInput"), Questions());
        await page.LoadAsync();

        var dismiss = page.Decision!.Choices.First(c => c.Label == "Dismiss the question");
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)dismiss.Command!)
            .ExecuteAsync(dismiss.Parameter);

        var sent = handler.Requests.Single(r => r.Path.EndsWith("/dismiss-question", StringComparison.Ordinal));
        Assert.Equal("POST", sent.Method);
        Assert.Contains("\"questionId\":\"q-token-scope\"", sent.Body, StringComparison.Ordinal);
        Assert.Contains("\"reason\":", sent.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"reason\":\"\"", sent.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_running_item_has_no_decision_but_still_has_its_lookups()
    {
        // Nobody is blocked on it, so there is nothing to decide — but a running item is exactly the one
        // whose output you want, and the lookups are not behind the card.
        var (_, shell, page) = Page(DecisionSamples.Row("Working"));
        await page.LoadAsync();

        Assert.Null(page.Decision);
        Assert.False(page.HasDecision);

        await page.ShowOutputCommand.ExecuteAsync(null);
        Assert.Single(shell.Sheets);
    }

    [Fact]
    public async Task A_lookup_with_nothing_behind_it_says_so_rather_than_opening_an_empty_sheet()
    {
        // A landed item's diff endpoint answers with nothing at all. A sheet containing that is a screen's
        // worth of chrome around the absence of an answer, and reads as a bug in the app.
        var (_, shell, page) = Page(DecisionSamples.Row("Done"));
        await page.LoadAsync();

        await page.ShowDiffCommand.ExecuteAsync(null);

        Assert.Empty(shell.Sheets);
        Assert.Contains(shell.Toasts, t => t.Contains("No diff recorded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_facts_say_what_the_item_is()
    {
        var (_, _, page) = Page(DecisionSamples.Row("Working"));
        await page.LoadAsync();

        var labels = page.Facts.Select(f => f.Label).ToList();
        Assert.Equal(["PROJECT", "AGENT", "STATE", "PRIORITY", "COST", "BRANCH", "ID"], labels);
        Assert.Equal("$12.44", page.Facts.Single(f => f.Label == "COST").Text);
    }

    private static string Questions() =>
        $$"""
        [{"id":"aa11","workItemId":"{{ItemId}}","questionId":"q-token-scope",
          "questionText":"Per push or per session?","state":"open",
          "askedAt":"2026-09-12T11:30:00+00:00","answeredAt":null,"answerText":null,
          "answeredBy":null,"dismissedAt":null}]
        """;
}
