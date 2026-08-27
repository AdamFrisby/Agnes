using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Agnes.Plugins.CodeyBox;

/// <summary>
/// The CodeyBox work queue, and the live agent output of whichever item is selected.
/// </summary>
/// <remarks>
/// Two halves, matching the two things CodeyBox offers over the wire: the queue is polled over REST
/// (there is no change feed for it), while the selected item's agent output is streamed from the
/// <c>agent-stdout</c> hub rather than polled, so it arrives as the agent produces it.
///
/// <para>Selecting an item pulls its stdout <i>tail</i> first and then follows the live stream. Neither
/// alone is enough: a subscription only carries what happens next, so without the tail an item that has
/// been running for an hour opens blank.</para>
/// </remarks>
public sealed partial class CodeyBoxQueueViewModel : ObservableObject, IAsyncDisposable
{
    private readonly CodeyBoxClient _client;
    private readonly Func<Action, Task> _toUi;
    private readonly StringBuilder _output = new();
    private readonly CancellationTokenSource _cts = new();

    private Task? _poller;

    public CodeyBoxQueueViewModel(CodeyBoxClient client, Func<Action, Task> toUi, bool configured = true)
    {
        _client = client;
        _toUi = toUi;
        IsConfigured = configured;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        TogglePauseCommand = new AsyncRelayCommand(TogglePauseAsync);
        CancelCommand = new AsyncRelayCommand<WorkItemRow>(row => Act(row, _client.CancelAsync));
        RetryCommand = new AsyncRelayCommand<WorkItemRow>(row => Act(row, _client.RetryAsync));
        PromoteCommand = new AsyncRelayCommand<WorkItemRow>(row => Act(row, _client.PromoteAsync));

        _client.StdoutReceived += OnStdout;
        _client.StreamCompleted += OnStreamCompleted;

        Sections = new CodeyBoxSectionsViewModel(client, toUi);
    }

    /// <summary>Everything the tab shows besides the queue — fleet, supervision, suggestions, releases and
    /// the orchestrator's own diagnostics. Each loads when first opened rather than up front.</summary>
    public CodeyBoxSectionsViewModel Sections { get; }

    /// <summary>Whether an API key was found. False renders a "configure me" state rather than an error
    /// loop — a machine with no CodeyBox is an ordinary machine, not a broken one.</summary>
    public bool IsConfigured { get; }

    public ObservableCollection<WorkItemRow> Items { get; } = [];

    [ObservableProperty]
    private WorkItemRow? _selected;

    [ObservableProperty]
    private bool _queuePaused;

    [ObservableProperty]
    private string _status = "Not loaded";

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>The selected item's agent output, oldest first.</summary>
    public string Output => _output.ToString();

    /// <summary>Raised per appended chunk, so a renderer can grow a text view rather than rebuild it.</summary>
    public event Action<string>? OutputAppended;

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand TogglePauseCommand { get; }
    public IAsyncRelayCommand<WorkItemRow> CancelCommand { get; }
    public IAsyncRelayCommand<WorkItemRow> RetryCommand { get; }
    public IAsyncRelayCommand<WorkItemRow> PromoteCommand { get; }

    public string PauseButtonText => QueuePaused ? "Resume queue" : "Pause queue";

    partial void OnQueuePausedChanged(bool value) => OnPropertyChanged(nameof(PauseButtonText));

    /// <summary>Loads the queue once, then keeps it fresh. Safe to call more than once.</summary>
    public void Start()
    {
        if (!IsConfigured || _poller is not null)
        {
            return;
        }

        _poller = Task.Run(PollAsync);
    }

