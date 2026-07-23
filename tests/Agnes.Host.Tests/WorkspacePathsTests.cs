using Agnes.Host.Files;

namespace Agnes.Host.Tests;

/// <summary>The shared path-safety guard: workspace-relative resolution that rejects every traversal — the
/// AC for attachments/file-browser/handoff (git-and-files/03).</summary>
public class WorkspacePathsTests
{
    // Built from the temp dir (a real absolute root) rather than a literal, so it's platform-correct.
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "agnes-ws", "proj");

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("sub/dir/file.md")]
    [InlineData(".agnes/attachments/img.png")]
    [InlineData("./a/../b/ok.txt")] // normalizes to b/ok.txt — still inside
    public void Accepts_paths_inside_the_workspace(string candidate)
    {
        var resolved = WorkspacePaths.ResolveWithin(Root, candidate);
        Assert.NotNull(resolved);
        Assert.StartsWith(Path.GetFullPath(Root), resolved);
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("../../etc/passwd")]
    [InlineData("sub/../../escape.txt")]
    [InlineData("a/b/c/../../../../outside.txt")]
    public void Rejects_directory_traversal(string candidate)
        => Assert.Null(WorkspacePaths.ResolveWithin(Root, candidate));

    [Fact]
    public void Rejects_absolute_paths_outside_the_root()
    {
        // The temp dir is an ancestor of Root (Root = temp/agnes-ws/proj), so it's outside.
        Assert.Null(WorkspacePaths.ResolveWithin(Root, Path.GetTempPath()));
    }

    [Fact]
    public void Rejects_a_sibling_that_shares_the_root_prefix()
    {
        // ".../agnes-ws/proj-evil" starts with ".../agnes-ws/proj" as a string but is NOT under it.
        Assert.Null(WorkspacePaths.ResolveWithin(Root, Path.Combine("..", "proj-evil", "x")));
    }

    [Fact]
    public void The_root_itself_resolves_to_the_root()
        => Assert.Equal(Path.GetFullPath(Root), WorkspacePaths.ResolveWithin(Root, "."));

    [Theory]
    [InlineData("", "x")]
    [InlineData("anyroot", "")]
    public void Rejects_empty_inputs(string root, string candidate)
        => Assert.Null(WorkspacePaths.ResolveWithin(root, candidate));
}
