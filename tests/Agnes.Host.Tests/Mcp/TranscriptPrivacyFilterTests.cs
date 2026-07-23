using Agnes.Abstractions;
using Agnes.Host.Mcp;

namespace Agnes.Host.Tests.Mcp;

public sealed class TranscriptPrivacyFilterTests
{
    // A session whose tool call embeds a file path (in the Title) and file contents (in a diff block), plus a
    // permission request that names a path — the raw material the conservative default must exclude.
    private static IReadOnlyList<SessionEvent> SampleEvents() =>
    [
        new MessageChunkEvent(MessageRole.User, new TextContent("please refactor the auth module")),
        new MessageChunkEvent(MessageRole.Assistant, new TextContent("Sure, editing the file now.")),
        new ToolCallEvent(
            "tc-1",
            "Edit /srv/secret/app/Auth.cs",
            ToolKind.Edit,
            ToolCallStatus.Completed,
            [new DiffContent("/srv/secret/app/Auth.cs", "old password = hunter2", "new password = s3cr3t")]),
        new PermissionRequestedEvent("req-1", "tc-2", "Run rm -rf /srv/secret", []),
        new TurnEndedEvent(StopReason.EndTurn),
    ];

    [Fact]
    public void Default_omits_raw_tool_args_and_file_paths()
    {
        var filter = new TranscriptPrivacyFilter();

        var transcript = filter.Build(SampleEvents(), forwardRawContext: false);
        var text = string.Join('\n', transcript.Lines);

        Assert.False(transcript.IncludedRawContext);
        // File path, diff/content, and the permission title's path must all be absent.
        Assert.DoesNotContain("/srv/secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Auth.cs", text, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cr3t", text, StringComparison.Ordinal);
        Assert.DoesNotContain("rm -rf", text, StringComparison.Ordinal);

        // But the safe, structural summary is still present.
        Assert.Contains("used a Edit tool", text, StringComparison.Ordinal);
        Assert.Contains("requested a permission", text, StringComparison.Ordinal);
        // The user's and agent's own prose are never sensitive, so they're kept.
        Assert.Contains("refactor the auth module", text, StringComparison.Ordinal);
        Assert.Contains("editing the file now", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Opt_in_includes_raw_tool_args_and_file_paths()
    {
        var filter = new TranscriptPrivacyFilter();

        var transcript = filter.Build(SampleEvents(), forwardRawContext: true);
        var text = string.Join('\n', transcript.Lines);

        Assert.True(transcript.IncludedRawContext);
        Assert.Contains("/srv/secret/app/Auth.cs", text, StringComparison.Ordinal);
        Assert.Contains("s3cr3t", text, StringComparison.Ordinal);
        Assert.Contains("rm -rf /srv/secret", text, StringComparison.Ordinal);
    }
}
