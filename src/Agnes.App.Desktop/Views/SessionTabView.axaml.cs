using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Agnes.Ui.Core.Transcript;
using Agnes.Ui.Core.ViewModels;
using Avalonia;
using Avalonia.Controls;
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

    public SessionTabView()
    {
        InitializeComponent();
        _workspace = this.FindControl<Grid>("Workspace");
        _transcript = this.FindControl<ListBox>("Transcript");
        if (_workspace is not null)
        {
            _workspace.DataContextChanged += (_, _) => HookSession();
            HookSession();
        }

        // Activating a tab re-attaches its view; land at the latest message rather than wherever it was.
        AttachedToVisualTree += (_, _) => RequestScrollToBottom();

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
    }

    private void OnScrollToBottomRequested() => RequestScrollToBottom();

    // Pin to the very bottom and remember we're pinned. Deferred to a background pass so it works even
    // before the ScrollViewer / its extent are realised (session open, tab activation, agent switch).
    private void RequestScrollToBottom()
    {
        _stickToBottom = true;
        Dispatcher.UIThread.Post(() => { EnsureScrollHooked(); ScrollToEndNow(); }, DispatcherPriority.Background);
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
        }
    }

    private void OnTranscriptScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_transcriptScroll is not { } sv)
        {
            return;
        }

        // Content grew/settled (streaming text, a new row, or the first layout): if we're pinned, follow
        // it all the way to the true bottom — content keeps growing after the offset was set, which is why
        // targeting the last item once only got ~75% of the way.
        if (System.Math.Abs(e.ExtentDelta.Y) > 0.5)
        {
            if (_stickToBottom)
            {
                ScrollToEndNow();
            }
        }
        // The user (or our own pin) moved the offset with no content change: re-evaluate whether we're at
        // the bottom, so scrolling up releases the pin and scrolling back to the end re-arms it.
        else if (System.Math.Abs(e.OffsetDelta.Y) > 0.5)
        {
            _stickToBottom = sv.Extent.Height - (sv.Offset.Y + sv.Viewport.Height) < 24;
        }

        UpdateStickyHeader();
    }

    private void OnTranscriptItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        EnsureScrollHooked();
        if (e.Action == NotifyCollectionChangedAction.Add && _stickToBottom)
        {
            Dispatcher.UIThread.Post(ScrollToEndNow, DispatcherPriority.Background);
        }

        Dispatcher.UIThread.Post(UpdateStickyHeader, DispatcherPriority.Background);
    }

    // Pin the last message above the viewport when a run of tool calls has pushed every message off-screen.
    private void UpdateStickyHeader()
    {
        var header = this.FindControl<Border>("StickyHeader");
        if (header is null || _session is null || _transcript is null || _transcriptScroll is null)
        {
            return;
        }

        var viewportHeight = _transcriptScroll.Viewport.Height;
        var firstVisibleIndex = int.MaxValue;
        var messageVisible = false;
        foreach (var container in _transcript.GetRealizedContainers())
        {
            var index = _transcript.IndexFromContainer(container);
            if (index < 0 || container.TranslatePoint(default, _transcriptScroll) is not { } p)
            {
                continue;
            }

            if (p.Y + container.Bounds.Height > 0 && p.Y < viewportHeight) // intersects the viewport
            {
                firstVisibleIndex = System.Math.Min(firstVisibleIndex, index);
                messageVisible |= container.DataContext is MessageBubbleItem;
            }
        }

        if (messageVisible || firstVisibleIndex is int.MaxValue or 0)
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
