using Agnes.Host.Files;
using Agnes.Host.Sessions.Handoff;

namespace Agnes.Host.Tests;

/// <summary>The explicit workspace-transfer step of session-handoff (connectivity/03): streaming a project's
/// files over an <see cref="IHandoffChannel"/> byte pipe, the conflict policy when the target has files, and
/// the shared path-safety guard refusing an unsafe source root (AC3/AC4).</summary>
public class WorkspaceTransferTests : IDisposable
{
    private readonly List<string> _temp = [];

    private string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "agnes-wt-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        _temp.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var d in _temp)
        {
            try { Directory.Delete(d, recursive: true); } catch { /* best effort */ }
        }
    }

    private static void Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static async Task<MemoryStream> PackAsync(string source)
    {
        var stream = new MemoryStream();
        await WorkspaceTransfer.SendAsync(source, stream);
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public async Task Round_trips_a_nested_workspace_into_an_empty_target()
    {
        var source = NewDir();
        Write(source, "readme.md", "top-level");
        Write(source, Path.Combine("src", "app.cs"), "code here");
        Write(source, Path.Combine("src", "deep", "note.txt"), "nested");

        var target = Path.Combine(Path.GetTempPath(), "agnes-wt-" + Guid.NewGuid().ToString("n"));
        _temp.Add(target);

        using var packed = await PackAsync(source);
        var written = await WorkspaceTransfer.ReceiveAsync(packed, target, WorkspaceConflictPolicy.RequireEmpty);

        Assert.Equal(Path.GetFullPath(target), written);
        Assert.Equal("top-level", await File.ReadAllTextAsync(Path.Combine(target, "readme.md")));
        Assert.Equal("code here", await File.ReadAllTextAsync(Path.Combine(target, "src", "app.cs")));
        Assert.Equal("nested", await File.ReadAllTextAsync(Path.Combine(target, "src", "deep", "note.txt")));
    }

    [Fact]
    public async Task RequireEmpty_refuses_a_non_empty_target()
    {
        var source = NewDir();
        Write(source, "a.txt", "new");
        var target = NewDir();
        Write(target, "existing.txt", "keep me");

        using var packed = await PackAsync(source);
        await Assert.ThrowsAsync<IOException>(
            () => WorkspaceTransfer.ReceiveAsync(packed, target, WorkspaceConflictPolicy.RequireEmpty));

        // Never touched the occupied destination.
        Assert.Equal("keep me", await File.ReadAllTextAsync(Path.Combine(target, "existing.txt")));
        Assert.False(File.Exists(Path.Combine(target, "a.txt")));
    }

    [Fact]
    public async Task Replace_clears_the_target_then_writes_the_incoming_workspace()
    {
        var source = NewDir();
        Write(source, "a.txt", "new");
        var target = NewDir();
        Write(target, "stale.txt", "old");

        using var packed = await PackAsync(source);
        var written = await WorkspaceTransfer.ReceiveAsync(packed, target, WorkspaceConflictPolicy.Replace);

        Assert.Equal(Path.GetFullPath(target), written);
        Assert.False(File.Exists(Path.Combine(target, "stale.txt")));
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(target, "a.txt")));
    }

    [Fact]
    public async Task SiblingCopy_leaves_the_occupied_target_and_writes_to_a_fresh_sibling()
    {
        var source = NewDir();
        Write(source, "a.txt", "new");
        var target = NewDir();
        Write(target, "existing.txt", "keep me");

        using var packed = await PackAsync(source);
        var written = await WorkspaceTransfer.ReceiveAsync(packed, target, WorkspaceConflictPolicy.SiblingCopy);
        _temp.Add(written);

        Assert.NotEqual(Path.GetFullPath(target), written);
        Assert.Equal("keep me", await File.ReadAllTextAsync(Path.Combine(target, "existing.txt"))); // untouched
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(written, "a.txt")));           // sibling has the copy
    }

    [Fact]
    public async Task Refuses_the_home_directory_as_a_source_before_streaming_anything()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.False(string.IsNullOrEmpty(home));

        using var stream = new MemoryStream();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => WorkspaceTransfer.SendAsync(home, stream));
        Assert.Equal(0, stream.Length); // nothing was streamed
    }

    [Fact]
    public async Task Refuses_a_source_that_escapes_to_home_via_dotdot()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // A project-looking path that normalizes back up to the home directory.
        var escaping = Path.Combine(home, "some-project", "..", "..", Path.GetFileName(home));

        using var stream = new MemoryStream();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => WorkspaceTransfer.SendAsync(escaping, stream));
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public void IsSafeWorkspaceRoot_rejects_root_and_home_but_accepts_a_project_folder()
    {
        Assert.False(WorkspacePaths.IsSafeWorkspaceRoot(Path.GetPathRoot(Path.GetTempPath())));
        Assert.False(WorkspacePaths.IsSafeWorkspaceRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        Assert.False(WorkspacePaths.IsSafeWorkspaceRoot(""));
        Assert.True(WorkspacePaths.IsSafeWorkspaceRoot(NewDir()));
    }
}
