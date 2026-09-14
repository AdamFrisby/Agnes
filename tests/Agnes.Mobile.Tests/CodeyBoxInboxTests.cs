using Agnes.App.Mobile.Services;
using Agnes.App.Mobile.ViewModels;
using Agnes.Plugins.CodeyBox;
using Agnes.Plugins.CodeyBox.Tests;
using Agnes.Ui.Core;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.Mobile.Tests;

/// <summary>
/// The fleet in the Inbox, and the queue's fold.
/// </summary>
/// <remarks>
/// The Inbox is the phone's whole proposition — an agent blocked on a person is an agent doing nothing —
/// and a fleet parked on a question is exactly that, one service over. These pin the projection (which
/// items become rows, what each row offers) and the one thing the phone does that the desktop pane does
/// not: fold every queue section, not just the dispatch order.
/// </remarks>
[Collection(AvaloniaCollection.Name)]
public sealed class CodeyBoxInboxTests : IDisposable
{
    private readonly string _state = Path.Combine(
        Path.GetTempPath(), "agnes-codeybox-inbox-" + Guid.NewGuid().ToString("n"));

    public CodeyBoxInboxTests(AvaloniaSession avalonia)
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

    private const string ParkedId = "aaaaaaaa1111111111111111111111a1";
    private const string FailedId = "bbbbbbbb2222222222222222222222b2";

    /// <summary>A fleet with one item parked on a question and one that failed.</summary>
    private static FleetHandler Waiting() => new()
    {
        ItemsBody = $$"""
            [{"id":"{{ParkedId}}","title":"Decide the token scope","state":"NeedsOperatorInput",
              "agent":"claude","projectId":"codeybox-self","queuePosition":0,
              "updatedAt":"2026-09-12T11:00:00+00:00","lastError":null},
             {"id":"{{FailedId}}","title":"Land the upstream push retry","state":"AuditFailed",
              "agent":"claude","projectId":"codeybox-self","queuePosition":0,
              "updatedAt":"2026-09-12T10:00:00+00:00","lastError":"never converged"},
             {"id":"cccccccc3333333333333333333333c3","title":"Busy","state":"Working",
              "agent":"codex","projectId":"codeybox-self","queuePosition":0,
              "updatedAt":"2026-09-12T12:00:00+00:00","lastError":null}]
            """,
        QuestionsBody = $$"""
            [{"id":"q","workItemId":"{{ParkedId}}","questionId":"q-scope",
              "questionText":"Per push or per session?","state":"open",
              "askedAt":"2026-09-12T11:30:00+00:00","answeredAt":null,"answerText":null,
              "answeredBy":null,"dismissedAt":null}]
            """,
    };

    [Fact]
    public async Task A_parked_item_becomes_a_question_row_and_a_failed_one_becomes_a_decision()
    {
        var (_, fleet) = Fleet.Offline(Waiting());

        await fleet.RefreshNeedsYouAsync();

        Assert.Equal(2, fleet.NeedsYou.Count);
        Assert.Equal("Question", fleet.NeedsYou[0].Kind);
        Assert.Equal("Per push or per session?", fleet.NeedsYou[0].Title);
        Assert.Equal("Failed", fleet.NeedsYou[1].Kind);

        // The working item is not waiting on anybody, and a row that says so is a row in the way.
        Assert.DoesNotContain(fleet.NeedsYou, r => r.Item.Title == "Busy");
    }

    [Fact]
    public async Task The_inbox_read_is_cheap_enough_to_run_with_the_tab_closed()
    {
        // It has to work whether or not the fleet screen is open — "something needs you" is the fact the
        // phone exists to carry. So it costs one list read plus one per parked item, and nothing else:
        // no audit histories, no agent runs, no quota series.
        var handler = Waiting();
        var (_, fleet) = Fleet.Offline(handler);

        await fleet.RefreshNeedsYouAsync();

        Assert.Equal(
            ["/workitems", $"/workitems/{ParkedId}/questions"],
            handler.Requests.Select(r => r.Path));
    }

    [Fact]
    public void A_question_row_is_answerable_where_a_failure_row_is_not()
    {
        // Two shapes, because there are two things being asked for. A question takes prose and can be
        // answered from the list; a failure needs the evidence, so its row opens the card instead.
        var parked = DecisionSamples.Row("NeedsOperatorInput");
        var question = DecisionSamples.Question();
        var asked = new CodeyBoxNeedsRow(parked, question);

        Assert.True(asked.IsQuestion);
        Assert.Equal("Question", asked.Kind);
        Assert.Equal(question.QuestionText, asked.Title);
        Assert.Equal(parked.Title, asked.Detail);
        Assert.Equal("Open the item", asked.OpenVerb);

        var failed = new CodeyBoxNeedsRow(
            DecisionSamples.Row("AuditFailed", "never converged"), null);

        Assert.False(failed.IsQuestion);
        Assert.Equal("Failed", failed.Kind);
        Assert.Equal("never converged", failed.Detail);
        Assert.Equal("Decide what to do", failed.OpenVerb);
    }

