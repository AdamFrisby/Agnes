using System.Collections.Concurrent;
using Agnes.Protocol;
using Agnes.Ui.Core;
using Agnes.Ui.Core.Transcript;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.App.Mobile.ViewModels;

/// <summary>
/// The phone's access to the files an agent sent: which ones a session has, and how to get their bytes.
///
/// A <see cref="SharedFileItem"/> deliberately carries no bytes — the host keeps the copy, and a phone on
/// a train should not be paying for one until someone taps it. Everything here therefore goes back to the
/// session's own host connection, which is already authenticated and certificate-pinned; nothing about a
/// received file is fetched over a second, differently-trusted path.
/// </summary>
public static class SharedFileAccess
{
    /// <summary>Every file this session has been sent, oldest first (transcript order).</summary>
    public static IEnumerable<SharedFileItem> Of(SessionViewModel session)
        => session.Items.OfType<SharedFileItem>();

    /// <summary>
    /// Fetches the file's bytes, ready to hand to an <see cref="IReceivedFileHandler"/>.
    ///
    /// A file whose preview is already in hand is not fetched again — tapping Save on a screenshot you are
    /// currently looking at should cost nothing, and on a phone that difference is a second and some data.
    /// </summary>
    public static async Task<ReceivedFile> DownloadAsync(
        SessionViewModel session, SharedFileItem file, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var bytes = SharedFilePreviews.Cached(session, file)
            ?? await session.Host.DownloadFileAsync(session.SessionId, file.RelativePath).ConfigureAwait(false);

        return new ReceivedFile(file.FileName, file.MimeType, bytes);
    }

    /// <summary>
    /// Reads the file for display rather than for saving: text comes back as text, an image as bytes.
    /// Returns null when the host can't serve it (an older host has no file browser at all), so a preview
    /// degrades to a type glyph instead of an error the user can do nothing about.
    /// </summary>
    public static async Task<FileContent?> PreviewAsync(
        SessionViewModel session, SharedFileItem file, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await session.Host.ReadFileAsync(session.SessionId, file.RelativePath).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Bytes for shared files, fetched once per file and kept for the life of the process.
///
/// The transcript re-creates its card containers as you scroll, so without this a long conversation would
/// re-download the same screenshot every time it came back on screen — the one thing a phone on cellular
/// data must not do. It is also what makes Save on an image you're already looking at free: the sheet's
/// download consults this first.
/// </summary>
public static class SharedFilePreviews
{
    /// <summary>The largest file kept in memory. Above this a fetch still returns the bytes; it just
    /// doesn't hold on to them.</summary>
    private const int CacheLimit = 8 * 1024 * 1024;

    private static readonly ConcurrentDictionary<string, byte[]> Cache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, Task<byte[]?>> InFlight = new(StringComparer.Ordinal);

    /// <summary>The cache key for a file: scoped by session, because file ids are only unique within one.</summary>
    public static string KeyFor(SessionViewModel session, SharedFileItem file)
        => session.SessionId + "/" + file.FileId;

    /// <summary>Pre-fills the cache. Used by the preview harness and the tests, which have bytes in hand
    /// and no host that can serve them.</summary>
    public static void Seed(SessionViewModel session, SharedFileItem file, byte[] bytes)
        => Cache[KeyFor(session, file)] = bytes;

    /// <summary>The bytes if they're already here — lets a card show an image on its first layout pass
    /// rather than flashing empty and filling in.</summary>
    public static byte[]? Cached(SessionViewModel session, SharedFileItem file)
        => Cache.TryGetValue(KeyFor(session, file), out var bytes) ? bytes : null;

    /// <summary>Fetches (or returns) the file's bytes. Null when the host can't serve them.</summary>
    public static Task<byte[]?> LoadAsync(SessionViewModel session, SharedFileItem file)
    {
        var key = KeyFor(session, file);
        if (Cache.TryGetValue(key, out var hit))
        {
            return Task.FromResult<byte[]?>(hit);
        }

        // One fetch per file even if three cards ask at once (a scroll can easily do that).
        return InFlight.GetOrAdd(key, _ => FetchAsync(key, session, file));
    }

    private static async Task<byte[]?> FetchAsync(string key, SessionViewModel session, SharedFileItem file)
    {
        try
        {
            byte[]? bytes = null;
            try
            {
                bytes = await session.Host.DownloadFileAsync(session.SessionId, file.RelativePath).ConfigureAwait(false);
            }
            catch
            {
                // Fall through to the reader below — a host may serve one and not the other.
            }

            if (bytes is null or { Length: 0 }
                && await SharedFileAccess.PreviewAsync(session, file).ConfigureAwait(false) is { Bytes: { Length: > 0 } read })
            {
                bytes = read;
            }

            if (bytes is not { Length: > 0 })
            {
                return null;
            }

            // Only small things are kept. A screenshot is worth holding for the life of the session; a
            // 300 MB build artifact would be an out-of-memory crash dressed up as a cache.
            if (bytes.Length <= CacheLimit)
            {
                Cache[key] = bytes;
            }

            return bytes;
        }
        finally
        {
            InFlight.TryRemove(key, out _);
        }
    }
}

/// <summary>
/// One file an agent sent, as an inbox row: which session it came from and how long ago, so a file you
/// were sent while the phone was in a pocket is findable without remembering which agent sent it.
/// </summary>
public sealed class SharedFileRow
{
    public SharedFileRow(SessionEntry entry, SharedFileItem file)
    {
        Entry = entry;
        File = file;
    }

    public SessionEntry Entry { get; }

    public SharedFileItem File { get; }

    public string FileName => File.FileName;

    public string SessionTitle => Entry.Title;

    public string HostName => Entry.HostName;

    /// <summary>"1.2 MB · PNG", with the extension dropped when the name has none.</summary>
    public string Meta => File.Extension.Length > 0 ? $"{File.SizeText} · {File.Extension}" : File.SizeText;

    /// <summary>"now / 4m / 2h / 3d" — the same relative clock the session list uses.</summary>
    public string Age => RelativeTime.Format(File.Timestamp);

    /// <summary>When it arrived, for ordering. Newest first.</summary>
    public DateTimeOffset When => File.Timestamp;

    /// <summary>The moment in the transcript this row points at, so opening it lands on the card.</summary>
    public long Sequence => File.Sequence;
}
