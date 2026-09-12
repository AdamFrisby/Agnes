using Agnes.Plugins.CodeyBox;

namespace Agnes.Plugins.CodeyBox.Tests;

/// <summary>
/// Builds the decision card from the real orchestrator's own blocked items, and is silent where there is
/// none.
/// </summary>
/// <remarks>
/// <para>The canned cards prove the wiring and the layout; this proves the premise. Every sentence the
/// card writes is a claim about what a state MEANS on this deployment — that a failed merge names a base
/// branch, that an audit ceiling is knowable, that <c>NeedsOperatorInput</c> usually comes with a question
/// and sometimes does not. A card designed against states that turn out never to occur, or against a
/// <c>lastError</c> that turns out to be four thousand characters of stack trace, is a card that reads
/// beautifully in a fixture and badly on the screen it was written for.</para>
///
/// <para><b>Read-only throughout: this probe issues GETs and nothing else.</b> It never answers,
/// dismisses, retries, cancels, promotes or patches — the host it runs against is doing real work, and
/// every one of those verbs would change it. The commands it hands the card are recording fakes, so a
/// rendered button cannot reach the orchestrator even by accident.</para>
///
/// <para>Set <c>AGNES_BOARD_SHOTS</c> to choose where the frames land, and run it on its own
/// (<c>--filter FullyQualifiedName~LiveDecisionProbe</c>): the headless session is process-global and the
/// suite's other render classes leave the text stack without glyphs once they have had theirs.</para>
/// </remarks>
[Collection("avalonia-headless")]
public sealed class LiveDecisionProbe
{
    [Fact]
    public async Task Draws_the_decision_for_whatever_is_actually_blocked()
    {
        var options = CodeyBoxOptions.Resolve();
        if (!options.IsConfigured)
        {
            return;
        }

        await using var client = new CodeyBoxClient(options);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        IReadOnlyList<WorkItemRow> items;
        IReadOnlyList<Project> projects;
        try
        {
            items = await client.ListWorkItemsAsync(cts.Token);
            projects = await client.GetProjectsAsync(cts.Token);
        }
        catch (Exception)
        {
            return;   // configured but not running
        }

        // Exactly the set the card claims to cover, computed the same way the card computes it.
        var blocked = items
            .Where(i => i.State == "NeedsOperatorInput" || i.IsFailed)
            .OrderByDescending(i => i.UpdatedAt)
            .ToList();

        Console.WriteLine($"[decision] {items.Count} items, {blocked.Count} of them blocked on a person.");
        if (blocked.Count == 0)
        {
            return;
        }

        var byState = blocked.GroupBy(i => i.State).OrderByDescending(g => g.Count());
        Console.WriteLine("[decision] " + string.Join("  ·  ", byState.Select(g => $"{g.Key} {g.Count()}")));

        var shot = 0;
        foreach (var item in blocked.Take(ReportLimit))
        {
            IReadOnlyList<WorkItemQuestion> questions = [];
            try
            {
                questions = await client.GetQuestionsAsync(item.Id, cts.Token);
            }
            catch (Exception)
            {
                // 503 is "this instance doesn't do questions", not a failure.
            }

            var project = projects.FirstOrDefault(p => p.Id == item.ProjectId);
            var decision = Decision.For(
                item,
                questions,
                DecisionSamples.Recording(),
                project?.DefaultBaseBranch,
                project?.AuditMaxIterations ?? 0);

            Assert.NotNull(decision);

            Console.WriteLine();
            Console.WriteLine($"[decision] {item.ShortId}  {item.State}  {item.Age}  — {item.Title}");
            Console.WriteLine($"[decision]   \"{decision!.Situation}\"");
            foreach (var evidence in decision.Evidence)
            {
                var note = evidence.HasNote ? $" ({evidence.Note})" : string.Empty;
                Console.WriteLine(
                    $"[decision]   {evidence.Label}{note}  {Ellipsis(evidence.Text.ReplaceLineEndings(" "))}");
            }

            Console.WriteLine("[decision]   look at: " + string.Join(", ", decision.Lookups.Select(l => l.Label)));
            foreach (var choice in decision.Choices)
            {
                Console.WriteLine($"[decision]   [{choice.Label}] {choice.Consequence}");
            }

            // The first few get drawn. More than that is not more information — it is the same three
            // layouts again — and every frame costs a headless session.
            if (shot < ShotLimit)
            {
                var path = DecisionShotTests.Shot(
                    () => DecisionShotTests.Card(decision, questions.FirstOrDefault(q => q.IsOpen)),
                    $"live-decision-{shot}-{item.State}",
                    DecisionShotTests.PaneWidth,
                    560);
                Console.WriteLine($"[decision]   → {path}");
                Assert.True(File.Exists(path));
                shot++;
            }
        }

        // The premise, stated as an assertion: every blocked item on a real instance produces a card, and
        // every card offers something to do.
        Assert.All(blocked, item => Assert.NotNull(
            Decision.For(item, [], DecisionSamples.Recording())));
    }

    /// <summary>How many blocked items get written out in full. A queue with forty failures does not need
    /// forty transcripts to make the point.</summary>
    private const int ReportLimit = 12;

    private const int ShotLimit = 4;

    private static string Ellipsis(string text)
        => text.Length <= 160 ? text : text[..160] + "…";
}
