using Agnes.Ui.Core;
using Android.Content;
using Android.Media;
using Android.Provider;
using AndroidEnvironment = Android.OS.Environment;
using AndroidUri = Android.Net.Uri;

namespace Agnes.App.Mobile.Services;

/// <summary>
/// What this phone does with a file an agent sent it: keep it, pass it on, or hand it to another app.
///
/// The three verbs are genuinely different on Android and each has one right mechanism:
///
/// <list type="bullet">
/// <item><b>Save</b> goes to <c>Downloads</c> through <see cref="MediaStore"/>, which since API 29 is the
/// only way to write there without a storage permission at all — the file is created through the media
/// database, staged as <c>IS_PENDING</c> so nothing indexes a half-written file, and published when the
/// bytes are down. On 26–28 MediaStore has no Downloads collection, so it's a plain write to the public
/// directory plus a media scan, which is what makes it appear in the Files app rather than only on disk.</item>
/// <item><b>Share</b> stages a copy in the app's own cache and hands out a <c>content://</c> URI from our
/// <see cref="AgnesFileProvider"/>. A <c>file://</c> URI in an Intent has thrown <c>FileUriExposedException</c>
/// since Android 7 — the receiving app has no permission to our storage, and the grant has to travel with
/// the URI.</item>
/// <item><b>Open</b> is the same URI with <see cref="Intent.ActionView"/>, through a chooser when nothing
/// claims the type, so an unknown format offers "open with…" instead of failing silently.</item>
/// </list>
///
/// The staged copy lives in the cache directory on purpose: Android may reclaim it, which is correct —
/// a share is a hand-off, and the host still holds the original.
/// </summary>
public sealed class AndroidReceivedFileHandler : IReceivedFileHandler
{
    /// <summary>Sub-directory of the app cache the <see cref="AgnesFileProvider"/> is allowed to serve.
    /// Must match <c>Resources/xml/file_paths.xml</c>.</summary>
    private const string StageDirectory = "received";

    /// <summary>How long a staged copy is kept before the next share sweeps it up.</summary>
    private static readonly TimeSpan StageLifetime = TimeSpan.FromHours(24);

    private readonly Context _context;

    public AndroidReceivedFileHandler(Context context) => _context = context;

    /// <summary>Android always has somewhere to put a file.</summary>
    public bool CanSave => true;

    /// <summary>Android always has an "open with" chooser, even when nothing claims the type.</summary>
    public bool CanOpen => true;

    /// <summary>Android always has a share sheet — the phone's single best trick with a received file.</summary>
    public bool CanShare => true;

    // ---- save ----

    /// <inheritdoc />
    public Task SaveAsync(ReceivedFile file, CancellationToken cancellationToken = default)
        => OperatingSystem.IsAndroidVersionAtLeast(29)
            ? SaveThroughMediaStoreAsync(file, cancellationToken)
            : SaveToPublicDirectoryAsync(file, cancellationToken);

