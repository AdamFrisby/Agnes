using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Tmds.DBus.Protocol;

namespace Agnes.Sandbox.Incus.Graphical;

/// <summary>
/// Reads a sandbox VM's framebuffer and injects input, entirely from outside the guest: QEMU's
/// <c>-display dbus</c> hands us every damage rectangle the virtual GPU produces, and takes key and
/// pointer events straight into the emulated devices. Nothing runs inside the guest to make this work
/// — no VNC server, no capture agent, no cooperation from whatever the agent is driving.
/// </summary>
/// <remarks>
/// <para>
/// The shape is dictated by QEMU. We are a *client* of the instance's private bus for control
/// (<c>RegisterListener</c>, keyboard, mouse), and a *server* for the pixels: QEMU makes a second,
/// peer-to-peer D-Bus connection back over a socket we hand it, and calls
/// <c>org.qemu.Display1.Listener</c> methods on us. Hence one connection object per direction.
/// </para>
/// <para>
/// The listener object deliberately advertises an empty <c>Interfaces</c> property. QEMU reads it to
/// decide whether to send pixels through shared memory (<c>…Listener.Unix.Map</c>) or a dmabuf; saying
/// we implement neither is what keeps us on the simple path where the pixels arrive inline as
/// <c>ay</c>. It also has to be *present*: QEMU passes the property through
/// <c>g_strv_contains()</c>, which does not tolerate a missing value.
/// </para>
/// </remarks>
internal sealed class IncusDisplaySession : IDisplaySession, IPathMethodHandler
{
    internal const string QemuService = "org.qemu";
    internal const string ConsolePath = "/org/qemu/Display1/Console_0";
    internal const string ListenerPath = "/org/qemu/Display1/Listener";
    private const string ConsoleInterface = "org.qemu.Display1.Console";
    private const string ListenerInterface = "org.qemu.Display1.Listener";
    private const string MouseInterface = "org.qemu.Display1.Mouse";
    private const string KeyboardInterface = "org.qemu.Display1.Keyboard";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";

    private readonly DBusConnection _bus;
    private readonly DBusConnection _listener;
    private readonly Socket _listenerSocket;
    private readonly Channel<DisplayUpdate> _updates;
    private readonly ILogger _logger;
    private readonly Lock _frameLock = new();
    private readonly int _dpi;
    private readonly Action<TimeSpan>? _onFrameProcessed;

    private byte[] _frame = [];
    private DisplayGeometry _geometry;
    private long _sequence;
    private bool _absolutePointer;
    private int _pointerX;
    private int _pointerY;
    private bool _disposed;

    private IncusDisplaySession(
        DBusConnection bus, DBusConnection listener, Socket listenerSocket, int dpi, ILogger logger, Action<TimeSpan>? onFrameProcessed)
    {
        _bus = bus;
        _listener = listener;
        _listenerSocket = listenerSocket;
        _dpi = dpi;
        _logger = logger;
        _onFrameProcessed = onFrameProcessed;
        _updates = Channel.CreateUnbounded<DisplayUpdate>(new UnboundedChannelOptions { SingleWriter = true });
    }

    public DisplayGeometry Geometry => _geometry;

    public ChannelReader<DisplayUpdate> Updates => _updates.Reader;

    // ---- opening ----

