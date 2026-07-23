using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using Agnes.Abstractions;
using Agnes.Client;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Agnes.Ui.Core.ViewModels;

/// <summary>
/// A small badge surface over a connected-service profile's quota/usage (.ideas/providers/03). It pulls a
/// <see cref="QuotaSnapshot"/> for one profile from whatever <see cref="IAgnesHost"/> the accessor returns, so
/// it drives a real SignalR host and the offline simulation identically. Purely informational: a null snapshot
/// (an unsupported provider, an unknown profile, a fetch failure) is surfaced as an explicit
/// <see cref="IsUnavailable"/> state rather than an error or an indefinitely-spinning element. Meter formatting
/// mirrors the session usage badge (<c>UsageInfo</c>): a bar when both used and limit are known, a raw number
/// otherwise.
/// </summary>
public sealed class QuotaBadgeViewModel : ObservableObject
{
    private readonly Func<IAgnesHost?> _host;
    private readonly IUiDispatcher _dispatcher;

    public QuotaBadgeViewModel(Func<IAgnesHost?> host, IUiDispatcher dispatcher)
    {
        _host = host;
        _dispatcher = dispatcher;
        RefreshCommand = new AsyncRelayCommand<string>(RefreshAsync);
    }

    /// <summary>Refreshes the badge for a profile id (pulls a fresh-or-cached snapshot from the host).</summary>
    public ICommand RefreshCommand { get; }

    /// <summary>The per-meter rows for the current snapshot, in provider order.</summary>
    public ObservableCollection<QuotaMeterRow> Meters { get; } = [];

    private string? _profileId;
    /// <summary>The profile this badge last displayed, or null before any refresh.</summary>
    public string? ProfileId { get => _profileId; private set => SetProperty(ref _profileId, value); }

    private string _planLabel = string.Empty;
    /// <summary>The plan/tier caption (e.g. "Pro"), or empty when unavailable.</summary>
    public string PlanLabel { get => _planLabel; private set => SetProperty(ref _planLabel, value); }

    private bool _hasQuota;
    /// <summary>True once a snapshot has loaded — the badge has something to show.</summary>
    public bool HasQuota { get => _hasQuota; private set => SetProperty(ref _hasQuota, value); }

    private bool _isUnavailable;
    /// <summary>True when the host reported no quota for this profile (unsupported provider, unknown profile,
    /// or a fetch failure) — the badge shows a clear "usage unavailable" note rather than nothing.</summary>
    public bool IsUnavailable { get => _isUnavailable; private set => SetProperty(ref _isUnavailable, value); }

    private DateTimeOffset? _fetchedAt;
    /// <summary>When the underlying data was actually retrieved (may be older than "now" when served from the
    /// host cache) — the honest "as of" staleness indicator.</summary>
    public DateTimeOffset? FetchedAt { get => _fetchedAt; private set { if (SetProperty(ref _fetchedAt, value)) { OnPropertyChanged(nameof(AsOfText)); } } }

    /// <summary>A short "as of …" caption for the badge, or empty when there's nothing loaded.</summary>
    public string AsOfText => _fetchedAt is { } at ? $"as of {at.LocalDateTime:g}" : string.Empty;

    /// <summary>
    /// Pulls the snapshot for <paramref name="profileId"/> and updates the badge. Never throws: a null snapshot
    /// or a transport error both resolve to the <see cref="IsUnavailable"/> state.
    /// </summary>
    public async Task RefreshAsync(string? profileId)
    {
        if (string.IsNullOrEmpty(profileId))
        {
            return;
        }

        var host = _host();
        if (host is null)
        {
            _dispatcher.Post(() => Clear(profileId));
            return;
        }

        QuotaSnapshot? snapshot;
        try
        {
            snapshot = await host.GetQuotaSnapshotAsync(profileId).ConfigureAwait(false);
        }
        catch
        {
            // A transport hiccup is just "unavailable" for an informational badge — never a crash.
            snapshot = null;
        }

        _dispatcher.Post(() => Apply(profileId, snapshot));
    }

    private void Apply(string profileId, QuotaSnapshot? snapshot)
    {
        ProfileId = profileId;
        Meters.Clear();

        if (snapshot is null)
        {
            Clear(profileId);
            return;
        }

        foreach (var meter in snapshot.Meters)
        {
            Meters.Add(new QuotaMeterRow(meter));
        }

        PlanLabel = snapshot.PlanLabel;
        FetchedAt = snapshot.FetchedAt;
        HasQuota = true;
        IsUnavailable = false;
    }

    private void Clear(string profileId)
    {
        ProfileId = profileId;
        Meters.Clear();
        PlanLabel = string.Empty;
        FetchedAt = null;
        HasQuota = false;
        IsUnavailable = true;
    }
}

/// <summary>
/// One row of a <see cref="QuotaBadgeViewModel"/>: a single <see cref="QuotaMeter"/> with display helpers that
/// mirror the session usage badge. Shows a percentage bar only when both used and limit are known; otherwise
/// falls back to the raw used value (or nothing).
/// </summary>
public sealed class QuotaMeterRow
{
    private readonly QuotaMeter _meter;

    public QuotaMeterRow(QuotaMeter meter) => _meter = meter;

    public string Name => _meter.Name;

    /// <summary>Both a used count and a limit are known, so a proportion bar is meaningful.</summary>
    public bool HasBar => _meter.Used is >= 0 && _meter.Limit is > 0;

    /// <summary>Fill percentage (0–100) when <see cref="HasBar"/>, else 0.</summary>
    public double Percent => HasBar ? Math.Clamp(100.0 * _meter.Used!.Value / _meter.Limit!.Value, 0, 100) : 0;

    /// <summary>"120 / 500 requests" when the limit is known, else "120 requests", else empty.</summary>
    public string ValueText
    {
        get
        {
            var unit = string.IsNullOrEmpty(_meter.Unit) ? string.Empty : " " + _meter.Unit;
            if (HasBar)
            {
                return $"{_meter.Used!.Value:N0} / {_meter.Limit!.Value:N0}{unit}";
            }

            return _meter.Used is { } used ? $"{used:N0}{unit}" : string.Empty;
        }
    }
}
