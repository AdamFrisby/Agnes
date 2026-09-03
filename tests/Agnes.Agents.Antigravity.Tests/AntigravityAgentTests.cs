using Agnes.Abstractions;
using Agnes.Agents.Antigravity;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Agents.Antigravity.Tests;

public sealed class AntigravityAgentTests
{
    [Fact]
    public void The_launch_line_never_contains_a_bare_print_flag()
    {
        // agy 1.1.24's --print TAKES A VALUE, so a bare --print swallows whatever follows it as the
        // prompt: "--print took \"--dangerously-skip-permissions\" as its prompt, so the intended prompt
        // was left as an argument and ignored". CodeyBox's runner still builds
        // [agy, --print, --dangerously-skip-permissions] and is broken on this version.
        var spec = AntigravityAgent.CreateLaunchSpec();

        Assert.DoesNotContain("--print", spec.Arguments);
        Assert.DoesNotContain("-p", spec.Arguments);
        Assert.Contains("--input-format", spec.Arguments);
        Assert.Contains("stream-json", spec.Arguments);
    }

    [Fact]
    public void The_response_timeout_is_raised_above_the_five_minute_default()
    {
        // agy aborts the whole session with "timed out waiting for response" at its 5m default, which a
        // real coding turn exceeds.
        var spec = AntigravityAgent.CreateLaunchSpec(new AntigravityOptions { PrintTimeout = TimeSpan.FromMinutes(30) });

        var index = spec.Arguments.ToList().IndexOf("--print-timeout");
        Assert.True(index >= 0);
        Assert.Equal("1800s", spec.Arguments[index + 1]);
    }

    [Fact]
    public void A_zero_timeout_omits_the_flag_rather_than_sending_zero()
    {
        // "0s" would mean "give up immediately", the opposite of "no opinion".
        var spec = AntigravityAgent.CreateLaunchSpec(new AntigravityOptions { PrintTimeout = TimeSpan.Zero });

        Assert.DoesNotContain("--print-timeout", spec.Arguments);
    }

    [Fact]
    public void Resume_pins_the_conversation_rather_than_continuing_the_last_one()
    {
        // --continue resumes whatever ran last in this directory, which is wrong when a client holds two
        // sessions against one repository.
        Assert.Equal(["--conversation", "abc-123"], AntigravityAgent.BuildResumeArguments("abc-123"));
        Assert.Equal(["--model", "gemini-3.8-flash-low"], AntigravityAgent.BuildModelArguments("gemini-3.8-flash-low"));
    }

    [Fact]
    public void Models_are_parsed_from_the_real_agy_models_output()
    {
        // Captured verbatim, header line included.
        const string stdout = """
            Fetching available models...
            gemini-3.8-flash-high	Gemini 3.8 Flash (High)
            gemini-3.8-flash-low	Gemini 3.8 Flash (Low)
            claude-opus-4-6-thinking	Claude Opus 4.6 (Thinking)
            """;

        var models = AntigravityAgent.ParseModels(stdout);

        Assert.Equal(3, models.Count);
        Assert.Equal("gemini-3.8-flash-high", models[0].Id);
        Assert.Equal("Gemini 3.8 Flash (High)", models[0].DisplayName);
        Assert.DoesNotContain(models, m => m.Id.Contains("Fetching"));
    }

    [Fact]
    public void Model_parsing_survives_no_output_at_all()
    {
        // An unauthenticated or missing CLI is a normal state, not an error to surface.
        Assert.Empty(AntigravityAgent.ParseModels(null));
        Assert.Empty(AntigravityAgent.ParseModels(""));
        Assert.Empty(AntigravityAgent.ParseModels("Fetching available models...\n"));
    }

    [Fact]
    public async Task An_attended_session_is_refused_and_says_why()
    {
        var adapter = AntigravityAgent.Create(NullLoggerFactory.Instance);

        var refusal = await Assert.ThrowsAsync<NotSupportedException>(
            () => adapter.StartSessionAsync(new AgentSessionOptions
            {
                WorkingDirectory = "/tmp",
                SkipPermissions = false,
            }));

        // The message must name the actual hazard: not "unsupported", but "it will look like it worked".
        Assert.Contains("scratch", refusal.Message);
        Assert.Contains("autonomous", refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_console_is_offered_because_bare_agy_is_its_interactive_TUI()
        => Assert.NotNull(AntigravityAgent.CreateLaunchSpec().ConsoleArguments);
}

public sealed class AntigravityWorkspaceTests
{
    [Fact]
    public void The_working_directory_is_added_to_the_workspace()
    {
        // The single most consequential flag on this adapter. Without it agy writes to
        // ~/.gemini/antigravity-cli/scratch/ and reports success — with --dangerously-skip-permissions
        // set — so an agent looks like it worked and the repository is untouched. Proven in a clean
        // Incus guest: same prompt, scratch without the flag, working directory with it.
        Assert.Equal(["--add-dir", "/work/repo"], AntigravityAgent.BuildWorkingDirectoryArguments("/work/repo"));
    }

    [Fact]
    public void The_launch_spec_actually_carries_the_hook()
    {
        // Guards the wiring, not just the builder: a correct function nobody calls is the same bug.
        var spec = AntigravityAgent.CreateLaunchSpec();

        Assert.NotNull(spec.WorkingDirectoryArguments);
        Assert.Equal(["--add-dir", "/guest/work"], spec.WorkingDirectoryArguments!("/guest/work"));
    }
}
