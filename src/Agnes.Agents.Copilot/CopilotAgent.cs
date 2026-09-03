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

    /// <summary>
    /// Tools withheld from the model (<c>--excluded-tools</c>). Empty by default.
    ///
    /// <para>This exists because of one specific incompatibility. Copilot offers <c>apply_patch</c> as an
    /// OpenAI <b>custom tool with a Lark grammar</b> — <c>"type": "custom"</c> rather than
    /// <c>"type": "function"</c> — and a local OpenAI-compatible server that implements only the function
    /// form rejects the whole request: <c>Failed to parse tools: Unsupported tool type</c>. Excluding it
    /// costs one editing tool and is the difference between a local model working and not starting at
    /// all. See <see cref="CopilotLocalCompatibility"/>.</para>
    /// </summary>
    public IReadOnlyList<string> ExcludedTools { get; init; } = [];

    /// <summary>
    /// Runs Copilot with no network access beyond the model provider (<c>COPILOT_OFFLINE</c>): no GitHub
    /// authentication, telemetry, web tools, GitHub MCP server or auto-update.
    ///
    /// <para>This is what "local models with no GitHub credentials" actually means, and it is worth
    /// setting deliberately rather than relying on BYOK alone: with a provider configured but offline
    /// mode off, Copilot still reaches GitHub for everything that is not inference.</para>
    ///
    /// <para>Copilot requires a provider for this, so it is ignored unless <see cref="Provider"/> is
    /// configured — turning it on without one would produce a CLI that can neither authenticate nor
    /// infer.</para>
    /// </summary>
    public bool Offline { get; init; }

    /// <summary>
    /// Which built-in subagents get pointed at the session's model. Defaults to the ones whose shipped
    /// definition pins a model (<c>explore</c>, <c>task</c>, <c>research</c>) — see
    /// <see cref="CopilotSubagentSettings"/>. Empty disables the rewrite entirely, for an operator who would
    /// rather Copilot's settings file were left alone.
    /// </summary>
    public IReadOnlyList<string> SubagentNames { get; init; } = CopilotSubagentSettings.ModelPinningAgents;

    /// <summary>
    /// Starts each session in Copilot's fleet mode — parallel subagent execution, what its own UI offers as
    /// "build on autopilot + /fleet". Off by default because it is a real behavioural change: a fleet
    /// session spends far more, and an operator should choose it.
    /// </summary>
    /// <remarks>
    /// Copilot exposes this <b>only</b> as the in-session <c>/fleet</c> command — there is no launch flag,
    /// no environment variable, and no settings key, and the ACP mode list it advertises is just Agent /
    /// Plan / Autopilot. So Agnes turns it on the one way that exists: by invoking the command, which
    /// Copilot's ACP surface accepts as a prompt (verified against v1.0.80, which advertises <c>fleet</c>
    /// among 32 commands). Worth pairing with <see cref="SubagentNames"/> under BYOK — a fleet of subagents
    /// pinned to models the provider does not serve is a fleet that cannot sail.
    /// </remarks>
    public bool FleetMode { get; init; }

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
    /// <remarks>
    /// <para>Sandboxing changes what the path check is worth. Copilot verifies filesystem access separately
    /// from its tool prompt, so <c>--allow-all-tools</c> alone leaves it asking "access paths outside
    /// trusted directories?" — correct on an unsandboxed host, where that check is the only filesystem
    /// boundary left once the per-tool prompt is waived. Inside a VM it guards nothing the VM does not,
    /// and it asks constantly: Copilot keeps its own session state (subagent briefs and the like) outside
    /// the working directory, so ordinary work trips it repeatedly. One live session collected sixteen such
    /// prompts in an hour, in a session whose whole point was not to be interrupted.</para>
    ///
    /// <para><c>--allow-all-urls</c> is still withheld either way: network egress is a different boundary,
    /// and the sandbox's proxy and credential broker are what govern it.</para>
    /// </remarks>
    public static IReadOnlyList<string> BuildPermissionArguments(PermissionStance stance)
        => (stance.SkipPermissions, stance.Sandboxed) switch
        {
            (false, _) => [],
            (true, false) => ["--allow-all-tools"],
            (true, true) => ["--allow-all-tools", "--allow-all-paths"],
        };

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

    /// <summary>
    /// The full environment for a launch: the BYOK provider variables, plus offline mode when asked for
    /// and actually usable.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildEnvironment(CopilotOptions options)
    {
        var env = new Dictionary<string, string>(BuildProviderEnvironment(options.Provider), StringComparer.Ordinal);

        // Copilot refuses offline mode without a provider — with nothing to infer against it could
        // neither authenticate nor answer — so the flag is honoured only when BYOK is configured rather
        // than passed through to fail at launch.
        if (options.Offline && options.Provider is { IsConfigured: true })
        {
            env["COPILOT_OFFLINE"] = "true";
        }

        return env;
    }

    /// <summary>
    /// The launch argv: the configured arguments plus one <c>--excluded-tools</c> entry per withheld
    /// tool. Copilot takes the flag repeatably rather than as a list, so each is passed separately.
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(CopilotOptions options)
    {
        var excluded = options.ExcludedTools.Where(t => !string.IsNullOrWhiteSpace(t)).ToArray();
        if (excluded.Length == 0)
        {
            return options.Arguments;
        }

        var arguments = new List<string>(options.Arguments);
        foreach (var tool in excluded)
        {
            arguments.Add("--excluded-tools");
            arguments.Add(tool.Trim());
        }

        return arguments;
    }

    public static AcpLaunchSpec CreateLaunchSpec(CopilotOptions? options = null)
    {
        options ??= new CopilotOptions();
        var arguments = BuildArguments(options);
        var lister = options.ModelLister
            ?? (ct => CopilotModelCatalog.ProbeAsync(options.Command, arguments, ct));
        return new AcpLaunchSpec
        {
            Command = options.Command,
            Arguments = arguments,
            // Bare `copilot` is the interactive console: ACP is the flagged mode, so the console is the
            // command with none of the ACP argv. Explicitly empty, not null — null would mean "no console".
            ConsoleArguments = [],
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
            InlineConfig = (_, _) => BuildEnvironment(options),
            // Copilot can't be asked to print its catalogue from argv (an unknown --model answers "not
            // available" without listing the alternatives), so there is no in-sandbox verification probe.
            StartupCommands = options.FleetMode ? ["/fleet"] : [],
        };
    }

    public static CopilotAcpAdapter Create(ILoggerFactory loggerFactory, CopilotOptions? options = null)
    {
        options ??= new CopilotOptions();
        return new CopilotAcpAdapter(CreateLaunchSpec(options), loggerFactory, options);
    }
}

