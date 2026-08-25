using System.Text.Json;
using Agnes.Abstractions;
using Agnes.Acp;
using Agnes.Agents.Copilot;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Host.Tests;

/// <summary>
/// GitHub Copilot CLI's adapter, pinned at its pure edges: the argv it builds, the BYOK environment it
/// renders, and the two boundary formats it parses. Everything here is a function of its inputs, so none of
/// it needs a CLI — the live shape it mirrors was captured from <c>copilot --acp</c> v1.0.78.
/// </summary>
public sealed class CopilotAdapterTests
{
    // ---- permissions: the security-relevant one ----

    [Fact]
    public void Attended_session_adds_no_permission_flag_so_the_agent_asks_per_tool()
    {
        var spec = CopilotAgent.CreateLaunchSpec();
        var options = new AgentSessionOptions { WorkingDirectory = Path.GetTempPath() };

        var args = AcpAgentAdapter.BuildAgentArguments(spec, options);

        // Copilot's ACP default IS ask-per-tool; adding anything here would quietly widen it.
        Assert.Equal(["--acp"], args);
    }

    private sealed class FakeSandbox : ISandboxCommand
    {
        public (string Command, IReadOnlyList<string> Arguments) WrapCommand(
            string command, IReadOnlyList<string> arguments, string workingDirectory)
            => (command, arguments);
    }

    [Fact]
    public void Autonomous_session_on_the_host_allows_tools_but_never_paths_or_urls()
    {
        var spec = CopilotAgent.CreateLaunchSpec();
        var options = new AgentSessionOptions { WorkingDirectory = Path.GetTempPath(), SkipPermissions = true };

        var args = AcpAgentAdapter.BuildAgentArguments(spec, options);

        Assert.Contains("--allow-all-tools", args);
        // --allow-all / --yolo would also disable path verification and URL confirmation. Skipping the
        // prompt is what the user opted into; discarding the filesystem boundary is not — with nothing
        // else confining the agent, Copilot's own path check is the boundary that remains.
        Assert.DoesNotContain("--allow-all", args);
        Assert.DoesNotContain("--yolo", args);
        Assert.DoesNotContain("--allow-all-paths", args);
        Assert.DoesNotContain("--allow-all-urls", args);
    }

    [Fact]
    public void Autonomous_session_in_a_sandbox_also_trusts_paths()
    {
        var spec = CopilotAgent.CreateLaunchSpec();
        var options = new AgentSessionOptions
        {
            WorkingDirectory = Path.GetTempPath(),
            SkipPermissions = true,
            Sandbox = new FakeSandbox(),
        };

        var args = AcpAgentAdapter.BuildAgentArguments(spec, options);

        Assert.Contains("--allow-all-tools", args);
        // The VM is the filesystem boundary here, and Copilot's own path check guards nothing it doesn't —
        // it only produces prompts. It keeps session state (subagent briefs) outside the working directory,
        // so ordinary work trips it constantly, in a session explicitly opted out of being interrupted.
        Assert.Contains("--allow-all-paths", args);
        // Egress is a different boundary, governed by the sandbox's proxy and credential broker.
        Assert.DoesNotContain("--allow-all-urls", args);
    }

    [Fact]
    public void A_sandbox_alone_does_not_widen_an_attended_session()
    {
        var spec = CopilotAgent.CreateLaunchSpec();
        var options = new AgentSessionOptions { WorkingDirectory = Path.GetTempPath(), Sandbox = new FakeSandbox() };

        // Confinement is not consent: a user who never asked for autonomous operation still gets asked.
        Assert.Equal(["--acp"], AcpAgentAdapter.BuildAgentArguments(spec, options));
    }

    // ---- model + MCP argv ----

    [Fact]
    public void Threads_model_id_into_launch_args_as_model_flag()
    {
        var spec = CopilotAgent.CreateLaunchSpec();
        var options = new AgentSessionOptions { WorkingDirectory = Path.GetTempPath(), ModelId = "claude-sonnet-4.5" };

        var args = AcpAgentAdapter.BuildAgentArguments(spec, options).ToList();

        var flag = args.IndexOf("--model");
        Assert.True(flag >= 0, "expected a --model flag in the launch args");
        Assert.Equal("claude-sonnet-4.5", args[flag + 1]);
    }

    [Fact]
    public void Omits_model_flag_when_no_model_selected()
    {
        var args = AcpAgentAdapter.BuildAgentArguments(
            CopilotAgent.CreateLaunchSpec(),
            new AgentSessionOptions { WorkingDirectory = Path.GetTempPath() });

        Assert.DoesNotContain("--model", args);
    }

