using Agnes.Ui.Core.Markdown;

namespace Agnes.Ui.Core.Tests;

public sealed class MarkdownFenceTests
{
    [Theory]
    [InlineData("```markdown\n# Title\n```", "markdown", "# Title\n")]
    [InlineData("~~~MD\n- one\n- two\n~~~~", "MD", "- one\n- two\n")]
    [InlineData("  ```` md  \n**bold**\n`````\n", "md", "**bold**\n")]
    public void Explicit_markdown_fences_are_recognised(string input, string language, string body)
    {
        var match = MarkdownFence.Pattern.Match(input);

        Assert.True(match.Success);
        Assert.Equal(language, MarkdownFence.Language(match));
        Assert.Equal(body, MarkdownFence.Body(match));
    }

    [Theory]
    [InlineData("```\n# Title\n```")]
    [InlineData("```csharp\n# not markdown\n```")]
    [InlineData("```markdown extra\n# Title\n```")]
    [InlineData("`markdown`")]
    public void Other_code_and_inline_code_are_not_claimed(string input)
        => Assert.DoesNotMatch(MarkdownFence.Pattern, input);

    [Fact]
    public void Several_blocks_are_matched_independently_and_in_order()
    {
        const string input = "Before\n\n```md\n# One\n```\n\nBetween\n\n~~~markdown\n## Two\n~~~\n\nAfter";

        var matches = MarkdownFence.Pattern.Matches(input);

        Assert.Equal(2, matches.Count);
        Assert.Equal("# One\n", MarkdownFence.Body(matches[0]));
        Assert.Equal("## Two\n", MarkdownFence.Body(matches[1]));
    }

    [Fact]
    public void An_unfinished_streaming_fence_owns_the_text_to_the_current_end()
    {
        const string input = "```markdown\n# Still streaming";

        var match = MarkdownFence.Pattern.Match(input);

        Assert.True(match.Success);
        Assert.Equal("# Still streaming", MarkdownFence.Body(match));
    }

    [Fact]
    public void A_shorter_closing_run_does_not_finish_the_block()
    {
        const string input = "````markdown\n# Title\n```\ncontinued\n````";

        var match = MarkdownFence.Pattern.Match(input);

        Assert.True(match.Success);
        Assert.Equal("# Title\n```\ncontinued\n", MarkdownFence.Body(match));
    }
}