/// <summary>
/// Copilot's ACP adapter: the generic <see cref="AcpAgentAdapter"/> plus the two things that are Copilot's
/// own — reading the MCP servers its CLI already has configured, and naming its interactive login command
/// (which Copilot itself advertises in the ACP handshake as the <c>copilot-login</c> auth method).
/// </summary>
public sealed class CopilotAcpAdapter : AcpAgentAdapter, IMcpDiscoveryAdapter, IModelSettingsAdapter
{
    private readonly string _command;
    private readonly CopilotOptions _options;

    public CopilotAcpAdapter(AcpLaunchSpec spec, ILoggerFactory loggerFactory, CopilotOptions? options = null)
        : base(spec, loggerFactory)
    {
        _command = spec.Command;
        _options = options ?? new CopilotOptions();
    }

    /// <inheritdoc />
    public string SettingsFilePath => CopilotSubagentSettings.HomeRelativePath;

    /// <summary>
    /// Points Copilot's model-pinning subagents at the session's model — but only under BYOK.
    /// </summary>
    /// <remarks>
    /// On a GitHub subscription the pinned ids resolve and were chosen deliberately: <c>explore</c> and
    /// <c>task</c> run on a small fast model precisely so they stay cheap, and overriding that with
    /// whatever the session happens to be on would make every subagent as expensive as the main one.
    /// Under BYOK the same ids resolve to nothing, so the choice is not between two models but between the
    /// session's model and no subagents at all. That is the whole of the difference, and it is why the
    /// rewrite is gated rather than unconditional.
    /// </remarks>
    public string? RenderSettings(string? existingContents, string? modelId)
        => _options.Provider?.IsConfigured == true
            ? CopilotSubagentSettings.Apply(existingContents, modelId, _options.SubagentNames)
            : null;

    public Task<IReadOnlyList<NativeMcpServer>> DetectNativeConfigAsync(string workspaceDirectory, CancellationToken ct = default)
        => CopilotNativeMcpConfig.DetectAsync(workspaceDirectory, ct);

    /// <summary>Copilot logs in interactively with <c>copilot login</c>, run through the shared CLI-fallback
    /// terminal. No <c>GetAuthStatusAsync</c> override: Copilot keeps its credentials somewhere Agnes has no
    /// documented way to read, and a confidently-wrong "not logged in" badge is worse than none.</summary>
    public ProviderLoginCommand? GetInteractiveLoginCommand() => new(_command, ["login"]);
}
