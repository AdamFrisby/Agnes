using System.Collections.Concurrent;
using Agnes.Abstractions;

namespace Agnes.Host.Tests;

/// <summary>
/// An in-memory <see cref="IChannelBridge"/> for tests: it records every outbound <see cref="SendAsync"/>
/// call and lets a test simulate an inbound chat reply by calling <see cref="RaiseInboundAsync"/> — proving
/// the bridge round-trip without any real Telegram/Slack network transport.
/// </summary>
public sealed class FakeChannelBridge(string id = "fake") : IChannelBridge
{
    public sealed record Sent(string ExternalChatId, string Message, ChannelBridgeContext Context);

    private readonly ConcurrentQueue<Sent> _sent = new();

    public string Id { get; } = id;

    /// <summary>Every outbound message this bridge was asked to deliver, in order.</summary>
    public IReadOnlyList<Sent> SentMessages => _sent.ToArray();

    public Task SendAsync(string externalChatId, string message, ChannelBridgeContext context, CancellationToken ct = default)
    {
        _sent.Enqueue(new Sent(externalChatId, message, context));
        return Task.CompletedTask;
    }

    public event Func<InboundChannelMessage, Task>? OnInboundMessage;

    /// <summary>Simulates the bridge's webhook/poll handler receiving a chat message and handing it to the host.</summary>
    public Task RaiseInboundAsync(string externalChatId, string text, string? requestId = null)
        => OnInboundMessage?.Invoke(new InboundChannelMessage(Id, externalChatId, text, requestId)) ?? Task.CompletedTask;
}