    private async Task SaveThroughMediaStoreAsync(ReceivedFile file, CancellationToken cancellationToken)
    {
        var name = ReceivedFileNaming.Sanitize(file.FileName);
        var resolver = _context.ContentResolver
            ?? throw new IOException("Android didn't hand over a content resolver.");

        var values = new ContentValues();
        values.Put(MediaStore.IMediaColumns.DisplayName, name);
        values.Put(MediaStore.IMediaColumns.MimeType, ReceivedFileNaming.MimeFor(file));
        values.Put(MediaStore.IMediaColumns.RelativePath, AndroidEnvironment.DirectoryDownloads);
        // Staged: nothing else sees the row until the bytes are actually there, so a scanner or a file
        // manager can't offer a truncated download.
        values.Put(MediaStore.IMediaColumns.IsPending, 1);

        var collection = MediaStore.Downloads.ExternalContentUri
            ?? throw new IOException("This device has no Downloads collection.");

        var uri = resolver.Insert(collection, values)
            ?? throw new IOException("Android refused to create the file in Downloads.");

        try
        {
            using (var stream = resolver.OpenOutputStream(uri)
                   ?? throw new IOException("Android refused to open the new file for writing."))
            {
                await stream.WriteAsync(file.Bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            // A pending row nobody publishes is invisible and undeletable from the Files app; clean it up
            // rather than leaving a ghost behind after a failed save.
            resolver.Delete(uri, null, null);
            throw;
        }

        var publish = new ContentValues();
        publish.Put(MediaStore.IMediaColumns.IsPending, 0);
        resolver.Update(uri, publish, null, null);
    }

    private async Task SaveToPublicDirectoryAsync(ReceivedFile file, CancellationToken cancellationToken)
    {
        // 26–28: a real filesystem write, which needs the legacy storage permission. If it isn't granted,
        // ask — and say so, rather than failing with an IOException the user can't act on.
        if (!LegacyStorage.EnsureGranted())
        {
            throw new IOException(
                "Android needs permission to write to Downloads. Allow it, then tap Save again.");
        }

        var directory = AndroidEnvironment.GetExternalStoragePublicDirectory(AndroidEnvironment.DirectoryDownloads)
            ?? throw new IOException("This device has no Downloads folder.");

        directory.Mkdirs();
        var target = UniquePath(directory.AbsolutePath!, ReceivedFileNaming.Sanitize(file.FileName));
        await File.WriteAllBytesAsync(target, file.Bytes, cancellationToken).ConfigureAwait(false);

        // Without the scan the file exists but no file manager lists it, which reads as "the save failed".
        MediaScannerConnection.ScanFile(_context, [target], [ReceivedFileNaming.MimeFor(file)], null);
    }

    // ---- share / open ----

    /// <inheritdoc />
    public async Task ShareAsync(ReceivedFile file, CancellationToken cancellationToken = default)
    {
        var uri = await StageAsync(file, cancellationToken).ConfigureAwait(false);
        var mime = ReceivedFileNaming.MimeFor(file);

        var send = new Intent(Intent.ActionSend);
        send.SetType(mime);
        send.PutExtra(Intent.ExtraStream, uri);
        send.PutExtra(Intent.ExtraTitle, file.FileName);
        send.AddFlags(ActivityFlags.GrantReadUriPermission);

        // Always a chooser: "share" means the person picks where, and remembering a default for them is
        // the wrong call for something that leaves the device.
        Launch(Intent.CreateChooser(send, "Share " + file.FileName));
    }

    /// <inheritdoc />
    public async Task OpenAsync(ReceivedFile file, CancellationToken cancellationToken = default)
    {
        var uri = await StageAsync(file, cancellationToken).ConfigureAwait(false);

        var view = new Intent(Intent.ActionView);
        view.SetDataAndType(uri, ReceivedFileNaming.MimeFor(file));
        view.AddFlags(ActivityFlags.GrantReadUriPermission);

        // Nothing claims the type (or we couldn't name it): offer the chooser rather than throwing an
        // ActivityNotFoundException at someone who just wanted to look at a file.
        var resolved = view.ResolveActivity(_context.PackageManager!) is not null;
        Launch(resolved ? view : Intent.CreateChooser(view, "Open " + file.FileName));
    }

    /// <summary>
    /// Writes the bytes into the cache directory the provider serves and returns the <c>content://</c> URI
    /// for them.
    /// </summary>
    private async Task<AndroidUri> StageAsync(ReceivedFile file, CancellationToken cancellationToken)
    {
        var root = _context.CacheDir?.AbsolutePath
            ?? throw new IOException("This device gave the app no cache directory.");

        var stage = Path.Combine(root, StageDirectory);
        Directory.CreateDirectory(stage);
        Sweep(stage);

        var path = Path.Combine(stage, ReceivedFileNaming.Sanitize(file.FileName));
        await File.WriteAllBytesAsync(path, file.Bytes, cancellationToken).ConfigureAwait(false);

        return AndroidX.Core.Content.FileProvider.GetUriForFile(
                   _context, _context.PackageName + ".fileprovider", new Java.IO.File(path))
               ?? throw new IOException("Android wouldn't produce a shareable link for that file.");
    }

    /// <summary>Drops staged copies older than <see cref="StageLifetime"/>. Best-effort: a file that can't
    /// be deleted is not a reason to fail the share the user actually asked for.</summary>
    private static void Sweep(string stage)
    {
        try
        {
            var cutoff = DateTime.UtcNow - StageLifetime;
            foreach (var old in Directory.EnumerateFiles(stage))
            {
                if (File.GetLastWriteTimeUtc(old) < cutoff)
                {
                    File.Delete(old);
                }
            }
        }
        catch
        {
            // best-effort
        }
    }

    private void Launch(Intent? intent)
    {
        if (intent is null)
        {
            return;
        }

        intent.AddFlags(ActivityFlags.GrantReadUriPermission);

        // Prefer the live activity so the chooser appears over the app; only fall back to a new task when
        // there isn't one, since NEW_TASK from an activity puts the chooser in its own recents entry.
        if (AndroidHost.Activity is { } activity)
        {
            activity.StartActivity(intent);
            return;
        }

        intent.AddFlags(ActivityFlags.NewTask);
        _context.StartActivity(intent);
    }

    /// <summary>"report.pdf", then "report (2).pdf" — never silently overwriting something already saved.</summary>
    private static string UniquePath(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        if (!File.Exists(path))
        {
            return path;
        }

        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var n = 2; n < 1000; n++)
        {
            path = Path.Combine(directory, $"{stem} ({n}){extension}");
            if (!File.Exists(path))
            {
                return path;
            }
        }

        return Path.Combine(directory, $"{stem} ({Guid.NewGuid():n}){extension}");
    }
}

/// <summary>
/// The legacy storage permission, needed only on API 26–28 where writing to Downloads is a real
/// filesystem write. From 29 on, <see cref="MediaStore"/> needs no permission at all — which is why this
/// is quarantined here rather than sitting in <see cref="AndroidCapabilities"/> with the permissions the
/// app actually depends on.
/// </summary>
internal static class LegacyStorage
{
    private const int RequestCode = 0x5357; // "SW"

