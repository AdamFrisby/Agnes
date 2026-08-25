using System.Collections.ObjectModel;
using Agnes.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Agnes.Ui.Core.ViewModels;

/// <summary>The kinds of token a session consumes, in the order a panel lists them.</summary>
public enum TokenKind
{
    /// <summary>Fresh input the model had to read.</summary>
    Input,

    /// <summary>Input served from the prompt cache — the cheap majority of a long session.</summary>
    CacheRead,

    /// <summary>Input written into the prompt cache.</summary>
    CacheWrite,

    /// <summary>Tokens the model produced.</summary>
    Output,
}

/// <summary>One row of the usage panel: a token kind, totalled over each window.</summary>
public sealed record UsageStatRow(TokenKind Kind, long Day, long Week, long Lifetime)
{
    public string Label => Kind switch
    {
        TokenKind.Input => "Input",
        TokenKind.CacheRead => "Cache read",
        TokenKind.CacheWrite => "Cache write",
        _ => "Output",
    };

    public string DayText => Format(Day);
    public string WeekText => Format(Week);
    public string LifetimeText => Format(Lifetime);

    /// <summary>Compact enough for a sidebar column: 1.2M, 943k, 812. Tokens are counted in the
    /// millions over a long session, and a full-precision number would just be a column of digits.</summary>
    public static string Format(long value) => value switch
    {
        >= 1_000_000_000 => $"{value / 1_000_000_000d:0.#}B",
        >= 1_000_000 => $"{value / 1_000_000d:0.#}M",
        >= 10_000 => $"{value / 1_000d:0}k",
        >= 1_000 => $"{value / 1_000d:0.#}k",
        _ => value.ToString("N0"),
    };
}

/// <summary>
/// What a session has actually consumed, totalled over the last day, the last week, and its whole life.
/// </summary>
/// <remarks>
/// The context meter answers "how full is the window right now"; this answers the other question a
/// long-running session raises — "what has this cost me, and is it accelerating". They are different
/// measurements and must not be confused: occupancy is a level and is never added up, while the figures
/// here are per-call consumption and are only meaningful added up. See <see cref="UsageMetrics"/>.
///
/// <para>Built entirely from the event log the client already replays, so it needs nothing from the host
/// and is identical on every client watching the session. The windows come off each event's own
/// timestamp rather than arrival time, so a session opened today still reports last week correctly.</para>
///
/// <para>Kept in hourly buckets rather than as a list of reports: a busy session emits a usage event per
/// model call, and a week of them would be tens of thousands of rows held forever to answer three
/// numbers. An hour is finer than any window shown here needs, and buckets older than the longest
/// window are dropped as they age out — so the memory is bounded by the window, not by the session.</para>
///
/// <para>Only what an agent actually reported is counted. Adapters over ACP report occupancy and cost
/// but no breakdown, so for those <see cref="HasBreakdown"/> stays false and the panel stays hidden
/// rather than showing a table of zeroes that looks like a claim.</para>
/// </remarks>
public sealed class SessionUsageStats : ObservableObject
{
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);
    private static readonly TimeSpan Week = TimeSpan.FromDays(7);

    private sealed class Bucket
    {
        public long Input;
        public long CacheRead;
        public long CacheWrite;
        public long Output;
    }

    private readonly SortedDictionary<DateTimeOffset, Bucket> _hours = [];
    private readonly Bucket _lifetime = new();
    private readonly Func<DateTimeOffset> _now;

    /// <param name="now">The clock, injected so a test can place events in the past deliberately.</param>
    public SessionUsageStats(Func<DateTimeOffset>? now = null) => _now = now ?? (() => DateTimeOffset.Now);

    /// <summary>Whether any agent has reported a per-kind breakdown yet.</summary>
    public bool HasBreakdown { get; private set; }

    /// <summary>The rows to show, one per token kind, recomputed on demand.</summary>
    public ObservableCollection<UsageStatRow> Rows { get; } = [];

    /// <summary>Every kind added together — the headline figure.</summary>
    public long TotalLifetime => _lifetime.Input + _lifetime.CacheRead + _lifetime.CacheWrite + _lifetime.Output;

    public string TotalText => UsageStatRow.Format(TotalLifetime);

    /// <summary>Folds one reported usage event in. Reports with no breakdown are ignored rather than
    /// counted as zero: an adapter that never reports input tokens has not consumed none.</summary>
    public void Add(UsageMetrics metrics, DateTimeOffset when)
    {
        if (metrics is null || !(metrics.HasTokenBreakdown || metrics.OutputTokens is not null))
        {
            return;
        }

        var hour = new DateTimeOffset(when.Year, when.Month, when.Day, when.Hour, 0, 0, when.Offset);
        if (!_hours.TryGetValue(hour, out var bucket))
        {
            bucket = new Bucket();
            _hours[hour] = bucket;
        }

        Accumulate(bucket, metrics);
        Accumulate(_lifetime, metrics);
        HasBreakdown |= metrics.HasTokenBreakdown;
        Prune();
        Refresh();
    }

    private static void Accumulate(Bucket bucket, UsageMetrics m)
    {
        bucket.Input += m.InputTokens ?? 0;
        bucket.CacheRead += m.CacheReadTokens ?? 0;
        bucket.CacheWrite += m.CacheWriteTokens ?? 0;
        bucket.Output += m.OutputTokens ?? 0;
    }

    // Lifetime totals are kept separately, so an aged-out bucket costs nothing but the windowed detail.
    private void Prune()
    {
        var cutoff = _now() - Week;
        while (_hours.Count > 0 && _hours.Keys.First() < cutoff)
        {
            _hours.Remove(_hours.Keys.First());
        }
    }

    private void Refresh()
    {
        var now = _now();
        var day = Sum(now - Day);
        var week = Sum(now - Week);

        Set(0, TokenKind.Input, day.Input, week.Input, _lifetime.Input);
        Set(1, TokenKind.CacheRead, day.CacheRead, week.CacheRead, _lifetime.CacheRead);
        Set(2, TokenKind.CacheWrite, day.CacheWrite, week.CacheWrite, _lifetime.CacheWrite);
        Set(3, TokenKind.Output, day.Output, week.Output, _lifetime.Output);

        OnPropertyChanged(nameof(HasBreakdown));
        OnPropertyChanged(nameof(TotalLifetime));
        OnPropertyChanged(nameof(TotalText));
    }

    private void Set(int index, TokenKind kind, long day, long week, long lifetime)
    {
        var row = new UsageStatRow(kind, day, week, lifetime);
        if (Rows.Count > index)
        {
            if (Rows[index] != row)
            {
                Rows[index] = row;
            }
        }
        else
        {
            Rows.Add(row);
        }
    }

    // An hour bucket counts in full when it starts at or after the cutoff. A window boundary that falls
    // mid-hour therefore rounds outward, which is the honest direction: the alternative is apportioning
    // an hour's tokens across a boundary we have no evidence about.
    private Bucket Sum(DateTimeOffset since)
    {
        var total = new Bucket();
        foreach (var (hour, bucket) in _hours)
        {
            if (hour + TimeSpan.FromHours(1) <= since)
            {
                continue;
            }

            total.Input += bucket.Input;
            total.CacheRead += bucket.CacheRead;
            total.CacheWrite += bucket.CacheWrite;
            total.Output += bucket.Output;
        }

        return total;
    }
}
