using System.Net;
using System.Text;
using Agnes.Plugins.CodeyBox;

namespace Agnes.Plugins.CodeyBox.Tests;

/// <summary>
/// The REST half of the CodeyBox client. Verified against a stub transport rather than the live
/// orchestrator so the shapes are pinned without needing one running — the wire contract is the thing
/// under test, not the server.
/// </summary>
public class CodeyBoxClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        /// <summary>Request bodies, captured as they are sent. The client disposes each request with its
        /// content, so reading one afterwards is too late.</summary>
        public List<string> Bodies { get; } = [];

        public Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> Respond { get; set; }
            = _ => (HttpStatusCode.OK, "[]");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult() ?? string.Empty);
            var (status, body) = Respond(request);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static CodeyBoxClient New(StubHandler handler)
        => new(new CodeyBoxOptions("http://codeybox.test", "k-123"), handler);

    [Fact]
    public async Task Listing_work_items_reads_the_orchestrators_shape()
    {
        var handler = new StubHandler
        {
            Respond = _ => (HttpStatusCode.OK, """
                [{"id":"43c8ec28aa11","title":"Fix quota detection","state":"Working","agent":"claude",
                  "projectId":"p1","queuePosition":3,"updatedAt":"2026-08-28T06:00:00+00:00","lastError":null}]
                """),
        };
        await using var client = New(handler);

        var items = await client.ListWorkItemsAsync();

        var item = Assert.Single(items);
        Assert.Equal("43c8ec28", item.ShortId);      // abbreviated the way CodeyBox's own tools do
        Assert.Equal("Working", item.State);
        Assert.True(item.IsActive);
        Assert.False(item.IsTerminal);
        Assert.Equal(3, item.QueuePosition);
    }

    [Fact]
    public async Task Every_request_carries_the_bearer_key()
    {
        var handler = new StubHandler();
        await using var client = New(handler);

        await client.ListWorkItemsAsync();

        var auth = Assert.Single(handler.Requests).Headers.Authorization;
        Assert.NotNull(auth);
        Assert.Equal("Bearer", auth.Scheme);
        Assert.Equal("k-123", auth.Parameter);
    }

    [Theory]
    [InlineData("Done", true, false)]
    [InlineData("Cancelled", true, false)]
    [InlineData("AbandonedAfterRecoveryAttempts", true, false)]
    [InlineData("Merged", false, false)]   // deliberately NOT terminal, per the orchestrator
    [InlineData("Working", false, true)]
    [InlineData("Queued", false, false)]
    public void State_is_classified_the_way_the_orchestrator_does(string state, bool terminal, bool active)
    {
        var row = new WorkItemRow("id", "t", state, null, null, 0, DateTimeOffset.UtcNow, null);

        Assert.Equal(terminal, row.IsTerminal);
        Assert.Equal(active, row.IsActive);
    }

    [Fact]
    public async Task Queue_and_item_actions_hit_the_documented_verbs_and_paths()
    {
        var handler = new StubHandler { Respond = _ => (HttpStatusCode.OK, "{}") };
        await using var client = New(handler);

        await client.CancelAsync("wi1");
        await client.RetryAsync("wi2");
        await client.PromoteAsync("wi3");
        await client.PauseQueueAsync("because");
        await client.ResumeQueueAsync();

        var seen = handler.Requests.Select(r => $"{r.Method} {r.RequestUri!.AbsolutePath}").ToArray();
        Assert.Equal(
            [
                "DELETE /workitems/wi1",
                "POST /workitems/wi2/retry",
                "POST /workitems/wi3/promote",
                "POST /queue/pause",
                "POST /queue/resume",
            ],
            seen);
    }

    [Fact]
    public async Task A_failed_action_surfaces_rather_than_being_swallowed()
    {
        // The view model turns this into a status line; the client's job is only to not pretend it worked.
        var handler = new StubHandler { Respond = _ => (HttpStatusCode.Conflict, "{}") };
        await using var client = New(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.RetryAsync("wi1"));
    }

    [Fact]
    public async Task A_missing_stdout_tail_reads_as_empty_not_as_a_failure()
    {
        // An item that has produced nothing yet is ordinary, and the pane should open blank rather than
        // showing an error where the agent's output will appear.
        var handler = new StubHandler { Respond = _ => (HttpStatusCode.NotFound, "") };
        await using var client = New(handler);

        Assert.Equal(string.Empty, await client.GetStdoutTailAsync("wi1"));
    }

    [Fact]
    public async Task Queue_state_is_read_from_the_shape_the_orchestrator_actually_returns()
    {
        // Confirmed against a live instance: /queue/status answers a `state` STRING plus pause metadata,
        // not the `paused` boolean this was first modelled as. The original shape silently read as
        // "not paused" against a queue that was in fact paused.
        var handler = new StubHandler
        {
            Respond = _ => (HttpStatusCode.OK,
                """{"state":"Paused","pausedAt":"2026-08-27T22:43:00+00:00","pausedReason":"budget","refactorGates":[]}"""),
        };
        await using var client = New(handler);

        var status = await client.GetQueueStatusAsync();

        Assert.NotNull(status);
        Assert.True(status.IsPaused);
        Assert.Equal("budget", status.PausedReason);
    }

    [Fact]
    public async Task Pausing_the_queue_sends_the_reason_the_orchestrator_requires()
    {
        // It rejects an empty reason with a 400 — a paused queue nobody can explain later is what that
        // rule exists to prevent — so the reason has to reach the wire, not just the signature.
        var handler = new StubHandler { Respond = _ => (HttpStatusCode.OK, "{}") };
        await using var client = New(handler);

        await client.PauseQueueAsync("draining for a deploy");

        Assert.Contains("draining for a deploy", Assert.Single(handler.Bodies), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unavailable_diagnostic_surface_reads_as_absent_rather_than_failing()
    {
        // Several of the orchestrator's diagnostic endpoints answer 503 when their feature is switched
        // off — capacity and the quota surfaces do exactly that on a real instance — and a panel saying
        // "unavailable" is the honest rendering of that, not an error.
        var handler = new StubHandler { Respond = _ => (HttpStatusCode.ServiceUnavailable, "") };
        await using var client = New(handler);

        Assert.Null(await client.GetCapacityAsync());
        Assert.Null(await client.GetQuotaHistoryAsync());
    }

    [Fact]
    public async Task A_refused_injection_is_reported_rather_than_read_as_success()
    {
        // The orchestrator answers an injection with a receipt, because it can legitimately refuse one —
        // the session may have moved on — and that is not the same as the call failing.
        var handler = new StubHandler
        {
            Respond = _ => (HttpStatusCode.OK,
                """{"accepted":false,"status":"Rejected","error":"session ended"}"""),
        };
        await using var client = New(handler);

        var receipt = await client.InjectAsync("sess-1", "stop and summarise");

        Assert.NotNull(receipt);
        Assert.False(receipt.Accepted);
        Assert.Equal("session ended", receipt.Error);
    }

    [Fact]
    public async Task Fleet_rows_carry_the_budget_position_the_orchestrator_reports()
    {
        var handler = new StubHandler
        {
            Respond = _ => (HttpStatusCode.OK,
                """
                [{"projectId":"p1","displayName":"CodeyBox","queuedCount":10,"inFlightCount":0,
                  "isPaused":false,"hasRecentFailures":false,"monthlySpendUsd":12.5,
                  "monthlyBudgetUsd":100,"budgetThresholdState":"Ok"}]
                """),
        };
        await using var client = New(handler);

        var project = Assert.Single(await client.GetFleetAsync());

        Assert.Equal("CodeyBox", project.DisplayName);
        Assert.Equal(10, project.QueuedCount);
        Assert.Equal("$12.5 / $100", project.Spend);
    }
}
