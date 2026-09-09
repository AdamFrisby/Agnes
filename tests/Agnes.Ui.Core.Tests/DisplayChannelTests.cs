using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Agnes.Abstractions;
using Agnes.Client;
using Agnes.Protocol;

namespace Agnes.Ui.Core.Tests;

/// <summary>
/// The display channel's client half: address, framing, and the one policy that keeps a slow client from
/// drifting further behind forever.
/// </summary>
public class DisplayChannelTests
{
    private static byte[] Image(uint sequence, int size = 64, DisplayFrameKind kind = DisplayFrameKind.Full)
    {
        var payload = new byte[size];
        Array.Fill(payload, (byte)(sequence & 0xFF));
        return DisplayFrameCodec.Encode(
            new DisplayFrameHeader(kind, sequence, 0, 0, 1280, 800, 1280, 800, (uint)payload.Length),
            payload);
    }

    private static byte[] Info(int width = 1280, int height = 800)
        => DisplayFrameCodec.EncodeJson(
            DisplayFrameKind.Info, 0, new DisplayInfo(width, height, 96, DisplayControlHolder.Agent, null), width, height);

    private static async Task<List<DisplayFrame>> DrainAsync(IDisplayChannel channel, int expected, TimeSpan timeout)
    {
        var frames = new List<DisplayFrame>();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await foreach (var frame in channel.Frames.ReadAllAsync(cts.Token))
            {
                frames.Add(frame);
                if (frames.Count >= expected)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Fewer than expected within the timeout: the assertions say what that means.
        }

        return frames;
    }

    [Fact]
    public void The_address_is_the_hub_s_with_a_websocket_scheme_and_the_same_device_token()
    {
        var uri = DisplayChannelClient.BuildUri("https://box.local:5081", "s-1", "tok en/+");

        Assert.Equal("wss", uri.Scheme);
        Assert.Equal("box.local", uri.Host);
        Assert.Equal(5081, uri.Port);
        Assert.Equal("/display/s-1", uri.AbsolutePath);
        // Escaped, not pasted: a token with a slash or a plus in it must not restructure the URL.
        Assert.Contains("access_token=tok%20en%2F%2B", uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void A_plain_http_host_gets_a_plain_websocket_scheme()
        => Assert.Equal("ws", DisplayChannelClient.BuildUri("http://localhost:5081/", "s-1", "t").Scheme);

    [Fact]
    public async Task Frames_are_parsed_off_the_socket_with_their_headers_and_payloads_intact()
    {
        var socket = new FakeDuplexWebSocket();
        await using var channel = DisplayChannelClient.FromSocket(socket);

        socket.Push(Info());
        socket.Push(Image(7, size: 128));

        var frames = await DrainAsync(channel, 2, TimeSpan.FromSeconds(5));

        Assert.Equal(2, frames.Count);
        Assert.Equal(DisplayFrameKind.Info, frames[0].Header.Kind);
        var info = JsonSerializer.Deserialize<DisplayInfo>(frames[0].Payload.Span, DisplayWire.Json);
        Assert.Equal(1280, info!.Width);
        Assert.Equal(800, info.Height);

        Assert.Equal(DisplayFrameKind.Full, frames[1].Header.Kind);
        Assert.Equal(7u, frames[1].Header.Sequence);
        Assert.Equal(128, frames[1].Payload.Length);
        Assert.True(frames[1].IsImage);
    }

    [Fact]
    public async Task A_frame_whose_payload_does_not_match_its_header_ends_the_stream()
    {
        var socket = new FakeDuplexWebSocket();
        await using var channel = DisplayChannelClient.FromSocket(socket);

        // A header that claims 4096 bytes followed by 8. Truncation must be an error, not a short frame
        // handed on to a decoder.
        var lying = DisplayFrameCodec.Encode(
            new DisplayFrameHeader(DisplayFrameKind.Full, 1, 0, 0, 1280, 800, 1280, 800, 4096),
            new byte[8]);
        socket.Push(lying);

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var seen = 0;
            await foreach (var _ in channel.Frames.ReadAllAsync(cts.Token))
            {
                seen++; // draining until the reader faults is the assertion
            }

            Assert.Equal(0, seen);
        });
    }

