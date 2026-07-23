using System.Text.Json;
using Agnes.Abstractions;
using Microsoft.Extensions.Logging;

namespace Agnes.Agents.Native;

/// <summary>
/// Reads sessions Claude Code created on its own (outside Agnes) from its on-disk transcripts. Claude keeps
/// one JSONL file per conversation at <c>&lt;home&gt;/.claude/projects/&lt;encoded-cwd&gt;/&lt;sessionId&gt;.jsonl</c>
/// (each line a stream-json-shaped record). This is a pure, tolerant reader over that boundary format: the
/// Claude-home base directory is a parameter so it's fully offline-testable and never assumed to be the real
/// <c>~/.claude</c>. Discovery never throws — an unreadable/missing/partly-malformed transcript just contributes
/// what it can (or nothing). Attaching hands back a live, read-only <see cref="TailingExternalSession"/>.
/// </summary>
public static class ClaudeCodeExternalSessions
{
    /// <summary>Claude encodes a working directory into its projects-dir name by replacing every
    /// non-alphanumeric character with a dash (so <c>/home/me/proj</c> → <c>-home-me-proj</c>). Mirrors the
    /// host's own <c>ClaudeTitle.EncodeCwd</c> — kept here so the adapter doesn't depend on the host.</summary>
    public static string EncodeWorkspace(string workspaceDirectory)
    {
        var chars = workspaceDirectory.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (!char.IsAsciiLetterOrDigit(chars[i]))
            {
                chars[i] = '-';
            }
        }

        return new string(chars);
    }

    /// <summary>The directory Claude stores this workspace's transcripts in, under <paramref name="claudeHome"/>.</summary>
    public static string ProjectDirectory(string claudeHome, string workspaceDirectory)
        => Path.Combine(claudeHome, ".claude", "projects", EncodeWorkspace(workspaceDirectory));

    /// <summary>Discovers Claude's own on-disk sessions for <paramref name="workspaceDirectory"/> under
    /// <paramref name="claudeHome"/>. Most-recent first. Empty (never throws) when the directory is absent or
    /// no transcript is readable.</summary>
    public static Task<IReadOnlyList<ExternalSessionInfo>> DiscoverAsync(string claudeHome, string workspaceDirectory, CancellationToken ct = default)
    {
        var sessions = new List<ExternalSessionInfo>();
        if (string.IsNullOrWhiteSpace(claudeHome) || string.IsNullOrWhiteSpace(workspaceDirectory))
        {
            return Task.FromResult<IReadOnlyList<ExternalSessionInfo>>(sessions);
        }

        var directory = ProjectDirectory(claudeHome, workspaceDirectory);
        if (!Directory.Exists(directory))
        {
            return Task.FromResult<IReadOnlyList<ExternalSessionInfo>>(sessions);
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*.jsonl");
        }
        catch (IOException)
        {
            return Task.FromResult<IReadOnlyList<ExternalSessionInfo>>(sessions);
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult<IReadOnlyList<ExternalSessionInfo>>(sessions);
        }

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (ReadSummary(file, workspaceDirectory) is { } info)
            {
                sessions.Add(info);
            }
        }

        return Task.FromResult<IReadOnlyList<ExternalSessionInfo>>(
            sessions.OrderByDescending(s => s.LastActivity).ToArray());
    }

    /// <summary>Opens a live, read-only tail over the transcript named by <paramref name="externalId"/> (the
    /// transcript's absolute path). The tailing session maps each JSONL line to Agnes events via the shared
    /// stream-json mapper and never writes back to Claude.</summary>
    public static ExternalSessionAttachment Attach(string externalId, ILogger logger)
    {
        var workspace = ReadWorkspace(externalId) ?? string.Empty;
        var session = new TailingExternalSession(externalId, new ClaudeCodeStreamMapper(), logger);
        return new ExternalSessionAttachment(session, workspace);
    }

    // Reads a transcript's metadata tolerantly: message count (user/assistant turns), first user message as a
    // preview, and the newest record timestamp (falling back to the file's last-write time). Returns null only
    // if the file can't be opened at all.
    private static ExternalSessionInfo? ReadSummary(string file, string workspaceDirectory)
    {
        var messageCount = 0;
        var preview = string.Empty;
        DateTimeOffset? lastActivity = null;
        string? recordedWorkspace = null;

        try
        {
            foreach (var line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                TranscriptRecord? record;
                try
                {
                    record = JsonSerializer.Deserialize<TranscriptRecord>(line, Options);
                }
                catch (JsonException)
                {
                    continue; // tolerate a malformed line — skip it.
                }

                if (record is null)
                {
                    continue;
                }

                if (record.Cwd is { Length: > 0 } cwd)
                {
                    recordedWorkspace ??= cwd;
                }

                if (record.Timestamp is { } ts && (lastActivity is null || ts > lastActivity))
                {
                    lastActivity = ts;
                }

                if (record.Type is "user" or "assistant")
                {
                    messageCount++;
                    if (preview.Length == 0 && record.Type == "user"
                        && ExtractText(record.Message) is { Length: > 0 } text)
                    {
                        preview = text.Length > 200 ? text[..200] : text;
                    }
                }
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        DateTimeOffset activity;
        try
        {
            activity = lastActivity ?? File.GetLastWriteTimeUtc(file);
        }
        catch (IOException)
        {
            activity = lastActivity ?? DateTimeOffset.UtcNow;
        }

        return new ExternalSessionInfo(
            ExternalId: file,
            AdapterId: ClaudeCodeNative.AdapterId,
            WorkspaceDirectory: recordedWorkspace ?? workspaceDirectory,
            Preview: preview,
            LastActivity: activity,
            MessageCount: messageCount);
    }

    // The workspace a transcript ran in, read from the first record's cwd (best-effort; the encoded folder name
    // is lossy and can't be reversed reliably).
    private static string? ReadWorkspace(string file)
    {
        try
        {
            foreach (var line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    if (JsonSerializer.Deserialize<TranscriptRecord>(line, Options)?.Cwd is { Length: > 0 } cwd)
                    {
                        return cwd;
                    }
                }
                catch (JsonException)
                {
                    // skip and keep looking.
                }
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    // Pulls readable text out of a transcript record's polymorphic message.content (a plain string, or an array
    // of blocks where text blocks carry the words). The message stays a JsonElement because its content field is
    // genuinely polymorphic at this boundary; nothing untyped flows inward.
    private static string ExtractText(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("content", out var content))
        {
            return string.Empty;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty;
        }

        if (content.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind == JsonValueKind.Object
                    && block.TryGetProperty("type", out var t) && t.ValueEquals("text")
                    && block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
                    && text.GetString() is { Length: > 0 } s)
                {
                    parts.Add(s);
                }
            }

            return string.Join("\n", parts);
        }

        return string.Empty;
    }

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    // Typed view over the fields we read from a Claude transcript line. message stays a JsonElement (its content
    // is polymorphic — string in one record, array in the next) and is turned into text immediately above.
    private sealed record TranscriptRecord
    {
        public string? Type { get; init; }

        public DateTimeOffset? Timestamp { get; init; }

        public string? Cwd { get; init; }

        public JsonElement Message { get; init; }
    }
}
