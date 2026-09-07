using System.Net.WebSockets;
using System.Threading.Channels;

namespace Agnes.Ui.Core.Tests;

/// <summary>
/// An in-memory <see cref="WebSocket"/>: what a test pushes with <see cref="PushAsync"/> is what the client
/// reads, and what the client sends lands in <see cref="Sent"/>.
/// </summary>
/// <remarks>
/// A real socket would drag a listener, a port and a handshake into tests whose subject is framing and the
/// drop policy. Messages are delivered whole — a WebSocket's own framing is the transport's problem, not the
/// display channel's, and the display channel is what is under test.
/// </remarks>
public sealed class FakeDuplexWebSocket : WebSocket
{
    private readonly Channel<(byte[] Data, WebSocketMessageType Type)> _inbound =
        Channel.CreateUnbounded<(byte[] Data, WebSocketMessageType Type)>();

    private readonly List<(byte[] Data, WebSocketMessageType Type)> _sent = [];
    private readonly Lock _sentGate = new();

    public override WebSocketState State { get; } = WebSocketState.Open;

    public override WebSocketCloseStatus? CloseStatus => null;

    public override string? CloseStatusDescription => null;

    public override string? SubProtocol => null;

    /// <summary>Everything the client has sent, in order.</summary>
    public IReadOnlyList<(byte[] Data, WebSocketMessageType Type)> Sent
    {
        get
        {
            lock (_sentGate)
            {
                return _sent.ToArray();
            }
        }
    }

    /// <summary>Queues one whole message for the client to read.</summary>
    public void Push(byte[] data, WebSocketMessageType type = WebSocketMessageType.Binary)
        => _inbound.Writer.TryWrite((data, type));

    /// <summary>Ends the stream, as a host closing the connection would.</summary>
    public void Complete() => _inbound.Writer.TryComplete();

    public override async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        (byte[] Data, WebSocketMessageType Type) message;
        try
        {
            message = await _inbound.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            return new ValueWebSocketReceiveResult(0, WebSocketMessageType.Close, true);
        }

        // Deliver in buffer-sized chunks so the client's own reassembly loop is exercised rather than bypassed.
        var take = Math.Min(buffer.Length, message.Data.Length);
        message.Data.AsMemory(0, take).CopyTo(buffer);
        if (take < message.Data.Length)
        {
            _inbound.Writer.TryWrite((message.Data[take..], message.Type));
            return new ValueWebSocketReceiveResult(take, message.Type, endOfMessage: false);
        }

        return new ValueWebSocketReceiveResult(take, message.Type, endOfMessage: true);
    }

    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        var result = await ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
        return new WebSocketReceiveResult(result.Count, result.MessageType, result.EndOfMessage);
    }

    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        lock (_sentGate)
        {
            _sent.Add((buffer.ToArray(), messageType));
        }

        return Task.CompletedTask;
    }

    public override void Abort() => Complete();

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        Complete();
        return Task.CompletedTask;
    }

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        Complete();
        return Task.CompletedTask;
    }

    public override void Dispose() => Complete();
}
