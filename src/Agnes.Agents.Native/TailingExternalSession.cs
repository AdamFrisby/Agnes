using System.Text.Json;
using System.Threading.Channels;
using Agnes.Abstractions;
using Microsoft.Extensions.Logging;

namespace Agnes.Agents.Native;

/// <summary>
/// A read-only <see cref="IAgentSession"/> that <b>tails a coding CLI's own on-disk transcript</b> (a JSONL
/// file) and re-emits each line as a normalized <see cref="SessionEvent"/> via a shared
/// <see cref="INativeStreamMapper"/> — the same mapping used for the live stream-json path, so a watched
/// external session flows through Agnes's snapshot/tail/multi-client machinery like any other. It follows the
/// file as it grows with a bounded poll loop (no FileSystemWatcher dependency). It is watch-only: prompt,
/// cancel and permission responses are deliberate no-ops so the underlying CLI is never disturbed.
/// </summary>
internal sealed class TailingExternalSession : IAgentSession
{
    // On-disk transcript lines whose message content is a plain string are user prompts (the live stream-json
    // shape the mapper handles uses content arrays); we surface those directly and defer everything else
    // (assistant text, tool calls, tool results) to the shared mapper.
    private readonly string _path;
    private readonly INativeStreamMapper _mapper;
    private readonly ILogger _logger;
    private readonly TimeSpan _pollInterval;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<SessionEvent> _events =
        Channel.CreateUnbounded<SessionEvent>(new UnboundedChannelOptions { SingleReader = true });

    // Index of the next transcript line to process. A trailing line that doesn't yet parse (a partial write in
    // progress) is left un-consumed so the next poll retries it once complete.
    private int _consumedLines;

    public TailingExternalSession(string path, INativeStreamMapper mapper, ILogger logger, TimeSpan? pollInterval = null)
    {
        _path = path;
        _mapper = mapper;
        _logger = logger;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(500);
        AgentSessionId = Path.GetFileNameWithoutExtension(path) is { Length: > 0 } stem ? stem : Guid.NewGuid().ToString("n");
        _ = Task.Run(TailLoopAsync);
    }

    public string AgentSessionId { get; }

    public ChannelReader<SessionEvent> Events => _events.Reader;

    // Watch-only: never write anything back to the underlying CLI.
    public Task<StopReason> PromptAsync(IReadOnlyList<ContentBlock> content, CancellationToken cancellationToken = default)
        => Task.FromResult(StopReason.EndTurn);

    public Task CancelAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RespondToPermissionAsync(string requestId, string optionId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    private async Task TailLoopAsync()
    {
        // The channel is intentionally never completed by the loop (only on dispose) so the host's event pump
        // stays open for the life of the watch and never mistakes end-of-file for the agent process dying.
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                DrainNewLines();
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Transient read error tailing external transcript {Path}", _path);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogDebug(ex, "Access error tailing external transcript {Path}", _path);
            }

            try
            {
                await Task.Delay(_pollInterval, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void DrainNewLines()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        // Re-read the whole file each poll (transcripts are modest) and skip lines already consumed. Opened
        // share-read/write so a live CLI appending to the same file isn't blocked.
        string[] lines;
        using (var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream))
        {
            var all = new List<string>();
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                all.Add(line);
            }

            lines = all.ToArray();
        }

        for (var i = _consumedLines; i < lines.Length; i++)
        {
            var text = lines[i];
            if (string.IsNullOrWhiteSpace(text))
            {
                _consumedLines = i + 1;
                continue;
            }

            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(text);
                root = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                // A malformed line that is NOT the last is complete-but-bad → skip it (tolerant). The last line
                // may be a partial write in progress → stop here and retry it on the next poll.
                if (i == lines.Length - 1)
                {
                    break;
                }

                _consumedLines = i + 1;
                continue;
            }

            foreach (var @event in MapLine(root))
            {
                _events.Writer.TryWrite(@event);
            }

            _consumedLines = i + 1;
        }
    }

    private IEnumerable<SessionEvent> MapLine(JsonElement line)
    {
        if (GetString(line, "type") == "user" && TryGetUserText(line, out var userText))
        {
            return [new MessageChunkEvent(MessageRole.User, new TextContent(userText))];
        }

        return _mapper.ToEvents(line);
    }

    // A user prompt as Claude stores it on disk: message.content is a plain string, or an array of text blocks
    // (no tool_result). A tool_result array is left to the shared mapper, so this returns false for those.
    private static bool TryGetUserText(JsonElement line, out string text)
    {
        text = string.Empty;
        if (!line.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out var content))
        {
            return false;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            text = content.GetString() ?? string.Empty;
            return true;
        }

        if (content.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var block in content.EnumerateArray())
            {
                var kind = GetString(block, "type");
                if (kind == "tool_result")
                {
                    return false; // a tool result — the shared mapper turns it into a ToolCallUpdateEvent.
                }

                if (kind == "text" && GetString(block, "text") is { Length: > 0 } t)
                {
                    parts.Add(t);
                }
            }

            if (parts.Count > 0)
            {
                text = string.Join("\n", parts);
                return true;
            }
        }

        return false;
    }

    private static string? GetString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _events.Writer.TryComplete();
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }
}
