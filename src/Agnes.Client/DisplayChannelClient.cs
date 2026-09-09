using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Agnes.Protocol;

namespace Agnes.Client;

/// <summary>
/// The display channel over a real WebSocket to a real host: <c>wss://host/display/{sessionId}?access_token=…</c>,
/// pinned to the host's certificate exactly like the hub's socket is.
/// </summary>
/// <remarks>
/// <para>
/// Two decisions are the whole point of this class.
/// </para>
/// <para>
/// <b>Pinning.</b> A <see cref="ClientWebSocket"/> dials its own TLS connection and never touches an
/// <c>HttpMessageHandler</c>, so a pinned host — the normal Agnes deployment — rejects it unless we apply the
/// pin here. This is the same trap <see cref="HostConnection"/> documents for SignalR's WebSockets transport;
/// the display channel walks into it independently, so it applies <see cref="PinnedTls.Apply(ClientWebSocketOptions, string)"/>
/// itself.
/// </para>
/// <para>
/// <b>Dropping, not queueing.</b> Frames are views of a screen, not facts in a log: the newest one is the only
/// one worth having. So the reader writes into a small bounded channel and, when it is full, discards the
/// OLDEST image frame to make room — never an <see cref="DisplayFrameKind.Info"/> or
/// <see cref="DisplayFrameKind.Control"/> frame, which carry geometry and who is driving and are facts. A client
/// that fell behind then shows a slightly stale picture rather than growing an unbounded queue and drifting
/// further behind forever; the host is dropping for the same reason at the other end.
/// </para>
/// <para>
/// Reconnection is deliberately not here. The channel ends when the socket ends, and the view model decides
/// whether a person still wants to be looking at that screen.
/// </para>
/// </remarks>
public sealed class DisplayChannelClient : IDisplayChannel
{
    // Small on purpose: a deep buffer is just latency. Three frames absorbs a UI-thread hiccup without
    // letting the client trail the guest by a visible amount.
    private const int Capacity = 3;

    private readonly WebSocket _socket;
    private readonly CancellationTokenSource _life = new();
    private readonly Channel<DisplayFrame> _frames;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Task _reader;

