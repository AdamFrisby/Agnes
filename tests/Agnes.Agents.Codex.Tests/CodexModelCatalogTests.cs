using Agnes.Agents.Codex;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Agents.Codex.Tests;

public sealed class CodexModelCatalogTests
{
    [Fact]
    public void Static_models_default_to_codex_config_and_avoid_retired_chatgpt_ids()
    {
        var adapter = CodexAppServer.Create(NullLoggerFactory.Instance);

        Assert.Equal(string.Empty, adapter.StaticModels[0].Id);
        Assert.Equal("Codex default", adapter.StaticModels[0].DisplayName);
        Assert.DoesNotContain(adapter.StaticModels, m => m.Id == "gpt-5");
        Assert.DoesNotContain(adapter.StaticModels, m => m.Id == "gpt-5-codex");
        Assert.Contains(adapter.StaticModels, m => m.Id == "gpt-5.6");
        Assert.Contains(adapter.StaticModels, m => m.Id == "gpt-5.6-sol");
        Assert.Contains(adapter.StaticModels, m => m.Id == "gpt-5.6-terra");
        Assert.Contains(adapter.StaticModels, m => m.Id == "gpt-5.6-luna");
    }
}
