namespace Agnes.Host.Files;

/// <summary>
/// Best-effort mime type for a file an agent sends the user. Extension-only and deliberately small: the value
/// is a rendering *hint* for the clients (show a screenshot inline, offer a PDF for download, syntax-colour a
/// log), never an authorization or safety decision, so sniffing contents would buy nothing and guessing wrong
/// costs only a plainer card. Unknown returns null rather than
/// <c>application/octet-stream</c> — "we don't know" is honest, and a client can then decide for itself.
/// </summary>
public static class SharedFileTypes
{
    private static readonly IReadOnlyDictionary<string, string> ByExtension =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif",
            [".webp"] = "image/webp",
            [".svg"] = "image/svg+xml",

            [".pdf"] = "application/pdf",

            [".txt"] = "text/plain",
            [".log"] = "text/plain",
            [".md"] = "text/markdown",
            [".csv"] = "text/csv",
            [".json"] = "application/json",
            [".xml"] = "application/xml",
            [".yaml"] = "application/yaml",
            [".yml"] = "application/yaml",
            [".html"] = "text/html",

            [".zip"] = "application/zip",
            [".tar"] = "application/x-tar",
            [".gz"] = "application/gzip",

            [".mp4"] = "video/mp4",
            [".webm"] = "video/webm",
        };

    /// <summary>The mime type for <paramref name="fileName"/>'s extension, or null when unrecognised.</summary>
    public static string? MimeTypeFor(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var extension = Path.GetExtension(fileName);
        return extension.Length > 0 && ByExtension.TryGetValue(extension, out var mime) ? mime : null;
    }
}
