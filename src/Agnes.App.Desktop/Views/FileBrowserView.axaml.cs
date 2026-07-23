using System;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using Agnes.Protocol;
using Agnes.Ui.Core.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace Agnes.App.Desktop.Views;

/// <summary>
/// Desktop host for the workspace file browser (git-and-files/03). All list/read/write/rename/delete/download
/// transport and path handling lives in the framework-agnostic <see cref="FileBrowserViewModel"/> (unit
/// tested); this file is the Avalonia glue only — double-tap to open, decoding image bytes into a
/// <see cref="Bitmap"/> for preview, and the native save dialog for a download.
/// </summary>
public partial class FileBrowserView : UserControl
{
    private FileBrowserViewModel? _vm;

    public FileBrowserView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }

        _vm = DataContext as FileBrowserViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
        }

        RefreshImagePreview();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FileBrowserViewModel.OpenFile) or nameof(FileBrowserViewModel.IsImageOpen))
        {
            Dispatcher.UIThread.Post(RefreshImagePreview);
        }
    }

    // Rebuilds the image preview from the open file's bytes (or clears it), on the UI thread.
    private void RefreshImagePreview()
    {
        var image = this.FindControl<Image>("ImagePreview");
        if (image is null)
        {
            return;
        }

        image.Source = null;
        if (_vm?.OpenFile is { Kind: FileContentKind.Image, Bytes: { Length: > 0 } bytes })
        {
            try
            {
                using var stream = new MemoryStream(bytes);
                image.Source = new Bitmap(stream);
            }
            catch (Exception)
            {
                // An oversized/corrupt image decodes to nothing rather than throwing into the UI — the pane
                // simply shows no bitmap (git-and-files/03 AC: a clear fallback, not a broken control).
                image.Source = null;
            }
        }
    }

    private void OnEntryActivated(object? sender, RoutedEventArgs e)
    {
        if (_vm?.SelectedEntry is { } entry)
        {
            _ = _vm.NavigateIntoAsync(entry);
        }
    }

    private void OnCreateFolder(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || this.FindControl<TextBox>("NameInput") is not { Text: { Length: > 0 } name } box)
        {
            return;
        }

        box.Text = string.Empty;
        _ = _vm.CreateFolderAsync(name);
    }

    private void OnRename(object? sender, RoutedEventArgs e)
    {
        if (_vm?.SelectedEntry is { } entry
            && this.FindControl<TextBox>("NameInput") is { Text: { Length: > 0 } name } box)
        {
            box.Text = string.Empty;
            _ = _vm.RenameAsync(entry, name);
        }
    }

    private async void OnDownload(object? sender, RoutedEventArgs e)
    {
        if (_vm?.SelectedEntry is not { IsDirectory: false } entry
            || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        var target = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Download file",
            SuggestedFileName = entry.Name,
        });

        if (target is null)
        {
            return;
        }

        var bytes = await _vm.DownloadAsync(entry).ConfigureAwait(true);
        await using var stream = await target.OpenWriteAsync().ConfigureAwait(true);
        await stream.WriteAsync(bytes).ConfigureAwait(true);
    }
}
