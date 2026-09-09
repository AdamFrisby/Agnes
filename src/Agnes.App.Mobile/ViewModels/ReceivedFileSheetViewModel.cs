using Agnes.Ui.Core;
using Agnes.Ui.Core.Transcript;
using Agnes.Ui.Core.ViewModels;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Agnes.App.Mobile.ViewModels;

/// <summary>
/// A file the agent sent, opened: the thing itself as large as the sheet allows, then the three verbs.
///
/// Order matters and is deliberate. <b>Share</b> is first and primary because it is what a phone is
/// uniquely good at — the screenshot the agent just produced is two taps from the group chat, which on a
/// laptop is a download, a file manager and an upload. <b>Save to Downloads</b> is second: keeping it is
/// the next most common intent, and Downloads is the one folder every Android app can find again.
/// <b>Open</b> is last because it hands the file to some other app, which ends the errand — a fine
/// outcome, just rarely the one you wanted from a notification.
///
/// Everything reports in words. "Saved to Downloads" is a line in the sheet rather than a toast, because
/// the sheet is where the question was asked and a toast over a sheet is read as belonging to the screen
/// behind it.
/// </summary>
public sealed partial class ReceivedFileSheetViewModel : SheetViewModel
{
    private readonly SessionViewModel _session;
    private readonly IReceivedFileHandler _handler;
    private readonly IAppShell _shell;

    /// <summary>How much of a text file the preview shows. Past this, a phone is the wrong reader and
    /// the honest move is to hand it to an app that is the right one.</summary>
    private const int TextPreviewLimit = 20_000;

    public ReceivedFileSheetViewModel(IAppShell shell, SessionViewModel session, SharedFileItem file)
    {
        _shell = shell;
        _session = session;
        _handler = shell.ReceivedFiles;
        File = file;

        ShareCommand = new AsyncRelayCommand(
            () => RunAsync("Opening the share sheet…", _handler.ShareAsync, null),
            () => _handler.CanShare && !IsBusy);
        SaveCommand = new AsyncRelayCommand(
            () => RunAsync("Saving…", _handler.SaveAsync, "Saved to Downloads"),
            () => _handler.CanSave && !IsBusy);
        OpenCommand = new AsyncRelayCommand(
            () => RunAsync("Opening…", _handler.OpenAsync, null),
            () => _handler.CanOpen && !IsBusy);

        _ = LoadPreviewAsync();
    }

    public SharedFileItem File { get; }

    public override string Title => File.FileName;

    /// <summary>"1.2 MB · PNG" — the two facts that decide whether you want it on the phone at all.</summary>
    public override string? Subtitle => File.Extension.Length > 0
        ? $"{File.SizeText} · {File.Extension}"
        : File.SizeText;

    public override double HeightFraction => 0.9;

    /// <summary>What the agent said about it, if anything.</summary>
    public string Caption => File.Caption ?? string.Empty;

    public bool HasCaption => File.HasCaption;

    // ---- preview ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage))]
    [NotifyPropertyChangedFor(nameof(HasNoPreview))]
    private Bitmap? _image;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasText))]
    [NotifyPropertyChangedFor(nameof(HasNoPreview))]
    private string? _text;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoPreview))]
    private bool _isLoadingPreview = true;

    public bool HasImage => Image is not null;

    public bool HasText => !string.IsNullOrEmpty(Text);

    /// <summary>True once we've looked and there is nothing to show — a zip, a binary, a host that can't
    /// serve the bytes. The sheet then shows the type glyph and says so, rather than a blank rectangle.</summary>
    public bool HasNoPreview => !IsLoadingPreview && !HasImage && !HasText;

    // ---- actions ----

    public IAsyncRelayCommand ShareCommand { get; }
    public IAsyncRelayCommand SaveCommand { get; }
    public IAsyncRelayCommand OpenCommand { get; }

    public bool CanShare => _handler.CanShare;
    public bool CanSave => _handler.CanSave;
    public bool CanOpen => _handler.CanOpen;

    /// <summary>True when this device can't do any of the three — the preview is still worth having, so
    /// the sheet says why the buttons are missing instead of showing three dead ones.</summary>
    public bool CanDoNothing => !CanShare && !CanSave && !CanOpen;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private bool _statusIsError;

    public bool HasStatus => Status.Length > 0;

    private async Task LoadPreviewAsync()
    {
        try
        {
            if (File.IsImage)
            {
                var bytes = await SharedFilePreviews.LoadAsync(_session, File).ConfigureAwait(true);
                if (bytes is { Length: > 0 })
                {
                    using var stream = new MemoryStream(bytes);
                    Image = new Bitmap(stream);
                }
            }
            else if (File.IsText
                     && await SharedFileAccess.PreviewAsync(_session, File).ConfigureAwait(true) is { Text: { } body })
            {
                Text = body.Length > TextPreviewLimit
                    ? body[..TextPreviewLimit] + "\n\n… truncated. Open or save it to read the rest."
                    : body;
            }
        }
        catch
        {
            // A preview is a courtesy; failing to build one must never take the actions away.
        }
        finally
        {
            IsLoadingPreview = false;
        }
    }

    /// <summary>
    /// Fetches the bytes, hands them to the platform, and reports the outcome in the sheet.
    /// </summary>
    /// <param name="working">What to say while it happens.</param>
    /// <param name="verb">The handler call.</param>
    /// <param name="done">What to say afterwards, or null when the platform's own UI is the confirmation
    /// (a share sheet or another app opening says more than a line of ours could).</param>
    private async Task RunAsync(
        string working, Func<ReceivedFile, CancellationToken, Task> verb, string? done)
    {
        IsBusy = true;
        StatusIsError = false;
        Report(working);
        RaiseCommands();

        try
        {
            var received = await SharedFileAccess.DownloadAsync(_session, File).ConfigureAwait(true);
            if (received.Bytes.Length == 0)
            {
                StatusIsError = true;
                Report("The host couldn't hand over this file. It may have been cleaned up.");
                return;
            }

            await verb(received, CancellationToken.None).ConfigureAwait(true);
            Report(done ?? string.Empty);
            _shell.Haptics.Success();
        }
        catch (Exception ex)
        {
            StatusIsError = true;
            Report(ex.Message);
        }
        finally
        {
            IsBusy = false;
            RaiseCommands();
        }
    }

    private void Report(string message)
    {
        Status = message;
        OnPropertyChanged(nameof(HasStatus));
    }

    private void RaiseCommands()
    {
        ShareCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        OpenCommand.NotifyCanExecuteChanged();
    }
}