    [Fact]
    public void Loads_an_agnes_managed_mcp_config_by_path_not_by_value()
    {
        var spec = CopilotAgent.CreateLaunchSpec();
        var options = new AgentSessionOptions
        {
            WorkingDirectory = Path.GetTempPath(),
            McpConfigPath = "/run/agnes/mcp.json",
        };

        var args = AcpAgentAdapter.BuildAgentArguments(spec, options).ToList();

        var flag = args.IndexOf("--additional-mcp-config");
        Assert.True(flag >= 0, "expected an --additional-mcp-config flag in the launch args");
        // The @ prefix is what makes Copilot read a file; without it the path is parsed as JSON and rejected.
        Assert.Equal("@/run/agnes/mcp.json", args[flag + 1]);
    }

    [Fact]
    public void Omits_the_mcp_flag_when_agnes_manages_no_servers()
    {
        var args = AcpAgentAdapter.BuildAgentArguments(
            CopilotAgent.CreateLaunchSpec(),
            new AgentSessionOptions { WorkingDirectory = Path.GetTempPath() });

        Assert.DoesNotContain("--additional-mcp-config", args);
    }

    // ---- BYOK environment ----

    [Fact]
    public void Byok_is_inert_until_a_base_url_is_configured()
    {
        // Everything but the base URL: Copilot ignores all of it, so Agnes must emit none of it either —
        // a half-set BYOK environment would be a silent no-op that looks configured.
        var provider = new CopilotProviderOptions { ApiKey = "sk-test", Model = "gpt-5.4" };

        Assert.Empty(CopilotAgent.BuildProviderEnvironment(provider));
        Assert.Empty(CopilotAgent.BuildProviderEnvironment(null));
    }

    [Fact]
    public void Byok_environment_states_the_provider_axis_copilot_exposes_nowhere_else()
    {
        var env = CopilotAgent.BuildProviderEnvironment(new CopilotProviderOptions
        {
            BaseUrl = "https://gateway.example.com/v1",
            Type = CopilotProviderType.Anthropic,
            ApiKey = "sk-ant-test",
            WireApi = CopilotWireApi.Responses,
            Transport = CopilotTransport.WebSockets,
            Model = "claude-sonnet-4-20250514",
            MaxOutputTokens = 8192,
            Headers = ["X-Gateway-Key: abc123", "X-Tenant-Id: mai"],
        });

        Assert.Equal("https://gateway.example.com/v1", env["COPILOT_PROVIDER_BASE_URL"]);
        Assert.Equal("anthropic", env["COPILOT_PROVIDER_TYPE"]);
        Assert.Equal("sk-ant-test", env["COPILOT_PROVIDER_API_KEY"]);
        Assert.Equal("responses", env["COPILOT_PROVIDER_WIRE_API"]);
        Assert.Equal("websockets", env["COPILOT_PROVIDER_TRANSPORT"]);
        Assert.Equal("claude-sonnet-4-20250514", env["COPILOT_MODEL"]);
        Assert.Equal("8192", env["COPILOT_PROVIDER_MAX_OUTPUT_TOKENS"]);
        // Copilot parses headers as newline-separated "Name: Value" pairs.
        Assert.Equal("X-Gateway-Key: abc123\nX-Tenant-Id: mai", env["COPILOT_PROVIDER_HEADERS"]);
        Assert.DoesNotContain("COPILOT_PROVIDER_BEARER_TOKEN", env.Keys);
    }

    [Fact]
    public void Byok_sends_one_credential_not_two()
    {
        var env = CopilotAgent.BuildProviderEnvironment(new CopilotProviderOptions
        {
            BaseUrl = "https://api.example.com/v1",
            ApiKey = "sk-key",
            BearerToken = "bearer-token",
        });

        // Copilot prefers the bearer token when both are set, so emitting both would leave which credential
        // is actually used up to Copilot's internals rather than to the operator.
        Assert.Equal("bearer-token", env["COPILOT_PROVIDER_BEARER_TOKEN"]);
        Assert.DoesNotContain("COPILOT_PROVIDER_API_KEY", env.Keys);
    }

    [Fact]
    public void Inline_config_carries_byok_and_leaves_the_model_to_argv()
    {
        var adapter = CopilotAgent.Create(
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            new CopilotOptions { Provider = new CopilotProviderOptions { BaseUrl = "http://localhost:11434/v1" } });

        var env = ((IModelEnvironmentAdapter)adapter).InlineConfigEnvironment("gpt-5.4", []);

        Assert.Equal("http://localhost:11434/v1", env["COPILOT_PROVIDER_BASE_URL"]);
        // The session's model travels on --model; duplicating it here would give one choice two sources.
        Assert.DoesNotContain("COPILOT_MODEL", env.Keys);
    }

    // ---- model catalogue: parsed from the ACP session/new result ----

