using Agnes.Agents.Codex;
using Agnes.Agents.Codex.Wire;
using Agnes.Abstractions;
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

    [Fact]
    public void Live_models_map_model_id_and_display_name_after_codex_default()
    {
        var models = CodexModelCatalog.ToModelInfo(
            [
                new CodexModel { Id = "api-id", Model = "gpt-live", DisplayName = "GPT Live" },
                new CodexModel { Id = "fallback-id", DisplayName = "Fallback Name" },
            ],
            includeHidden: false);

        Assert.Collection(
            models,
            model =>
            {
                Assert.Equal(string.Empty, model.Id);
                Assert.Equal("Codex default", model.DisplayName);
            },
            model =>
            {
                Assert.Equal("gpt-live", model.Id);
                Assert.Equal("GPT Live", model.DisplayName);
                Assert.True(model.IsCustomEntryAllowed);
            },
            model =>
            {
                Assert.Equal("fallback-id", model.Id);
                Assert.Equal("Fallback Name", model.DisplayName);
            });
    }

    [Fact]
    public async Task Connection_model_list_follows_next_cursor()
    {
        var (client, server) = FakeCodexAppServer.Create();
        await using var _ = server;
        await using var connection = new CodexConnection(client, client, NullLogger.Instance);

        server.OnModelList = p =>
        {
            var cursor = p.TryGetProperty("cursor", out var value) ? value.GetString() : null;
            return cursor switch
            {
                null => new
                {
                    data = new[] { new { id = "one", model = "one", displayName = "One", hidden = false } },
                    nextCursor = (string?)"page-2",
                },
                "page-2" => new
                {
                    data = new[] { new { id = "two", model = "two", displayName = "Two", hidden = false } },
                    nextCursor = (string?)null,
                },
                _ => throw new InvalidOperationException($"Unexpected cursor '{cursor}'."),
            };
        };

        await connection.InitializeAsync(default);
        var models = await connection.ListModelsAsync(includeHidden: false);

        Assert.Equal(["one", "two"], models.Select(m => m.Model!).ToArray());
        Assert.Equal(2, server.ModelListRequests.Count);
        Assert.False(server.ModelListRequests[0].GetProperty("includeHidden").GetBoolean());
        Assert.False(server.ModelListRequests[0].TryGetProperty("cursor", out var ignored));
        Assert.Equal("page-2", server.ModelListRequests[1].GetProperty("cursor").GetString());
    }

    [Fact]
    public async Task Adapter_live_listing_excludes_hidden_models_by_default()
    {
        var adapter = new CodexAppServerAdapter(
            new CodexLaunchSpec(),
            NullLoggerFactory.Instance,
            (_, _) => Task.FromResult<IReadOnlyList<CodexModel>>(
                [
                    new CodexModel { Model = "visible", DisplayName = "Visible", Hidden = false },
                    new CodexModel { Model = "hidden", DisplayName = "Hidden", Hidden = true },
                ]));

        var models = await adapter.ListModelsAsync();

        Assert.NotNull(models);
        Assert.Equal(["", "visible"], models.Select(m => m.Id).ToArray());
    }

    [Fact]
    public async Task Model_catalog_resolve_falls_back_to_static_models_when_codex_live_listing_fails()
    {
        var adapter = new CodexAppServerAdapter(
            new CodexLaunchSpec(),
            NullLoggerFactory.Instance,
            (_, _) => throw new NotSupportedException("model/list is not supported"));

        var models = await ModelCatalog.ResolveAsync(adapter);

        Assert.Equal(adapter.StaticModels, models);
    }
}
