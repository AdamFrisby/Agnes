using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Agnes.Ui.Core.Transcript;
using Agnes.Ui.Core.ViewModels;
using Agnes.App.Desktop.Keymaps;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Agnes.App.Desktop.Views;

public partial class SessionTabView : UserControl
{
    private const double SplitterWidth = 5;
    private Grid? _workspace;
    private ListBox? _transcript;
    private SessionViewModel? _session;
    private double _leftWidth = 288;
    private double _rightWidth = 540;
    private ScrollViewer? _transcriptScroll;
    private readonly TranscriptScrollPolicy _scrollPolicy = new();
    private CancellationTokenSource? _wheelIdle;
    private long? _pointerGestureGeneration;
    private bool _overlayUpdateScheduled;
    private ScrollBar? _indexBar;
    private bool _indexActive;    // the transcript is past the threshold: the bar's unit is a message index
    private bool _indexSeekScheduled;
    private KeymapService? _keymap;

    public SessionTabView()
    {
        InitializeComponent();
        _workspace = this.FindControl<Grid>("Workspace");
        _transcript = this.FindControl<ListBox>("Transcript");
        _indexBar = this.FindControl<ScrollBar>("IndexScroll");
        if (_workspace is not null)
        {
            _workspace.DataContextChanged += (_, _) => HookSession();
            HookSession();
        }

        // Activating a tab re-attaches its view; land at the latest message rather than wherever it was.
        AttachedToVisualTree += (_, _) =>
        {
            RequestScrollToBottom();
            SetKeymap(KeymapBinder.GetService(this));
        };
        DetachedFromVisualTree += (_, _) =>
        {
            CancelWheelIdle();
            _scrollPolicy.ReadHistory();
            SetKeymap(null);
        };

        // Drop files (or an image) anywhere on the session to attach them to the composer.
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // Take over paste in the composer so an image or a copied file attaches (text still pastes).
        if (this.FindControl<TextBox>("Composer") is { } composer)
        {
            composer.AddHandler(InputElement.KeyDownEvent, OnComposerKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        }
    }

    // Full paste handler: a clipboard image → inline image; a copied file → attachment; text → inserted.
    private async void OnComposerKeyDown(object? sender, KeyEventArgs e)
    {
        var isPaste = e.Key == Key.V && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta));
        if (!isPaste || sender is not TextBox box || _session is null
            || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        e.Handled = true; // we own paste now (set synchronously so the TextBox doesn't also paste)
        try
        {
            if (await clipboard.TryGetDataAsync() is not { } transfer)
            {
                return;
            }

            // A copied file (document or image) keeps its identity as an attachment.
            if (await transfer.TryGetFilesAsync() is { } files)
            {
                var attached = false;
                foreach (var item in files)
                {
                    if (item is Avalonia.Platform.Storage.IStorageFile file)
                    {
                        await AttachStorageFileAsync(file);
                        attached = true;
                    }
                }

                if (attached)
                {
                    return;
                }
            }

            // A raw clipboard image (e.g. a screenshot) → upload the PNG bytes and reference the path.
            if (await transfer.TryGetBitmapAsync() is { } bitmap)
            {
                using var ms = new System.IO.MemoryStream();
                bitmap.Save(ms); // PNG
                await _session.AttachFileAsync("pasted-image.png", ms.ToArray());
                return;
            }

            // Otherwise it's text — insert it at the caret (replacing any selection).
            if (await transfer.TryGetTextAsync() is { } text)
            {
                InsertAtCaret(box, text);
            }
        }
        catch
        {
            // Clipboard access is best-effort per platform.
        }
    }

