using Agnes.Host.Projects;

namespace Agnes.Host.Tests;

public class ReviewCommentStoreTests
{
    private static ReviewCommentStore NewStore(out string path)
    {
        path = Path.Combine(Path.GetTempPath(), $"review-comments-{Guid.NewGuid():n}.json");
        return new ReviewCommentStore(path);
    }

    [Fact]
    public void Add_assigns_an_id_and_creation_time()
    {
        var store = NewStore(out var path);
        try
        {
            var comment = store.Add("proj-1", "src/config.ts", 7, "abc123", "needs a null check");

            Assert.False(string.IsNullOrWhiteSpace(comment.Id));
            Assert.Equal("proj-1", comment.ProjectId);
            Assert.Equal("src/config.ts", comment.FilePath);
            Assert.Equal(7, comment.LineNumber);
            Assert.Equal("abc123", comment.LineHash);
            Assert.Equal("needs a null check", comment.Text);
            Assert.NotEqual(default, comment.CreatedAt);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ListForProject_returns_only_that_projects_comments()
    {
        var store = NewStore(out var path);
        try
        {
            store.Add("proj-1", "a.cs", 1, "h1", "one");
            store.Add("proj-1", "b.cs", 2, "h2", "two");
            store.Add("proj-2", "c.cs", 3, "h3", "three");

            var forOne = store.ListForProject("proj-1");
            Assert.Equal(2, forOne.Count);
            Assert.All(forOne, c => Assert.Equal("proj-1", c.ProjectId));

            Assert.Single(store.ListForProject("proj-2"));
            Assert.Empty(store.ListForProject("proj-unknown"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Remove_deletes_by_id_and_reports_whether_it_removed_anything()
    {
        var store = NewStore(out var path);
        try
        {
            var comment = store.Add("proj-1", "a.cs", 1, "h1", "one");

            Assert.False(store.Remove("no-such-id"));
            Assert.True(store.Remove(comment.Id));
            Assert.Empty(store.ListForProject("proj-1"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Comments_persist_across_a_reload()
    {
        var store = NewStore(out var path);
        try
        {
            var comment = store.Add("proj-1", "src/app.cs", 42, "hash42", "extract this method");

            var reloaded = new ReviewCommentStore(path);
            var list = reloaded.ListForProject("proj-1");

            Assert.Single(list);
            Assert.Equal(comment.Id, list[0].Id);
            Assert.Equal(42, list[0].LineNumber);
            Assert.Equal("extract this method", list[0].Text);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_missing_file_loads_as_empty()
    {
        var path = Path.Combine(Path.GetTempPath(), $"review-comments-missing-{Guid.NewGuid():n}.json");
        var store = new ReviewCommentStore(path);
        Assert.Empty(store.ListForProject("proj-1"));
    }
}