    private DisplayChannelClient(WebSocket socket)
    {
        _socket = socket;
        // Unbounded, because "drop the oldest IMAGE frame" is not a policy BoundedChannelFullMode can express:
        // DropOldest would happily discard an Info or Control frame. The reader enforces the cap itself.
        _frames = Channel.CreateUnbounded<DisplayFrame>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = true,
        });
        _reader = Task.Run(() => ReadLoopAsync(_life.Token));
    }

    public ChannelReader<DisplayFrame> Frames => _frames.Reader;

    /// <summary>
    /// Wraps an already-open WebSocket. The seam for a test double, and for any future transport that hands us
    /// a connected socket; the reading, framing and drop policy are identical either way, which is the point —
    /// they are the parts a test needs to reach.
    /// </summary>
    public static DisplayChannelClient FromSocket(WebSocket socket) => new(socket);

    /// <summary>
    /// Opens the display channel for one session. <paramref name="hostUrl"/> is the host's base address as
    /// <see cref="IAgnesHost.HostUrl"/> gives it; <paramref name="pinnedFingerprint"/> is its certificate pin,
    /// or null for an unpinned (plain http, or CA-trusted) host.
    /// </summary>
    public static async Task<DisplayChannelClient> ConnectAsync(
        string hostUrl,
        string sessionId,
        string token,
        string? pinnedFingerprint,
        CancellationToken cancellationToken = default)
    {
        var socket = new ClientWebSocket();
        if (pinnedFingerprint is { Length: > 0 } pin
            && hostUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            PinnedTls.Apply(socket.Options, pin);
        }

        try
        {
            await socket.ConnectAsync(BuildUri(hostUrl, sessionId, token), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        return new DisplayChannelClient(socket);
    }

    /// <summary>
    /// The channel's address: the host's base URL with its scheme swapped for the WebSocket one, then
    /// <see cref="DisplayWire.Path"/>, the session id as one more segment, and the device token as the same
    /// <c>access_token</c> query the hub authenticates with.
    /// </summary>
    public static Uri BuildUri(string hostUrl, string sessionId, string token)
    {
        var baseUrl = hostUrl.TrimEnd('/');
        if (baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            baseUrl = "wss://" + baseUrl["https://".Length..];
        }
        else if (baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            baseUrl = "ws://" + baseUrl["http://".Length..];
        }

        return new Uri(
            $"{baseUrl}{DisplayWire.Path}/{Uri.EscapeDataString(sessionId)}"
            + $"?{WireProtocol.TokenParameter}={Uri.EscapeDataString(token)}");
    }

    public async Task SendAsync(DisplayClientMessage message, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message, DisplayWire.Json);
        // One writer at a time: a WebSocket permits exactly one send in flight, and pointer moves race with
        // quality changes and control requests from different callers.
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(json, WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        Exception? fault = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                    if (message.Length > DisplayWire.MaxPayloadBytes + DisplayFrameHeader.Size)
                    {
                        throw new InvalidDataException("Display frame exceeds the protocol's maximum size.");
                    }
                }
                while (!result.EndOfMessage);

                // A text message on this direction is not part of the contract; ignore rather than fault, so a
                // host that adds one later doesn't kill an old client's screen.
                if (result.MessageType != WebSocketMessageType.Binary)
                {
                    continue;
                }

                Publish(message.GetBuffer().AsSpan(0, (int)message.Length));
            }
        }
        catch (OperationCanceledException)
        {
            // disposing
        }
        catch (Exception ex)
        {
            fault = ex;
        }
        finally
        {
            _frames.Writer.TryComplete(fault);
        }
    }

    private void Publish(ReadOnlySpan<byte> message)
    {
        if (!DisplayFrameHeader.TryRead(message, out var header))
        {
            throw new InvalidDataException("Malformed display frame header.");
        }

        var body = message[DisplayFrameHeader.Size..];
        if (body.Length != header.PayloadLength)
        {
            throw new InvalidDataException(
                $"Display frame declared {header.PayloadLength} payload bytes but carried {body.Length}.");
        }

        var frame = new DisplayFrame(header, body.ToArray());

        // Info and Control are facts and always go through; only pictures are droppable.
        if (frame.IsImage)
        {
            TrimToCapacity();
        }

        _frames.Writer.TryWrite(frame);
    }

    /// <summary>
    /// Makes room for one more image frame by discarding the oldest images already queued, leaving Info and
    /// Control frames where they are. Reading a frame off the front to inspect it is destructive, so a
    /// non-image that surfaces during the trim is written straight back — it goes behind whatever is still
    /// queued, which is harmless: geometry and driver state are last-write-wins, not a sequence.
    /// </summary>
    private void TrimToCapacity()
    {
        var rescued = new List<DisplayFrame>();
        while (_frames.Reader.Count >= Capacity && _frames.Reader.TryRead(out var oldest))
        {
            if (!oldest.IsImage)
            {
                rescued.Add(oldest);
            }
        }

        foreach (var keep in rescued)
        {
            _frames.Writer.TryWrite(keep);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _life.CancelAsync().ConfigureAwait(false);
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, closing.Token)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            // Closing politely is a courtesy to the host; a socket that is already gone needs nothing.
        }

        try
        {
            await _reader.ConfigureAwait(false);
        }
        catch
        {
            // The read loop's fault is already reported through the channel's completion.
        }

        _socket.Dispose();
        _life.Dispose();
        _sendLock.Dispose();
    }
}

/// <summary>Encoding of a host → client display message, shared by the real host and test doubles.</summary>
public static class DisplayFrameCodec
{
    /// <summary>Header + payload as one binary WebSocket message.</summary>
    public static byte[] Encode(DisplayFrameHeader header, ReadOnlySpan<byte> payload)
    {
        var message = new byte[DisplayFrameHeader.Size + payload.Length];
        header.Write(message);
        payload.CopyTo(message.AsSpan(DisplayFrameHeader.Size));
        return message;
    }

    /// <summary>A JSON-payload frame (Info or Control), UTF-8 encoded in the channel's dialect.</summary>
    public static byte[] EncodeJson<T>(DisplayFrameKind kind, uint sequence, T payload, int displayWidth, int displayHeight)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, DisplayWire.Json);
        var header = new DisplayFrameHeader(
            kind, sequence, 0, 0, 0, 0,
            (ushort)displayWidth, (ushort)displayHeight, (uint)json.Length);
        return Encode(header, json);
    }

    /// <summary>Decodes a JSON payload frame.</summary>
    public static T? DecodeJson<T>(DisplayFrame frame)
        => JsonSerializer.Deserialize<T>(frame.Payload.Span, DisplayWire.Json);
}
