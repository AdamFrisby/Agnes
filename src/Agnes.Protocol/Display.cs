using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agnes.Abstractions;

namespace Agnes.Protocol;

// ---------------------------------------------------------------------------------------------------
// THE DISPLAY CHANNEL
//
// A graphical session's screen reaches clients over a dedicated binary WebSocket on the host's main
// TLS listener at DisplayWire.Path + "/" + sessionId, authenticated exactly like the hub (the device
// token as the access_token query) and authorised with the same per-session decision as Subscribe
// (view) and Prompt (input). It is NOT the event log — frames are views, not facts, and the log is
// replayed in full to every joining client — and NOT the SignalR hub, whose default message cap,
// base64'd byte[] and single ordered broadcast are wrong for a hot stream.
//
// Host → client: binary messages, each a DisplayFrameHeader followed by a payload. Kind Full/Tile carry
// JPEG; Info and Control carry small UTF-8 JSON (DisplayInfo / DisplayControlNotice). The first message
// after open is Info. The host never decodes guest pixels into anything a client must trust beyond a
// JPEG — the same decoder clients already point at agent-sent images.
//
// Client → host: text messages, one DisplayClientMessage each, discriminated by "t".
// ---------------------------------------------------------------------------------------------------

public static class DisplayWire
{
    /// <summary>The WebSocket path prefix; the session id follows as one more segment.</summary>
    public const string Path = "/display";

    public const int HeaderVersion = 1;

    /// <summary>A frame payload larger than this is a protocol error and the connection is dropped.</summary>
    public const int MaxPayloadBytes = 8 * 1024 * 1024;

    /// <summary>The JSON dialect of the channel's text messages and JSON payloads: camelCase, enums as strings.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}

public enum DisplayFrameKind : byte
{
    /// <summary>A JPEG of the whole display.</summary>
    Full = 0,
    /// <summary>A JPEG of the region (X, Y, Width, Height) only.</summary>
    Tile = 1,
    /// <summary>UTF-8 JSON <see cref="DisplayInfo"/>.</summary>
    Info = 2,
    /// <summary>UTF-8 JSON <see cref="DisplayControlNotice"/>.</summary>
    Control = 3,
}

/// <summary>
/// The fixed 28-byte header in front of every host → client message. Big-endian. Layout:
/// <c>"AGDF"</c> (4) · version u8 · kind u8 · reserved u16 · sequence u32 · x u16 · y u16 · width u16 ·
/// height u16 · displayWidth u16 · displayHeight u16 · payloadLength u32.
/// </summary>
public readonly record struct DisplayFrameHeader(
    DisplayFrameKind Kind,
    uint Sequence,
    ushort X,
    ushort Y,
    ushort Width,
    ushort Height,
    ushort DisplayWidth,
    ushort DisplayHeight,
    uint PayloadLength)
{
    public const int Size = 28;

    private static ReadOnlySpan<byte> Magic => "AGDF"u8;

    public void Write(Span<byte> destination)
    {
        Magic.CopyTo(destination);
        destination[4] = DisplayWire.HeaderVersion;
        destination[5] = (byte)Kind;
        BinaryPrimitives.WriteUInt16BigEndian(destination[6..], 0);
        BinaryPrimitives.WriteUInt32BigEndian(destination[8..], Sequence);
        BinaryPrimitives.WriteUInt16BigEndian(destination[12..], X);
        BinaryPrimitives.WriteUInt16BigEndian(destination[14..], Y);
        BinaryPrimitives.WriteUInt16BigEndian(destination[16..], Width);
        BinaryPrimitives.WriteUInt16BigEndian(destination[18..], Height);
        BinaryPrimitives.WriteUInt16BigEndian(destination[20..], DisplayWidth);
        BinaryPrimitives.WriteUInt16BigEndian(destination[22..], DisplayHeight);
        BinaryPrimitives.WriteUInt32BigEndian(destination[24..], PayloadLength);
    }

    /// <summary>Reads a header, or returns false for a bad magic, version, kind or oversize payload.</summary>
    public static bool TryRead(ReadOnlySpan<byte> source, out DisplayFrameHeader header)
    {
        header = default;
        if (source.Length < Size || !source[..4].SequenceEqual(Magic) || source[4] != DisplayWire.HeaderVersion)
        {
            return false;
        }

        var kind = source[5];
        if (kind > (byte)DisplayFrameKind.Control)
        {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt32BigEndian(source[24..]);
        if (length > DisplayWire.MaxPayloadBytes)
        {
            return false;
        }

        header = new DisplayFrameHeader(
            (DisplayFrameKind)kind,
            BinaryPrimitives.ReadUInt32BigEndian(source[8..]),
            BinaryPrimitives.ReadUInt16BigEndian(source[12..]),
            BinaryPrimitives.ReadUInt16BigEndian(source[14..]),
            BinaryPrimitives.ReadUInt16BigEndian(source[16..]),
            BinaryPrimitives.ReadUInt16BigEndian(source[18..]),
            BinaryPrimitives.ReadUInt16BigEndian(source[20..]),
            BinaryPrimitives.ReadUInt16BigEndian(source[22..]),
            length);
        return true;
    }
}

/// <summary>What the client learns on open: the display's geometry and who is driving.</summary>
public sealed record DisplayInfo(int Width, int Height, int Dpi, DisplayControlHolder Holder, string? HolderDeviceId);

/// <summary>Control changed hands (also appended to the log as <see cref="DisplayControlChangedEvent"/>).</summary>
public sealed record DisplayControlNotice(DisplayControlHolder Holder, string? DeviceId);

/// <summary>Client → host. All coordinates are guest pixels; the client maps from its own view.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "t")]
[JsonDerivedType(typeof(DisplayPointerMove), "move")]
[JsonDerivedType(typeof(DisplayPointerButton), "button")]
[JsonDerivedType(typeof(DisplayPointerScroll), "scroll")]
[JsonDerivedType(typeof(DisplayKey), "key")]
[JsonDerivedType(typeof(DisplayQuality), "quality")]
[JsonDerivedType(typeof(DisplayControlRequest), "control")]
public abstract record DisplayClientMessage;

public sealed record DisplayPointerMove(int X, int Y) : DisplayClientMessage;

/// <param name="Button">0 left, 1 middle, 2 right.</param>
public sealed record DisplayPointerButton(int Button, bool Down) : DisplayClientMessage;

public sealed record DisplayPointerScroll(int X, int Y, int Dx, int Dy) : DisplayClientMessage;

/// <param name="Key">X keysym / xdotool name: <c>Return</c>, <c>ctrl</c>, <c>a</c>, <c>KP_0</c>.</param>
public sealed record DisplayKey(string Key, bool Down) : DisplayClientMessage;

/// <summary>What this subscriber can use; the host serves the smallest size any subscriber asked for and
/// caps the frame rate per subscriber, dropping rather than queueing for a slow one.</summary>
public sealed record DisplayQuality(int MaxWidth, int MaxFps, int JpegQuality = 75) : DisplayClientMessage;

/// <summary>Take control (the agent's input tools then refuse) or hand it back.</summary>
public sealed record DisplayControlRequest(bool Take) : DisplayClientMessage;