    /// <summary>
    /// Connects to the instance's private bus, registers a listener, and waits for the first scanout
    /// (which is what tells us the surface's real size — the requested size is only a request until
    /// the guest's X session has mode-set).
    /// </summary>
    /// <param name="onFrameProcessed">Optional metrics sink, called with how long each damage rectangle
    /// took from arrival to published update. Injected rather than hooked so the session stays a
    /// function of its inputs; it is what the capture spike measures with.</param>
    internal static async Task<IncusDisplaySession> OpenAsync(
        string busAddress, int dpi, TimeSpan firstFrameTimeout, ILogger logger, CancellationToken cancellationToken,
        Action<TimeSpan>? onFrameProcessed = null)
    {
        var bus = new DBusConnection(busAddress);
        await bus.ConnectAsync().ConfigureAwait(false);

        DisplayInterop.CreateSocketPair(out var peerHandle, out var ours);
        var stream = new ListenerHandshakeStream(new NetworkStream(ours, ownsSocket: false));
        var listener = new DBusConnection(new StreamConnectionOptions(stream));

        var session = new IncusDisplaySession(bus, listener, ours, dpi, logger, onFrameProcessed);
        try
        {
            // The ordering here is forced, and it is the one subtle thing in this file.
            //
            // Both halves have to be in flight at once: QEMU only starts its side of the peer-to-peer
            // authentication once RegisterListener has reached it, and our ConnectAsync only finishes
            // once QEMU has answered. Awaiting either first deadlocks. And Tmds refuses to register a
            // method handler on a connection that has not finished connecting, while QEMU's first
            // method call arrives *during* our ConnectAsync — so registering after connect is always
            // too late. ListenerHandshakeStream is what makes it not a race: it holds QEMU's messages
            // at the socket until Release, below.
            var connectTask = listener.ConnectAsync().AsTask();
            using (peerHandle)
            {
                await bus.CallMethodAsync(CreateRegisterListenerMessage(bus, peerHandle)).ConfigureAwait(false);
            }

            await connectTask.WaitAsync(firstFrameTimeout, cancellationToken).ConfigureAwait(false);
            listener.AddMethodHandler(session);
            stream.Release();
            await session.WaitForFirstFrameAsync(firstFrameTimeout, cancellationToken).ConfigureAwait(false);
            session._absolutePointer = await session.ReadIsAbsoluteAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation(
                "Display session open: {Width}x{Height}, absolute pointer {Absolute}",
                session._geometry.Width, session._geometry.Height, session._absolutePointer);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task WaitForFirstFrameAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        while (_geometry.Width == 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), cts.Token).ConfigureAwait(false);
        }
    }

    private async Task<bool> ReadIsAbsoluteAsync(CancellationToken cancellationToken)
    {
        var value = await _bus.CallMethodAsync(
            CreatePropertyGetMessage(_bus, MouseInterface, "IsAbsolute"),
            static (Message message, object? _) => message.GetBodyReader().ReadVariantValue(),
            null).WaitAsync(cancellationToken).ConfigureAwait(false);
        return value.GetBool();
    }

    // ---- IDisplaySession ----

    public Task<ReadOnlyMemory<byte>> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        lock (_frameLock)
        {
            return Task.FromResult<ReadOnlyMemory<byte>>(_frame.AsSpan().ToArray());
        }
    }

    public async Task InjectAsync(DisplayInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ObjectDisposedException.ThrowIf(_disposed, this);

        switch (input)
        {
            case PointerMove move:
                await MoveAsync(move.X, move.Y).ConfigureAwait(false);
                break;

            case PointerButton button:
                await CallUIntAsync(MouseInterface, button.Down ? "Press" : "Release", QemuButton(button.Button)).ConfigureAwait(false);
                break;

            case PointerScroll scroll:
                await MoveAsync(scroll.X, scroll.Y).ConfigureAwait(false);
                await ScrollAsync(scroll.Dy, QemuWheelDown, QemuWheelUp).ConfigureAwait(false);
                await ScrollAsync(scroll.Dx, QemuWheelRight, QemuWheelLeft).ConfigureAwait(false);
                break;

            case KeyPress key:
                await KeyAsync(key).ConfigureAwait(false);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(input), input, "Unsupported display input.");
        }
    }

    private async Task MoveAsync(int x, int y)
    {
        var geometry = _geometry;
        var cx = Math.Clamp(x, 0, Math.Max(0, geometry.Width - 1));
        var cy = Math.Clamp(y, 0, Math.Max(0, geometry.Height - 1));

        if (_absolutePointer)
        {
            await _bus.CallMethodAsync(CreatePointerMessage("SetAbsPosition", "uu", (uint)cx, (uint)cy)).ConfigureAwait(false);
        }
        else
        {
            // A relative-only pointer (no usb-tablet) can't be placed, only nudged. We track where we
            // believe it is and send the difference; it drifts if anything else moves the pointer, which
            // is exactly why the graphical tier adds a tablet.
            await _bus.CallMethodAsync(CreatePointerMessage("RelMotion", "ii", (uint)(cx - _pointerX), (uint)(cy - _pointerY))).ConfigureAwait(false);
        }

        _pointerX = cx;
        _pointerY = cy;
    }

    private async Task ScrollAsync(int detents, uint positiveButton, uint negativeButton)
    {
        var button = detents > 0 ? positiveButton : negativeButton;
        for (var i = 0; i < Math.Abs(detents); i++)
        {
            await CallUIntAsync(MouseInterface, "Press", button).ConfigureAwait(false);
            await CallUIntAsync(MouseInterface, "Release", button).ConfigureAwait(false);
        }
    }

    private async Task KeyAsync(KeyPress key)
    {
        if (!QemuKeyMap.TryResolve(key.Key, out var qnum, out var needsShift))
        {
            throw new ArgumentOutOfRangeException(nameof(key), key.Key, "Unknown key name.");
        }

        // Shift is synthesised around the key, not left to the caller: the caller named a *character*.
        if (needsShift && key.Down)
        {
            await CallUIntAsync(KeyboardInterface, "Press", QemuKeyMap.ShiftLeft).ConfigureAwait(false);
        }

        await CallUIntAsync(KeyboardInterface, key.Down ? "Press" : "Release", qnum).ConfigureAwait(false);

        if (needsShift && !key.Down)
        {
            await CallUIntAsync(KeyboardInterface, "Release", QemuKeyMap.ShiftLeft).ConfigureAwait(false);
        }
    }

    private Task CallUIntAsync(string @interface, string member, uint argument)
        => _bus.CallMethodAsync(CreateUIntMessage(@interface, member, argument));

    // ---- message construction. MessageWriter is a ref struct, so every message is built inside a
    // synchronous method and only the finished buffer crosses an await. ----

    private static MessageBuffer CreateRegisterListenerMessage(DBusConnection bus, SafeHandle listener)
    {
        using var writer = bus.GetMessageWriter();
        writer.WriteMethodCallHeader(QemuService, ConsolePath, ConsoleInterface, "RegisterListener", "h");
        writer.WriteHandle(listener);
        return writer.CreateMessage();
    }

    private static MessageBuffer CreatePropertyGetMessage(DBusConnection bus, string @interface, string property)
    {
        using var writer = bus.GetMessageWriter();
        writer.WriteMethodCallHeader(QemuService, ConsolePath, PropertiesInterface, "Get", "ss");
        writer.WriteString(@interface);
        writer.WriteString(property);
        return writer.CreateMessage();
    }

    private MessageBuffer CreateUIntMessage(string @interface, string member, uint argument)
    {
        using var writer = _bus.GetMessageWriter();
        writer.WriteMethodCallHeader(QemuService, ConsolePath, @interface, member, "u");
        writer.WriteUInt32(argument);
        return writer.CreateMessage();
    }

    private MessageBuffer CreatePointerMessage(string member, string signature, uint first, uint second)
    {
        using var writer = _bus.GetMessageWriter();
        writer.WriteMethodCallHeader(QemuService, ConsolePath, MouseInterface, member, signature);
        if (signature == "uu")
        {
            writer.WriteUInt32(first);
            writer.WriteUInt32(second);
        }
        else
        {
            writer.WriteInt32((int)first);
            writer.WriteInt32((int)second);
        }

        return writer.CreateMessage();
    }

    // QEMU's InputButton enum (ui/input.c): left, middle, right, wheel-up, wheel-down, side, extra,
    // wheel-left, wheel-right.
    private const uint QemuWheelUp = 3;
    private const uint QemuWheelDown = 4;
    private const uint QemuWheelLeft = 7;
    private const uint QemuWheelRight = 8;

    private static uint QemuButton(PointerButtonKind kind) => kind switch
    {
        PointerButtonKind.Left => 0,
        PointerButtonKind.Middle => 1,
        PointerButtonKind.Right => 2,
        PointerButtonKind.Back => 5,
        PointerButtonKind.Forward => 6,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported pointer button."),
    };

    // ---- IPathMethodHandler: QEMU calling us with pixels ----

    public string Path => ListenerPath;

    public bool HandlesChildPaths => false;

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            HandleMethod(context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Display listener failed handling {Member}", context.Request.MemberAsString);
            if (!context.ReplySent && !context.NoReplyExpected)
            {
                context.ReplyError("org.qemu.Display1.Error.Failed", ex.Message);
            }
        }

        return ValueTask.CompletedTask;
    }

    private void HandleMethod(MethodContext context)
    {
        var started = Stopwatch.GetTimestamp();
        if (context.IsDBusIntrospectRequest)
        {
            context.ReplyIntrospectXml([ListenerIntrospectionXml], []);
            return;
        }

        var member = context.Request.MemberAsString ?? string.Empty;
        if (context.Request.InterfaceAsString == PropertiesInterface)
        {
            HandleProperties(context, member);
            return;
        }

        switch (member)
        {
            case "Scanout":
                HandleScanout(context);
                _onFrameProcessed?.Invoke(Stopwatch.GetElapsedTime(started));
                break;

            case "Update":
                HandleUpdate(context);
                _onFrameProcessed?.Invoke(Stopwatch.GetElapsedTime(started));
                break;

            case "Disable":
            case "MouseSet":
            case "CursorDefine":
                // Acknowledged and ignored: the cursor is drawn by the guest into the surface we already
                // capture, and Disable means "no output right now", which the next Scanout supersedes.
                ReplyEmpty(context);
                break;

            default:
                // ScanoutDMABUF/UpdateDMABUF land here. They can't happen — this QEMU has no GL display
                // backend — but answering with an error rather than silence keeps QEMU from stalling.
                context.ReplyError("org.qemu.Display1.Error.Unsupported", $"{member} is not supported.");
                break;
        }
    }

    /// <summary>
    /// Answers the properties round trip QEMU makes before it will send a single pixel. Only one
    /// property exists, <c>Interfaces</c>, and its value is an empty array — see the class remarks for
    /// why that is a decision rather than a stub.
    /// </summary>
    private static void HandleProperties(MethodContext context, string member)
    {
        switch (member)
        {
            case "GetAll":
            {
                using var writer = context.CreateReplyWriter("a{sv}");
                var dictionary = writer.WriteDictionaryStart();
                writer.WriteDictionaryEntryStart();
                writer.WriteString(InterfacesProperty);
                writer.WriteVariant(NoInterfaces);
                writer.WriteDictionaryEnd(dictionary);
                context.Reply(writer.CreateMessage());
                break;
            }

            case "Get":
            {
                var reader = context.Request.GetBodyReader();
                reader.ReadString();
                var property = reader.ReadString();
                if (property != InterfacesProperty)
                {
                    context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", property);
                    break;
                }

                using var writer = context.CreateReplyWriter("v");
                writer.WriteVariant(NoInterfaces);
                context.Reply(writer.CreateMessage());
                break;
            }

            default:
                context.ReplyError("org.freedesktop.DBus.Error.UnknownMethod", member);
                break;
        }
    }

    private const string InterfacesProperty = "Interfaces";

    private static VariantValue NoInterfaces => new Tmds.DBus.Protocol.Array<string>().AsVariantValue();

    private void HandleScanout(MethodContext context)
    {
        var reader = context.Request.GetBodyReader();
        var width = (int)reader.ReadUInt32();
        var height = (int)reader.ReadUInt32();
        var stride = (int)reader.ReadUInt32();
        var format = reader.ReadUInt32();
        var pixels = reader.ReadArrayOfByte();
        ReplyEmpty(context);
        Blit(0, 0, width, height, stride, format, pixels, scanout: true);
    }

    private void HandleUpdate(MethodContext context)
    {
        var reader = context.Request.GetBodyReader();
        var x = reader.ReadInt32();
        var y = reader.ReadInt32();
        var width = reader.ReadInt32();
        var height = reader.ReadInt32();
        var stride = (int)reader.ReadUInt32();
        var format = reader.ReadUInt32();
        var pixels = reader.ReadArrayOfByte();
        // Reply before doing the work: QEMU's display thread is blocked on this call, so the sooner it
        // is answered the sooner the next frame starts. The pixels are already ours (the message body
        // outlives the reply within this callback).
        ReplyEmpty(context);
        Blit(x, y, width, height, stride, format, pixels, scanout: false);
    }

    private static void ReplyEmpty(MethodContext context)
    {
        if (context.NoReplyExpected)
        {
            return;
        }

        using var writer = context.CreateReplyWriter(null);
        context.Reply(writer.CreateMessage());
    }

    /// <summary>
    /// Copies a damage rectangle into the framebuffer and publishes it. The published copy is always
    /// packed (<c>Stride == Width * 4</c>) whatever stride QEMU used, so a consumer has one rule.
    /// </summary>
    private void Blit(int x, int y, int width, int height, int stride, uint pixmanFormat, byte[] pixels, bool scanout)
    {
        if (!TryMapFormat(pixmanFormat, out var format))
        {
            _logger.LogWarning("Ignoring a {Kind} in unsupported pixman format 0x{Format:x8}", scanout ? "scanout" : "update", pixmanFormat);
            return;
        }

        if (width <= 0 || height <= 0 || stride < width * 4)
        {
            _logger.LogWarning("Ignoring a malformed damage rectangle {W}x{H} stride {Stride}", width, height, stride);
            return;
        }

        var packedStride = width * 4;
        if (pixels.Length < ((height - 1) * stride) + packedStride)
        {
            _logger.LogWarning("Truncated damage rectangle: {Have} bytes for {W}x{H} stride {Stride}", pixels.Length, width, height, stride);
            return;
        }

        // QEMU already packs an Update's rows (stride == width*4); a Scanout carries the surface's own
        // stride, which may be padded. Re-pack only when it actually differs, so the common case is free.
        byte[] packed;
        if (stride == packedStride && pixels.Length == packedStride * height)
        {
            packed = pixels;
        }
        else
        {
            packed = new byte[packedStride * height];
            for (var row = 0; row < height; row++)
            {
                pixels.AsSpan(row * stride, packedStride).CopyTo(packed.AsSpan(row * packedStride));
            }
        }

        lock (_frameLock)
        {
            if (scanout && (_geometry.Width != width || _geometry.Height != height))
            {
                // The guest mode-set. Everything downstream keys off Geometry, and a full-surface update
                // is riding along in this very call, so the resize needs no separate event.
                _logger.LogInformation("Display surface is now {Width}x{Height}", width, height);
                _geometry = new DisplayGeometry(width, height, _dpi);
                _frame = new byte[width * height * 4];
            }

            var geometry = _geometry;
            for (var row = 0; row < height; row++)
            {
                var destRow = y + row;
                if (destRow < 0 || destRow >= geometry.Height)
                {
                    continue;
                }

                var destX = Math.Max(0, x);
                var count = Math.Min(width - (destX - x), geometry.Width - destX) * 4;
                if (count <= 0)
                {
                    continue;
                }

                packed.AsSpan((row * packedStride) + ((destX - x) * 4), count)
                    .CopyTo(_frame.AsSpan(((destRow * geometry.Width) + destX) * 4));
            }
        }

        var sequence = Interlocked.Increment(ref _sequence);
        _updates.Writer.TryWrite(new DisplayUpdate(x, y, width, height, packedStride, format, packed, sequence, DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// Decodes a pixman format code. The encoding is
    /// <c>(bpp&lt;&lt;24)|(type&lt;&lt;16)|(a&lt;&lt;12)|(r&lt;&lt;8)|(g&lt;&lt;4)|b</c>, so
    /// <c>PIXMAN_x8r8g8b8</c> is 0x20020888 and <c>PIXMAN_a8r8g8b8</c> is 0x20028888 — both of which are
    /// B,G,R,x/A in memory on a little-endian host, i.e. exactly what we hand out.
    /// </summary>
    private static bool TryMapFormat(uint pixmanFormat, out DisplayPixelFormat format)
    {
        var bpp = (pixmanFormat >> 24) & 0xFF;
        var type = (pixmanFormat >> 16) & 0xFF;
        var alpha = (pixmanFormat >> 12) & 0xF;
        var red = (pixmanFormat >> 8) & 0xF;
        var green = (pixmanFormat >> 4) & 0xF;
        var blue = pixmanFormat & 0xF;
        const uint PixmanTypeArgb = 2;
        if (bpp == 32 && type == PixmanTypeArgb && red == 8 && green == 8 && blue == 8)
        {
            format = alpha == 8 ? DisplayPixelFormat.Bgra8888 : DisplayPixelFormat.Bgrx8888;
            return true;
        }

        format = default;
        return false;
    }

    private static readonly ReadOnlyMemory<byte> ListenerIntrospectionXml = Encoding.UTF8.GetBytes("""
          <interface name="org.qemu.Display1.Listener">
            <method name="Scanout">
              <arg type="u" name="width" direction="in"/>
              <arg type="u" name="height" direction="in"/>
              <arg type="u" name="stride" direction="in"/>
              <arg type="u" name="pixman_format" direction="in"/>
              <arg type="ay" name="data" direction="in"/>
            </method>
            <method name="Update">
              <arg type="i" name="x" direction="in"/>
              <arg type="i" name="y" direction="in"/>
              <arg type="i" name="width" direction="in"/>
              <arg type="i" name="height" direction="in"/>
              <arg type="u" name="stride" direction="in"/>
              <arg type="u" name="pixman_format" direction="in"/>
              <arg type="ay" name="data" direction="in"/>
            </method>
            <method name="Disable"/>
            <method name="MouseSet">
              <arg type="i" name="x" direction="in"/>
              <arg type="i" name="y" direction="in"/>
              <arg type="i" name="on" direction="in"/>
            </method>
            <method name="CursorDefine">
              <arg type="i" name="width" direction="in"/>
              <arg type="i" name="height" direction="in"/>
              <arg type="i" name="hot_x" direction="in"/>
              <arg type="i" name="hot_y" direction="in"/>
              <arg type="ay" name="data" direction="in"/>
            </method>
            <property name="Interfaces" type="as" access="read"/>
          </interface>

        """);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _updates.Writer.TryComplete();
        _listener.Dispose();
        _bus.Dispose();
        _listenerSocket.Dispose();
        await Task.CompletedTask.ConfigureAwait(false);
    }
}

/// <summary>
/// Connection options that hand Tmds an already-open stream instead of an address, which is how the
/// peer-to-peer half of the QEMU display protocol has to be spoken: there is no bus behind it and no
/// address to dial, only the socket QEMU was given.
/// </summary>
internal sealed class StreamConnectionOptions : DBusConnectionOptions
{
    private readonly Stream _stream;

    internal StreamConnectionOptions(Stream stream)
    {
        _stream = stream;
        AutoConnect = false;
    }

    protected override ValueTask<SetupResult> SetupAsync(CancellationToken cancellationToken)
        => new(new SetupResult
        {
            ConnectionStream = _stream,
            UserId = DisplayInterop.EffectiveUserId(),
            MachineId = DisplayInterop.MachineId(),
            SupportsFdPassing = false,
        });
}
