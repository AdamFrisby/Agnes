using Agnes.Host.Sessions;

namespace Agnes.Host.Tests;

public class ClaudeTitleTests
{
    [Theory]
    [InlineData("/work", "-work")]
    [InlineData("/home/adam/Projects/Agnes1", "-home-adam-Projects-Agnes1")]
    [InlineData("/home/adam/Projects/SWE-Context", "-home-adam-Projects-SWE-Context")]
    [InlineData("/home/user/.config", "-home-user--config")]
    public void EncodeCwd_replaces_non_alphanumerics_with_dashes(string cwd, string expected)
        => Assert.Equal(expected, ClaudeTitle.EncodeCwd(cwd));

    [Fact]
    public void TranscriptRelativePath_names_the_file_by_session_id()
        => Assert.Equal(".claude/projects/-work/abc-123.jsonl", ClaudeTitle.TranscriptRelativePath("/work", "abc-123"));

    [Fact]
    public void ParseLatestTitle_returns_the_most_recent_ai_title()
    {
        var transcript = string.Join('\n',
            """{"type":"mode","mode":"normal","sessionId":"s"}""",
            """{"type":"ai-title","aiTitle":"initial-guess","sessionId":"s"}""",
            """{"type":"user","message":"..."}""",
            """{"type":"ai-title","aiTitle":"agnes-structured-questions","sessionId":"s"}""");

        Assert.Equal("agnes-structured-questions", ClaudeTitle.ParseLatestTitle(transcript));
    }

    [Fact]
    public void ParseLatestTitle_is_null_when_there_is_no_title_yet()
        => Assert.Null(ClaudeTitle.ParseLatestTitle("""{"type":"mode","mode":"normal"}"""));

    [Fact]
    public void ParseLatestTitle_skips_a_partially_written_trailing_line()
    {
        var transcript = string.Join('\n',
            """{"type":"ai-title","aiTitle":"good-title","sessionId":"s"}""",
            """{"type":"ai-title","aiTitle":"tru"""); // truncated final append — must not blow up
        Assert.Equal("good-title", ClaudeTitle.ParseLatestTitle(transcript));
    }
}
