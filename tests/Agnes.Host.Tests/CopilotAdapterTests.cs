using Agnes.Abstractions;
using Agnes.Acp;
using Agnes.Agents.Copilot;

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

    [Fact]
    public void Autonomous_session_allows_tools_but_never_paths_or_urls()
    {
        var spec = CopilotAgent.CreateLaunchSpec();
        var options = new AgentSessionOptions { WorkingDirectory = Path.GetTempPath(), SkipPermissions = true };

        var args = AcpAgentAdapter.BuildAgentArguments(spec, options);

        Assert.Contains("--allow-all-tools", args);
        // --allow-all / --yolo would also disable path verification and URL confirmation. Skipping the
        // prompt is what the user opted into; discarding the filesystem boundary is not.
        Assert.DoesNotContain("--allow-all", args);
        Assert.DoesNotContain("--yolo", args);
        Assert.DoesNotContain("--allow-all-paths", args);
        Assert.DoesNotContain("--allow-all-urls", args);
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
}
