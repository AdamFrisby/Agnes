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
        public Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> Respond { get; set; }
            = _ => (HttpStatusCode.OK, "[]");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
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
        await client.SetQueuePausedAsync(true);

        var seen = handler.Requests.Select(r => $"{r.Method} {r.RequestUri!.AbsolutePath}").ToArray();
        Assert.Equal(
            [
                "DELETE /workitems/wi1",
                "POST /workitems/wi2/retry",
                "POST /workitems/wi3/promote",
                "POST /queue/pause",
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
}
