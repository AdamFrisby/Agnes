using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agnes.Abstractions;

namespace Agnes.Agents.Copilot;

/// <summary>
/// Reads the models Copilot can actually reach. Copilot has no <c>models</c> subcommand — asking for one it
/// doesn't have just answers "not available" without naming the alternatives — and its catalogue depends on
/// the signed-in account's entitlements, so a hard-coded list would offer models the user cannot use and
/// hide the ones they can. The one place Copilot states the catalogue is the ACP handshake: the
/// <c>session/new</c> result carries <c>models.availableModels</c>. So the probe performs exactly that
/// handshake against <c>copilot --acp</c> and stops.
///
/// The parse is separated from the spawn so the boundary format is testable without a CLI: untyped JSON is
/// deserialized straight into the records below and never flows inward.
/// </summary>
public static class CopilotModelCatalog
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    // ---- boundary records: the slice of the ACP session/new response we read ----

    private sealed record RpcResponse(
        [property: JsonPropertyName("result")] NewSessionResult? Result);

    private sealed record NewSessionResult(
        [property: JsonPropertyName("models")] SessionModelState? Models);

    private sealed record SessionModelState(
        [property: JsonPropertyName("availableModels")] IReadOnlyList<AvailableModel>? AvailableModels);

    private sealed record AvailableModel(
        [property: JsonPropertyName("modelId")] string? ModelId,
        [property: JsonPropertyName("name")] string? Name);

    /// <summary>
    /// Parses the JSON-RPC response line for <c>session/new</c> into a model catalogue. Anything that isn't
    /// that — an error response, a truncated line, empty input — yields an empty list, which callers read as
    /// "couldn't determine" and fall back from. Never throws.
    /// </summary>
    public static IReadOnlyList<ModelInfo> Parse(string? sessionNewResponse)
    {
        if (string.IsNullOrWhiteSpace(sessionNewResponse))
        {
            return [];
        }

        RpcResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<RpcResponse>(sessionNewResponse, Options);
        }
        catch (JsonException)
        {
            return [];
        }

        var available = response?.Result?.Models?.AvailableModels;
        if (available is null)
        {
            return [];
        }

        var models = new List<ModelInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in available)
        {
            if (model.ModelId is not { Length: > 0 } id || !seen.Add(id))
            {
                continue;
            }

            models.Add(new ModelInfo(id, string.IsNullOrWhiteSpace(model.Name) ? id : model.Name));
        }

        return models;
    }

    /// <summary>
    /// Runs the ACP handshake against <paramref name="command"/> and returns the raw <c>session/new</c>
    /// response line, or null when the CLI is absent, not logged in, or doesn't answer in time. Returning
    /// null rather than throwing is deliberate: an unavailable CLI is a normal state and degrades to "no
    /// model picker", not to an error.
    /// </summary>
    public static async Task<string?> ProbeAsync(
        string command,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (!AgentCommand.IsOnPath(command))
        {
            return null;
        }

        // The probe must not outlive the user's patience for a model picker, and a wedged CLI would
        // otherwise leave a process behind.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        Process? process = null;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = command,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            await process.StandardInput.WriteLineAsync(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":1,"clientCapabilities":{"fs":{"readTextFile":false,"writeTextFile":false},"terminal":false}}}""")
                .ConfigureAwait(false);
            await process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);

            // Wait for the initialize reply before asking for a session: the catalogue lives on the
            // session/new result, and an agent that hasn't finished initializing may reject it.
            if (await ReadResponseAsync(process, id: 1, timeout.Token).ConfigureAwait(false) is null)
            {
                return null;
            }

            // Serialized rather than interpolated: a Windows path is full of backslashes that must be
            // escaped, and getting that wrong would send malformed JSON down the pipe.
            var cwd = JsonSerializer.Serialize(Directory.GetCurrentDirectory(), Options);
            await process.StandardInput.WriteLineAsync(
                """{"jsonrpc":"2.0","id":2,"method":"session/new","params":{"cwd":""" + cwd + ""","mcpServers":[]}}""")
                .ConfigureAwait(false);
            await process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);

            return await ReadResponseAsync(process, id: 2, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Any failure — CLI missing, protocol change, timeout, broken pipe — is "no catalogue".
            return null;
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception)
                {
                    // Already gone, or we can't signal it; nothing useful to do either way.
                }

                process.Dispose();
            }
        }
    }

    /// <summary>Reads newline-delimited JSON from the agent until the response to <paramref name="id"/>
    /// arrives. The agent interleaves <c>session/update</c> notifications with its replies, so lines are
    /// correlated by the JSON-RPC <c>id</c> — matched by parsing, not by substring, since a tool call id in
    /// a notification can look like anything. Null at end of stream.</summary>
    private static async Task<string?> ReadResponseAsync(Process process, int id, CancellationToken cancellationToken)
    {
        while (await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (MatchesId(line, id))
            {
                return line;
            }
        }

        return null;
    }

    /// <summary>Whether a JSON-RPC line is the reply to <paramref name="id"/>. Non-JSON lines (a banner, a
    /// stray log line) are simply not a match.</summary>
    private static bool MatchesId(string line, int id)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("id", out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out var parsed)
                && parsed == id;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