    private static void InsertAtCaret(TextBox box, string text)
    {
        var current = box.Text ?? string.Empty;
        var start = System.Math.Clamp(System.Math.Min(box.SelectionStart, box.SelectionEnd), 0, current.Length);
        var end = System.Math.Clamp(System.Math.Max(box.SelectionStart, box.SelectionEnd), 0, current.Length);
        box.Text = current[..start] + text + current[end..];
        box.CaretIndex = start + text.Length;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void HookSession()
    {
        CancelWheelIdle();
        _scrollPolicy.SwitchSession();
        if (_session is not null)
        {
            _session.PropertyChanged -= OnSessionPropertyChanged;
            _session.ScrollToRequested -= OnScrollToRequested;
            _session.ScrollToBottomRequested -= OnScrollToBottomRequested;
            _session.Items.CollectionChanged -= OnTranscriptItemsChanged;
        }

        _session = _workspace?.DataContext as SessionViewModel;
        if (_session is not null)
        {
            _session.PropertyChanged += OnSessionPropertyChanged;
            _session.ScrollToRequested += OnScrollToRequested;
            _session.ScrollToBottomRequested += OnScrollToBottomRequested;
            _session.Items.CollectionChanged += OnTranscriptItemsChanged;
            RequestScrollToBottom(); // a freshly attached session starts at the latest message
        }

        UpdateColumns();
        UpdateComposerHint();
    }

    private void OnScrollToBottomRequested() => RequestScrollToBottom();

    // Pin to the very bottom and remember we're pinned. Deferred to a background pass so it works even
    // before the ScrollViewer / its extent are realised (session open, tab activation, agent switch).
    private void RequestScrollToBottom()
    {
        var generation = _scrollPolicy.FollowTail();
        Dispatcher.UIThread.Post(() =>
        {
            if (!_scrollPolicy.IsCurrent(generation) || !_scrollPolicy.IsAtLiveTail)
            {
                return;
            }

            EnsureScrollHooked();
            ScrollToEndNow();
            ScheduleOverlayUpdate();
        }, DispatcherPriority.Background);
    }

    private void ScrollToEndNow()
    {
        if (_transcriptScroll is { } sv)
        {
            sv.Offset = new Vector(sv.Offset.X, sv.Extent.Height); // clamped to max → the true bottom
        }
    }

    private void EnsureScrollHooked()
    {
        if (_transcriptScroll is not null || _transcript is null)
        {
            return;
        }

        _transcriptScroll = _transcript.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (_transcriptScroll is not null)
        {
            _transcriptScroll.ScrollChanged += OnTranscriptScrollChanged;
            // The pin is (dis)armed by ACTUAL user input, not scroll deltas: over a big virtualized list the
            // extent is re-estimated as items realize, so ExtentDelta fires during a plain scroll — the old
            // delta-based release never triggered and the "follow" kept yanking back, so the scrollbar could
            // not be dragged. Now a wheel/drag re-evaluates the pin from geometry; ScrollChanged only follows
            // streaming content while pinned AND the user isn't actively scrolling.
            _transcript.AddHandler(InputElement.PointerWheelChangedEvent, OnTranscriptWheel, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            _transcript.AddHandler(InputElement.PointerPressedEvent, OnTranscriptPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            _transcript.AddHandler(InputElement.PointerReleasedEvent, OnTranscriptPointerReleased, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            _transcript.AddHandler(InputElement.PointerCaptureLostEvent, OnTranscriptPointerCaptureLost, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        }
    }

    /// <summary>Input may re-enter live following only when layout says the viewport is truly at its end.</summary>
    private bool IsGenuinelyAtBottom()
    {
        return _transcriptScroll is not { } sv
            || sv.Extent.Height - (sv.Offset.Y + sv.Viewport.Height) <= 1;
    }

    private void OnTranscriptWheel(object? sender, PointerWheelEventArgs e)
        => BeginWheelGesture();

    private void BeginWheelGesture()
    {
        var generation = _scrollPolicy.BeginGesture();
        CancelWheelIdle();
        var idle = new CancellationTokenSource();
        _wheelIdle = idle;
        _ = SettleWheelAfterIdleAsync(generation, idle.Token);
        ScheduleOverlayUpdate();
    }

    private async Task SettleWheelAfterIdleAsync(long generation, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_scrollPolicy.FinishGesture(generation, IsGenuinelyAtBottom()))
            {
                ScheduleOverlayUpdate();
            }
        }, DispatcherPriority.Render);
    }

    private void CancelWheelIdle()
    {
        _wheelIdle?.Cancel();
        _wheelIdle?.Dispose();
        _wheelIdle = null;
    }

    private void OnTranscriptPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        CancelWheelIdle();
        _pointerGestureGeneration = _scrollPolicy.BeginGesture();
        ScheduleOverlayUpdate();
    }

    private void OnTranscriptPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        FinishPointerGesture();
    }

