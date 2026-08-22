using System.Text.Json;
using Agnes.Abstractions;
using Agnes.Acp;
using Agnes.Agents.ClaudeCode;
using Agnes.Agents.OpenCode;

namespace Agnes.Host.Tests;

public sealed class ModelSelectionTests
{
    /// <summary>A fake optional-capability adapter: its live probe returns whatever it's told (null to model
    /// "the CLI can't be asked"), with a fixed static fallback.</summary>
    private sealed class FakeModelAdapter : IModelListingAdapter
    {
        private readonly IReadOnlyList<ModelInfo>? _live;

        public FakeModelAdapter(IReadOnlyList<ModelInfo>? live, IReadOnlyList<ModelInfo> staticModels)
        {
            _live = live;
            StaticModels = staticModels;
        }

        public IReadOnlyList<ModelInfo> StaticModels { get; }

        public Task<IReadOnlyList<ModelInfo>?> ListModelsAsync(CancellationToken ct = default)
            => Task.FromResult(_live);
    }

    [Fact]
    public async Task Resolve_falls_back_to_static_when_live_probe_returns_null()
    {
        var staticModels = new[] { new ModelInfo("s1", "Static One") };
        var adapter = new FakeModelAdapter(live: null, staticModels);

        var resolved = await ModelCatalog.ResolveAsync(adapter);

        Assert.Equal(staticModels, resolved);
    }

    [Fact]
    public async Task Resolve_uses_live_list_when_probing_succeeds()
    {
        var live = new[] { new ModelInfo("live-1", "Live One"), new ModelInfo("live-2", "Live Two") };
        var adapter = new FakeModelAdapter(live, staticModels: [new ModelInfo("s1", "Static One")]);

        var resolved = await ModelCatalog.ResolveAsync(adapter);

        Assert.Equal(live, resolved);
    }

