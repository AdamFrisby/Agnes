namespace Agnes.Plugins.CodeyBox;

/// <summary>
/// Everything one overview gather produced, plus the raw readings the screens around it also want.
/// </summary>
/// <remarks>
/// The overview is not the only thing built from this pass — the desktop's quota strip binds the probes,
/// its header binds the concurrency, and the phone's queue builds its runway from the items and the
/// projects. Handing them back here is what stops each of those asking the orchestrator for the same six
/// things again.
/// </remarks>
public sealed record GatheredOverview(
    Overview Overview,
    IReadOnlyList<WorkItemRow> Items,
    IReadOnlyList<Project> Projects,
    IReadOnlyList<QuotaProbe> Probes,
    Concurrency? Concurrency,
    QueueStatus? Queue);

/// <summary>
/// The overview's data layer: the one pass over the orchestrator that feeds <see cref="OverviewModel"/>.
/// </summary>
/// <remarks>
/// <para><b>Why it is its own object.</b> The gather is the expensive half of the overview — half a dozen
/// fixed reads plus one audit-progress and one agent-runs call per live item — and the rules that keep it
/// affordable are all <em>caches</em>: a trace is refetched only when its item's <c>UpdatedAt</c> moved, a
/// week of quota series is re-read at most once a minute. That state has to live somewhere, and living on
/// a desktop view model meant the second head that wanted an overview had to either re-implement the
/// rules or copy them. So the caches and the pass over them are here, and
/// <see cref="CodeyBoxSectionsViewModel"/> and the Android client are both thin callers.</para>
///
/// <para>Nothing here touches a UI thread, an <c>ObservableObject</c> or Avalonia: it takes a client and
/// returns a value. The only mutable state is the caches, which are local, explicit, and guarded — two
/// gathers can be asked for at once (a feed burst and an idle tick, or a manual reload over either) and
/// they share those caches, so overlapping them would both double the load on the orchestrator and race
/// the dictionaries they exist to save it with.</para>
/// </remarks>
public sealed class OverviewGather
{
    private readonly CodeyBoxClient _client;
    private readonly OverviewHistory _history;
    private readonly SemaphoreSlim _gathering = new(1, 1);

    /// <param name="history">Where the sparkline samples are kept. Injected because the two heads put
    /// their local state in different places — and because a test must never touch the real file.</param>
    public OverviewGather(CodeyBoxClient client, OverviewHistory? history = null)
    {
        _client = client;
        _history = history ?? new OverviewHistory();
    }

    /// <summary>How many audit-progress reads may be in flight at once. The orchestrator serves a single
    /// fleet; a client that fans out over every live item at once is a load spike, not a fast refresh.</summary>
    private const int TraceParallelism = 4;

    /// <summary>How far back the quota series is asked for. A week covers the longest window a provider
    /// publishes (<c>seven_day</c>), so a burn-down never starts mid-window with no history behind it.</summary>
    private static readonly TimeSpan QuotaWindow = TimeSpan.FromDays(7);

    /// <summary>How stale a burn-down may be. Matches the idle refresh, so the series is re-read on the
    /// timer and not on every transition.</summary>
    private static readonly TimeSpan QuotaBurnMaxAge = TimeSpan.FromSeconds(60);

    /// <summary>Audit progress by work-item id, keyed on the item's <c>UpdatedAt</c>.</summary>
    /// <remarks>
    /// The heaviest items answer <c>/audit-progress</c> with 1.2–1.8 MB, and the overview re-reads on every
    /// transition anywhere in the fleet. Without this, one item finishing would re-download the audit
    /// history of every other live item — so a row is refetched only when the item itself has moved, which
    /// is exactly when its trace can have changed.
    /// </remarks>
    private readonly Dictionary<string, (DateTimeOffset At, IReadOnlyList<AuditProgressRow> Rows)> _traces = [];

    private readonly Dictionary<string, (DateTimeOffset At, IReadOnlyList<AgentRun> Runs)> _effort = new(StringComparer.Ordinal);

    private IReadOnlyList<QuotaBurn>? _quotaBurns;
    private DateTimeOffset _quotaBurnsAt;

