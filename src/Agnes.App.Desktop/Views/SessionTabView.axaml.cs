using System.ComponentModel;
using Agnes.Ui.Core.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace Agnes.App.Desktop.Views;

public partial class SessionTabView : UserControl
{
    private const double SplitterWidth = 5;
    private Grid? _workspace;
    private ListBox? _transcript;
    private SessionViewModel? _session;
    private double _leftWidth = 288;
    private double _rightWidth = 540;

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

        // Drop files (or an image) anywhere on the session to attach them to the composer.
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void HookSession()
    {
        if (_session is not null)
        {
            _session.PropertyChanged -= OnSessionPropertyChanged;
            _session.ScrollToRequested -= OnScrollToRequested;
        }

        _session = _workspace?.DataContext as SessionViewModel;
        if (_session is not null)
        {
            _session.PropertyChanged += OnSessionPropertyChanged;
            _session.ScrollToRequested += OnScrollToRequested;
        }

        UpdateColumns();
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

    // Attach a picked/dropped file: images become inline image blocks, everything else an @-reference.
    private async System.Threading.Tasks.Task AttachStorageFileAsync(Avalonia.Platform.Storage.IStorageFile file)
    {
        if (_session is null)
        {
            return;
        }

        var name = file.Name;
        if (IsImage(name))
        {
            await using var stream = await file.OpenReadAsync();
            using var ms = new System.IO.MemoryStream();
            await stream.CopyToAsync(ms);
            var mime = name.EndsWith(".png", System.StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
            _session.Attach(new PromptAttachment(name, "img",
                new Agnes.Abstractions.ImageContent(mime, System.Convert.ToBase64String(ms.ToArray()))));
        }
        else
        {
            _session.Attach(PromptAttachment.Reference(file.Path.LocalPath));
        }
    }

    private static bool IsImage(string name)
        => name.EndsWith(".png", System.StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".jpg", System.StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".jpeg", System.StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".gif", System.StringComparison.OrdinalIgnoreCase);

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
