using System.Net;
using System.Text;
using Agnes.Plugins.CodeyBox;

namespace Agnes.Plugins.CodeyBox.Tests;

/// <summary>
/// What the overview asks the orchestrator for, and what it refuses to ask twice.
///
/// <para>The gather is the expensive half of this screen: a dozen fixed reads plus one audit-progress
/// call per live item, against a host that is busy running the fleet. Every rule below exists because the
/// naive version of it re-downloads 1.8 MB of audit history for an item that has not moved.</para>
/// </summary>
public class OverviewGatherTests
{
    /// <summary>A stub that answers by route, and counts what was asked.</summary>
    private sealed class RoutingHandler : HttpMessageHandler
    {
        /// <summary>Guarded, because the gather deliberately fans out over the live items: an unlocked
        /// list here loses requests and the count under test silently comes out low.</summary>
        private readonly Lock _gate = new();

        private readonly List<string> _paths = [];

        public IReadOnlyList<string> Paths
        {
            get { lock (_gate) { return [.. _paths]; } }
        }

        public void ClearPaths()
        {
            lock (_gate)
            {
                _paths.Clear();
            }
        }

        public string ItemsBody { get; set; } = "[]";

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            lock (_gate)
            {
                _paths.Add(uri.AbsolutePath);
            }