    [Fact]
    public async Task Under_pressure_the_oldest_images_are_dropped_and_the_newest_survive()
    {
        var socket = new FakeDuplexWebSocket();
        await using var channel = DisplayChannelClient.FromSocket(socket);

        // Nobody reads while these arrive, which is exactly the case the policy exists for.
        for (uint i = 1; i <= 40; i++)
        {
            socket.Push(Image(i));
        }

        await WaitForQuietAsync(channel);

        var frames = await DrainAsync(channel, 40, TimeSpan.FromSeconds(1));

        Assert.NotEmpty(frames);
        // Bounded: a client that fell behind holds a handful of frames, not the forty that were sent.
        Assert.True(frames.Count <= 8, $"queue grew to {frames.Count} frames");
        // And what it kept is the recent end of the stream, not the stale front of it.
        Assert.Equal(40u, frames[^1].Header.Sequence);
        Assert.True(frames[0].Header.Sequence > 30, $"oldest surviving frame was {frames[0].Header.Sequence}");
    }

    [Fact]
    public async Task Info_and_control_frames_are_never_dropped_however_far_behind_the_client_falls()
    {
        var socket = new FakeDuplexWebSocket();
        await using var channel = DisplayChannelClient.FromSocket(socket);

        socket.Push(Info());
        for (uint i = 1; i <= 40; i++)
        {
            socket.Push(Image(i));
            if (i == 20)
            {
                socket.Push(DisplayFrameCodec.EncodeJson(
                    DisplayFrameKind.Control,
                    i,
                    new DisplayControlNotice(DisplayControlHolder.User, "device-9"),
                    1280,
                    800));
            }
        }

        await WaitForQuietAsync(channel);

        var frames = await DrainAsync(channel, 60, TimeSpan.FromSeconds(1));

        // Geometry and who is driving are facts, not pictures: they survive the whole flood.
        Assert.Single(frames, f => f.Header.Kind == DisplayFrameKind.Info);
        var control = Assert.Single(frames, f => f.Header.Kind == DisplayFrameKind.Control);
        var notice = JsonSerializer.Deserialize<DisplayControlNotice>(control.Payload.Span, DisplayWire.Json);
        Assert.Equal(DisplayControlHolder.User, notice!.Holder);
        Assert.Equal("device-9", notice.DeviceId);
    }

    [Fact]
    public async Task Input_goes_back_as_the_channel_s_own_json_dialect()
    {
        var socket = new FakeDuplexWebSocket();
        await using var channel = DisplayChannelClient.FromSocket(socket);

        await channel.SendAsync(new DisplayPointerMove(640, 400));
        await channel.SendAsync(new DisplayKey("Return", true));

        var sent = socket.Sent;
        Assert.Equal(2, sent.Count);
        Assert.All(sent, m => Assert.Equal(WebSocketMessageType.Text, m.Type));
        Assert.Equal("""{"t":"move","x":640,"y":400}""", Encoding.UTF8.GetString(sent[0].Data));
        Assert.Equal("""{"t":"key","key":"Return","down":true}""", Encoding.UTF8.GetString(sent[1].Data));
    }

    /// <summary>Waits until the reader has stopped adding frames, so a drop-policy assertion is not racing it.</summary>
    private static async Task WaitForQuietAsync(IDisplayChannel channel)
    {
        var previous = -1;
        for (var i = 0; i < 100; i++)
        {
            await Task.Delay(20);
            var count = channel.Frames.Count;
            if (count == previous && count > 0)
            {
                return;
            }

            previous = count;
        }
    }
}
