using Agnes.Protocol;

namespace Agnes.Host.Files;

/// <summary>
/// The file-browser operations over a session's working directory (git-and-files/03). Every method takes a
/// client-supplied, workspace-relative path and routes it through <see cref="WorkspacePaths.ResolveWithin"/>
/// — the single shared path-safety guard also used by attachment uploads and session-handoff — so a
/// <c>..</c> segment or an absolute path can never touch a file outside <paramref name="workspaceRoot"/>.
/// Pure functions over their inputs (a root + a relative path): no ambient state, so they're directly
/// testable against a temp directory.
/// </summary>
public static class WorkspaceBrowser
{
    // Extensions we surface as inline image previews (text + image only for the first version, per the spec).
    private static readonly IReadOnlyDictionary<string, string> ImageMimeByExtension =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif",
            [".webp"] = "image/webp",
            [".bmp"] = "image/bmp",
            [".svg"] = "image/svg+xml",
            [".ico"] = "image/x-icon",
        };

    /// <summary>
    /// Lists the entries of a directory (default: the workspace root), directories first then by name
    /// (case-insensitive, ordinal). Throws <see cref="UnauthorizedAccessException"/> if the path escapes the
    /// root and <see cref="DirectoryNotFoundException"/> if it isn't an existing directory.
    /// </summary>
    public static IReadOnlyList<FileEntry> List(string workspaceRoot, string relativePath)
    {
        var dir = Resolve(workspaceRoot, relativePath);
        if (!Directory.Exists(dir))
        {
            throw new DirectoryNotFoundException($"'{relativePath}' is not a directory in the workspace.");
        }

        var root = RootFullPath(workspaceRoot);
        var entries = new List<FileEntry>();
        foreach (var path in Directory.EnumerateFileSystemEntries(dir))
        {
            var info = new FileInfo(path);
            var isDirectory = info.Attributes.HasFlag(FileAttributes.Directory);
            entries.Add(new FileEntry(
                Path.GetFileName(path),
                ToRelative(root, path),
                isDirectory,
                isDirectory ? 0 : info.Length,
                isDirectory ? Directory.GetLastWriteTimeUtc(path) : info.LastWriteTimeUtc));
        }

        entries.Sort(static (a, b) =>
            a.IsDirectory != b.IsDirectory
                ? (a.IsDirectory ? -1 : 1)
                : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return entries;
    }

    /// <summary>
    /// Reads a file for preview. Text files come back as decoded text; recognised image types come back as
    /// bytes plus a mime type; anything else is reported as opaque <see cref="FileContentKind.Binary"/> with
    /// no body (text + image preview only, per the spec's open question).
    /// </summary>
    public static FileContent Read(string workspaceRoot, string relativePath)
    {
        var file = Resolve(workspaceRoot, relativePath);
        if (!File.Exists(file))
        {
            throw new FileNotFoundException($"'{relativePath}' is not a file in the workspace.");
        }

        var root = RootFullPath(workspaceRoot);
        var rel = ToRelative(root, file);
        var bytes = File.ReadAllBytes(file);

        var extension = Path.GetExtension(file);
        if (ImageMimeByExtension.TryGetValue(extension, out var mime))
        {
            return new FileContent(rel, FileContentKind.Image, Text: null, bytes, mime, bytes.LongLength);
        }

        if (LooksLikeText(bytes))
        {
            return new FileContent(rel, FileContentKind.Text, System.Text.Encoding.UTF8.GetString(bytes), Bytes: null, MimeType: null, bytes.LongLength);
        }

        return new FileContent(rel, FileContentKind.Binary, Text: null, Bytes: null, MimeType: null, bytes.LongLength);
    }

    /// <summary>Writes UTF-8 <paramref name="content"/> to a file (creating parent directories), for the
    /// "quick edit without an agent turn" case. Returns the workspace-relative path written.</summary>
    public static string Write(string workspaceRoot, string relativePath, string content)
    {
        var file = Resolve(workspaceRoot, relativePath);
        var parent = Path.GetDirectoryName(file);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        File.WriteAllText(file, content);
        return ToRelative(RootFullPath(workspaceRoot), file);
    }

    /// <summary>Creates a directory (and any missing parents). Returns its workspace-relative path.</summary>
    public static string CreateDirectory(string workspaceRoot, string relativePath)
    {
        var dir = Resolve(workspaceRoot, relativePath);
        Directory.CreateDirectory(dir);
        return ToRelative(RootFullPath(workspaceRoot), dir);
    }

    /// <summary>Renames/moves a file or directory. Both endpoints are validated to stay within the root.</summary>
    public static void Rename(string workspaceRoot, string fromRelativePath, string toRelativePath)
    {
        var from = Resolve(workspaceRoot, fromRelativePath);
        var to = Resolve(workspaceRoot, toRelativePath);

        var parent = Path.GetDirectoryName(to);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        if (Directory.Exists(from))
        {
            Directory.Move(from, to);
        }
        else if (File.Exists(from))
        {
            File.Move(from, to, overwrite: false);
        }
        else
        {
            throw new FileNotFoundException($"'{fromRelativePath}' does not exist in the workspace.");
        }
    }

    /// <summary>Deletes a file or directory (directories recursively).</summary>
    public static void Delete(string workspaceRoot, string relativePath)
    {
        var target = Resolve(workspaceRoot, relativePath);
        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }
        else if (File.Exists(target))
        {
            File.Delete(target);
        }
        else
        {
            throw new FileNotFoundException($"'{relativePath}' does not exist in the workspace.");
        }
    }

    /// <summary>Reads a file's raw bytes for download.</summary>
    public static byte[] Download(string workspaceRoot, string relativePath)
    {
        var file = Resolve(workspaceRoot, relativePath);
        if (!File.Exists(file))
        {
            throw new FileNotFoundException($"'{relativePath}' is not a file in the workspace.");
        }

        return File.ReadAllBytes(file);
    }

    // Resolves a workspace-relative path through the shared guard, treating an empty path as the root itself.
    // A path that would escape the workspace root is rejected here before any filesystem call touches disk.
    private static string Resolve(string workspaceRoot, string relativePath)
    {
        var candidate = string.IsNullOrEmpty(relativePath) ? "." : relativePath;
        return WorkspacePaths.ResolveWithin(workspaceRoot, candidate)
            ?? throw new UnauthorizedAccessException($"Path '{relativePath}' escapes the session workspace.");
    }

    private static string RootFullPath(string workspaceRoot)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));

    // Workspace-relative, POSIX-separated (matches how attachments are reported to the agent).
    private static string ToRelative(string root, string fullPath)
        => Path.GetRelativePath(root, fullPath).Replace(Path.DirectorySeparatorChar, '/');

    // A cheap "is this decodable text" heuristic: a NUL byte in the first chunk means binary. Good enough to
    // decide text-vs-image-vs-binary for preview; we are not trying to guess an exact charset.
    private static bool LooksLikeText(byte[] bytes)
    {
        var limit = Math.Min(bytes.Length, 8000);
        for (var i = 0; i < limit; i++)
        {
            if (bytes[i] == 0)
            {
                return false;
            }
        }

        return true;
    }
}
