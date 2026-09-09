namespace Agnes.Abstractions;

/// <summary>
/// A model an agent's CLI can be told to use. Distinct from <see cref="SessionMode"/> (Ask/Code/Plan) —
/// this is the underlying model axis. <see cref="IsCustomEntryAllowed"/> gates whether the picker also
/// accepts a free-text id in place of a catalogued one (providers ship models faster than a static list
/// can track), so an adapter can lock this down where a free-text id genuinely wouldn't make sense.
/// </summary>
public sealed record ModelInfo(string Id, string DisplayName, bool IsCustomEntryAllowed = true);

/// <summary>
/// Optional capability an <see cref="IAgentAdapter"/> may implement (checked via <c>is IModelListingAdapter</c>)
/// to enumerate the models its CLI accepts. ACP has no standard model-list call, so live probing is optional:
/// <see cref="ListModelsAsync"/> returns null when the CLI can't be asked, and the caller falls back to
/// <see cref="StaticModels"/> — the picker is therefore never empty just because probing isn't supported.
/// </summary>
public interface IModelListingAdapter
{
    /// <summary>Live-probes the provider for currently available models, or null when the CLI can't be asked
    /// (fall back to <see cref="StaticModels"/>).</summary>
    Task<IReadOnlyList<ModelInfo>?> ListModelsAsync(CancellationToken ct = default);

    /// <summary>Static fallback list, used when live probing isn't supported or returns null.</summary>
    IReadOnlyList<ModelInfo> StaticModels { get; }
}

/// <summary>
/// Optional capability an <see cref="IAgentAdapter"/> may implement (checked via
/// <c>is IModelEnvironmentAdapter</c>) when its CLI selects a model through the <b>environment</b> rather
/// than argv. Not every CLI takes a <c>--model</c> flag: OpenCode's ACP server takes none at all, and reads
/// its model from config, which <c>OPENCODE_CONFIG_CONTENT</c> overrides. Agnes needs this as a separate
/// axis from argv because the two travel differently into a sandbox — argv rides the wrapped
/// <c>incus exec</c> command, while environment has to be materialized into the guest's agent-env file
/// (the run wrapper scrubs the host environment with <c>env -i</c>).
/// </summary>
public interface IModelEnvironmentAdapter
{
    /// <summary>
    /// The environment that carries this CLI's inline configuration — the model it must use, and any MCP
    /// servers it should reach. Both travel together because a CLI configured this way has <b>one</b>
    /// inline-config variable: emitting them separately would have the second overwrite the first.
    /// Empty when the adapter has no environment-based configuration, so callers can apply it unconditionally.
    /// </summary>
    IReadOnlyDictionary<string, string> InlineConfigEnvironment(string? modelId, IReadOnlyList<InlineMcpServer> mcpServers);
}

/// <summary>
/// Optional capability an <see cref="IAgentAdapter"/> may implement (checked via
/// <c>is IModelSettingsAdapter</c>) when its CLI takes part of its model configuration from a <b>file in
/// its own home directory</b> — not argv, and not the environment. Copilot is the case that forced this:
/// its built-in subagents pin models in their shipped definitions, and the only override is a key in
/// <c>~/.copilot/settings.json</c>, so a session on a BYOK provider can dispatch to no subagent that pins
/// a model it does not serve.
///
/// A third axis is needed for the same reason the environment one was: the three travel differently into a
/// sandbox. argv rides the wrapped exec, environment is materialized into the guest's agent-env file, and a
/// settings file has to be written into the guest's home. Because the file already exists and belongs to
/// the CLI, the adapter is handed what it currently says and returns what it should say — a merge it owns,
/// rather than a render that would silently discard settings a person chose.
/// </summary>
public interface IModelSettingsAdapter
{
    /// <summary>Where the file lives, relative to the agent's home directory (e.g.
    /// <c>.copilot/settings.json</c>). Constant per adapter: the same path on the host and in a guest.</summary>
    string SettingsFilePath { get; }

    /// <summary>
    /// What the file should contain for a session running <paramref name="modelId"/>, given
    /// <paramref name="existingContents"/> (null when the file does not exist yet) — or <c>null</c> for
    /// "leave it alone", which covers no model selected, nothing this adapter wants to say, and the file
    /// already saying it. Callers write only a non-null result, so a relaunch that changes nothing touches
    /// no file.
    /// </summary>
    string? RenderSettings(string? existingContents, string? modelId);
}

/// <summary>An MCP server an agent should reach over HTTP, in the minimal shape an inline config needs.
/// Deliberately not <c>McpServerInfo</c>: that lives in the wire protocol, and Agnes.Abstractions takes no
/// dependency on it.</summary>
public sealed record InlineMcpServer(string Name, string Url, string? AuthorizationHeader = null);

/// <summary>
/// Optional capability an <see cref="IAgentAdapter"/> may implement to have its model catalogue probed
/// <b>in the environment the agent actually runs in</b>, rather than wherever the daemon happens to live.
/// The distinction is not academic: a CLI whose catalogue depends on credentials sees a different set of
/// models inside a sandbox than on the host, and at least one (OpenCode) responds to being asked for a
/// model it cannot reach by silently streaming from one it can — so the mismatch has to be detected rather
/// than trusted. <see cref="ProbeArguments"/> is the argv that prints the catalogue; null means the CLI
/// can't be asked and no verification is attempted.
/// </summary>
public interface IModelProbeAdapter
{
    /// <summary>Argv that makes this CLI print the models it can reach, or null when it has no such command.</summary>
    IReadOnlyList<string>? ProbeArguments { get; }

    /// <summary>Parses that command's stdout into the catalogue.</summary>
    IReadOnlyList<ModelInfo> ParseProbeOutput(string stdout);
}

/// <summary>Resolves an adapter's effective model catalog. Pure over its input, so the live-vs-static rule
/// lives in exactly one place: use the live-probed list when the adapter supplies one, else the static
/// fallback.</summary>
public static class ModelCatalog
{
    public static async Task<IReadOnlyList<ModelInfo>> ResolveAsync(IModelListingAdapter adapter, CancellationToken ct = default)
    {
        var live = await adapter.ListModelsAsync(ct).ConfigureAwait(false);
        return live ?? adapter.StaticModels;
    }
}
