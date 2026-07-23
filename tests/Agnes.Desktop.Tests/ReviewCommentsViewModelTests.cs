using Agnes.Abstractions;
using Agnes.Ui.Core;
using Agnes.Ui.Core.Diff;
using Agnes.Ui.Core.ViewModels;
using CommunityToolkit.Mvvm.Input;

namespace Agnes.Desktop.Tests;

public class ReviewCommentsViewModelTests
{
    private static ReviewCommentsViewModel New(FakeHost host, string? projectId = "proj-1")
        => new(host, "s1", projectId, ImmediateDispatcher.Instance);

    [Fact]
    public async Task Comments_are_grouped_by_file()
    {
        var vm = New(new FakeHost());

        await vm.AddAsync("a.cs", 1, "one", "first");
        await vm.AddAsync("a.cs", 5, "five", "second");
        await vm.AddAsync("b.cs", 2, "two", "third");

        Assert.Equal(2, vm.Files.Count);
        var groupA = Assert.Single(vm.Files, g => g.FilePath == "a.cs");
        Assert.Equal(2, groupA.Comments.Count);
        Assert.Single(vm.Files, g => g.FilePath == "b.cs");
        Assert.True(vm.HasComments);
    }

    [Fact]
    public async Task A_comment_on_an_unchanged_line_is_not_stale_but_becomes_stale_when_the_line_changes()
    {
        var vm = New(new FakeHost());

        // Anchor a comment to new-side line 2 ("var x = 1;") of the current diff.
        var before = UnifiedDiff.Format("a.cs", string.Empty, "a\nvar x = 1;\nc\n");
        vm.UpdateDiffs([("a.cs", before)]);
        await vm.AddAsync("a.cs", 2, "var x = 1;", "needs a guard");

        var row = vm.Files[0].Comments[0];
        Assert.False(row.IsStale); // line at position 2 still hashes to what it did at comment time

        // The line at position 2 now holds different content → the anchor no longer matches.
        var after = UnifiedDiff.Format("a.cs", string.Empty, "a\nvar y = 2;\nc\n");
        vm.UpdateDiffs([("a.cs", after)]);

        Assert.True(row.IsStale);
    }

    [Fact]
    public async Task Loading_reflects_comments_previously_added_to_the_project()
    {
        var host = new FakeHost();
        var seed = New(host);
        await seed.AddAsync("a.cs", 3, "line", "left in an earlier session");

        // A fresh VM (as a new session would create) still sees the project's comment.
        var fresh = New(host);
        await fresh.LoadAsync();

        var group = Assert.Single(fresh.Files);
        Assert.Equal("a.cs", group.FilePath);
        Assert.Equal("left in an earlier session", group.Comments[0].Text);
    }

    [Fact]
    public async Task Sending_a_comment_prompts_the_session_with_a_located_resource_link()
    {
        var host = new FakeHost();
        var vm = New(host);
        await vm.AddAsync("src/config.ts", 7, "retries", "make this configurable");
        var row = vm.Files[0].Comments[0];

        await ((IAsyncRelayCommand)vm.SendCommand).ExecuteAsync(row);

        Assert.NotNull(host.LastContent);
        var link = Assert.Single(host.LastContent!.OfType<ResourceLinkContent>());
        Assert.Equal("src/config.ts#L7", link.Uri);
        Assert.Contains(host.LastContent!.OfType<TextContent>(), t => t.Text == "make this configurable");
    }

    [Fact]
    public async Task Removing_a_comment_drops_its_group_when_empty()
    {
        var vm = New(new FakeHost());
        await vm.AddAsync("a.cs", 1, "one", "only comment");
        var row = vm.Files[0].Comments[0];

        await ((IAsyncRelayCommand)vm.RemoveCommand).ExecuteAsync(row);

        Assert.Empty(vm.Files);
        Assert.False(vm.HasComments);
    }

    [Fact]
    public void HashLine_is_stable_and_ignores_surrounding_whitespace()
    {
        Assert.Equal(ReviewCommentsViewModel.HashLine("  var x = 1; "), ReviewCommentsViewModel.HashLine("var x = 1;"));
        Assert.NotEqual(ReviewCommentsViewModel.HashLine("var x = 1;"), ReviewCommentsViewModel.HashLine("var x = 2;"));
    }
}