    /// <summary>True when the app may write. Otherwise it asks and returns false — the user grants it and
    /// taps Save again, which is one extra tap on an OS version nobody new is running.</summary>
    public static bool EnsureGranted()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            return true;
        }

        try
        {
            const string permission = global::Android.Manifest.Permission.WriteExternalStorage;
            if (AndroidHost.Context.CheckSelfPermission(permission) == global::Android.Content.PM.Permission.Granted)
            {
                return true;
            }

            AndroidHost.Activity?.RequestPermissions([permission], RequestCode);
            return false;
        }
        catch
        {
            // A device that refuses to answer gets the attempt anyway; the write's own error is clearer
            // than a guess made here.
            return true;
        }
    }
}

/// <summary>
/// The app's <c>content://</c> provider, exposing exactly one directory: the <c>received/</c> folder in the
/// app cache where <see cref="AndroidReceivedFileHandler"/> stages a file before sharing it.
///
/// Declared as an attribute rather than in a hand-written manifest, because this head has no
/// <c>AndroidManifest.xml</c> — the manifest is generated, and the reasons for each entry stay next to the
/// code that needs them (see <c>Permissions.cs</c> for the same choice). <c>Exported=false</c> plus
/// <c>GrantUriPermissions=true</c> is the whole security model: nothing can enumerate the provider, and an
/// app only ever sees the single file a share handed it, for as long as that Intent lives.
/// </summary>
[ContentProvider(
    ["${applicationId}.fileprovider"],
    Exported = false,
    GrantUriPermissions = true)]
[MetaData("android.support.FILE_PROVIDER_PATHS", Resource = "@xml/file_paths")]
public sealed class AgnesFileProvider : AndroidX.Core.Content.FileProvider
{
}
