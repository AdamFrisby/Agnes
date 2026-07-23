using Agnes.Abstractions;
using Agnes.Host.Hosting;

namespace Agnes.Host.Tests;

public class SystemPromptAdditionTests
{
    [Fact]
    public void Enabled_additions_are_collected_in_order_and_disabled_ones_are_excluded()
    {
        var library = new PromptLibrary();
        library.Save(new LibraryPrompt(string.Empty, "A house style", "Always write tests.") { IsSystemPromptAddition = true });
        library.Save(new LibraryPrompt(string.Empty, "B tone", "Be concise.") { IsSystemPromptAddition = true });
        library.Save(new LibraryPrompt(string.Empty, "C snippet", "Not a standing instruction.") { IsSystemPromptAddition = false });

        var additions = library.ListSystemPromptAdditions();
        Assert.Equal(2, additions.Count);
        Assert.Equal(["A house style", "B tone"], additions.Select(a => a.Title));

        var assembled = library.AssembleSystemPromptAdditions();
        Assert.Equal("Always write tests.\n\nBe concise.", assembled);
        // The disabled prompt's body never appears in the assembled system prompt.
        Assert.DoesNotContain("Not a standing instruction.", assembled);
    }

    [Fact]
    public void No_enabled_additions_assemble_to_null()
    {
        var library = new PromptLibrary();
        library.Save(new LibraryPrompt(string.Empty, "plain", "reusable body"));
        Assert.Empty(library.ListSystemPromptAdditions());
        Assert.Null(library.AssembleSystemPromptAdditions());
    }

    [Fact]
    public void The_system_prompt_flag_round_trips_through_save_and_list()
    {
        var library = new PromptLibrary();
        var saved = library.Save(new LibraryPrompt(string.Empty, "rule", "body") { IsSystemPromptAddition = true });
        Assert.True(saved.IsSystemPromptAddition);
        Assert.True(library.List().Single().IsSystemPromptAddition);
    }
}
