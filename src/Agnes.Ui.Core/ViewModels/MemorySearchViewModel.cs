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
/// Drives host-backed transcript search (see <c>.ideas/ops/02-memory-search.md</c>): a query runs against
/// the connected host's full-text index over <b>every</b> session it has ever recorded — including closed
/// ones — which is what distinguishes it from the shell's in-memory search across only the open tabs.
/// Head-agnostic: it talks to whatever <see cref="IAgnesHost"/> the accessor returns and raises
/// <see cref="OpenRequested"/> for a hit so each frontend can route the jump its own way (the Desktop shell
/// activates the session's tab).
/// </summary>
public sealed class MemorySearchViewModel : ObservableObject
{
    private readonly Func<IAgnesHost?> _host;
    private readonly IUiDispatcher _dispatcher;

    public MemorySearchViewModel(Func<IAgnesHost?> host, IUiDispatcher dispatcher)
    {
        _host = host;
        _dispatcher = dispatcher;
        SearchCommand = new AsyncRelayCommand(SearchAsync);
        OpenResultCommand = new RelayCommand<MemorySearchResultRow>(row => { if (row is not null) { OpenRequested?.Invoke(row); } });
    }

    /// <summary>Ranked hits from the last search, best match first.</summary>
    public ObservableCollection<MemorySearchResultRow> Results { get; } = [];

    private string _query = string.Empty;
    public string Query { get => _query; set => SetProperty(ref _query, value); }

    private string _status = string.Empty;
    public string Status { get => _status; set => SetProperty(ref _status, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set => SetProperty(ref _isBusy, value); }

    public ICommand SearchCommand { get; }
    public ICommand OpenResultCommand { get; }

    /// <summary>Raised when the user picks a result; the frontend jumps to that session/sequence.</summary>
    public event Action<MemorySearchResultRow>? OpenRequested;

    private async Task SearchAsync()
    {
        var host = _host();
        if (host is null) { _dispatcher.Post(() => Status = "Connect to a host to search its history."); return; }

        var query = Query ?? string.Empty;
        if (string.IsNullOrWhiteSpace(query))
        {
            _dispatcher.Post(() => { Results.Clear(); Status = "Type a term to search past sessions."; });
            return;
        }

        try
        {
            _dispatcher.Post(() => { IsBusy = true; Status = "Searching…"; });
            var hits = await host.SearchMemoryAsync(query, new MemorySearchOptions()).ConfigureAwait(false);
            _dispatcher.Post(() =>
            {
                Results.Clear();
                foreach (var hit in hits) { Results.Add(new MemorySearchResultRow(hit)); }
                Status = Results.Count == 0 ? "No matching history." : $"{Results.Count} result(s).";
            });
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => Status = "Search failed: " + ex.Message);
        }
        finally
        {
            _dispatcher.Post(() => IsBusy = false);
        }
    }
}

/// <summary>One transcript-search hit as a bindable row (the target session/sequence plus its snippet).</summary>
public sealed class MemorySearchResultRow
{
    public MemorySearchResultRow(MemorySearchResult result)
    {
        SessionId = result.SessionId;
        Sequence = result.Sequence;
        Snippet = result.Snippet;
        Timestamp = result.Timestamp;
    }

    public string SessionId { get; }
    public long Sequence { get; }
    public string Snippet { get; }
    public DateTimeOffset Timestamp { get; }

    /// <summary>Short "when" label for the row (local date).</summary>
    public string When => Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}
