using Agnes.Plugins.CodeyBox;

namespace Agnes.Plugins.CodeyBox.Tests;

/// <summary>
/// Reads real audit verdicts from a configured orchestrator, and is silent where there is none.
///
/// <para>The canned-shape tests prove the mapping. This proves the contract: that the endpoint exists,
/// that the truncation flags mean what the field names say, and that a truncated finding really does come
/// back whole from the per-row detail route. Every one of those was assumed at some point in getting
/// here, and the assumptions that were checked are the ones that turned out wrong.</para>
/// </summary>
public sealed class LiveAuditProgressProbe
{
    [Fact]
    public async Task Reads_verdicts_and_untruncates_a_finding_on_demand()
    {
        var options = CodeyBoxOptions.Resolve();
        if (!options.IsConfigured)
        {
            return;
        }

        await using var client = new CodeyBoxClient(options);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        IReadOnlyList<WorkItemRow> items;
        try
        {
            items = await client.ListWorkItemsAsync(cts.Token);
        }
        catch (Exception)
        {
            return;   // configured but not running
        }

        // Scan far enough to actually reach one. Truncation is rare — the first item in list order with a
        // truncated BLOCKING finding sits at index 174 on this instance — so an earlier version of this
        // probe stopped at 40, asserted the cheap invariants, and passed in 325ms without ever testing
        // the thing it is named for. A probe that green-lights by not looking is worse than no probe.
        var itemsWithRows = 0;
        var blockedIterations = 0;

        foreach (var item in items)
        {
            var rows = await client.GetAuditProgressAsync(item.Id, cts.Token);
            if (rows.Count == 0)
            {
                continue;
            }

            itemsWithRows++;

            // Whatever else is true, an iteration that reports blocking findings must carry them.
            foreach (var row in rows.Where(r => r.Blocked))
            {
                blockedIterations++;
                Assert.NotEmpty(row.Blocking);
                Assert.All(row.Blocking, f => Assert.False(string.IsNullOrWhiteSpace(f.Title)));
            }

            var truncatedRow = rows.FirstOrDefault(r => r.Blocking.Any(f => f.DescriptionTruncated));
            if (truncatedRow is null)
            {
                continue;
            }

            var short_ = truncatedRow.Blocking.First(f => f.DescriptionTruncated);
            Assert.True(short_.Description.Length < short_.DescriptionLength);

            var full = await client.GetAuditProgressRowAsync(item.Id, truncatedRow.Id, cts.Token);
            Assert.NotNull(full);

            var whole = full!.Blocking.First(f => f.Title == short_.Title);
            Assert.False(whole.DescriptionTruncated);
            Assert.Equal(whole.DescriptionLength, whole.Description.Length);
            Assert.True(whole.Description.Length > short_.Description.Length);
            // Printed on purpose. A live probe that can legitimately do nothing needs to say when it did
            // something, or "passed" is unreadable — this one ran in half a second and looked like a
            // no-op until the line proved otherwise.
            Console.WriteLine(
                $"[audit-progress] untruncated {short_.Description.Length:N0} -> " +
                $"{whole.Description.Length:N0} of {whole.DescriptionLength:N0} chars ({whole.AuditorName})");
            return;
        }

        // Reached only when the whole instance held no truncated finding. The invariants above still ran,
        // and saying so is the point: this must never look like a pass that proved the round trip.
        Assert.True(itemsWithRows > 0, "The orchestrator returned no audit progress for any work item.");
        Assert.True(
            blockedIterations > 0,
            $"Checked {itemsWithRows} items with audit rows and found no blocked iteration, so the " +
            "blocking-findings invariant was never exercised.");
    }
}
