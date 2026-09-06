using Agnes.Plugins.CodeyBox;

namespace Agnes.Plugins.CodeyBox.Tests;

/// <summary>
/// Gathers a real <see cref="OverviewInputs"/> from a configured orchestrator, and is silent where there
/// is none.
///
/// <para>The canned-shape tests prove the wiring; this proves the premise. Every input the overview is
/// built on is optional on some host and several of them were assumed to be populated on this one —
/// quota history in particular exists only where the statistics plugin is loaded. A screen designed
/// against inputs that turn out to be empty is a screen that renders nothing, and finding that out from
/// the running instance is cheaper than finding it out from the view.</para>
///
/// <para>Read-only throughout: this probe issues GETs and nothing else. The host it runs against is
/// doing real work.</para>
/// </summary>
public sealed class LiveOverviewProbe
{
    [Fact]
    public async Task Gathers_inputs_that_are_worth_building_an_overview_from()
    {
        var options = CodeyBoxOptions.Resolve();
        if (!options.IsConfigured)
        {
            return;
        }

        await using var client = new CodeyBoxClient(options);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        IReadOnlyList<WorkItemRow> items;
        try
        {
            items = await client.ListWorkItemsAsync(cts.Token);
        }
        catch (Exception)
        {
            return;   // configured but not running
        }

        var probes = await client.GetQuotaProbesAsync(cts.Token);
        var concurrency = await client.GetConcurrencyAsync(cts.Token);
        var queue = await client.GetQueueStatusAsync(cts.Token);
        var health = await client.GetTransitionHealthAsync(cts.Token);
        var projects = await client.GetProjectsAsync(cts.Token);

        var live = items.Where(i => !i.IsTerminal || i.IsFailed).ToList();

        var traces = new List<ItemAuditProgress>();
        foreach (var item in live)
        {
            var rows = await client.GetAuditProgressAsync(item.Id, cts.Token);
            if (rows.Count > 0)
            {
                traces.Add(new ItemAuditProgress(item.Id, rows));
            }
        }

        var questions = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in items.Where(i => i.State == "NeedsOperatorInput"))
        {
            var open = (await client.GetQuestionsAsync(item.Id, cts.Token)).Count(q => q.IsOpen);
            if (open > 0)
            {
                questions[item.Id] = open;
            }
        }

        var since = DateTimeOffset.UtcNow.AddDays(-7);
        var rowsOut = new List<QuotaHistoryRow>();
        foreach (var agent in probes.Where(p => p.IsKnown).Select(p => p.Agent).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            rowsOut.AddRange(await client.GetQuotaHistoryAsync(agent, since, cts.Token));
        }

        var burns = QuotaHistoryMap.ToBurnDown(rowsOut);

        var ceilings = projects
            .Where(p => p.AuditMaxIterations > 0)
            .ToDictionary(p => p.Id, p => p.AuditMaxIterations, StringComparer.Ordinal);

        var inputs = new OverviewInputs(
            DateTimeOffset.Now, items, traces, questions, queue, concurrency, probes, burns, health,
            new OverviewHistory(Path.Combine(
                Path.GetTempPath(), $"agnes-overview-{Guid.NewGuid():N}", "history.json")).Read(),
            ceilings);

        // Printed on purpose. A live probe that can legitimately do nothing has to say when it did
        // something, or "passed" is unreadable.
        Console.WriteLine(
            $"[overview] {items.Count} items ({live.Count} live, {traces.Count} traced), " +
            $"{probes.Count(p => p.IsKnown)}/{probes.Count} probes known, {burns.Count} burn-down(s) " +
            $"from {rowsOut.Count} quota rows, {ceilings.Count} project ceiling(s), " +
            $"{questions.Count} item(s) awaiting an answer.");

        Assert.NotEmpty(inputs.Items);
        Assert.Contains(probes, p => p.IsKnown);
        Assert.NotEmpty(rowsOut);
        Assert.NotEmpty(burns);
        Assert.All(burns, b => Assert.NotEmpty(b.Samples));
        Assert.NotEmpty(ceilings);

        try
        {
            var overview = OverviewModel.Build(inputs);
            Assert.False(string.IsNullOrWhiteSpace(overview.Sentence));
            Assert.Equal(5, overview.Vitals.Count);
            Console.WriteLine($"[overview] {overview.Sentence}");
        }
        catch (NotImplementedException)
        {
            // The model lands separately. Tolerated ONLY for that one exception type, and only until it
            // does: anything else from Build is a real failure and must not be swallowed here.
        }
    }
}
