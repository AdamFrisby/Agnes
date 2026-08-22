using System.Net.Http.Json;
using System.Text.Json;
using Agnes.Abstractions;
using Microsoft.Extensions.Logging;

namespace Agnes.Agents.OpenCode.Native;

/// <summary>How to launch and drive OpenCode through its native HTTP server.</summary>
public sealed record OpenCodeNativeOptions
{
    /// <summary>The OpenCode executable (resolved on PATH by default).</summary>
    public string Command { get; init; } = "opencode";

    /// <summary>Extra environment for the server process (inline config, provider auth).</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>How long to wait for the server to announce its address before giving up.</summary>
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// OpenCode driven through <c>opencode serve</c> — its own HTTP + SSE API — rather than ACP.
/// </summary>
/// <remarks>
/// A second adapter beside the ACP one, the way Claude Code has both an ACP bridge and a native stream-json
/// adapter. It exists because the native surface answers questions ACP structurally cannot: 28 event types
/// against 6, including a failed step and a provider retry, which over ACP arrive as an ordinary turn end
/// and nothing at all. It also switches model without relaunching, and interrupts for real.
///
/// The cost is honest: this is OpenCode's internal API, not a versioned spec, and parts of it are marked
/// experimental. The ACP adapter stays the stable path; this one is opt-in per session.
/// </remarks>
public sealed class OpenCodeNativeAgent : IAgentAdapter, IModelEnvironmentAdapter, IModelListingAdapter
{
    public const string AdapterId = "opencode-native";

    private readonly OpenCodeNativeOptions _options;
    private readonly ILoggerFactory _loggerFactory;

    public OpenCodeNativeAgent(OpenCodeNativeOptions options, ILoggerFactory loggerFactory)
    {
        _options = options;
        _loggerFactory = loggerFactory;
    }

    public static AgentDescriptor Descriptor { get; } = new()
    {
        Id = AdapterId,
        DisplayName = "OpenCode (native)",
    };

    AgentDescriptor IAgentAdapter.Descriptor => Descriptor;

    public bool IsAvailable() => AgentCommand.IsOnPath(_options.Command);

    // Model selection and the catalogue work exactly as they do for the ACP adapter: OpenCode reads both
    // from its config, whichever protocol drives it.
    public IReadOnlyList<ModelInfo> StaticModels => [];

    public Task<IReadOnlyList<ModelInfo>?> ListModelsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ModelInfo>?>(null);

    public IReadOnlyDictionary<string, string> InlineConfigEnvironment(string? modelId, IReadOnlyList<InlineMcpServer> mcpServers)
        => OpenCodeAgent.BuildConfigEnvironment(modelId, mcpServers);

    public static OpenCodeNativeAgent Create(ILoggerFactory loggerFactory, OpenCodeNativeOptions? options = null)
        => new(options ?? new OpenCodeNativeOptions(), loggerFactory);

    public async Task<IAgentSession> StartSessionAsync(AgentSessionOptions options, CancellationToken cancellationToken = default)
    {
        if (options.Sandbox is not null)
        {
            // Deliberately explicit rather than silently degrading: the server binds loopback inside the
            // guest, and Agnes has no port-forward for it. Driving it from the host would need the guest's
            // bridge address plumbed through ISandbox, which doesn't exist yet.
            throw new NotSupportedException(
                "The native OpenCode adapter doesn't support sandboxed sessions yet — its server binds "
                + "loopback inside the guest and Agnes has no route to it. Use the 'opencode' (ACP) adapter "
                + "for sandboxed work.");
        }

        var logger = _loggerFactory.CreateLogger<OpenCodeNativeAgent>();
        var env = new Dictionary<string, string>(_options.Environment ?? new Dictionary<string, string>());
        foreach (var (k, v) in InlineConfigEnvironment(options.ModelId, []))
        {
            env[k] = v;
        }

        var server = await OpenCodeServer.StartAsync(
            _options.Command, options.WorkingDirectory, env, logger, _options.StartupTimeout, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var http = new HttpClient { BaseAddress = server.BaseAddress, Timeout = Timeout.InfiniteTimeSpan };
            var sessionId = await CreateSessionAsync(http, cancellationToken).ConfigureAwait(false);
            var session = new OpenCodeNativeSession(sessionId, http, _loggerFactory.CreateLogger<OpenCodeNativeSession>());

            // Set the model on the session itself rather than leaning on the inline config default. Here the
            // model is a property of the session, and a session created without one sends its provider call
            // unresolved — which surfaces as a provider 401 rather than anything about the model.
            if (options.ModelId is { Length: > 0 } modelId)
            {
                await session.SetModelAsync(modelId, cancellationToken).ConfigureAwait(false);
            }

            return new ServerOwningSession(session, server, http);
        }
        catch
        {
            await server.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<string> CreateSessionAsync(HttpClient http, CancellationToken cancellationToken)
    {
        var response = await http.PostAsJsonAsync("/api/session", new { }, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);

        // The v2 routes wrap their result in a "data" envelope; the older ones don't. Accept either rather
        // than pinning one, since both route families are live and the migration is visibly in progress.
        return SessionIdOf(doc.RootElement)
               ?? throw new InvalidOperationException($"opencode did not return a session id: {body}");
    }

    private static string? SessionIdOf(JsonElement root)
    {
        if (root.TryGetProperty("id", out var direct) && direct.GetString() is { Length: > 0 } id)
        {
            return id;
        }

        return root.TryGetProperty("data", out var data)
               && data.ValueKind == JsonValueKind.Object
               && data.TryGetProperty("id", out var nested)
               && nested.GetString() is { Length: > 0 } nestedId
            ? nestedId
            : null;
    }

    /// <summary>Ties the session's lifetime to the server it runs on, so disposing one stops the other —
    /// an orphaned <c>opencode serve</c> would hold the project directory and a provider connection open.</summary>
    private sealed class ServerOwningSession(OpenCodeNativeSession inner, OpenCodeServer server, HttpClient http) : IAgentSession
    {
        public string AgentSessionId => inner.AgentSessionId;
        public IReadOnlyList<SessionMode> Modes => inner.Modes;
        public string? CurrentModeId => inner.CurrentModeId;
        public System.Threading.Channels.ChannelReader<SessionEvent> Events => inner.Events;

        public Task<StopReason> PromptAsync(IReadOnlyList<ContentBlock> content, CancellationToken cancellationToken = default)
            => inner.PromptAsync(content, cancellationToken);

        public Task CancelAsync(CancellationToken cancellationToken = default) => inner.CancelAsync(cancellationToken);

        public Task RespondToPermissionAsync(string requestId, string optionId, CancellationToken cancellationToken = default)
            => inner.RespondToPermissionAsync(requestId, optionId, cancellationToken);

        public Task SetModeAsync(string modeId, CancellationToken cancellationToken = default)
            => inner.SetModeAsync(modeId, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            http.Dispose();
            await server.DisposeAsync().ConfigureAwait(false);
        }
    }
}
