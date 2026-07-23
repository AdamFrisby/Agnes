using Agnes.Abstractions;
using Agnes.Host.Hosting;

namespace Agnes.Host.Tests;

public class PromptLibraryTests
{
    private static string NewTempDir()
        => Path.Combine(Path.GetTempPath(), $"agnes-prompt-lib-{Guid.NewGuid():n}");

    [Fact]
    public void Save_assigns_an_id_when_blank_and_upserts_by_id()
    {
        var library = new PromptLibrary();

        var saved = library.Save(new LibraryPrompt(string.Empty, "Security review", "Review this diff for security issues."));
        Assert.False(string.IsNullOrWhiteSpace(saved.Id));

        // Re-saving the same id updates in place rather than duplicating.
        library.Save(saved with { Title = "Security review (updated)" });
        var all = library.List();
        Assert.Single(all);
        Assert.Equal("Security review (updated)", all[0].Title);
    }

    [Fact]
    public void Prompts_and_templates_round_trip_through_a_reload()
    {
        var dir = NewTempDir();
        try
        {
            var library = new PromptLibrary(dir);
            var review = library.Save(new LibraryPrompt(string.Empty, "Review", "Review these changes."));
            var tests = library.Save(new LibraryPrompt(string.Empty, "Tests", "Write unit tests."));
            library.SaveTemplate(new PromptTemplate("/review", review.Id, TemplateBehavior.Insert));
            library.SaveTemplate(new PromptTemplate("/tests", tests.Id, TemplateBehavior.InsertAndSend));

            // A brand-new instance over the same directory sees everything that was saved.
            var reloaded = new PromptLibrary(dir);
            Assert.Equal(2, reloaded.List().Count);
            Assert.Equal(2, reloaded.ListTemplates().Count);

            var (prompt, broken) = reloaded.Resolve("/review");
            Assert.False(broken);
            Assert.NotNull(prompt);
            Assert.Equal("Review these changes.", prompt!.MarkdownBody);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_blank_directory_keeps_the_library_in_memory_only()
    {
        // Null/blank directory => no persistence (used by tests): the store still works, but nothing is
        // written that a fresh instance could read back.
        var inMemory = new PromptLibrary(directory: null);
        var p = inMemory.Save(new LibraryPrompt(string.Empty, "P", "body"));
        inMemory.SaveTemplate(new PromptTemplate("/p", p.Id, TemplateBehavior.Insert));

        Assert.Single(inMemory.List());
        var (prompt, broken) = inMemory.Resolve("/p");
        Assert.False(broken);
        Assert.NotNull(prompt);

        var another = new PromptLibrary(directory: "   ");
        Assert.Empty(another.List());
        Assert.Empty(another.ListTemplates());
    }

    [Fact]
    public void Resolve_reports_the_right_behavior_for_insert_vs_insert_and_send()
    {
        var library = new PromptLibrary();
        var p = library.Save(new LibraryPrompt(string.Empty, "Review", "Review these changes."));
        library.SaveTemplate(new PromptTemplate("/insert", p.Id, TemplateBehavior.Insert));
        library.SaveTemplate(new PromptTemplate("/send", p.Id, TemplateBehavior.InsertAndSend));

        // Both resolve to the same prompt, unbroken...
        var (insertPrompt, insertBroken) = library.Resolve("/insert");
        var (sendPrompt, sendBroken) = library.Resolve("/send");
        Assert.False(insertBroken);
        Assert.False(sendBroken);
        Assert.Equal(p.Id, insertPrompt!.Id);
        Assert.Equal(p.Id, sendPrompt!.Id);

        // ...but each carries its own behavior signal.
        var templates = library.ListTemplates();
        Assert.Equal(TemplateBehavior.Insert, templates.Single(t => t.SlashToken == "insert").Behavior);
        Assert.Equal(TemplateBehavior.InsertAndSend, templates.Single(t => t.SlashToken == "send").Behavior);
    }

    [Fact]
    public void The_slash_token_resolves_with_or_without_a_leading_slash()
    {
        var library = new PromptLibrary();
        var p = library.Save(new LibraryPrompt(string.Empty, "Review", "body"));
        library.SaveTemplate(new PromptTemplate("/review", p.Id, TemplateBehavior.Insert));

        Assert.NotNull(library.Resolve("review").Prompt);
        Assert.NotNull(library.Resolve("/review").Prompt);
    }

    [Fact]
    public void An_unknown_token_resolves_to_nothing_but_is_not_flagged_broken()
    {
        var library = new PromptLibrary();
        var (prompt, broken) = library.Resolve("/nope");
        Assert.Null(prompt);
        Assert.False(broken);
    }

    [Fact]
    public void Deleting_a_referenced_prompt_leaves_its_template_flagged_broken()
    {
        var library = new PromptLibrary();
        var p = library.Save(new LibraryPrompt(string.Empty, "Review", "body"));
        library.SaveTemplate(new PromptTemplate("/review", p.Id, TemplateBehavior.Insert));

        // Before delete: resolves cleanly.
        Assert.False(library.Resolve("/review").Broken);

        library.Delete(p.Id);

        // After delete-then-invoke: the template still exists but resolves to a broken state (not empty).
        var (prompt, broken) = library.Resolve("/review");
        Assert.Null(prompt);
        Assert.True(broken);
        Assert.Single(library.ListTemplates());
    }

    [Fact]
    public void DeleteTemplate_reports_whether_it_removed_anything()
    {
        var library = new PromptLibrary();
        var p = library.Save(new LibraryPrompt(string.Empty, "Review", "body"));
        library.SaveTemplate(new PromptTemplate("/review", p.Id, TemplateBehavior.Insert));

        Assert.False(library.DeleteTemplate("/missing"));
        Assert.True(library.DeleteTemplate("/review"));
        Assert.Empty(library.ListTemplates());
    }
}
