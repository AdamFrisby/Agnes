using Agnes.Plugins.CodeyBox;

namespace Agnes.Plugins.CodeyBox.Tests;

/// <summary>
/// Where the plugin gets its CodeyBox address and key. It reads the same places CodeyBox's own CLI reads,
/// in the same order, so a machine already set up for <c>codeybox</c> needs no second configuration — a
/// second source of truth for one credential is how the two drift apart.
/// </summary>
public class CodeyBoxOptionsTests : IDisposable
{
    private readonly string? _url = Environment.GetEnvironmentVariable("CODEYBOX_CLI_API_URL");
    private readonly string? _key = Environment.GetEnvironmentVariable("CODEYBOX_CLI_API_KEY");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_URL", _url);
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", _key);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void The_environment_wins_over_the_config_file()
    {
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_URL", "http://from-env:1234/");
        Environment.SetEnvironmentVariable("CODEYBOX_CLI_API_KEY", "env-key");

        var options = CodeyBoxOptions.Resolve();

        Assert.Equal("http://from-env:1234", options.BaseUrl); // trailing slash normalised away
        Assert.Equal("env-key", options.ApiKey);
        Assert.True(options.IsConfigured);
    }

    [Fact]
    public void No_key_anywhere_is_an_ordinary_state_not_an_error()
    {
        // Most machines have no CodeyBox at all. The screen renders a "configure me" state off this rather
        // than throwing during construction, which would take the whole client plugin set down with it.
        var options = new CodeyBoxOptions(CodeyBoxOptions.DefaultBaseUrl, null);

        Assert.False(options.IsConfigured);
        Assert.Equal("http://localhost:5036", options.DefaultUrlForComparison());
    }
}

internal static class OptionsTestExtensions
{
    /// <summary>Reads the documented CLI default back, so a change to it fails a test rather than silently
    /// pointing the plugin somewhere the CLI would not look.</summary>
    public static string DefaultUrlForComparison(this CodeyBoxOptions _) => CodeyBoxOptions.DefaultBaseUrl;
}
