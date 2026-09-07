using Agnes.Ui.Core;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Agnes.App.Desktop;

/// <summary>
/// What the desktop can do with a file an agent sent: put it where the person chooses, or hand it to the
/// OS to open. There is deliberately no share verb — a desktop has no system share sheet, and a button
/// that opens nothing is worse than no button, so <see cref="CanShare"/> is false and the view hides it.
/// </summary>
/// <remarks>
/// The <see cref="TopLevel"/> is resolved per call rather than captured, because a session can be dragged
/// out into its own window: the picker has to be parented to the window the person is actually looking at,
/// or it appears behind the one they clicked in (or, on some platforms, not at all).
/// </remarks>
public sealed class DesktopReceivedFileHandler(Func<TopLevel?> topLevel) : IReceivedFileHandler
{
    /// <summary>Where "Open" materializes files, so they can be cleaned up as a group if it ever matters.</summary>
    public static string TempRoot => Path.Combine(Path.GetTempPath(), "agnes-received");

    public bool CanSave => true;
    public bool CanOpen => true;
    public bool CanShare => false;

    public async Task SaveAsync(ReceivedFile file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (topLevel()?.StorageProvider is not { } storage)
        {
            return;
        }

        var target = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save file",
            SuggestedFileName = file.FileName,
            SuggestedStartLocation = await storage.TryGetWellKnownFolderAsync(WellKnownFolder.Downloads).ConfigureAwait(true),
        }).ConfigureAwait(true);

        if (target is null)
        {
            return; // the person closed the picker — not a failure
        }

        await using var stream = await target.OpenWriteAsync().ConfigureAwait(true);
        await stream.WriteAsync(file.Bytes, cancellationToken).ConfigureAwait(true);
    }

    public async Task OpenAsync(ReceivedFile file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (topLevel() is not { } top)
        {
            return;
        }

        // A fresh folder per open, named by a guid: the agent chose the leaf name, and two files called
        // "screenshot.png" from two turns must not overwrite each other (nor let a crafted name land on
        // something already in the temp root).
        var dir = Path.Combine(TempRoot, Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Path.GetFileName(file.FileName));
        await File.WriteAllBytesAsync(path, file.Bytes, cancellationToken).ConfigureAwait(true);

        await top.Launcher.LaunchFileInfoAsync(new FileInfo(path)).ConfigureAwait(true);
    }

    /// <summary>Not a desktop verb. Present only because the interface has three; never offered in the UI.</summary>
    public Task ShareAsync(ReceivedFile file, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
