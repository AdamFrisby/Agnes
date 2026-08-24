using Agnes.Abstractions;
using Agnes.Acp;
using Microsoft.Extensions.Logging;

namespace Agnes.Agents.Copilot;

/// <summary>How to launch GitHub Copilot CLI as an ACP server (<c>copilot --acp</c>).</summary>
public sealed record CopilotOptions
{
    /// <summary>The Copilot executable (resolved on PATH by default).</summary>
    public string Command { get; init; } = "copilot";

    /// <summary>Arguments that start Copilot as an ACP server over stdio.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = ["--acp"];

    /// <summary>Extra environment variables for the agent.</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>Bring-your-own-key provider configuration, or null to use GitHub's own model routing.</summary>
    public CopilotProviderOptions? Provider { get; init; }

    /// <summary>Performs the model-catalogue handshake and returns the raw <c>session/new</c> response line,
    /// or null when the CLI can't be asked. Injectable so the catalogue parsing is testable without
    /// spawning a process; null uses the real CLI.</summary>
    public Func<CancellationToken, Task<string?>>? ModelLister { get; init; }
}

/// <summary>
/// Agent plugin for GitHub Copilot CLI. Copilot ships native ACP (<c>copilot --acp</c>, verified against
/// v1.0.78: protocol v1, <c>loadSession</c>, <c>session/request_permission</c>), so this is a launch
/// descriptor over the generic <see cref="AcpAgentAdapter"/> — the same shape as the OpenCode plugin. What
/// is Copilot-specific is the permission stance, the BYOK environment, and where its model catalogue lives.
/// </summary>
public static class CopilotAgent
{
    public const string AdapterId = "copilot";

    public static AgentDescriptor Descriptor { get; } = new()
    {
        Id = AdapterId,
        DisplayName = "GitHub Copilot CLI",
    };

    /// <summary>
    /// The permission stance. Copilot's default in ACP mode is the one Agnes wants: every tool call arrives
    /// as <c>session/request_permission</c> and the user answers it, so an attended session needs no flag.
    /// An autonomous session (the user's explicit opt-in, the same gate
    /// <c>--dangerously-skip-permissions</c> sits behind for Claude Code) gets <c>--allow-all-tools</c>.
    ///
    /// Deliberately <b>not</b> <c>--allow-all</c> / <c>--yolo</c>: those also imply
    /// <c>--allow-all-paths</c> and <c>--allow-all-urls</c>, which drop Copilot's own path verification and
    /// let the agent reach any URL. Skipping the per-tool prompt is what the user asked for; discarding the
    /// filesystem boundary as well is not, and it is exactly the boundary that still matters on an
    /// unsandboxed host.
    /// </summary>
    public static IReadOnlyList<string> BuildPermissionArguments(bool skipPermissions)
        => skipPermissions ? ["--allow-all-tools"] : [];

    /// <summary>Copilot selects a model with <c>--model &lt;id&gt;</c>.</summary>
    public static IReadOnlyList<string> BuildModelArguments(string modelId) => ["--model", modelId];

    /// <summary>Copilot loads an extra MCP config with <c>--additional-mcp-config &lt;json|@file&gt;</c>; the
    /// <c>@</c> prefix is what makes it read a path rather than parse the argument as JSON. It augments the
    /// user's own <c>~/.copilot/mcp-config.json</c> for the session rather than replacing it. The file Agnes
    /// materializes is in Claude Code's <c>{"mcpServers": {…}}</c> shape, which Copilot reads unchanged
    /// (verified against v1.0.78 for both stdio and http entries).</summary>
    public static IReadOnlyList<string> BuildMcpConfigArguments(string path) => ["--additional-mcp-config", "@" + path];

    /// <summary>
    /// Renders BYOK settings to the environment variables Copilot reads. Empty when
    /// <paramref name="provider"/> is null or has no base URL: BYOK is inactive until
    /// <c>COPILOT_PROVIDER_BASE_URL</c> is set, and emitting the rest without it would be noise the CLI
    /// ignores. Pure, so the mapping is unit-testable without launching anything.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildProviderEnvironment(CopilotProviderOptions? provider)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (provider is not { IsConfigured: true } byok || byok.BaseUrl is not { } baseUrl)
        {
            return env;
        }

        env["COPILOT_PROVIDER_BASE_URL"] = baseUrl;
        env["COPILOT_PROVIDER_TYPE"] = byok.Type switch
        {
            CopilotProviderType.Azure => "azure",
            CopilotProviderType.Anthropic => "anthropic",
            _ => "openai",
        };
        env["COPILOT_PROVIDER_WIRE_API"] = byok.WireApi == CopilotWireApi.Responses ? "responses" : "completions";
        env["COPILOT_PROVIDER_TRANSPORT"] = byok.Transport == CopilotTransport.WebSockets ? "websockets" : "http";

