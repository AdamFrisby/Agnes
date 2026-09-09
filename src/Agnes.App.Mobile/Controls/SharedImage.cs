using Agnes.App.Mobile.ViewModels;
using Agnes.Ui.Core.Transcript;
using Agnes.Ui.Core.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace Agnes.App.Mobile.Controls;

/// <summary>
/// The inline thumbnail on a "the agent sent you a file" card: an <see cref="Image"/> that fetches its own
/// bytes from the session's host the first time it is shown, and stays empty until they arrive.
///
/// It loads itself rather than being handed a bitmap because the transcript binds
/// <see cref="SharedFileItem"/> directly — the card's data context is the shared contract type, which by
/// design carries no bytes and knows nothing about a host. Doing the fetch here keeps that true and keeps
/// the cost where it belongs: nothing is downloaded until a card with an image on it is actually on screen.
/// </summary>
public sealed class SharedImage : Image
{
    /// <summary>The session the file came from — supplies the host connection and the session id.</summary>
    public static readonly StyledProperty<SessionViewModel?> SessionProperty =
        AvaloniaProperty.Register<SharedImage, SessionViewModel?>(nameof(Session));

    /// <summary>The file to show.</summary>
    public static readonly StyledProperty<SharedFileItem?> ItemProperty =
        AvaloniaProperty.Register<SharedImage, SharedFileItem?>(nameof(Item));

    private string? _loaded;

    public SessionViewModel? Session
    {
        get => GetValue(SessionProperty);
        set => SetValue(SessionProperty, value);
    }

    public SharedFileItem? Item
    {
        get => GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SessionProperty || change.Property == ItemProperty)
        {
            Reload();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Reload();
    }

    private void Reload()
    {
        if (Session is not { } session || Item is not { IsImage: true } file)
        {
            Source = null;
            _loaded = null;
            return;
        }

        var key = SharedFilePreviews.KeyFor(session, file);
        if (_loaded == key)
        {
            return;
        }

        _loaded = key;

        // Already fetched (this card scrolled back into view): decode straight away so there's no flash.
        if (SharedFilePreviews.Cached(session, file) is { } cached)
        {
            Show(key, cached);
            return;
        }

        Source = null;
        _ = LoadAsync(session, file, key);
    }

    private async Task LoadAsync(SessionViewModel session, SharedFileItem file, string key)
    {
        byte[]? bytes;
        try
        {
            bytes = await SharedFilePreviews.LoadAsync(session, file).ConfigureAwait(true);
        }
        catch
        {
            // A host that can't serve the bytes leaves the card as its type glyph and caption. That is a
            // complete, honest card — never an error banner inside a transcript.
            return;
        }

        if (bytes is { Length: > 0 })
        {
            Show(key, bytes);
        }
    }

    private void Show(string key, byte[] bytes)
    {
        // The container may have been recycled onto a different file while the fetch was in flight.
        if (_loaded != key)
        {
            return;
        }

        try
        {
            using var stream = new MemoryStream(bytes);
            Source = new Bitmap(stream);
        }
        catch
        {
            // Not a format this device can decode (an SVG, say). The glyph already said "image".
            Source = null;
        }
    }
}
