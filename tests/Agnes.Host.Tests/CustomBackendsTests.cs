using Agnes.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Host.Tests;

public class CustomBackendsTests
{
    // dotnet must be on PATH for the test host to run at all — reuse it as a known-present command.
    private static readonly string OnPathCommand = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
    private const string MissingCommand = "definitely-not-a-real-agent-cli-xyz";

    [Fact]
    public void Build_exposes_configured_descriptor()
    {
        var adapter = CustomBackends.Build(
            new CustomAcpBackendConfig
            {
                Id = "my-acp",
                DisplayName = "My ACP CLI",
                Command = OnPathCommand,
                Arguments = ["acp"],
            },
            NullLoggerFactory.Instance);

        Assert.Equal("my-acp", adapter.Descriptor.Id);
        Assert.Equal("My ACP CLI", adapter.Descriptor.DisplayName);
    }

    [Fact]
    public void IsAvailable_is_true_when_command_is_on_path()
    {
        var adapter = CustomBackends.Build(
            new CustomAcpBackendConfig { Id = "on-path", DisplayName = "On Path", Command = OnPathCommand },
            NullLoggerFactory.Instance);

        Assert.True(adapter.IsAvailable());
    }

    [Fact]
    public void IsAvailable_is_false_when_command_is_missing()
    {
        var adapter = CustomBackends.Build(
            new CustomAcpBackendConfig { Id = "missing", DisplayName = "Missing", Command = MissingCommand },
            NullLoggerFactory.Instance);

        Assert.False(adapter.IsAvailable());
    }

    [Theory]
    [InlineData("", "Name", "cmd")]
    [InlineData(" ", "Name", "cmd")]
    [InlineData("id", "", "cmd")]
    [InlineData("id", "Name", "")]
    public void Build_rejects_incomplete_config(string id, string displayName, string command)
    {
        Assert.Throws<ArgumentException>(() => CustomBackends.Build(
            new CustomAcpBackendConfig { Id = id, DisplayName = displayName, Command = command },
            NullLoggerFactory.Instance));
    }

    [Fact]
    public void Load_skips_invalid_and_duplicate_entries_but_keeps_the_valid_one()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // 0: valid
                ["Agnes:CustomBackends:0:Id"] = "good",
                ["Agnes:CustomBackends:0:DisplayName"] = "Good",
                ["Agnes:CustomBackends:0:Command"] = OnPathCommand,
                // 1: invalid (no Command) — must be skipped, not fatal
                ["Agnes:CustomBackends:1:Id"] = "bad",
                ["Agnes:CustomBackends:1:DisplayName"] = "Bad",
                // 2: duplicate id — must be skipped
                ["Agnes:CustomBackends:2:Id"] = "good",
                ["Agnes:CustomBackends:2:DisplayName"] = "Dup",
                ["Agnes:CustomBackends:2:Command"] = OnPathCommand,
            })
            .Build();

        var loaded = CustomBackends.Load(configuration, NullLogger.Instance);

        var only = Assert.Single(loaded);
        Assert.Equal("good", only.Id);
    }

    [Fact]
    public void Load_returns_empty_when_section_absent()
    {
        var configuration = new ConfigurationBuilder().Build();
        Assert.Empty(CustomBackends.Load(configuration, NullLogger.Instance));
    }

    [Fact]
    public void Register_materializes_only_valid_backends_into_di()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Agnes:CustomBackends:0:Id"] = "good",
                ["Agnes:CustomBackends:0:DisplayName"] = "Good",
                ["Agnes:CustomBackends:0:Command"] = OnPathCommand,
                // Malformed second entry: the host must still wire up cleanly.
                ["Agnes:CustomBackends:1:Id"] = "bad",
                ["Agnes:CustomBackends:1:DisplayName"] = "Bad",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        CustomBackends.Register(services, configuration, NullLogger.Instance);

        using var provider = services.BuildServiceProvider();
        var adapters = provider.GetServices<IAgentAdapter>().ToList();

        var only = Assert.Single(adapters);
        Assert.Equal("good", only.Descriptor.Id);
    }
}