        // Bearer token wins over the API key inside Copilot, so pass only what was actually configured
        // rather than both — two credentials for one endpoint is an ambiguity, not a fallback.
        if (byok.BearerToken is { Length: > 0 } bearer)
        {
            env["COPILOT_PROVIDER_BEARER_TOKEN"] = bearer;
        }
        else if (byok.ApiKey is { Length: > 0 } apiKey)
        {
            env["COPILOT_PROVIDER_API_KEY"] = apiKey;
        }

        Set("COPILOT_PROVIDER_AZURE_API_VERSION", byok.AzureApiVersion);
        Set("COPILOT_MODEL", byok.Model);
        Set("COPILOT_PROVIDER_MODEL_ID", byok.ModelId);
        Set("COPILOT_PROVIDER_WIRE_MODEL", byok.WireModel);
        Set("COPILOT_PROVIDER_MAX_PROMPT_TOKENS", byok.MaxPromptTokens?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Set("COPILOT_PROVIDER_MAX_OUTPUT_TOKENS", byok.MaxOutputTokens?.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // Copilot parses these as newline-separated "Name: Value" pairs.
        var headers = byok.Headers.Where(h => !string.IsNullOrWhiteSpace(h)).ToArray();
        if (headers.Length > 0)
        {
            env["COPILOT_PROVIDER_HEADERS"] = string.Join('\n', headers);
        }

        return env;

        void Set(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                env[key] = value;
            }
        }
    }

    public static AcpLaunchSpec CreateLaunchSpec(CopilotOptions? options = null)
    {
        options ??= new CopilotOptions();
        var lister = options.ModelLister
            ?? (ct => CopilotModelCatalog.ProbeAsync(options.Command, options.Arguments, ct));
        return new AcpLaunchSpec
        {
            Command = options.Command,
            Arguments = options.Arguments,
            Environment = options.Environment,
            Descriptor = Descriptor,
            // No static catalogue: which models Copilot offers depends on the account's entitlements, and
            // the ids move fast enough that a baked-in list would be wrong within a release. Live probe
            // only, degrading to "no picker".
            LiveModelProbe = async ct => CopilotModelCatalog.Parse(await lister(ct).ConfigureAwait(false)),
            ModelArguments = BuildModelArguments,
            PermissionArguments = BuildPermissionArguments,
            McpConfigArguments = BuildMcpConfigArguments,
            // The model travels on argv (--model), which the sandbox wrapper carries into the guest with the
            // rest of the command, so this axis carries only BYOK — the settings Copilot exposes NOWHERE
            // else. Threading the model here too would give one choice two sources of truth.
            InlineConfig = (_, _) => BuildProviderEnvironment(options.Provider),
            // Copilot can't be asked to print its catalogue from argv (an unknown --model answers "not
            // available" without listing the alternatives), so there is no in-sandbox verification probe.
        };
    }

    public static CopilotAcpAdapter Create(ILoggerFactory loggerFactory, CopilotOptions? options = null)
        => new(CreateLaunchSpec(options), loggerFactory);
}

/// <summary>
/// Copilot's ACP adapter: the generic <see cref="AcpAgentAdapter"/> plus the two things that are Copilot's
/// own — reading the MCP servers its CLI already has configured, and naming its interactive login command
/// (which Copilot itself advertises in the ACP handshake as the <c>copilot-login</c> auth method).
/// </summary>
public sealed class CopilotAcpAdapter : AcpAgentAdapter, IMcpDiscoveryAdapter
{
    private readonly string _command;

    public CopilotAcpAdapter(AcpLaunchSpec spec, ILoggerFactory loggerFactory) : base(spec, loggerFactory)
    {
        _command = spec.Command;
    }

    public Task<IReadOnlyList<NativeMcpServer>> DetectNativeConfigAsync(string workspaceDirectory, CancellationToken ct = default)
        => CopilotNativeMcpConfig.DetectAsync(workspaceDirectory, ct);

    /// <summary>Copilot logs in interactively with <c>copilot login</c>, run through the shared CLI-fallback
    /// terminal. No <c>GetAuthStatusAsync</c> override: Copilot keeps its credentials somewhere Agnes has no
    /// documented way to read, and a confidently-wrong "not logged in" badge is worse than none.</summary>
    public ProviderLoginCommand? GetInteractiveLoginCommand() => new(_command, ["login"]);
}
