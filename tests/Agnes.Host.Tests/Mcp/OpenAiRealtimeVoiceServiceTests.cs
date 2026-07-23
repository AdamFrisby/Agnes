using Microsoft.Extensions.Configuration;
using Agnes.Host.Mcp;

namespace Agnes.Host.Tests.Mcp;

public sealed class OpenAiRealtimeVoiceServiceTests
{
    private const string ApiKey = "sk-super-secret-key-value";
    private const string Endpoint = "https://host.example/mcp-agnes";

    private static VoiceRealtimeOptions Options(string? apiKey) =>
        new(ApiKey: apiKey ?? string.Empty, Model: "gpt-realtime", McpEndpointUrl: Endpoint, McpAuthToken: "device-token", Voice: "verse");

    [Fact]
    public void Session_config_references_the_agnes_mcp_endpoint_and_the_model()
    {
        var service = new OpenAiRealtimeVoiceService(Options(ApiKey));

        Assert.True(service.IsAvailable);
        var config = service.BuildSessionConfig();

        Assert.Equal("gpt-realtime", config.Model);
        var connector = Assert.Single(config.Tools);
        Assert.Equal("mcp", connector.Type);
        Assert.Equal(Endpoint, connector.ServerUrl);
        Assert.Equal("agnes", connector.ServerLabel);
        Assert.Equal("Bearer device-token", connector.Authorization);
    }

    [Fact]
    public void Without_a_key_the_service_is_unusable_and_refuses_to_assemble()
    {
        var service = new OpenAiRealtimeVoiceService(Options(apiKey: null));

        Assert.False(service.IsAvailable);
        Assert.Throws<InvalidOperationException>(() => service.BuildSessionConfig());
    }

    [Fact]
    public void The_api_key_never_appears_in_the_assembled_session_config()
    {
        var service = new OpenAiRealtimeVoiceService(Options(ApiKey));

        var connection = service.BuildConnection();

        // The key rides the connection (for the Authorization header) but is absent from the serialized body.
        Assert.Equal(ApiKey, connection.ApiKey);
        Assert.DoesNotContain(ApiKey, connection.SessionConfigJson, StringComparison.Ordinal);
        Assert.Contains(Endpoint, connection.SessionConfigJson, StringComparison.Ordinal);
        Assert.Contains("gpt-realtime", connection.SessionConfigJson, StringComparison.Ordinal);
    }

    [Fact]
    public void From_configuration_sources_the_key_and_model_from_host_settings_and_falls_back_to_the_local_endpoint()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agnes:Voice:OpenAI:ApiKey"] = ApiKey,
            ["Agnes:Voice:OpenAI:McpAuthToken"] = "device-token",
        }).Build();

        var options = VoiceRealtimeOptions.FromConfiguration(config, defaultMcpEndpointUrl: "/mcp-agnes");

        Assert.True(options.IsUsable);
        Assert.Equal(ApiKey, options.ApiKey);
        Assert.Equal(VoiceRealtimeOptions.DefaultModel, options.Model);   // default when unset
        Assert.Equal("/mcp-agnes", options.McpEndpointUrl);              // default endpoint when no override
    }

    [Fact]
    public void From_configuration_with_no_key_is_not_usable()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();

        var options = VoiceRealtimeOptions.FromConfiguration(config, defaultMcpEndpointUrl: "/mcp-agnes");

        Assert.False(options.IsUsable);
    }
}