    [Fact]
    public async Task The_inbox_follows_whatever_the_fleet_last_found()
    {
        var (shell, fleet) = Fleet.Offline(Waiting());
        var inbox = Inbox(shell, fleet);

        Assert.False(inbox.HasCodeyBoxRows);
        Assert.True(inbox.IsEmpty);

        // A live projection, like the blocked list: the fleet re-reads on its own schedule and the Inbox
        // follows, rather than polling a second time.
        await fleet.RefreshNeedsYouAsync();

        Assert.Equal(2, inbox.CodeyBoxRows.Count);
        Assert.True(inbox.HasCodeyBoxRows);
        Assert.False(inbox.IsEmpty);
    }

    [Fact]
    public async Task Answering_from_the_inbox_opens_the_sheet_and_dismissing_posts_a_reason()
    {
        var handler = Waiting();
        var (shell, fleet) = Fleet.Offline(handler);
        var inbox = Inbox(shell, fleet);
        await fleet.RefreshNeedsYouAsync();

        var question = inbox.CodeyBoxRows.Single(r => r.IsQuestion);
        inbox.AnswerCodeyBoxCommand.Execute(question);
        Assert.IsType<CodeyBoxAnswerSheetViewModel>(shell.Sheets.Single());

        await inbox.DismissCodeyBoxCommand.ExecuteAsync(question);

        var sent = handler.Requests.Single(r => r.Method == "POST");
        Assert.Equal($"/workitems/{ParkedId}/dismiss-question", sent.Path);
        Assert.Contains("\"reason\":", sent.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"reason\":\"\"", sent.Body, StringComparison.Ordinal);
    }

    private static InboxViewModel Inbox(StubShell shell, CodeyBoxViewModel fleet)
    {
        var sessions = new SessionsViewModel(
            shell, shell.Hosts, new FilePromptStore(JsonStore.PathFor("p.json")),
            new FilePermissionPolicy(JsonStore.PathFor("pp.json")), NullNotifier.Instance);
        return new InboxViewModel(shell, shell.Hosts, sessions, fleet);
    }

    [Fact]
    public void Every_queue_section_folds_past_the_preview_and_a_tail_of_one_never_does()
    {
        // The desktop pane folds only Next, because a 1200px column can carry a waiting group whole. A
        // phone cannot carry any of them, so all of them fold — by the plugin's own number.
        var sections = CodeyBoxViewModel.BuildSections(BoardSamples.Busy());

        var next = sections.Single(s => s.Header.StartsWith("Next", StringComparison.Ordinal));
        Assert.Equal(Board.NextPreview, next.Rows.Count);
        Assert.True(next.HasMore);
        Assert.Equal("Show 11 more", next.MoreLabel);

        next.ShowMoreCommand.Execute(null);
        Assert.Equal(16, next.Rows.Count);
        Assert.False(next.HasMore);

        // Replacing one row with a button that reveals one row is a worse row.
        var one = new CodeyBoxQueueSection("Now", string.Empty, [.. BoardSamples.Fleet().Now.Take(1)]);
        Assert.False(one.HasMore);
    }

    [Fact]
    public void A_section_says_its_shared_reason_once()
    {
        // Sixteen rows each repeating "waiting for an audit slot for 11h 06m" is one fact printed sixteen
        // times, in the space their titles needed.
        var next = CodeyBoxViewModel.BuildSections(BoardSamples.Busy())
            .Single(s => s.Header.StartsWith("Next", StringComparison.Ordinal));

        Assert.True(next.HasSharedWhy);
        Assert.Contains("audit slot", next.SharedWhy, StringComparison.Ordinal);
        Assert.DoesNotContain(
            next.Rows,
            r => r.ShownWhy.Contains("waiting for an audit slot", StringComparison.Ordinal));
    }

    [Fact]
    public void A_trace_row_says_the_most_urgent_true_thing_about_itself()
    {
        // A chip has room for one word, and "blocked" on an item that is also wedged is the less useful
        // half of the truth. The order is ItemTrace.Rank's.
        Assert.Equal("wedged", Row(Motion.Wedged, Convergence.Converging).StateWord);
        Assert.Equal("oscillating", Row(Motion.Moving, Convergence.Oscillating).StateWord);
        Assert.Equal("stuck", Row(Motion.Moving, Convergence.Stuck).StateWord);
        Assert.Equal("needs you", Row(Motion.Blocked, Convergence.New, needsPerson: true).StateWord);
        Assert.Equal("blocked", Row(Motion.Blocked, Convergence.New).StateWord);
        Assert.Equal("parked", Row(Motion.Parked, Convergence.New).StateWord);
        Assert.Equal("moving", Row(Motion.Moving, Convergence.Converging).StateWord);

        // …and wears the same hue the plugin's own MotionDot would draw.
        Assert.True(Row(Motion.Moving, Convergence.Converging).IsWorking);
        Assert.True(Row(Motion.Blocked, Convergence.New).IsAttention);
        Assert.True(Row(Motion.Wedged, Convergence.New).IsError);
    }

    private static CodeyBoxTraceRow Row(
        Motion motion, Convergence shape, bool needsPerson = false, bool nearCeiling = false)
        => new(new ItemTrace(
            DecisionSamples.Row("Working"), [], 25, motion, shape, "because", nearCeiling, needsPerson,
            TimeSpan.FromMinutes(4), 0));
}
