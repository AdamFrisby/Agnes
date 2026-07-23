using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agnes.Cli;

/// <summary>One reachable-or-not host in the <c>machines</c> listing. <see cref="Id"/> is the registry name,
/// the token a <c>--host</c> prefix resolves against; <see cref="DisplayName"/>/<see cref="Version"/> are what
/// the host reports when reachable.</summary>
public sealed record MachineJson(string Id, string Url, bool Reachable, string? DisplayName, string? Version);

/// <summary>The <c>status</c> view of a session.</summary>
public sealed record StatusJson(string SessionId, string Adapter, string WorkingDirectory, string State, long HeadSequence);

/// <summary>The <c>spawn</c> result (id emitted as JSON when <c>--json</c> is set).</summary>
public sealed record SpawnJson(string SessionId, string Adapter, string Host);

/// <summary>
/// Pipeline-clean JSON rendering for the read commands: a single, minified line per object so
/// <c>agnes-agent status &lt;id&gt; --json | jq .state</c> works without scraping human text. Kept as pure
/// string-returning functions over typed records (never a <c>JsonElement</c> bag) so output shape is
/// compiler-checked and unit-testable.
/// </summary>
public static class JsonOutput
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static string Render(MachineJson machine) => JsonSerializer.Serialize(machine, Options);

    public static string Render(IReadOnlyList<MachineJson> machines) => JsonSerializer.Serialize(machines, Options);

    public static string Render(StatusJson status) => JsonSerializer.Serialize(status, Options);

    public static string Render(SpawnJson spawn) => JsonSerializer.Serialize(spawn, Options);
}
