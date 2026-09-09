using System.Text.Json;
using System.Text.Json.Nodes;

namespace Agnes.Agents.Copilot;

/// <summary>
/// Points Copilot's built-in subagents at the model the session is actually running on, by writing
/// <c>subagents.agents.&lt;name&gt;.model</c> into Copilot's <c>settings.json</c>.
/// </summary>
/// <remarks>
/// <para>Three of Copilot's shipped agent definitions pin a model in the YAML itself — verified in
/// v1.0.80's <c>definitions/</c>: <c>explore</c> and <c>task</c> declare <c>claude-haiku-4.5</c>,
/// <c>research</c> declares <c>claude-sonnet-4.6</c>. Under a GitHub subscription those ids resolve and the
/// choice is a good one (cheap, fast, and picked by people who know the agents). Under <b>BYOK</b> they
/// resolve to nothing: the provider is whatever endpoint the operator pointed Copilot at, and it has never
/// heard of <c>claude-haiku-4.5</c>. So dispatching to those agents is dispatching to a model that does not
/// exist, and only the agents that pin nothing — <c>general-purpose</c> and friends, which inherit the
/// session model — can run at all. That is why a BYOK session appears to do everything in one context.</para>
///
/// <para>Copilot exposes exactly one override for this, and it is a settings file rather than argv or
/// environment: <c>subagents.agents.&lt;name&gt;</c>, whose members are <c>model</c> (string),
/// <c>effortLevel</c> (string), <c>contextTier</c> (<c>inherit</c>|<c>default</c>|<c>long_context</c>) and
/// <c>autoInvoke</c> (boolean). Note that only <c>contextTier</c> takes <c>"inherit"</c> — there is no
/// inherit-the-session-model spelling, so the model has to be named, which is precisely why this has to be
/// rewritten whenever the session's model changes.</para>
///
/// <para>The file belongs to Copilot, not to Agnes, and a real one carries settings a person chose
/// (<c>allowedUrls</c>, <c>effortLevel</c>, a preferred <c>model</c>). So this merges: it parses to
/// <see cref="JsonNode"/> — untyped on purpose, at a genuine external boundary, because the point is to
/// carry through keys whose schema we do not own and must not drop — and touches nothing but the
/// <c>model</c> of the agents named. Everything else survives byte-for-byte in its original order.</para>
/// </remarks>
public static class CopilotSubagentSettings
{
    /// <summary>Where Copilot keeps the file, relative to the agent's home directory.</summary>
    public const string HomeRelativePath = ".copilot/settings.json";

    /// <summary>
    /// The built-in agents that pin a model in their shipped definition, and so are unreachable on a
    /// provider that does not serve it. Agents that pin nothing already inherit the session's model and are
    /// deliberately left alone — naming them here would replace an inherit-whatever-runs with a hard-coded
    /// id that goes stale the moment the session switches model.
    /// </summary>
    public static IReadOnlyList<string> ModelPinningAgents { get; } = ["explore", "task", "research"];

    private static readonly JsonSerializerOptions Write = new() { WriteIndented = true };

    /// <summary>The file Copilot reads on this machine. <c>COPILOT_HOME</c> overrides the location, exactly
    /// as the CLI documents and as <see cref="CopilotNativeMcpConfig.ConfigPath"/> already honours.</summary>
    public static string SettingsPath()
    {
        var home = Environment.GetEnvironmentVariable("COPILOT_HOME");
        return string.IsNullOrWhiteSpace(home)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), HomeRelativePath)
            : Path.Combine(home, "settings.json");
    }

    /// <summary>
    /// The contents <paramref name="existing"/> should have for a session running <paramref name="modelId"/>,
    /// or <c>null</c> when there is nothing to do — no model chosen, no agents to point, or the file already
    /// says exactly this. Returning null rather than an identical string is what keeps a relaunch from
    /// rewriting a file it did not change.
    /// </summary>
    /// <param name="existing">The file's current contents, or null when it does not exist yet.</param>
    /// <param name="modelId">The session's model, as Copilot names it.</param>
    /// <param name="agentNames">Which agents to point; defaults to <see cref="ModelPinningAgents"/>.</param>
    public static string? Apply(string? existing, string? modelId, IReadOnlyList<string>? agentNames = null)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return null;
        }

        var names = agentNames ?? ModelPinningAgents;
        if (names.Count == 0)
        {
            return null;
        }

        // A file we cannot parse is a file we cannot merge into. Overwriting it would discard settings a
        // person chose over a transient syntax error, so leave it exactly as it is and say nothing.
        var root = Parse(existing);
        if (root is null)
        {
            return null;
        }

        var subagents = Child(root, "subagents");
        var agents = Child(subagents, "agents");

        var changed = false;
        foreach (var name in names)
        {
            var agent = Child(agents, name);
            if (agent["model"]?.GetValue<string>() == modelId)
            {
                continue;
            }

            agent["model"] = modelId;
            changed = true;
        }

        return changed ? root.ToJsonString(Write) : null;
    }

    /// <summary>An existing object at <paramref name="key"/>, or a fresh one put there. A non-object sitting
    /// in the way is replaced: it cannot hold what has to go here, and Copilot would reject it too.</summary>
    private static JsonObject Child(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject existing)
        {
            return existing;
        }

        var created = new JsonObject();
        parent[key] = created;
        return created;
    }

    private static JsonObject? Parse(string? existing)
    {
        if (string.IsNullOrWhiteSpace(existing))
        {
            return [];
        }

        try
        {
            return JsonNode.Parse(existing) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