    /// <summary>
    /// Everything the overview needs, gathered in one pass and handed to the pure model.
    /// </summary>
    /// <remarks>
    /// <para>Every surface in it is optional except the work-item list: quota history, transition health
    /// and concurrency are all switched off on some hosts, so each degrades to null/empty and the model
    /// says so rather than inventing a number.</para>
    ///
    /// <para>Audit progress is read for the live items and the failed family only — a decided item's trace
    /// cannot change, and reading 325 finished items would cost more than the whole rest of the overview
    /// put together.</para>
    /// </remarks>
    public async Task<GatheredOverview> GatherAsync(CancellationToken cancellationToken = default)
    {
        await _gathering.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await GatherOnceAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gathering.Release();
        }
    }

    private async Task<GatheredOverview> GatherOnceAsync(CancellationToken cancellationToken)
    {
        var items = await _client.ListWorkItemsAsync(cancellationToken).ConfigureAwait(false);
        var queue = await _client.GetQueueStatusAsync(cancellationToken).ConfigureAwait(false);
        var concurrency = await _client.GetConcurrencyAsync(cancellationToken).ConfigureAwait(false);
        var probes = await _client.GetQuotaProbesAsync(cancellationToken).ConfigureAwait(false);
        var health = await _client.GetTransitionHealthAsync(cancellationToken).ConfigureAwait(false);
        var projects = await _client.GetProjectsAsync(cancellationToken).ConfigureAwait(false);

        // A failed item is terminal but still the operator's problem, so its trace is what explains why.
        var traced = items.Where(i => !i.IsTerminal || i.IsFailed).ToList();
        var progress = await TracesAsync(traced, cancellationToken).ConfigureAwait(false);
        var questions = await QuestionsAsync(items, cancellationToken).ConfigureAwait(false);
        var quota = await QuotaBurnAsync(probes, cancellationToken).ConfigureAwait(false);
        var effort = await EffortAsync(items, cancellationToken).ConfigureAwait(false);
        var ceilings = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var project in projects.Where(p => p.AuditMaxIterations > 0))
        {
            ceilings[project.Id] = project.AuditMaxIterations;
        }

        var inputs = new OverviewInputs(
            DateTimeOffset.Now,
            items,
            progress,
            questions,
            queue,
            concurrency,
            probes,
            quota,
            health,
            _history.Read(),
            ceilings)
        {
            Effort = effort,
        };

        var built = OverviewModel.Build(inputs);

        // The sample this build contributes is the one the NEXT build reads, so nothing on screen is
        // waiting for it — and it is written here, off whatever thread the caller will marshal from,
        // because this class is the only part of the overview allowed to touch a disk.
        _history.Append(built.Sample);

        return new GatheredOverview(built, items, projects, probes, concurrency, queue);
    }

    /// <summary>
    /// Active agent time per item, for the drain estimate: the last <see cref="OverviewModel.BurnSample"/>
    /// landed items (fetched once each — a finished item's runs do not change) and everything not yet
    /// terminal (re-read while it moves, cached against its UpdatedAt like the traces).
    /// </summary>
    private async Task<IReadOnlyList<ItemEffort>> EffortAsync(
        IReadOnlyList<WorkItemRow> items, CancellationToken cancellationToken)
    {
        var landed = items.Where(i => i.State == "Done").OrderByDescending(i => i.UpdatedAt).Take(OverviewModel.BurnSample).ToList();
        var live = items.Where(i => !i.IsTerminal).ToList();
        var stale = landed.Concat(live)
            .Where(i => !(_effort.TryGetValue(i.Id, out var cached) && cached.At == i.UpdatedAt))
            .ToList();
        var fetched = new System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<AgentRun>>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(
            stale,
            new ParallelOptions { MaxDegreeOfParallelism = TraceParallelism, CancellationToken = cancellationToken },
            async (item, token) =>
            {
                try
                {
                    fetched[item.Id] = await _client.GetAgentRunsAsync(item.Id, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Diagnostic.Report($"agent-history {item.ShortId}", ex);
                }
            }).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var effort = new List<ItemEffort>(landed.Count + live.Count);
        foreach (var item in landed.Concat(live))
        {
            if (fetched.TryGetValue(item.Id, out var runs))
            {
                _effort[item.Id] = (item.UpdatedAt, runs);
            }
            else if (_effort.TryGetValue(item.Id, out var cached))
            {
                runs = cached.Runs;
            }
            else
            {
                continue;
            }

            if (runs.Count > 0)
            {
                effort.Add(new ItemEffort(item.Id, ItemEffort.ActiveTime(runs, now, item.IsActive), item.State == "Done", item.UpdatedAt));
            }
        }

        return effort;
    }

    /// <summary>Audit progress for the items that can still change, capped and cached.</summary>
    private async Task<IReadOnlyList<ItemAuditProgress>> TracesAsync(
        IReadOnlyList<WorkItemRow> items, CancellationToken cancellationToken)
    {
        var fetched = new System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<AuditProgressRow>>(
            StringComparer.Ordinal);

        var stale = items.Where(i => !(_traces.TryGetValue(i.Id, out var cached) && cached.At == i.UpdatedAt)).ToList();

        await Parallel.ForEachAsync(
            stale,
            new ParallelOptions { MaxDegreeOfParallelism = TraceParallelism, CancellationToken = cancellationToken },
            async (item, token) =>
            {
                try
                {
                    fetched[item.Id] = await _client.GetAuditProgressAsync(item.Id, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One item's history failing must not cost the other forty their traces.
                    Diagnostic.Report($"audit-progress {item.ShortId}", ex);
                }
            }).ConfigureAwait(false);

        var traces = new List<ItemAuditProgress>(items.Count);
        foreach (var item in items)
        {
            if (fetched.TryGetValue(item.Id, out var rows))
            {
                _traces[item.Id] = (item.UpdatedAt, rows);
            }
            else if (_traces.TryGetValue(item.Id, out var cached))
            {
                rows = cached.Rows;
            }
            else
            {
                continue;
            }

            if (rows.Count > 0)
            {
                traces.Add(new ItemAuditProgress(item.Id, rows));
            }
        }

        // Items that have left the live set never come back to it; keeping their traces would grow the
        // cache by the size of the whole queue's history over a long-running session.
        var live = items.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in _traces.Keys.Where(id => !live.Contains(id)).ToList())
        {
            _traces.Remove(gone);
        }

        return traces;
    }

    /// <summary>
    /// Open-question counts, for the items that say they are waiting on one. Read only for
    /// <c>NeedsOperatorInput</c>: the endpoint is per item, and the state is the orchestrator's own claim
    /// that there is something to find.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, int>> QuestionsAsync(
        IReadOnlyList<WorkItemRow> items, CancellationToken cancellationToken)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var item in items.Where(i => i.State.Equals("NeedsOperatorInput", StringComparison.Ordinal)))
        {
            try
            {
                var questions = await _client.GetQuestionsAsync(item.Id, cancellationToken).ConfigureAwait(false);
                var open = questions.Count(q => q.IsOpen);
                if (open > 0)
                {
                    counts[item.Id] = open;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Diagnostic.Report($"questions {item.ShortId}", ex);
            }
        }

        return counts;
    }

    /// <summary>
    /// The burn-downs, one series request per agent that actually reported a reading. Absent everywhere on
    /// a host without the statistics plugin, which is why this returns empty rather than failing the load.
    /// </summary>
    private async Task<IReadOnlyList<QuotaBurn>> QuotaBurnAsync(
        IReadOnlyList<QuotaProbe> probes, CancellationToken cancellationToken)
    {
        // A week of samples is 31 753 rows — about 8 MB — across the four agents this instance probes, and
        // a busy fleet can ask for a gather every five seconds. The series moves on the sampler's clock,
        // not on work-item transitions, so re-reading it faster than it is written buys nothing and costs
        // that 8 MB each time.
        if (_quotaBurns is { } cached && DateTimeOffset.UtcNow - _quotaBurnsAt < QuotaBurnMaxAge)
        {
            return cached;
        }

        var since = DateTimeOffset.UtcNow - QuotaWindow;
        var rows = new List<QuotaHistoryRow>();

        foreach (var agent in probes.Where(p => p.IsKnown).Select(p => p.Agent)
                     .Where(a => !string.IsNullOrWhiteSpace(a))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                rows.AddRange(await _client
                    .GetQuotaHistoryAsync(agent, since, cancellationToken: cancellationToken)
                    .ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Diagnostic.Report($"quota-history {agent}", ex);
            }
        }

        _quotaBurns = QuotaHistoryMap.ToBurnDown(rows, probes, DateTimeOffset.UtcNow);
        _quotaBurnsAt = DateTimeOffset.UtcNow;
        return _quotaBurns;
    }
}