    [Fact]
    public async Task Lists_models_from_the_acp_handshake()
    {
        // Trimmed from a real `copilot --acp` session/new response (v1.0.78).
        const string Response = """
            {"jsonrpc":"2.0","id":2,"result":{"sessionId":"a1d757be","models":{"availableModels":[
              {"modelId":"auto","name":"Auto","description":"Let Copilot pick the best model"},
              {"modelId":"gpt-5.4","name":"GPT-5.4","description":"GPT-5.4"},
              {"modelId":"claude-sonnet-4.5","name":"Claude Sonnet 4.5","description":"Claude Sonnet 4.5"}]}}}
            """;

        var adapter = CopilotAgent.Create(
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            new CopilotOptions { ModelLister = _ => Task.FromResult<string?>(Response) });

        var models = await ((IModelListingAdapter)adapter).ListModelsAsync();

        Assert.NotNull(models);
        Assert.Equal(["auto", "gpt-5.4", "claude-sonnet-4.5"], models.Select(m => m.Id));
        Assert.Equal("Claude Sonnet 4.5", models[2].DisplayName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("""{"jsonrpc":"2.0","id":2,"error":{"code":-32000,"message":"not authenticated"}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":2,"result":{"sessionId":"a1d757be"}}""")]
    public void Model_catalogue_degrades_to_empty_rather_than_throwing(string? response)
    {
        // Every one of these is a real state (CLI absent, not logged in, an older Copilot with no model
        // axis). None of them is an error the user should see — they mean "no picker".
        Assert.Empty(CopilotModelCatalog.Parse(response));
    }

    // ---- native MCP discovery ----

    [Fact]
    public void Reads_the_servers_copilot_already_has_configured()
    {
        const string Config = """
            {"mcpServers":{
              "Playwright":{"type":"local","command":"npx","args":["@playwright/mcp@latest"],"tools":["*"]},
              "Figma":{"type":"http","url":"http://10.0.0.44:9000/mcp","headers":{},"tools":["*"]}}}
            """;

        var servers = CopilotNativeMcpConfig.ParseContent(Config).OrderBy(s => s.Name).ToList();

        Assert.Equal(2, servers.Count);
        Assert.Equal("http", servers[0].Transport);
        Assert.Equal("http://10.0.0.44:9000/mcp", servers[0].Url);
        Assert.Equal("stdio", servers[1].Transport);
        Assert.Equal("npx", servers[1].Command);
        Assert.Equal(["@playwright/mcp@latest"], servers[1].Args);
        Assert.All(servers, s => Assert.Equal(CopilotNativeMcpConfig.SourceLabel, s.SourceLabel));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("""{"mcpServers":{}}""")]
    [InlineData("""{"mcpServers":{"broken":{"tools":["*"]}}}""")]
    public void Native_mcp_discovery_never_throws_on_a_config_it_doesnt_own(string config)
        => Assert.Empty(CopilotNativeMcpConfig.ParseContent(config));

    [Fact]
    public void Offers_the_login_command_copilot_advertises_in_its_handshake()
    {
        var adapter = CopilotAgent.Create(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

        var login = adapter.GetInteractiveLoginCommand();

        Assert.NotNull(login);
        Assert.Equal("copilot", login.Command);
        Assert.Equal(["login"], login.Arguments);
    }

    // ---- subagent models: what makes subagents reachable at all under BYOK ----

    [Fact]
    public void Subagent_settings_point_the_model_pinning_agents_at_the_session_model()
    {
        var merged = CopilotSubagentSettings.Apply(existing: null, modelId: "stealth/ox-alpha");

        Assert.NotNull(merged);
        var agents = JsonDocument.Parse(merged).RootElement
            .GetProperty("subagents").GetProperty("agents");
        Assert.Equal("stealth/ox-alpha", agents.GetProperty("explore").GetProperty("model").GetString());
        Assert.Equal("stealth/ox-alpha", agents.GetProperty("task").GetProperty("model").GetString());
        Assert.Equal("stealth/ox-alpha", agents.GetProperty("research").GetProperty("model").GetString());
    }

    /// <summary>The file belongs to Copilot and carries settings a person chose. Merging that keeps only
    /// what it recognises is the same bug as overwriting.</summary>
    [Fact]
    public void Merging_keeps_every_setting_it_does_not_own()
    {
        const string existing = """
            {
              "renderMarkdown": true,
              "effortLevel": "max",
              "model": "gpt-5.6-luna",
              "allowedUrls": ["https://github.com", "https://www.nuget.org"],
              "subagents": { "maxConcurrency": 8, "agents": { "task": { "effortLevel": "low" } } }
            }
            """;

        var merged = CopilotSubagentSettings.Apply(existing, "stealth/ox-alpha");

        Assert.NotNull(merged);
        var root = JsonDocument.Parse(merged).RootElement;
        Assert.True(root.GetProperty("renderMarkdown").GetBoolean());
        Assert.Equal("max", root.GetProperty("effortLevel").GetString());
        Assert.Equal("gpt-5.6-luna", root.GetProperty("model").GetString());   // the SESSION model travels on argv
        Assert.Equal(2, root.GetProperty("allowedUrls").GetArrayLength());

        var subagents = root.GetProperty("subagents");
        Assert.Equal(8, subagents.GetProperty("maxConcurrency").GetInt32());

        var task = subagents.GetProperty("agents").GetProperty("task");
        Assert.Equal("stealth/ox-alpha", task.GetProperty("model").GetString());
        Assert.Equal("low", task.GetProperty("effortLevel").GetString());       // sibling keys untouched
    }

    /// <summary>Null means "leave it alone", which is what keeps a relaunch on an unchanged model from
    /// rewriting a file it has nothing to say about.</summary>
    [Fact]
    public void A_file_that_already_says_this_is_left_alone()
    {
        var first = CopilotSubagentSettings.Apply(null, "stealth/ox-alpha");
        Assert.NotNull(first);
        Assert.Null(CopilotSubagentSettings.Apply(first, "stealth/ox-alpha"));
    }

    [Fact]
    public void Switching_model_rewrites_every_pinned_agent()
    {
        var first = CopilotSubagentSettings.Apply(null, "stealth/ox-alpha");
        var second = CopilotSubagentSettings.Apply(first, "gpt-5.6-sol");

        Assert.NotNull(second);
        var agents = JsonDocument.Parse(second).RootElement.GetProperty("subagents").GetProperty("agents");
        foreach (var name in CopilotSubagentSettings.ModelPinningAgents)
        {
            Assert.Equal("gpt-5.6-sol", agents.GetProperty(name).GetProperty("model").GetString());
        }
    }

    [Fact]
    public void No_model_selected_writes_nothing()
        => Assert.Null(CopilotSubagentSettings.Apply(null, modelId: null));

    /// <summary>A syntax error in a hand-edited file must cost the subagent override, not the file.</summary>
    [Fact]
    public void Unparseable_settings_are_never_overwritten()
        => Assert.Null(CopilotSubagentSettings.Apply("{ not json", "stealth/ox-alpha"));

    /// <summary>On a GitHub subscription the pinned ids resolve and were chosen on purpose — a small fast
    /// model for the cheap agents. Only BYOK turns that choice into "no subagents at all".</summary>
    [Fact]
    public void Without_byok_the_settings_file_is_left_alone()
    {
        var adapter = CopilotAgent.Create(NullLoggerFactory.Instance);
        Assert.Null(((IModelSettingsAdapter)adapter).RenderSettings(null, "gpt-5.6-luna"));
    }

    [Fact]
    public void With_byok_the_adapter_writes_copilots_own_settings_path()
    {
        var adapter = CopilotAgent.Create(NullLoggerFactory.Instance, new CopilotOptions
        {
            Provider = new CopilotProviderOptions { BaseUrl = "https://openrouter.ai/api/v1" },
        });

        var settings = (IModelSettingsAdapter)adapter;
        Assert.Equal(".copilot/settings.json", settings.SettingsFilePath);
        Assert.NotNull(settings.RenderSettings(null, "stealth/ox-alpha"));
    }

    /// <summary>An operator who would rather Agnes did not touch the file says so by naming no agents.</summary>
    [Fact]
    public void An_empty_subagent_list_disables_the_rewrite()
    {
        var adapter = CopilotAgent.Create(NullLoggerFactory.Instance, new CopilotOptions
        {
            Provider = new CopilotProviderOptions { BaseUrl = "https://openrouter.ai/api/v1" },
            SubagentNames = [],
        });

        Assert.Null(((IModelSettingsAdapter)adapter).RenderSettings(null, "stealth/ox-alpha"));
    }

    // ---- fleet mode: the one lever Copilot gives for it ----

    /// <summary>Copilot exposes fleet mode nowhere but the in-session command — no flag, no environment,
    /// and its ACP mode list is only Agent/Plan/Autopilot. So the spec carries it as a startup command.</summary>
    [Fact]
    public void Fleet_mode_is_reached_by_invoking_copilots_own_command()
    {
        var spec = CopilotAgent.CreateLaunchSpec(new CopilotOptions { FleetMode = true });
        Assert.Equal(["/fleet"], spec.StartupCommands);

        // ...and not by smuggling it onto argv, where Copilot would reject it.
        var argv = AcpAgentAdapter.BuildAgentArguments(
            spec, new AgentSessionOptions { WorkingDirectory = Path.GetTempPath() });
        Assert.DoesNotContain(argv, a => a.Contains("fleet", StringComparison.Ordinal));
    }

    /// <summary>A fleet session spends far more than a plain one, so it is the operator's choice.</summary>
    [Fact]
    public void Fleet_mode_is_off_unless_asked_for()
        => Assert.Empty(CopilotAgent.CreateLaunchSpec().StartupCommands);
}
