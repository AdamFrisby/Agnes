using System.Threading.Channels;
using Agnes.Protocol;

namespace Agnes.Client;

/// <summary>One host → client message off the display channel: the parsed header and its payload.</summary>
public sealed record DisplayFrame(DisplayFrameHeader Header, ReadOnlyMemory<byte> Payload)
{
    public bool IsImage => Header.Kind is DisplayFrameKind.Full or DisplayFrameKind.Tile;
}

/// <summary>
/// A client's open connection to one session's display. Frames arrive on <see cref="Frames"/> (the first
/// is always <see cref="DisplayFrameKind.Info"/>); input and preferences go back with <see cref="SendAsync"/>.
/// Obtained from <see cref="IAgnesHost.OpenDisplayAsync"/>, which speaks the pinned-TLS WebSocket.
/// </summary>
public interface IDisplayChannel : IAsyncDisposable
{
    ChannelReader<DisplayFrame> Frames { get; }

    Task SendAsync(DisplayClientMessage message, CancellationToken cancellationToken = default);
}