    [Fact]
    public async Task ClaudeCode_resolves_to_its_static_models_when_probing_unsupported()
    {
        // ClaudeCode ships a static list and no live probe (ACP has no model-list call).
        var adapter = ClaudeCodeAgent.Create(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

        var lister = Assert.IsAssignableFrom<IModelListingAdapter>(adapter);
        Assert.Null(await lister.ListModelsAsync());
        var resolved = await ModelCatalog.ResolveAsync(lister);
        Assert.Equal(ClaudeCodeAgent.StaticModels, resolved);
        Assert.NotEmpty(resolved);
    }

    [Fact]
    public void ClaudeCode_threads_model_id_into_launch_args_as_model_flag()
    {
        var spec = ClaudeCodeAgent.CreateLaunchSpec();
        var options = new AgentSessionOptions { WorkingDirectory = Path.GetTempPath(), ModelId = "opus" };

        var args = AcpAgentAdapter.BuildAgentArguments(spec, options).ToList();

        var flagIndex = args.IndexOf("--model");
        Assert.True(flagIndex >= 0, "expected a --model flag in the launch args");
        Assert.Equal("opus", args[flagIndex + 1]);
    }

    [Fact]
    public void ClaudeCode_omits_model_flag_when_no_model_selected()
    {
        var spec = ClaudeCodeAgent.CreateLaunchSpec();
        var options = new AgentSessionOptions { WorkingDirectory = Path.GetTempPath() };

        var args = AcpAgentAdapter.BuildAgentArguments(spec, options);

        Assert.DoesNotContain("--model", args);
    }

    [Fact]
    public void ClaudeCode_threads_system_prompt_additions_into_launch_args()
    {
        var spec = ClaudeCodeAgent.CreateLaunchSpec();
        var options = new AgentSessionOptions { WorkingDirectory = Path.GetTempPath(), SystemPrompt = "Always write tests." };

        var args = AcpAgentAdapter.BuildAgentArguments(spec, options).ToList();

        var flagIndex = args.IndexOf("--append-system-prompt");
        Assert.True(flagIndex >= 0, "expected an --append-system-prompt flag in the launch args");
        Assert.Equal("Always write tests.", args[flagIndex + 1]);
    }

    [Fact]
    public void ClaudeCode_omits_system_prompt_flag_when_no_additions()
    {
        var spec = ClaudeCodeAgent.CreateLaunchSpec();
        var options = new AgentSessionOptions { WorkingDirectory = Path.GetTempPath() };

        var args = AcpAgentAdapter.BuildAgentArguments(spec, options);

        Assert.DoesNotContain("--append-system-prompt", args);
    }

    // ---- OpenCode: the model axis is environment-based, not argv-based ----

    [Fact]
    public async Task OpenCode_lists_models_from_the_cli_output()
    {
        var spec = OpenCodeAgent.CreateLaunchSpec(new OpenCodeOptions
        {
            ModelLister = _ => Task.FromResult<string?>("opencode/big-pickle\nopencode-go/glm-5.3\n"),
        });

        var models = await spec.LiveModelProbe!(CancellationToken.None);

        Assert.NotNull(models);
        Assert.Equal(["opencode/big-pickle", "opencode-go/glm-5.3"], models.Select(m => m.Id));
    }

    [Fact]
    public void OpenCode_model_parsing_ignores_banners_blanks_and_duplicates()
    {
        // The CLI prints an ASCII banner on some invocations; only bare provider/model ids are models.
        const string output = """
            █▀▀█ █▀▀█ █▀▀█
            Commands:
              opencode acp    start ACP server

            opencode/big-pickle
            opencode/big-pickle
            opencode-go/glm-5.3
            """;

        var models = OpenCodeAgent.ParseModels(output);

        Assert.Equal(["opencode/big-pickle", "opencode-go/glm-5.3"], models.Select(m => m.Id));
    }

    [Fact]
    public void OpenCode_empty_or_failed_probe_yields_no_catalogue()
    {
        // A missing or unauthenticated CLI is normal — it degrades to "no picker", never an error.
        Assert.Empty(OpenCodeAgent.ParseModels(null));
        Assert.Empty(OpenCodeAgent.ParseModels("   \n\n"));
    }

    [Fact]
    public void OpenCode_selects_a_model_through_the_environment_not_argv()
    {
        // `opencode acp` takes no --model flag, so the id must never leak into the launch args.
        var spec = OpenCodeAgent.CreateLaunchSpec();
        var options = new AgentSessionOptions { WorkingDirectory = Path.GetTempPath(), ModelId = "opencode-go/glm-5.3" };

        var args = AcpAgentAdapter.BuildAgentArguments(spec, options);
        var env = AcpAgentAdapter.BuildAgentEnvironment(spec, options);

        Assert.DoesNotContain("--model", args);
        Assert.DoesNotContain("opencode-go/glm-5.3", args);
        Assert.Equal("""{"model":"opencode-go/glm-5.3"}""", env["OPENCODE_CONFIG_CONTENT"]);
    }

    [Fact]
    public void OpenCode_overlay_sets_only_the_model_so_user_config_survives()
    {
        // OPENCODE_CONFIG_CONTENT is merged over the user's opencode.json; emitting anything beyond the
        // model key would silently override settings Agnes has no business touching.
        var overlay = JsonDocument.Parse(OpenCodeAgent.BuildConfigEnvironment("p/m", [])["OPENCODE_CONFIG_CONTENT"]);

        Assert.Equal(["model"], overlay.RootElement.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void OpenCode_emits_no_model_environment_when_no_model_selected()
    {
        var spec = OpenCodeAgent.CreateLaunchSpec();
        var options = new AgentSessionOptions { WorkingDirectory = Path.GetTempPath() };

        Assert.Empty(AcpAgentAdapter.BuildAgentEnvironment(spec, options));
    }

    [Fact]
    public void OpenCode_adapter_exposes_the_environment_model_capability()
    {
        // SessionManager discovers the sandbox wiring through this capability, not by knowing about OpenCode.
        var adapter = OpenCodeAgent.Create(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

        var capability = Assert.IsAssignableFrom<IModelEnvironmentAdapter>(adapter);
        Assert.Equal(
            """{"model":"opencode/big-pickle"}""",
            capability.InlineConfigEnvironment("opencode/big-pickle", [])["OPENCODE_CONFIG_CONTENT"]);
        Assert.Empty(capability.InlineConfigEnvironment(null, []));
    }

    [Fact]
    public void ClaudeCode_has_no_environment_model_axis()
    {
        // Claude Code selects with --model; it must not also emit model environment.
        var adapter = ClaudeCodeAgent.Create(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

        var capability = Assert.IsAssignableFrom<IModelEnvironmentAdapter>(adapter);
        Assert.Empty(capability.InlineConfigEnvironment("opus", []));
    }

    [Fact]
    public void OpenCode_carries_the_model_and_mcp_servers_in_ONE_overlay()
    {
        // Two variables would mean the second silently replaced the first — OpenCode has a single
        // inline-config env var, so both have to be stated together.
        var env = OpenCodeAgent.BuildConfigEnvironment(
            "opencode-go/ox-alpha-free",
            [new InlineMcpServer("agnes", "http://10.99.5.1:5099/mcp-agnes", "Bearer tok")]);

        var overlay = JsonDocument.Parse(Assert.Single(env).Value).RootElement;
        Assert.Equal("opencode-go/ox-alpha-free", overlay.GetProperty("model").GetString());
        var agnes = overlay.GetProperty("mcp").GetProperty("agnes");
        Assert.Equal("remote", agnes.GetProperty("type").GetString());
        Assert.Equal("http://10.99.5.1:5099/mcp-agnes", agnes.GetProperty("url").GetString());
        Assert.Equal("Bearer tok", agnes.GetProperty("headers").GetProperty("Authorization").GetString());
    }

    [Fact]
    public void An_mcp_server_alone_still_produces_an_overlay()
    {
        // A session with no model pinned must still be told where Agnes is.
        var env = OpenCodeAgent.BuildConfigEnvironment(null, [new InlineMcpServer("agnes", "http://h/mcp-agnes")]);

        var overlay = JsonDocument.Parse(Assert.Single(env).Value).RootElement;
        Assert.False(overlay.TryGetProperty("model", out _)); // nothing to say about the model
        Assert.True(overlay.TryGetProperty("mcp", out _));
    }

    [Fact]
    public void Nothing_to_configure_means_no_overlay_at_all()
    {
        // Emitting an empty overlay would still override the user's own config with "nothing".
        Assert.Empty(OpenCodeAgent.BuildConfigEnvironment(null, []));
    }
}
