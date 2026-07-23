using Agnes.Host.Files;
using Agnes.Protocol;

namespace Agnes.Host.Tests;

/// <summary>
/// The file-browser operations over a session's working directory (git-and-files/03): the happy-path ops plus
/// the load-bearing AC that every op routes its client-supplied path through the shared
/// <see cref="WorkspacePaths.ResolveWithin"/> guard, so a <c>..</c>-escaping path is rejected without touching
/// anything outside the workspace root. Real temp directories, no host or CLI needed.
/// </summary>
public sealed class WorkspaceBrowserTests : IDisposable
{
    private readonly string _root;

    public WorkspaceBrowserTests()
    {
        // A fresh, unique workspace under the temp dir (no absolute-path literal — PH2080).
        _root = Path.Combine(Path.GetTempPath(), "agnes-browser-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void List_returns_directories_first_then_files_by_name()
    {
        Directory.CreateDirectory(Path.Combine(_root, "zed"));
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "b.txt"), "b");
        File.WriteAllText(Path.Combine(_root, "a.txt"), "a");

        var entries = WorkspaceBrowser.List(_root, "");

        Assert.Collection(entries,
            e => AssertEntry(e, "alpha", isDirectory: true),
            e => AssertEntry(e, "zed", isDirectory: true),
            e => AssertEntry(e, "a.txt", isDirectory: false),
            e => AssertEntry(e, "b.txt", isDirectory: false));
    }

    [Fact]
    public void List_of_a_subdirectory_reports_posix_relative_paths()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "Foo.cs"), "class Foo;");

        var entries = WorkspaceBrowser.List(_root, "src");

        Assert.Equal("src/Foo.cs", Assert.Single(entries).RelativePath);
    }

    [Fact]
    public void Read_returns_text_for_a_text_file()
    {
        File.WriteAllText(Path.Combine(_root, "notes.md"), "hello world");

        var content = WorkspaceBrowser.Read(_root, "notes.md");

        Assert.Equal(FileContentKind.Text, content.Kind);
        Assert.Equal("hello world", content.Text);
        Assert.Null(content.Bytes);
    }

    [Fact]
    public void Read_returns_bytes_and_mime_for_an_image()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 1, 2 };
        File.WriteAllBytes(Path.Combine(_root, "shot.png"), png);

        var content = WorkspaceBrowser.Read(_root, "shot.png");

        Assert.Equal(FileContentKind.Image, content.Kind);
        Assert.Equal("image/png", content.MimeType);
        Assert.Equal(png, content.Bytes);
        Assert.Null(content.Text);
    }

    [Fact]
    public void Write_persists_content_creating_missing_parents()
    {
        WorkspaceBrowser.Write(_root, "docs/readme.txt", "quick edit");

        Assert.Equal("quick edit", File.ReadAllText(Path.Combine(_root, "docs", "readme.txt")));
    }

    [Fact]
    public void CreateDirectory_makes_the_folder()
    {
        WorkspaceBrowser.CreateDirectory(_root, "newdir");

        Assert.True(Directory.Exists(Path.Combine(_root, "newdir")));
    }

    [Fact]
    public void Rename_moves_a_file()
    {
        File.WriteAllText(Path.Combine(_root, "old.txt"), "x");

        WorkspaceBrowser.Rename(_root, "old.txt", "new.txt");

        Assert.False(File.Exists(Path.Combine(_root, "old.txt")));
        Assert.Equal("x", File.ReadAllText(Path.Combine(_root, "new.txt")));
    }

    [Fact]
    public void Delete_removes_a_file()
    {
        File.WriteAllText(Path.Combine(_root, "gone.txt"), "x");

        WorkspaceBrowser.Delete(_root, "gone.txt");

        Assert.False(File.Exists(Path.Combine(_root, "gone.txt")));
    }

    [Fact]
    public void Download_returns_the_raw_bytes()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        File.WriteAllBytes(Path.Combine(_root, "blob.bin"), bytes);

        Assert.Equal(bytes, WorkspaceBrowser.Download(_root, "blob.bin"));
    }

    // ---- path safety (the explicit AC): every op rejects a traversal without touching outside the root ----

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("../../etc/passwd")]
    [InlineData("sub/../../escape.txt")]
    public void Read_rejects_directory_traversal(string escaping)
        => Assert.Throws<UnauthorizedAccessException>(() => WorkspaceBrowser.Read(_root, escaping));

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("../../etc/passwd")]
    public void Download_rejects_directory_traversal(string escaping)
        => Assert.Throws<UnauthorizedAccessException>(() => WorkspaceBrowser.Download(_root, escaping));

    [Fact]
    public void List_rejects_directory_traversal()
        => Assert.Throws<UnauthorizedAccessException>(() => WorkspaceBrowser.List(_root, ".."));

    [Fact]
    public void CreateDirectory_rejects_directory_traversal()
        => Assert.Throws<UnauthorizedAccessException>(() => WorkspaceBrowser.CreateDirectory(_root, "../evil"));

    [Fact]
    public void Write_that_escapes_the_root_touches_nothing_outside_it()
    {
        // A sibling of the workspace, i.e. outside it. It must never be created by an escaping write.
        var outsideRelative = "../" + Path.GetFileName(_root) + "-evil.txt";
        var outsideAbsolute = Path.Combine(Directory.GetParent(_root)!.FullName, Path.GetFileName(_root) + "-evil.txt");

        Assert.Throws<UnauthorizedAccessException>(() => WorkspaceBrowser.Write(_root, outsideRelative, "pwned"));
        Assert.False(File.Exists(outsideAbsolute));
    }

    [Fact]
    public void Delete_that_escapes_the_root_leaves_the_outside_file_intact()
    {
        // A real file that lives outside the workspace root; an escaping delete must not remove it.
        var outsideAbsolute = Path.Combine(Directory.GetParent(_root)!.FullName, Path.GetFileName(_root) + "-keep.txt");
        File.WriteAllText(outsideAbsolute, "keep me");
        try
        {
            var escaping = "../" + Path.GetFileName(_root) + "-keep.txt";
            Assert.Throws<UnauthorizedAccessException>(() => WorkspaceBrowser.Delete(_root, escaping));
            Assert.True(File.Exists(outsideAbsolute));
            Assert.Equal("keep me", File.ReadAllText(outsideAbsolute));
        }
        finally
        {
            File.Delete(outsideAbsolute);
        }
    }

    [Fact]
    public void Rename_that_escapes_the_root_is_rejected_and_moves_nothing()
    {
        File.WriteAllText(Path.Combine(_root, "src.txt"), "data");
        var outsideAbsolute = Path.Combine(Directory.GetParent(_root)!.FullName, Path.GetFileName(_root) + "-moved.txt");

        var escaping = "../" + Path.GetFileName(_root) + "-moved.txt";
        Assert.Throws<UnauthorizedAccessException>(() => WorkspaceBrowser.Rename(_root, "src.txt", escaping));

        Assert.True(File.Exists(Path.Combine(_root, "src.txt")));
        Assert.False(File.Exists(outsideAbsolute));
    }

    private static void AssertEntry(FileEntry entry, string name, bool isDirectory)
    {
        Assert.Equal(name, entry.Name);
        Assert.Equal(isDirectory, entry.IsDirectory);
    }
}
