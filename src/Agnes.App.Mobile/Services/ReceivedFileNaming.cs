using Agnes.Ui.Core;

namespace Agnes.App.Mobile.Services;

/// <summary>
/// The two decisions a received file needs before it touches Android, kept here as pure functions of
/// their inputs so they can be tested without a device.
///
/// Both exist because the name and the type come from the <em>agent</em>, not from us. An agent that
/// writes <c>../../etc/passwd</c> or a 400-character filename into a <see cref="Agnes.Abstractions.FileSharedEvent"/>
/// must not be able to steer where the phone writes, and a file the agent didn't type must still land in
/// front of an app that can open it rather than in front of nothing.
/// </summary>
public static class ReceivedFileNaming
{
    /// <summary>How long a saved name may get before it's truncated (extension kept).</summary>
    private const int MaxLength = 96;

    /// <summary>Fallback when nothing usable survives sanitising.</summary>
    private const string Fallback = "file";

    /// <summary>
    /// A single, safe file name: no directory separators (of either flavour), no control characters, no
    /// leading dots, and short enough for every Android filesystem. The result is always a name, never a
    /// path — which is the point: it is used as a leaf under Downloads and under the app's cache.
    /// </summary>
    public static string Sanitize(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return Fallback;
        }

        // Take the leaf ourselves rather than via Path.GetFileName: that one is separator-aware per OS,
        // and a Windows-style name arriving on Android would keep its backslashes.
        var leaf = fileName;
        var cut = leaf.LastIndexOfAny(['/', '\\']);
        if (cut >= 0)
        {
            leaf = leaf[(cut + 1)..];
        }

        var safe = new string([.. leaf.Select(c =>
            c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' or '\0' || char.IsControl(c)
                ? '_'
                : c)]);

        safe = safe.Trim().Trim('.');
        if (safe.Length == 0)
        {
            return Fallback;
        }

        if (safe.Length <= MaxLength)
        {
            return safe;
        }

        // Truncate the stem, not the extension — the extension is what tells Android which app to offer.
        var dot = safe.LastIndexOf('.');
        var extension = dot > 0 && safe.Length - dot <= 12 ? safe[dot..] : string.Empty;
        return safe[..(MaxLength - extension.Length)] + extension;
    }

    /// <summary>
    /// The MIME type to hand Android. The host's declared type wins; otherwise it's inferred from the
    /// extension, and a file we can't place gets <c>*&#47;*</c> so the chooser offers everything rather
    /// than nothing.
    /// </summary>
    public static string MimeFor(ReceivedFile file) => MimeFor(file.FileName, file.MimeType);

    /// <inheritdoc cref="MimeFor(ReceivedFile)"/>
    public static string MimeFor(string fileName, string? declared)
    {
        // Only trust a declared type that is actually shaped like one: a bare word in an Intent's type
        // makes the chooser resolve nothing at all, which reads to the user as "sharing is broken".
        if (declared is { Length: > 2 } && declared.IndexOf('/') > 0 && !declared.EndsWith('/'))
        {
            return declared.Split(';')[0].Trim();
        }

        var extension = fileName.LastIndexOf('.') is var dot && dot >= 0 && dot < fileName.Length - 1
            ? fileName[(dot + 1)..].ToLowerInvariant()
            : string.Empty;

        return extension switch
        {
            "png" => "image/png",
            "jpg" or "jpeg" => "image/jpeg",
            "gif" => "image/gif",
            "webp" => "image/webp",
            "bmp" => "image/bmp",
            "svg" => "image/svg+xml",
            "pdf" => "application/pdf",
            "json" => "application/json",
            "xml" => "application/xml",
            "zip" => "application/zip",
            "csv" => "text/csv",
            "html" or "htm" => "text/html",
            "md" or "markdown" => "text/markdown",
            "txt" or "log" or "diff" or "patch" => "text/plain",
            "yml" or "yaml" => "application/x-yaml",
            "mp4" => "video/mp4",
            "webm" => "video/webm",
            "mp3" => "audio/mpeg",
            _ => "*/*",
        };
    }
}
