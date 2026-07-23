using Agnes.Abstractions;
using Agnes.Acp;
using Agnes.Agents.ClaudeCode;

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
}