            var body = uri.AbsolutePath switch
            {
                "/workitems" => ItemsBody,
                "/queue/status" => QueueBody,
                "/concurrency" => ConcurrencyBody,
                "/quota" => QuotaBody,
                "/fleet/transition-health" => HealthBody,
                "/projects" => ProjectsBody,
                "/quota/history" => QuotaHistoryBody,
                var p when p.EndsWith("/audit-progress", StringComparison.Ordinal) => """{"progress":[]}""",
                var p when p.EndsWith("/questions", StringComparison.Ordinal) => "[]",
                _ => "{}",
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private const string QueueBody = """{"state":"Running","pausedAt":null,"pausedReason":null}""";

    private const string ConcurrencyBody =
        """{"globalMaxConcurrent":3,"currentlyRunningTotal":1,"perAgentCaps":{}}""";

    private const string QuotaBody =
        """
        {"probes":[{"agent":"claude","modelId":"sonnet","billing":"Subscription","paused":false,
          "pausedReason":null,"wouldAllow":true,"observedFailuresLast60m":[],
          "latestSnapshot":{"availablePct":55.5,"isKnown":true,"resetAt":null}}]}
        """;

    private const string HealthBody =
        """{"score":0.95,"infraFailureRate":0.02,"totalTransitions":40,"worstStage":"audit"}""";

    private const string ProjectsBody =
        """
        [{"id":"p1","displayName":"P","repositoryUrl":null,"defaultBaseBranch":"main",
          "defaultAgent":"claude","auditMaxIterations":25,"auditTypes":[]}]
        """;

    private const string QuotaHistoryBody = """{"count":0,"rows":[]}""";

    private static string Item(string id, string state, string updatedAt) =>
        $$"""
          {"id":"{{id}}","title":"{{id}}","state":"{{state}}","agent":"claude","projectId":"p1",
           "queuePosition":0,"updatedAt":"{{updatedAt}}","lastError":null}
          """;

    private static CodeyBoxSectionsViewModel New(RoutingHandler handler, out CodeyBoxClient client)
    {
        client = new CodeyBoxClient(new CodeyBoxOptions("http://codeybox.test", "k"), handler);
        return new CodeyBoxSectionsViewModel(
            client,
            action => { action(); return Task.CompletedTask; },
            history: new OverviewHistory(Path.Combine(
                Path.GetTempPath(), $"agnes-overview-{Guid.NewGuid():N}", "history.json")));
    }

    private const string Stamp = "2026-09-06T09:00:00+00:00";
    private const string Moved = "2026-09-06T09:05:00+00:00";

    [Fact]
    public async Task Audit_progress_is_read_for_the_live_items_and_the_failed_family_only()
    {
        var handler = new RoutingHandler
        {
            ItemsBody = "[" + string.Join(",",
            [
                Item("live-1", "Working", Stamp),
                Item("live-2", "Auditing", Stamp),
                Item("failed", "AuditFailed", Stamp),      // terminal, but still the operator's problem
                Item("done-1", "Done", Stamp),             // decided; its trace cannot change
                Item("done-2", "Cancelled", Stamp),
            ]) + "]",
        };

        var sections = New(handler, out var client);
        await using (client)
        await using (sections)
        {
            await sections.LoadAsync(CodeyBoxSection.Dashboard);
        }

        var traced = handler.Paths.Where(p => p.EndsWith("/audit-progress", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, traced.Count);
        Assert.Contains("/workitems/live-1/audit-progress", traced);
        Assert.Contains("/workitems/failed/audit-progress", traced);
        Assert.DoesNotContain("/workitems/done-1/audit-progress", traced);
    }

    [Fact]
    public async Task An_item_that_has_not_moved_is_not_read_again()
    {
        var handler = new RoutingHandler
        {
            ItemsBody = "[" + string.Join(",",
            [
                Item("still", "Working", Stamp),
                Item("moves", "Working", Stamp),
            ]) + "]",
        };

        var sections = New(handler, out var client);
        await using (client)
        await using (sections)
        {
            await sections.LoadAsync(CodeyBoxSection.Dashboard);
            Assert.Equal(2, handler.Paths.Count(p => p.EndsWith("/audit-progress", StringComparison.Ordinal)));

            // One item transitions; the other is byte-for-byte the row it was.
            handler.ClearPaths();
            handler.ItemsBody = "[" + string.Join(",",
            [
                Item("still", "Working", Stamp),
                Item("moves", "Auditing", Moved),
            ]) + "]";

            await sections.LoadAsync(CodeyBoxSection.Dashboard, force: true);
        }

        // The cache is keyed on UpdatedAt, which is the only thing that can have changed the trace.
        var traced = handler.Paths.Where(p => p.EndsWith("/audit-progress", StringComparison.Ordinal)).ToList();
        Assert.Equal(["/workitems/moves/audit-progress"], traced);
    }

    [Fact]
    public async Task Questions_are_read_only_for_the_items_that_say_they_are_waiting_on_one()
    {
        var handler = new RoutingHandler
        {
            ItemsBody = "[" + string.Join(",",
            [
                Item("asking", "NeedsOperatorInput", Stamp),
                Item("busy", "Working", Stamp),
            ]) + "]",
        };

        var sections = New(handler, out var client);
        await using (client)
        await using (sections)
        {
            await sections.LoadAsync(CodeyBoxSection.Dashboard);
        }

        Assert.Equal(
            ["/workitems/asking/questions"],
            handler.Paths.Where(p => p.EndsWith("/questions", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task The_fixed_reads_are_made_once_each_and_the_quota_series_once_per_known_agent()
    {
        var handler = new RoutingHandler { ItemsBody = "[" + Item("live", "Working", Stamp) + "]" };

        var sections = New(handler, out var client);
        await using (client)
        await using (sections)
        {
            await sections.LoadAsync(CodeyBoxSection.Dashboard);
        }

        foreach (var path in new[] { "/workitems", "/queue/status", "/concurrency", "/quota",
                                     "/fleet/transition-health", "/projects" })
        {
            Assert.Equal(1, handler.Paths.Count(p => p == path));
        }

        // One probe reported a number, so one series is asked for. A probe that reported nothing has no
        // history worth drawing and is not worth a round trip.
        Assert.Equal(1, handler.Paths.Count(p => p == "/quota/history"));
    }

    [Fact]
    public async Task A_transition_only_nudges_the_overview_while_it_is_the_section_on_screen()
    {
        var handler = new RoutingHandler { ItemsBody = "[" + Item("live", "Working", Stamp) + "]" };

        var sections = New(handler, out var client);
        await using (client)
        await using (sections)
        {
            sections.Section = CodeyBoxSection.Queue;
            sections.StartOverviewRefresh();
            sections.NoteWorkItemsChanged();

            // Well inside the debounce window, and the section is not visible in any case: refreshing a
            // panel nobody is looking at is load on the orchestrator and nothing else.
            await Task.Delay(200);
            Assert.Empty(handler.Paths);
        }
    }
}
