using Agnes.Host.Sessions;

namespace Agnes.Host.Tests;

public class DirectoryCopierTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agnes-copytest-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Copies_the_full_tree_including_nested_files()
    {
        var src = Path.Combine(_root, "src");
        Directory.CreateDirectory(Path.Combine(src, ".git"));
        Directory.CreateDirectory(Path.Combine(src, "sub", "deep"));
        await File.WriteAllTextAsync(Path.Combine(src, "root.txt"), "root");
        await File.WriteAllTextAsync(Path.Combine(src, ".git", "HEAD"), "ref: refs/heads/main");
        await File.WriteAllTextAsync(Path.Combine(src, "sub", "deep", "a.cs"), "class A {}");

        var dst = Path.Combine(_root, "dst");
        await DirectoryCopier.CopyAsync(src, dst);

        Assert.Equal("root", await File.ReadAllTextAsync(Path.Combine(dst, "root.txt")));
        Assert.Equal("ref: refs/heads/main", await File.ReadAllTextAsync(Path.Combine(dst, ".git", "HEAD")));
        Assert.Equal("class A {}", await File.ReadAllTextAsync(Path.Combine(dst, "sub", "deep", "a.cs")));
    }

    [Fact]
    public async Task Refuses_to_overwrite_an_existing_target()
    {
        var src = Path.Combine(_root, "src");
        Directory.CreateDirectory(src);
        await File.WriteAllTextAsync(Path.Combine(src, "f.txt"), "x");
        var dst = Path.Combine(_root, "dst");
        Directory.CreateDirectory(dst);

        await Assert.ThrowsAsync<IOException>(() => DirectoryCopier.CopyAsync(src, dst));
    }

    [Fact]
    public async Task Throws_when_the_source_is_missing()
        => await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => DirectoryCopier.CopyAsync(Path.Combine(_root, "nope"), Path.Combine(_root, "dst")));
}
