using Agnes.Abstractions;
using Agnes.Agents.Native;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Agents.Native.Tests;

/// <summary>The native Claude adapter used to drop the selected model entirely (it never read
/// <see cref="AgentSessionOptions.ModelId"/>). It now surfaces a model catalog and threads <c>--model</c>,
/// so the picker appears and the choice reaches the CLI.</summary>
public class NativeModelSelectionTests
{
    [Fact]
    public void Native_claude_adapter_lists_selectable_models()
    {
        var adapter = ClaudeCodeNative.Create(NullLoggerFactory.Instance);

        var listing = Assert.IsAssignableFrom<IModelListingAdapter>(adapter);
        Assert.Contains(listing.StaticModels, m => m.Id == "sonnet");
        Assert.Contains(listing.StaticModels, m => m.Id == "opus");
        // Aliases resolve to the latest concrete model, so a specific dated id is a custom entry.
        Assert.All(listing.StaticModels, m => Assert.True(m.IsCustomEntryAllowed));
    }

    [Fact]
    public void Model_arguments_use_the_model_flag()
    {
        // The claude CLI selects a model with `--model <id>` — the spec's ModelArguments the adapter threads.
        var spec = new NativeLaunchSpec
        {
            Command = "claude",
            Descriptor = ClaudeCodeNative.Descriptor,
            Mapper = new ClaudeCodeStreamMapper(),
            ModelArguments = static id => ["--model", id],
        };

        Assert.Equal(["--model", "opus"], spec.ModelArguments!("opus"));
    }
}