    private void OnTranscriptPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        FinishPointerGesture();
    }

    private void FinishPointerGesture()
    {
        if (_pointerGestureGeneration is not { } generation)
        {
            return;
        }

        _pointerGestureGeneration = null;
        Dispatcher.UIThread.Post(() =>
        {
            if (_scrollPolicy.FinishGesture(generation, IsGenuinelyAtBottom()))
            {
                ScheduleOverlayUpdate();
            }
        }, DispatcherPriority.Render);
    }

    private void OnTranscriptScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_transcriptScroll is not { } sv)
        {
            return;
        }

        // Extent corrections follow only in the explicit tail state. Input moves the policy out of that
        // state before Avalonia applies its scroll delta, so virtualized remeasurement cannot pull an
        // active gesture back to the bottom.
        if (_scrollPolicy.IsAtLiveTail && System.Math.Abs(e.ExtentDelta.Y) > 0.5)
        {
            ScrollToEndNow();
        }

        ScheduleOverlayUpdate();
    }

    /// <summary>What the viewport is showing right now — one pass over the realized rows, shared by
    /// everything that tracks the scroll position (sticky header, hint, index bar).</summary>
    private readonly record struct VisibleRows(int FirstIndex, int Count, TranscriptItem? Top, bool HasMessage);

    private VisibleRows ScanVisibleRows()
    {
        if (_transcript is null || _transcriptScroll is null)
        {
            return new VisibleRows(int.MaxValue, 0, null, false);
        }

        var viewportHeight = _transcriptScroll.Viewport.Height;
        var firstIndex = int.MaxValue;
        var count = 0;
        var hasMessage = false;
        TranscriptItem? top = null;
        var topY = double.MaxValue;

        foreach (var container in _transcript.GetRealizedContainers())
        {
            if (container.TranslatePoint(default, _transcriptScroll) is not { } p
                || p.Y + container.Bounds.Height <= 0 || p.Y >= viewportHeight) // doesn't intersect
            {
                continue;
            }

            count++;
            hasMessage |= container.DataContext is MessageBubbleItem;

            var index = _transcript.IndexFromContainer(container);
            if (index >= 0)
            {
                firstIndex = System.Math.Min(firstIndex, index);
            }

            if (p.Y < topY && container.DataContext is TranscriptItem item)
            {
                topY = p.Y;
                top = item;
            }
        }

        return new VisibleRows(firstIndex, count, top, hasMessage);
    }

    private void ScheduleOverlayUpdate()
    {
        if (_overlayUpdateScheduled)
        {
            return;
        }

        _overlayUpdateScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _overlayUpdateScheduled = false;
            UpdateOverlays();
        }, DispatcherPriority.Render);
    }

    private void UpdateOverlays()
    {
        var rows = ScanVisibleRows();
        UpdateStickyHeader(rows);
        UpdateScrollHint(rows);
        SyncIndexScroll(rows);
    }

    // A floating "you are here" timestamp shown while the user has scrolled up from the bottom, so long
    // conversations aren't disorienting. Hidden while following the live tail.
    private void UpdateScrollHint(VisibleRows rows)
    {
        var hint = this.FindControl<Border>("ScrollHint");
        if (hint is null)
        {
            return;
        }

        if (_scrollPolicy.IsAtLiveTail || _transcript is null || _transcriptScroll is null
            || rows.Top is not { } top || top.Timestamp == default)
        {
            hint.IsVisible = false;
            return;
        }

        if (this.FindControl<TextBlock>("ScrollHintText") is { } label)
        {
            var local = top.Timestamp.ToLocalTime();
            var when = local.Date == System.DateTimeOffset.Now.Date
                ? local.ToString("HH:mm")
                : local.ToString("MMM d, HH:mm");
            // Dragging by index deserves index feedback: which row of how many you've landed on.
            label.Text = _indexActive && rows.FirstIndex is not int.MaxValue
                ? $"{when}  ·  {rows.FirstIndex + 1} / {_transcript.ItemCount}"
                : when;
        }

        hint.IsVisible = true;
    }

    private void OnTranscriptItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        EnsureScrollHooked();
        if (e.Action == NotifyCollectionChangedAction.Add && _scrollPolicy.ShouldFollowAppend)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_scrollPolicy.ShouldFollowAppend)
                {
                    ScrollToEndNow();
                }
            }, DispatcherPriority.Background);
        }

        ScheduleOverlayUpdate();
    }

    // Pin the last message above the viewport when a run of tool calls has pushed every message off-screen.
    private void UpdateStickyHeader(VisibleRows rows)
    {
        var header = this.FindControl<Border>("StickyHeader");
        if (header is null || _session is null || _transcript is null || _transcriptScroll is null)
        {
            return;
        }

        var firstVisibleIndex = rows.FirstIndex;
        if (rows.HasMessage || firstVisibleIndex is int.MaxValue or 0)
        {
            header.IsVisible = false;
            return;
        }

        // Walk back to the last message before the first visible row. DisplayItems == Items unless a
        // subagent filter / rewind is active (rare); only materialise then.
        var items = _session.SelectedAgentId is null && !_session.IsRewound
            ? (System.Collections.Generic.IReadOnlyList<TranscriptItem>)_session.Items
            : _session.DisplayItems.ToList();

        MessageBubbleItem? last = null;
        for (var i = System.Math.Min(firstVisibleIndex, items.Count) - 1; i >= 0; i--)
        {
            if (items[i] is MessageBubbleItem mb) { last = mb; break; }
        }

        if (last is null)
        {
            header.IsVisible = false;
            return;
        }

        if (this.FindControl<TextBlock>("StickySpeaker") is { } speaker) { speaker.Text = last.Speaker; }
        if (this.FindControl<TextBlock>("StickyText") is { } text) { text.Text = FirstLine(last.Text); }
        header.IsVisible = true;
    }

    // ---- index scrolling ------------------------------------------------------------------------
    // See TranscriptIndexScroll for why a large transcript stops being scrolled in pixels. Here it is
    // just two moves: keep the bar's range in transcript rows, and turn a drag on it into "put row N at
    // the top of the viewport". The wheel is untouched — it still scrolls pixels, and only feeds the
    // bar's value back.

    /// <summary>Put the bar where the viewport is, switching modes if the transcript crossed the
    /// threshold. A no-op while the thumb is held: the range must not move under a drag.</summary>
    private void SyncIndexScroll(VisibleRows rows)
    {
        if (_indexBar is null || _transcript is null || _transcriptScroll is null
            || _scrollPolicy.State == TranscriptScrollState.IndexDragging)
        {
            return;
        }

        var count = _transcript.ItemCount;
        var active = TranscriptIndexScroll.IsActive(count);
        if (active != _indexActive)
        {
            _indexActive = active;
            _indexBar.Visibility = active ? ScrollBarVisibility.Visible : ScrollBarVisibility.Hidden;
            // Only one bar at a time: the ListBox's own is hidden (not disabled — the wheel still works).
            ScrollViewer.SetVerticalScrollBarVisibility(
                _transcript, active ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Auto);
        }

        if (!active)
        {
            return;
        }

        var first = rows.FirstIndex is int.MaxValue ? 0 : rows.FirstIndex;
        var range = TranscriptIndexScroll.Range(count, rows.Count, first);
        _indexBar.Maximum = range.Maximum;
        _indexBar.ViewportSize = range.ViewportSize;
        _indexBar.LargeChange = System.Math.Max(1, range.ViewportSize - 1);
        // While following, the value is the end by definition even before the last rows are realized.
        _indexBar.Value = _scrollPolicy.IsAtLiveTail ? range.Maximum : range.Value;
    }

    private void OnIndexScroll(object? sender, ScrollEventArgs e)
    {
        if (_indexBar is null || !_indexActive)
        {
            return;
        }

        HandleIndexTarget(e.NewValue, e.ScrollEventType);
    }

    private void HandleIndexTarget(double value, ScrollEventType eventType)
    {
        if (_indexBar is null || !_indexActive)
        {
            return;
        }

        if (eventType == ScrollEventType.ThumbTrack
            && _scrollPolicy.State != TranscriptScrollState.IndexDragging)
        {
            _scrollPolicy.BeginIndexDrag(_indexBar.Maximum);
        }

        _scrollPolicy.RequestIndexTarget(value, _indexBar.Maximum);
        ScheduleLatestIndexSeek();

        if (eventType == ScrollEventType.EndScroll)
        {
            _scrollPolicy.EndIndexDrag();
            ScheduleOverlayUpdate();
        }
    }

    // Narrow headless-regression surface: drive the same input paths while retaining real Avalonia layout.
    internal void SimulateWheelInputForTesting() => BeginWheelGesture();
    internal void SimulateIndexInputForTesting(double value, bool beginDrag = false, bool endDrag = false)
    {
        if (beginDrag)
        {
            HandleIndexTarget(value, ScrollEventType.ThumbTrack);
        }
        else
        {
            HandleIndexTarget(value, endDrag ? ScrollEventType.EndScroll : ScrollEventType.ThumbTrack);
        }
    }

    internal int FirstVisibleTranscriptIndexForTesting => ScanVisibleRows().FirstIndex;
    internal TranscriptScrollState ScrollStateForTesting => _scrollPolicy.State;
    internal void RefreshTranscriptScrollForTesting()
    {
        EnsureScrollHooked();
        ScheduleOverlayUpdate();
    }
    internal double TranscriptRowTopForTesting(int index)
        => _transcript is not null && _transcriptScroll is not null
            ? _transcript.ContainerFromIndex(index)?.TranslatePoint(default, _transcriptScroll)?.Y ?? double.NaN
            : double.NaN;

    /// <summary>Coalesce the thumb's event burst to one target read per render pass.</summary>
    private void ScheduleLatestIndexSeek()
    {
        if (_indexSeekScheduled)
        {
            return;
        }

        _indexSeekScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _indexSeekScheduled = false;
            if (!_scrollPolicy.TryTakeLatestIndexTarget(out var request))
            {
                return;
            }

            if (request.FollowTail)
            {
                ScrollToEndNow();
                _scrollPolicy.CompleteIndexSeek(request.Generation);
                ScheduleOverlayUpdate();
                return;
            }

            SeekPass(request, attempt: 0, previousOffset: double.NaN);
        }, DispatcherPriority.Render);
    }

    /// <summary>
    /// Scroll so the requested row sits at the top. The request's generation makes every pass a no-op
    /// as soon as a wheel, pointer drag, session switch, or newer thumb target takes control.
    /// </summary>
    // One seek is iterative because the only thing anyone can say about an unrealized row is where the
    // extent *estimates* it — and in a transcript that estimate is poor (a status line and a 2000px diff
    // are both one row). So: jump to the estimate, look at where the row actually landed, correct, repeat.
    // Each pass realizes rows nearer the target and re-estimates from better data, so a handful converge;
    // the cap stops a pathological list looping.
    private const int MaxSeekPasses = 12;

    private void SeekPass(IndexSeekRequest request, int attempt, double previousOffset)
    {
        if (!_scrollPolicy.IsCurrent(request.Generation))
        {
            return;
        }

        if (_transcript is null || _transcriptScroll is null || _transcript.ItemCount == 0)
        {
            _scrollPolicy.CompleteIndexSeek(request.Generation);
            return;
        }

        var sv = _transcriptScroll;
        var index = System.Math.Clamp(request.Row, 0, _transcript.ItemCount - 1);
        var maxOffset = System.Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
        var here = sv.Offset.Y;
        var offBy = _transcript.ContainerFromIndex(index)?.TranslatePoint(default, sv)?.Y;

        if (offBy is { } landedAt && System.Math.Abs(landedAt) <= 1)
        {
            _scrollPolicy.CompleteIndexSeek(request.Generation);
            ScheduleOverlayUpdate();
            return;
        }

        // Three ways to move: correct by measurement when the row is on screen, let the panel realize its
        // way there when it isn't, and — when neither budged the viewport — jump to the estimate.
        var corrected = offBy is { } d ? System.Math.Clamp(here + d, 0, maxOffset) : double.NaN;
        if (attempt > 0 && System.Math.Abs(here - previousOffset) < 1)
        {
            // The last pass left the viewport exactly where it was. Either the row we measured was a
            // recycled container still parked at an old position, or the extent re-estimated and the
            // panel's scroll anchoring put the offset straight back. Jump to where the estimate says the
            // row is — a place we've not been — and measure again from there.
            sv.Offset = new Vector(sv.Offset.X,
                System.Math.Clamp(sv.Extent.Height * index / _transcript.ItemCount, 0, maxOffset));
        }
        else if (!double.IsNaN(corrected) && System.Math.Abs(corrected - here) >= 1)
        {
            sv.Offset = new Vector(sv.Offset.X, corrected); // the row is on screen: we know the exact gap
        }
        else
        {
            _transcript.ScrollIntoView(index); // unrealized: let the panel realize its way there
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (!_scrollPolicy.IsCurrent(request.Generation))
            {
                return;
            }

            if (attempt + 1 < MaxSeekPasses)
            {
                SeekPass(request, attempt + 1, here);
            }
            else
            {
                _scrollPolicy.CompleteIndexSeek(request.Generation);
                ScheduleOverlayUpdate();
            }
        }, DispatcherPriority.Background);
    }

    private static string FirstLine(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return string.Empty;
        }

        var newline = s.IndexOf('\n');
        var line = newline >= 0 ? s[..newline] : s;
        return line.Length > 200 ? line[..200] : line;
    }

    // Deep-link: scroll the transcript to the item carrying the given anchor id.
    private void OnScrollToRequested(string anchorId)
    {
        if (_session is null || _transcript is null)
        {
            return;
        }

        for (var i = 0; i < _session.Items.Count; i++)
        {
            if (_session.Items[i].AnchorId == anchorId)
            {
                // The transcript virtualizes, so the target row may not be realized yet — ScrollIntoView
                // realizes and scrolls to it (BringIntoView alone no-ops on an unrealized container).
                var index = i;
                var generation = _scrollPolicy.ReadHistory();
                Dispatcher.UIThread.Post(() =>
                {
                    if (!_scrollPolicy.IsCurrent(generation))
                    {
                        return;
                    }

                    _transcript.ScrollIntoView(index);
                    _transcript.ContainerFromIndex(index)?.BringIntoView();
                    ScheduleOverlayUpdate();
                });
                return;
            }
        }
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SessionViewModel.ShowLeftPanel)
            or nameof(SessionViewModel.ShowRightPanel)
            or nameof(SessionViewModel.IsPreviewFullScreen))
        {
            UpdateColumns();
        }
        else if (e.PropertyName == nameof(SessionViewModel.IsTurnActive))
        {
            UpdateComposerHint();
        }
    }

    private void SetKeymap(KeymapService? keymap)
    {
        if (ReferenceEquals(_keymap, keymap)) return;
        if (_keymap is not null) _keymap.Changed -= OnKeymapChanged;
        _keymap = keymap;
        if (_keymap is not null) _keymap.Changed += OnKeymapChanged;
        UpdateComposerHint();
    }

    /// <summary>Injects the same keymap supplied by the containing window. Normally inherited when the view
    /// attaches; explicit injection also makes the presentation behavior headless-testable without a native
    /// windowing lifetime.</summary>
    public void InstallKeymap(KeymapService keymap) => SetKeymap(keymap);

    private void OnKeymapChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess()) UpdateComposerHint();
        else Dispatcher.UIThread.Post(UpdateComposerHint);
    }

    private void UpdateComposerHint()
    {
        if (_keymap is null || _session is null || this.FindControl<TextBlock>("ComposerSendHint") is not { } hint) return;
        var send = _keymap.Effective.PrimaryGesture(AgnesCommand.ComposerSend, KeymapContext.ComposerFocus);
        var sendNow = _keymap.Effective.PrimaryGesture(AgnesCommand.ComposerSendNow, KeymapContext.ComposerFocus);
        var sendText = send is null ? "Send" : KeyGestureParser.Display(send);
        var sendNowText = sendNow is null ? "Send now" : KeyGestureParser.Display(sendNow);
        hint.Text = _session.IsTurnActive
            ? $"{sendText} queues after this turn · {sendNowText} sends now"
            : $"{sendText} to send";
    }

    private void UpdateColumns()
    {
        if (_workspace?.DataContext is not SessionViewModel vm)
        {
            return;
        }

        var columns = _workspace.ColumnDefinitions;

        // Full-screen review: the preview fills the tab; chat, left panel and splitters collapse.
        if (vm.IsPreviewFullScreen && vm.ShowRightPanel)
        {
            columns[0].Width = new GridLength(0);
            columns[1].Width = new GridLength(0);
            columns[2].MinWidth = 0;
            columns[2].Width = new GridLength(0);
            columns[3].Width = new GridLength(0);
            columns[4].MaxWidth = double.PositiveInfinity;
            columns[4].Width = new GridLength(1, GridUnitType.Star);
            return;
        }

        columns[2].MinWidth = 300;
        columns[2].Width = new GridLength(1, GridUnitType.Star);
        columns[4].MaxWidth = 760;
        Apply(columns[0], columns[1], vm.ShowLeftPanel, ref _leftWidth);
        Apply(columns[4], columns[3], vm.ShowRightPanel, ref _rightWidth);
    }

    private async void OnBrowseWorkingDirectory(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.SessionDocument doc
            || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        var folders = await storage.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = "Choose the project folder",
            AllowMultiple = false,
        });

        if (folders.FirstOrDefault()?.Path.LocalPath is { Length: > 0 } path)
        {
            doc.WorkingDirectory = path;
        }
    }

    private async void OnAttachFile(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_session is null || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        var files = await storage.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Attach a file",
            AllowMultiple = true,
        });

        foreach (var file in files)
        {
            await AttachStorageFileAsync(file);
        }
    }

    // Attach a picked/dropped file: read its bytes on this client and upload them to the workspace, then
    // reference the materialized path — never inline binary, and never a client-local path the host can't
    // see (referencing a file already in the workspace by path is the separate @-reference flow).
    private async System.Threading.Tasks.Task AttachStorageFileAsync(Avalonia.Platform.Storage.IStorageFile file)
    {
        if (_session is null)
        {
            return;
        }

        await using var stream = await file.OpenReadAsync();
        using var ms = new System.IO.MemoryStream();
        await stream.CopyToAsync(ms);
        await _session.AttachFileAsync(file.Name, ms.ToArray());
    }

    private void OnDragOver(object? sender, Avalonia.Input.DragEventArgs e)
        => e.DragEffects = e.DataTransfer.Contains(Avalonia.Input.DataFormat.File)
            ? Avalonia.Input.DragDropEffects.Copy
            : Avalonia.Input.DragDropEffects.None;

    private async void OnDrop(object? sender, Avalonia.Input.DragEventArgs e)
    {
        if (_session is null || e.DataTransfer.TryGetFiles() is not { } items)
        {
            return;
        }

        foreach (var item in items)
        {
            if (item is Avalonia.Platform.Storage.IStorageFile file)
            {
                await AttachStorageFileAsync(file);
            }
        }
    }

    private async void OnCopySessionLink(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_session is not null && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(_session.HandoffReference);
        }
    }

    // "Come and look at this": a pointer to the session that confers nothing, so it can go in a group chat.
    private async void OnCopyShareLink(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_session is not null && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(_session.ShareLink);
        }
    }

    // The same, aimed at one message. The event-log sequence is the same number on every client, so the
    // recipient's app scrolls to the moment you meant rather than the top of the transcript.
    private async void OnCopyMessageLink(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Control { DataContext: Agnes.Ui.Core.Transcript.TranscriptItem item }
            && _session is not null
            && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(_session.ShareLinkTo(item.Sequence));
        }
    }

    private async void OnCopyPreview(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_workspace?.DataContext is SessionViewModel { SelectedPreview: { } preview }
            && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(preview.Body);
        }
    }

    // Copy the whole message's raw text (Markdown) to the clipboard, from the per-message "⋯" menu.
    private async void OnCopyMessage(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Control { DataContext: Agnes.Ui.Core.Transcript.MessageBubbleItem message }
            && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(message.Text);
        }
    }

    // Collapses a side column to 0 when hidden (remembering any dragged width) and restores it
    // when shown — so panels appear only when needed, and the GridSplitter keeps its width.
    private static void Apply(ColumnDefinition panel, ColumnDefinition splitter, bool show, ref double remembered)
    {
        if (show)
        {
            if (panel.Width.Value <= 0)
            {
                panel.Width = new GridLength(remembered, GridUnitType.Pixel);
            }

            splitter.Width = new GridLength(SplitterWidth, GridUnitType.Pixel);
        }
        else
        {
            if (panel.Width.IsAbsolute && panel.Width.Value > 0)
            {
                remembered = panel.Width.Value;
            }

            panel.Width = new GridLength(0);
            splitter.Width = new GridLength(0);
        }
    }
}
