using System.Collections.ObjectModel;
using Agnes.Client;
using Agnes.Ui.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Agnes.App.Desktop.ViewModels;

/// <summary>
/// The Settings page for the local event cache: what it holds, how much disk that is, and the two things
/// an operator ever needs to do to it — drop one session, or drop the lot.
/// </summary>
/// <remarks>
/// The cache is invisible when it works, which is exactly why it needs a page: a directory that grows by
/// the size of every long session opened should be findable, explicable and emptiable from inside the
/// app rather than discovered from a disk-usage tool. Emptying it costs nothing but the next open's
/// download; nothing else depends on it.
/// </remarks>
public sealed partial class EventCacheViewModel : ObservableObject
{
    private readonly IInspectableSessionEventCache? _cache;
    private readonly IUiDispatcher _dispatcher;

    public EventCacheViewModel(ISessionEventCache? cache, IUiDispatcher dispatcher)
    {
        _cache = cache as IInspectableSessionEventCache;
        _dispatcher = dispatcher;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        ClearCommand = new AsyncRelayCommand(ClearAsync, () => Entries.Count > 0);
    }

    /// <summary>False when this build runs without a cache (disabled, or it could not be opened).</summary>
    public bool IsAvailable => _cache is not null;

    public string Path => _cache?.Path ?? string.Empty;

    public ObservableCollection<EventCacheEntryVm> Entries { get; } = [];

    [ObservableProperty] private string _summary = "No cache.";

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand ClearCommand { get; }

    public async Task RefreshAsync()
    {
        if (_cache is null)
        {
            return;
        }

        IReadOnlyList<SessionEventCacheEntry> entries;
        try
        {
            entries = await _cache.ListAsync();
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => Summary = "The cache could not be read: " + ex.Message);
            return;
        }

        _dispatcher.Post(() =>
        {
            Entries.Clear();
            foreach (var entry in entries.OrderByDescending(e => e.Bytes))
            {
                Entries.Add(new EventCacheEntryVm(entry, ForgetAsync));
            }
            var bytes = entries.Sum(e => e.Bytes);
            Summary = entries.Count == 0
                ? "Nothing cached yet. Sessions are cached as you open them."
                : $"{entries.Count} session{(entries.Count == 1 ? string.Empty : "s")} · {Size(bytes)} on disk";
            ClearCommand.NotifyCanExecuteChanged();
        });
    }

    private async Task ClearAsync()
    {
        if (_cache is null)
        {
            return;
        }

        await _cache.ClearAsync();
        await RefreshAsync();
    }

    private async Task ForgetAsync(EventCacheEntryVm? entry)
    {
        if (_cache is null || entry is null)
        {
            return;
        }

        await _cache.ForgetAsync(entry.HostId, entry.SessionId);
        await RefreshAsync();
    }

    /// <summary>Bytes as a person reads them: "184 MB", "1.2 GB", "37 kB".</summary>
    public static string Size(long bytes)
        => bytes switch
        {
            >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} GB",
            >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} MB",
            >= 1L << 10 => $"{bytes / (double)(1L << 10):0} kB",
            _ => $"{bytes} B",
        };
}

/// <summary>One cached session as the page shows it, with its own Forget so the row needs no reach up the tree.</summary>
public sealed class EventCacheEntryVm
{
    private readonly SessionEventCacheEntry _entry;

    public EventCacheEntryVm(SessionEventCacheEntry entry, Func<EventCacheEntryVm, Task> forget)
    {
        _entry = entry;
        ForgetCommand = new AsyncRelayCommand(() => forget(this));
    }

    public IAsyncRelayCommand ForgetCommand { get; }

    public string HostId => _entry.HostId;
    public string SessionId => _entry.SessionId;

    /// <summary>The host with its scheme stripped — the page is about sessions, not addresses.</summary>
    public string Host => _entry.HostId.Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase)
        .Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase);

    public string SizeText => EventCacheViewModel.Size(_entry.Bytes);

    /// <summary>"complete to event 338,146" — the head is a sequence, and the floor says whether the start is held.</summary>
    public string RangeText => _entry.Range.Floor == 0
        ? $"complete to event {_entry.Range.Head:N0}"
        : $"events after {_entry.Range.Floor:N0}, to {_entry.Range.Head:N0}";
}
