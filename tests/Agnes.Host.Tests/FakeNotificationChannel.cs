using System.Collections.Concurrent;
using Agnes.Abstractions;

namespace Agnes.Host.Tests;

/// <summary>
/// An in-memory <see cref="INotificationChannel"/> for tests: records every <see cref="SendAsync"/> payload
/// and every <see cref="RegisterAsync"/> token, proving the dispatcher's decisions without any real FCM/APNs
/// transport. Mirrors <see cref="FakeChannelBridge"/>.
/// </summary>
public sealed class FakeNotificationChannel(string id = "mobile-push") : INotificationChannel
{
    private readonly ConcurrentQueue<NotificationPayload> _sent = new();
    private readonly ConcurrentDictionary<string, string> _registered = new(StringComparer.Ordinal);

    public string Id { get; } = id;

    /// <summary>Every payload this channel was asked to deliver, in order.</summary>
    public IReadOnlyList<NotificationPayload> Sent => _sent.ToArray();

    /// <summary>The channel token last registered for a device, or null.</summary>
    public string? TokenFor(string deviceId) => _registered.GetValueOrDefault(deviceId);

    public Task RegisterAsync(string deviceId, string channelToken, CancellationToken ct = default)
    {
        _registered[deviceId] = channelToken;
        return Task.CompletedTask;
    }

    public Task SendAsync(NotificationPayload payload, CancellationToken ct = default)
    {
        _sent.Enqueue(payload);
        return Task.CompletedTask;
    }
}
