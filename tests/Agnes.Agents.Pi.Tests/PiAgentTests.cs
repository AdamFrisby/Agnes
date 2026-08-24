using Agnes.Abstractions;
using Agnes.Agents.Pi;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Agents.Pi.Tests;

/// <summary>The Pi plugin's launch surface: what it puts on the command line, what it refuses, and how it
/// reads Pi's model table.</summary>
public sealed class PiAgentTests
{
    [Fact]
    public void Launches_pi_in_rpc_mode()
    {
        var spec = PiAgent.CreateLaunchSpec();

        Assert.Equal("pi", spec.Command);
        Assert.Equal(["--mode", "rpc"], spec.Arguments);
        Assert.Equal("pi", spec.Descriptor.Id);
    }

    [Fact]
    public void Resumes_with_session_id_not_resume()
    {
        // --resume opens Pi's interactive session picker, which would wedge a headless launch; --session-id
        // is "use exactly this session, creating it if it doesn't exist".
        Assert.Equal(["--session-id", "01a034ae-8f56"], PiAgent.BuildResumeArguments("01a034ae-8f56"));
        Assert.Equal(["--session-id", "01a034ae-8f56"], PiAgent.CreateLaunchSpec().ResumeArguments("01a034ae-8f56"));
    }

    [Fact]
    public void Selects_a_model_by_provider_qualified_id()
        => Assert.Equal(["--model", "anthropic/claude-sonnet-5"], PiAgent.BuildModelArguments("anthropic/claude-sonnet-5"));

    [Fact]
    public void Declares_no_mcp_config_flag_because_pi_ships_no_mcp_client()
        => Assert.Null(PiAgent.CreateLaunchSpec().McpConfigFlag);

    // ---- the permission stance ----

    [Fact]
    public async Task An_attended_session_is_refused_rather_than_run_unguarded()
    {
        var adapter = PiAgent.Create(NullLoggerFactory.Instance);

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => adapter.StartSessionAsync(
            new AgentSessionOptions { WorkingDirectory = Path.GetTempPath() }));

        // Pi cannot ask before a tool call. Honouring "ask me" literally would run the session unguarded
        // while the UI showed the guarded state — so it fails closed, and says what to do instead.
        Assert.Contains("no per-tool permission system", ex.Message, StringComparison.Ordinal);
        Assert.Contains("autonomous mode", ex.Message, StringComparison.Ordinal);
        Assert.Contains("sandbox", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_autonomous_session_is_allowed_through_to_the_launch()
    {
        var adapter = PiAgent.Create(
            NullLoggerFactory.Instance,
            new PiOptions { Command = "agnes-no-such-binary-for-tests" });

        // Past the permission gate, so the only failure left is the missing binary — proof the gate let it by.
        await Assert.ThrowsAnyAsync<Exception>(() => adapter.StartSessionAsync(new AgentSessionOptions
        {
            WorkingDirectory = Path.GetTempPath(),
            SkipPermissions = true,
        }));
    }

    // ---- model catalogue ----

    [Fact]
    public async Task Lists_models_from_pis_own_table()
    {
        // Verbatim from `pi --list-models` (v0.84.3).
        const string Table = """
            provider   model                       context  max-out  thinking  images
            anthropic  claude-sonnet-5             1M       128K     yes       yes
            anthropic  claude-haiku-4-5            200K     64K      yes       yes
            openai     gpt-5                       400K     128K     yes       yes
            """;

        var adapter = PiAgent.Create(
            NullLoggerFactory.Instance,
            new PiOptions { ModelLister = _ => Task.FromResult<string?>(Table) });

        var models = await ((IModelListingAdapter)adapter).ListModelsAsync();

        Assert.NotNull(models);
        // Pi's --model flag takes provider/id, so that is the id a picker must hand back.
        Assert.Equal(
            ["anthropic/claude-sonnet-5", "anthropic/claude-haiku-4-5", "openai/gpt-5"],
            models.Select(m => m.Id));
        Assert.Equal("gpt-5 (openai)", models[2].DisplayName);
    }

    [Fact]
    public void The_table_header_is_not_mistaken_for_a_model()
    {
        var models = PiAgent.ParseModels("provider   model   context  max-out  thinking  images\n");

        Assert.Empty(models);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("No models available. Use /login to log into a provider via OAuth or API key. See:\n  /some/path.md")]
    public void An_unauthenticated_cli_yields_no_catalogue_rather_than_junk(string? stdout)
        // Verbatim from a real unauthenticated run: prose, not a table. Prose splits into columns just as
        // happily as a table does, so without anchoring on the header this line reads as a model "No/models".
        => Assert.Empty(PiAgent.ParseModels(stdout));

    [Fact]
    public void Duplicate_rows_are_collapsed()
    {
        var models = PiAgent.ParseModels(
            "provider   model            context  max-out  thinking  images\n" +
            "anthropic  claude-sonnet-5  1M       128K     yes       yes\n" +
            "anthropic  claude-sonnet-5  1M       128K     yes       yes\n");

        Assert.Single(models);
    }
}
