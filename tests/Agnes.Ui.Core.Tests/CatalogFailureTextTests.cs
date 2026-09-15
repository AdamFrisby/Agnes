using Agnes.Ui.Core.ViewModels;

namespace Agnes.Ui.Core.Tests;

/// <summary>A registry failure reads as one sentence naming the registry and the HTTP status, not the
/// client library's whole complaint.</summary>
public sealed class CatalogFailureTextTests
{
    [Theory]
    [InlineData("SkillsHub (skillshub.wtf): Response status code does not indicate success: 402 (Payment Required)",
        "SkillsHub (skillshub.wtf) couldn't be reached: HTTP 402.")]
    [InlineData("Official MCP registry: Response status code does not indicate success: 429 (Too Many Requests).",
        "Official MCP registry couldn't be reached: HTTP 429.")]
    [InlineData("GitHub (anthropics/skills): The operation was canceled.",
        "GitHub (anthropics/skills) couldn't be reached: The operation was canceled.")]
    [InlineData("GitHub: No such host is known: api.github.com",
        "GitHub couldn't be reached: No such host is known.")]
    [InlineData("just words", "just words.")]
    [InlineData("   ", "")]
    public void A_failure_is_shortened_to_who_and_why(string raw, string expected)
        => Assert.Equal(expected, CatalogFailureText.Shorten(raw));

    [Fact]
    public void Several_failures_become_one_line_and_none_becomes_empty()
    {
        Assert.Equal(string.Empty, CatalogFailureText.Sentence([]));
        Assert.Equal(
            "A couldn't be reached: HTTP 500. B couldn't be reached: HTTP 403.",
            CatalogFailureText.Sentence(["A: status 500 (Internal Server Error)", "B: status 403 (Forbidden)"]));
    }

    [Theory]
    [InlineData(0, "no saved prompts")]
    [InlineData(1, "one saved prompt")]
    [InlineData(3, "3 saved prompts")]
    public void Counts_read_as_words(int n, string expected)
        => Assert.Equal(expected, CatalogFailureText.Count(n, "saved prompt"));
}