    private async Task PollAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            await RefreshAsync().ConfigureAwait(false);
            try
            {
                // The queue has no change feed, so it is polled — slowly, because it is a work queue and
                // not a transcript, and the thing that actually moves second-by-second (agent output)
                // arrives over the hub instead.
                await Task.Delay(TimeSpan.FromSeconds(5), _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async Task RefreshAsync()
    {
        if (!IsConfigured)
        {
            await _toUi(() => Status = "No CodeyBox API key found.").ConfigureAwait(false);
            return;
        }

        try
        {
            var items = await _client.ListWorkItemsAsync(_cts.Token).ConfigureAwait(false);
            var queue = await _client.GetQueueStatusAsync(_cts.Token).ConfigureAwait(false);

            await _toUi(() =>
            {
                var keep = Selected?.Id;
                Items.Clear();
                foreach (var item in items.OrderBy(i => i.IsTerminal).ThenBy(i => i.QueuePosition))
                {
                    Items.Add(item);
                }

                // Re-point at the same item across a refresh: the rows are fresh records, so holding the
                // old instance would silently deselect on every poll.
                if (keep is not null)
                {
                    Selected = Items.FirstOrDefault(i => i.Id == keep) ?? Selected;
                }

                QueuePaused = queue?.IsPaused ?? false;
                Status = $"{Items.Count} work item(s)" + (QueuePaused ? " · queue paused" : string.Empty);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            Diagnostic.Report("refresh", ex);
            await _toUi(() => Status = $"CodeyBox unreachable — {ex.Message}").ConfigureAwait(false);
        }
    }

    async partial void OnSelectedChanged(WorkItemRow? value)
    {
        if (value is null)
        {
            return;
        }

        await _toUi(() =>
        {
            _output.Clear();
            OnPropertyChanged(nameof(Output));
        }).ConfigureAwait(false);

        try
        {
            // Tail first, then follow: a subscription carries only what happens next, so an item already
            // an hour into its run would otherwise open on an empty pane.
            var tail = await _client.GetStdoutTailAsync(value.Id, _cts.Token).ConfigureAwait(false);
            await _toUi(() =>
            {
                _output.Append(tail);
                OnPropertyChanged(nameof(Output));
                OutputAppended?.Invoke(tail);
            }).ConfigureAwait(false);

            await _client.FollowAsync(value.Id, _cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            Diagnostic.Report($"follow {value.ShortId}", ex);
            await _toUi(() => Status = $"Couldn't follow {value.ShortId} — {ex.Message}").ConfigureAwait(false);
        }
    }

    private void OnStdout(StdoutChunk chunk)
    {
        // The hub scopes delivery to the subscribed item's group, but a Follow that has not yet taken
        // effect can still land one chunk from the previous item.
        if (Selected is not { } selected || chunk.WorkItemId != selected.Id)
        {
            return;
        }

        _ = _toUi(() =>
        {
            _output.Append(chunk.Chunk);
            OnPropertyChanged(nameof(Output));
            OutputAppended?.Invoke(chunk.Chunk);
        });
    }

    private void OnStreamCompleted(string workItemId)
    {
        if (Selected?.Id == workItemId)
        {
            _ = _toUi(() => Status = "Agent stream finished.");
        }
    }

    private async Task TogglePauseAsync()
    {
        try
        {
            IsBusy = true;
            if (QueuePaused)
            {
                await _client.ResumeQueueAsync(_cts.Token).ConfigureAwait(false);
            }
            else
            {
                // The orchestrator requires a reason and rejects an empty one — a paused queue nobody can
                // explain later is exactly what that rule exists to prevent.
                await _client.PauseQueueAsync("Paused from Agnes", _cts.Token).ConfigureAwait(false);
            }

            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _toUi(() => Status = $"Couldn't change the queue — {ex.Message}").ConfigureAwait(false);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task Act(WorkItemRow? row, Func<string, CancellationToken, Task> action)
    {
        if (row is null)
        {
            return;
        }

        try
        {
            IsBusy = true;
            await action(row.Id, _cts.Token).ConfigureAwait(false);
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _toUi(() => Status = $"{row.ShortId}: {ex.Message}").ConfigureAwait(false);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _client.StdoutReceived -= OnStdout;
        _client.StreamCompleted -= OnStreamCompleted;
        await _cts.CancelAsync().ConfigureAwait(false);
        _cts.Dispose();
        await _client.DisposeAsync().ConfigureAwait(false);
    }
}
