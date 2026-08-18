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
    private bool _stickToBottom = true;
    private bool _userScrolling; // a pointer drag (scrollbar/content) is in progress — suppress auto-follow
    private ScrollBar? _indexBar;
    private bool _indexActive;    // the transcript is past the threshold: the bar's unit is a message index
    private bool _indexDragging;  // the index thumb is being dragged — freeze the bar's range under it
    private int _wantedRow = -1;  // latest row a drag asked for; a seek in flight picks it up when it lands
    private bool _seeking;        // a seek chain is running — new targets coalesce into it
    private double _lastSeekOffset = double.NaN; // offset at the previous seek pass, to notice a stalled one
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
        DetachedFromVisualTree += (_, _) => SetKeymap(null);

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
        _stickToBottom = true;
        Dispatcher.UIThread.Post(() => { EnsureScrollHooked(); ScrollToEndNow(); UpdateOverlays(); }, DispatcherPriority.Background);
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

    /// <summary>Pinned iff the viewport bottom is within a small margin of the true bottom.</summary>
    private void ReevaluatePin()
    {
        if (_transcriptScroll is { } sv)
        {
            _stickToBottom = sv.Extent.Height - (sv.Offset.Y + sv.Viewport.Height) < 24;
            UpdateOverlays();
        }
    }

    private void OnTranscriptWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.Delta.Y > 0)
        {
            _stickToBottom = false; // scrolling up (wheel away) — release immediately, even mid-stream
        }

        // After the wheel scroll applies, re-evaluate (so wheeling back down to the bottom re-arms).
        Dispatcher.UIThread.Post(ReevaluatePin, DispatcherPriority.Background);
    }

    private void OnTranscriptPointerPressed(object? sender, PointerPressedEventArgs e) => _userScrolling = true;

    private void OnTranscriptPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _userScrolling = false;
        // A drag (scrollbar thumb or content) just ended — arm the pin from where it landed.
        Dispatcher.UIThread.Post(ReevaluatePin, DispatcherPriority.Background);
    }

    private void OnTranscriptPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _userScrolling = false;
        Dispatcher.UIThread.Post(ReevaluatePin, DispatcherPriority.Background);
    }

    private void OnTranscriptScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_transcriptScroll is not { } sv)
        {
            return;
        }

        // Follow streaming/new content to the true bottom ONLY while pinned and the user isn't actively
        // scrolling. The pin itself is never changed here (see the input handlers) — that's what lets the
        // scrollbar be dragged over a virtualized list whose extent keeps being re-estimated.
        if (_stickToBottom && !_userScrolling && System.Math.Abs(e.ExtentDelta.Y) > 0.5)
        {
            ScrollToEndNow();
        }

        UpdateOverlays();
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

    private void UpdateOverlays()
    {
        var rows = ScanVisibleRows();
        UpdateStickyHeader(rows);
        UpdateScrollHint(rows);
        SyncIndexScroll(rows);
    }

    // A floating "you are here" timestamp shown while the user has scrolled up from the bottom, so long
    // conversations aren't disorienting. Hidden when pinned to the bottom (the live tail).
    private void UpdateScrollHint(VisibleRows rows)
    {
        var hint = this.FindControl<Border>("ScrollHint");
        if (hint is null)
        {
            return;
        }

        if (_stickToBottom || _transcript is null || _transcriptScroll is null
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
        if (e.Action == NotifyCollectionChangedAction.Add && _stickToBottom && !_userScrolling)
        {
            Dispatcher.UIThread.Post(ScrollToEndNow, DispatcherPriority.Background);
        }

        Dispatcher.UIThread.Post(UpdateOverlays, DispatcherPriority.Background);
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
        if (_indexBar is null || _transcript is null || _transcriptScroll is null || _indexDragging)
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
        // While pinned the value is the end by definition, even before the last rows have been realized.
        _indexBar.Value = _stickToBottom ? range.Maximum : range.Value;
    }

    private void OnIndexScroll(object? sender, ScrollEventArgs e)
    {
        // Only a thumb drag freezes the range; a track click or arrow is a single settled move. Holding
        // _userScrolling for the duration keeps streaming content from yanking the view out of the drag.
        // Cleared before the mode check, so a drag that outlives index mode still releases both flags.
        _indexDragging = _indexActive && e.ScrollEventType == ScrollEventType.ThumbTrack;
        _userScrolling = _indexDragging;

        if (_indexBar is null || !_indexActive)
        {
            return;
        }

        var maximum = _indexBar.Maximum;
        var value = System.Math.Clamp(e.NewValue, 0, maximum);
        _stickToBottom = TranscriptIndexScroll.IsAtEnd(value, maximum); // dragged to the end → live tail again

        if (_stickToBottom)
        {
            ScrollToEndNow();
        }
        else
        {
            ScrollIndexToTop((int)System.Math.Round(value));
        }

        if (!_indexDragging)
        {
            Dispatcher.UIThread.Post(UpdateOverlays, DispatcherPriority.Background);
        }
    }

    /// <summary>
    /// Scroll so the given row sits at the top of the viewport. Seeking is <i>coalesced</i>: a drag
    /// emits far more events than the list can seek, and firing one seek per event leaves the panel
    /// half-way through several at once — realized rows arranged for one offset while the scroll viewer
    /// holds another, which is the desync that makes a big list feel like it's fighting the cursor. So
    /// only the newest target is remembered, and it's applied when the seek in flight lands.
    /// </summary>
    private void ScrollIndexToTop(int index)
    {
        _wantedRow = index;
        if (!_seeking)
        {
            _seeking = true;
            SeekPass(0);
        }
    }

    // One seek is iterative because the only thing anyone can say about an unrealized row is where the
    // extent *estimates* it — and in a transcript that estimate is poor (a status line and a 2000px diff
    // are both one row). So: jump to the estimate, look at where the row actually landed, correct, repeat.
    // Each pass realizes rows nearer the target and re-estimates from better data, so a handful converge;
    // the cap stops a pathological list looping.
    private const int MaxSeekPasses = 12;

    private void SeekPass(int attempt)
    {
        if (_transcript is null || _transcriptScroll is null || _transcript.ItemCount == 0)
        {
            _seeking = false;
            return;
        }

        var sv = _transcriptScroll;
        var index = System.Math.Clamp(_wantedRow, 0, _transcript.ItemCount - 1);
        var maxOffset = System.Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
        var here = sv.Offset.Y;
        var offBy = _transcript.ContainerFromIndex(index)?.TranslatePoint(default, sv)?.Y;

        if (offBy is { } landedAt && System.Math.Abs(landedAt) <= 1 && _wantedRow == index)
        {
            _seeking = false; // the row is at the top of the viewport
            return;
        }

        // Three ways to move: correct by measurement when the row is on screen, let the panel realize its
        // way there when it isn't, and — when neither budged the viewport — jump to the estimate.
        var corrected = offBy is { } d ? System.Math.Clamp(here + d, 0, maxOffset) : double.NaN;
        if (attempt > 0 && System.Math.Abs(here - _lastSeekOffset) < 1)
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

        _lastSeekOffset = here;

        Dispatcher.UIThread.Post(() =>
        {
            if (_wantedRow != index)
            {
                SeekPass(0); // the drag moved on while this pass ran — chase the new row from scratch
            }
            else if (attempt + 1 < MaxSeekPasses)
            {
                SeekPass(attempt + 1);
            }
            else
            {
                _seeking = false;
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
                Dispatcher.UIThread.Post(() =>
                {
                    _transcript.ScrollIntoView(index);
                    _transcript.ContainerFromIndex(index)?.BringIntoView();
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
