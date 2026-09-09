using System.Buffers.Binary;
using System.Text;

namespace Agnes.Sandbox.Incus.Graphical;

/// <summary>
/// Makes a bus-shaped D-Bus client library usable on QEMU's bus-less display connection, and holds
/// QEMU's first message until the listener object is published.
/// </summary>
/// <remarks>
/// <para>
/// The connection QEMU makes back over the socket we hand it is <em>peer to peer</em>: there is no bus,
/// no <c>org.freedesktop.DBus</c>, and nothing that answers <c>Hello</c>. Tmds.DBus.Protocol has no
/// peer-to-peer mode — even handed a ready-made stream it sends <c>Hello</c> and waits for the unique
/// name before it considers itself connected. So this stream answers that one message itself: it
/// swallows the <c>Hello</c> call and feeds back a synthetic <c>METHOD_RETURN</c> carrying a unique
/// name. Everything after it is real traffic between us and QEMU. Forty bytes of hand-built D-Bus is a
/// far smaller thing to own than a second implementation of the wire format.
/// </para>
/// <para>
/// The second job is ordering. Tmds will not accept a method handler on a connection that has not
/// finished connecting, and QEMU sends its first call the instant its side of the handshake completes —
/// during our <c>ConnectAsync</c>. Registering after connect is therefore always too late: the call
/// arrives with no handler and gets "unknown method", which leaves QEMU with a listener that never
/// receives pixels. So reads past the handshake wait for <see cref="Release"/>, which the session calls
/// once the handler is in place. After that the gate is open for good and every call is a straight
/// delegation, so the pixel path pays nothing for it.
/// </para>
/// </remarks>
internal sealed class ListenerHandshakeStream : Stream
{
    private static readonly byte[] BeginCommand = Encoding.ASCII.GetBytes("BEGIN\r\n");
    private static readonly byte[] HelloMember = Encoding.ASCII.GetBytes("Hello");
    private static readonly byte[] BusName = Encoding.ASCII.GetBytes("org.freedesktop.DBus");

    private readonly Stream _inner;
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly byte[] _tail = new byte[BeginCommand.Length];

    private TaskCompletionSource _pendingSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _tailLength;
    private byte[]? _pendingRead;
    private volatile bool _handshakeComplete;
    private volatile bool _helloAnswered;
    private volatile bool _released;

    internal ListenerHandshakeStream(Stream inner) => _inner = inner;

    /// <summary>Lets QEMU's messages through. Idempotent.</summary>
    internal void Release()
    {
        _released = true;
        _gate.TrySetResult();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        // The reader parks here for the gate, so a synthetic reply queued *while* it is parked has to
        // wake it — otherwise the Hello answer sits in the queue and the connection never completes.
        while (true)
        {
            if (TryTakePending(buffer.Span, out var synthesized))
            {
                return synthesized;
            }

            if (!_handshakeComplete || _released)
            {
                break;
            }

            await Task.WhenAny(_gate.Task, _pendingSignal.Task).WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (TrySwallowHello(buffer.Span))
        {
            return;
        }

        await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        NoteWritten(buffer.Span);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        var span = buffer.AsSpan(offset, count);
        if (TrySwallowHello(span))
        {
            return;
        }

        _inner.Write(buffer, offset, count);
        NoteWritten(span);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (TryTakePending(buffer.AsSpan(offset, count), out var synthesized))
        {
            return synthesized;
        }

        if (_handshakeComplete && !_released)
        {
            _gate.Task.GetAwaiter().GetResult();
        }

        return _inner.Read(buffer, offset, count);
    }

    private bool TryTakePending(Span<byte> destination, out int count)
    {
        var pending = Interlocked.Exchange(ref _pendingRead, null);
        if (pending is null || pending.Length > destination.Length)
        {
            // A caller with a buffer too small for one synthetic reply would need re-entrant chunking
            // this stream has no reason to grow: Tmds reads into a buffer of at least 4 KiB.
            _pendingRead = pending;
            count = 0;
            return false;
        }

        pending.CopyTo(destination);
        Interlocked.Exchange(ref _pendingSignal, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        count = pending.Length;
        return true;
    }

    /// <summary>
    /// Recognises the client's <c>Hello</c> — the only message on this connection addressed to a bus
    /// that isn't there — and answers it instead of sending it.
    /// </summary>
    private bool TrySwallowHello(ReadOnlySpan<byte> message)
    {
        if (!_handshakeComplete || _helloAnswered || message.Length < 16 || message[1] != 1)
        {
            return false;
        }

        if (message.IndexOf(HelloMember) < 0 || message.IndexOf(BusName) < 0)
        {
            return false;
        }

        var serial = message[0] == (byte)'l'
            ? BinaryPrimitives.ReadUInt32LittleEndian(message[8..])
            : BinaryPrimitives.ReadUInt32BigEndian(message[8..]);
        _pendingRead = BuildHelloReply(serial);
        _helloAnswered = true;
        _pendingSignal.TrySetResult();
        return true;
    }

    /// <summary>
    /// A little-endian <c>METHOD_RETURN</c> carrying one string, with <c>REPLY_SERIAL</c> and
    /// <c>SIGNATURE</c> header fields. Offsets are absolute from the start of the message, which is how
    /// D-Bus alignment is defined: the header array starts at 16, its two fields at 16 and 24 (structs
    /// align to 8), the header ends at 31, pads to 32, and the body follows.
    /// </summary>
    private static byte[] BuildHelloReply(uint replySerial)
    {
        const string UniqueName = ":1.0";
        var name = Encoding.ASCII.GetBytes(UniqueName);
        var body = 4 + name.Length + 1;
        var message = new byte[32 + body];
        var span = message.AsSpan();

        span[0] = (byte)'l';
        span[1] = 2;              // METHOD_RETURN
        span[2] = 1;              // NO_REPLY_EXPECTED
        span[3] = 1;              // protocol version
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)body);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], 1);   // our serial
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], 15); // header array byte length

        span[16] = 5;             // REPLY_SERIAL
        span[17] = 1;
        span[18] = (byte)'u';
        span[19] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..], replySerial);

        span[24] = 8;             // SIGNATURE
        span[25] = 1;
        span[26] = (byte)'g';
        span[27] = 0;
        span[28] = 1;
        span[29] = (byte)'s';
        span[30] = 0;
        // span[31] is the pad to the 8-byte body boundary.

        BinaryPrimitives.WriteUInt32LittleEndian(span[32..], (uint)name.Length);
        name.CopyTo(span[36..]);
        return message;
    }

    /// <summary>
    /// Watches the outbound byte stream for the handshake's final <c>BEGIN</c> line, carrying a few
    /// bytes of tail between writes so the marker is still found if it is split across two of them.
    /// </summary>
    private void NoteWritten(ReadOnlySpan<byte> written)
    {
        if (_handshakeComplete || written.Length == 0)
        {
            return;
        }

        Span<byte> window = stackalloc byte[BeginCommand.Length * 2];
        _tail.AsSpan(0, _tailLength).CopyTo(window);
        var take = Math.Min(written.Length, BeginCommand.Length);
        written[^take..].CopyTo(window[_tailLength..]);
        var used = _tailLength + take;

        if (written.IndexOf(BeginCommand) >= 0 || window[..used].IndexOf(BeginCommand) >= 0)
        {
            _handshakeComplete = true;
            return;
        }

        _tailLength = Math.Min(used, BeginCommand.Length);
        window[(used - _tailLength)..used].CopyTo(_tail);
    }

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => _inner.CanWrite;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Release();
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
