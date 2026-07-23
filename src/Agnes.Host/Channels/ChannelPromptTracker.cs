using System.Collections.Concurrent;

namespace Agnes.Host.Channels;

/// <summary>An outstanding permission request that was pushed to a chat and can be answered by a reply. The
/// allow/deny option ids are captured at send time so the inbound router doesn't have to re-derive them from
/// the (possibly-already-gone) live request.</summary>
public sealed record ChannelPendingPrompt(string SessionId, string RequestId, string? AllowOptionId, string? DenyOptionId);

/// <summary>
/// Remembers, per <c>(bridgeId, chatId)</c>, the most recent answerable prompt the notifier pushed out — so
/// a chat reply of "allow" (which names no request) can be resolved to a concrete session + request +
/// option. Shared between <c>ChannelBridgeNotifier</c> (records) and <c>ChannelBridgeRouter</c> (consumes).
/// Deliberately keeps only the latest prompt per chat: a reply always answers the thing most recently asked.
/// </summary>
public sealed class ChannelPromptTracker
{
    private readonly ConcurrentDictionary<string, ChannelPendingPrompt> _pending = new(StringComparer.Ordinal);

    private static string Key(string bridgeId, string externalChatId) => $"{bridgeId} {externalChatId}";

    /// <summary>Records the latest prompt sent to a chat, replacing any earlier unanswered one.</summary>
    public void Record(string bridgeId, string externalChatId, ChannelPendingPrompt prompt)
        => _pending[Key(bridgeId, externalChatId)] = prompt;

    /// <summary>Removes and returns the outstanding prompt for a chat, or null if there is none.</summary>
    public ChannelPendingPrompt? TryTake(string bridgeId, string externalChatId)
        => _pending.TryRemove(Key(bridgeId, externalChatId), out var prompt) ? prompt : null;

    /// <summary>Drops any outstanding prompt for a chat (e.g. on unlink), without answering it.</summary>
    public void Clear(string bridgeId, string externalChatId)
        => _pending.TryRemove(Key(bridgeId, externalChatId), out _);
}
