using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Agnes.Host.Hosting;
using Agnes.Host.Sharing;
using Agnes.Protocol;
using Agnes.Sandbox;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Display;

/// <summary>
/// The display channel's front door: one authenticated binary WebSocket per watching client, at
/// <c>/display/{sessionId}</c> on the host's main TLS listener.
/// <para>
/// Deliberately NOT the SignalR hub. The hub is one ordered broadcast per session with a default message cap
/// and base64'd byte arrays — every one of those is wrong for a hot stream of JPEGs, and a slow phone on the
/// hub would apply back-pressure to the transcript everyone else is reading. A separate socket also means a
/// dropped frame is a dropped frame and nothing else: frames are views, the log is facts, and the two never
/// share a queue.
/// </para>
/// <para>Authentication is the device token in the <c>access_token</c> query, exactly as the hub's negotiate
/// gate reads it. Authorization is the same per-session decision the hub makes — Subscribe to watch, Prompt
/// to touch anything — asked through <see cref="SessionAccessDecider"/> so there is one policy, not two.</para>
/// </summary>
public static class DisplayChannelEndpoint
{
    /// <summary>Largest client → host text message accepted. A control message is tens of bytes; anything
    /// approaching this is not a client of ours.</summary>
    private const int MaxClientMessageBytes = 8 * 1024;

    /// <summary>
    /// How long a Prompt decision is reused on an open channel. Re-asking the share store for every pointer
    /// move would put an access lookup on the hottest path in the system; never re-asking would let a revoked
    /// collaborator keep driving until they disconnected. Five seconds bounds the revocation window to
    /// something a person cannot exploit and a machine cannot notice.
    /// </summary>
    private static readonly TimeSpan WriteDecisionTtl = TimeSpan.FromSeconds(5);

    public static void MapDisplayChannel(this WebApplication app)
    {
        app.Map(DisplayWire.Path + "/{sessionId}", async (HttpContext context, string sessionId) =>
        {
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Agnes.Display");
            var devices = context.RequestServices.GetRequiredService<DeviceRegistry>();
            var decider = context.RequestServices.GetRequiredService<SessionAccessDecider>();
            var registry = context.RequestServices.GetRequiredService<DisplayBrokerRegistry>();
            var options = context.RequestServices.GetRequiredService<DisplayOptions>();

            var token = context.Request.Query[WireProtocol.TokenParameter].ToString();
            if (!devices.IsValid(token))
            {
                // 401 before the upgrade, so a client with a stale token gets an HTTP answer it can read
                // rather than a socket that closes for no stated reason. Public links are deliberately not
                // accepted here: a public link grants a read-only transcript, never a live screen.
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            var caller = decider.CallerFor(token);
            if (!await decider.DecideAsync(sessionId, SessionAccessKind.Subscribe, caller, context.RequestAborted).ConfigureAwait(false))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            if (!registry.HasDisplay(sessionId))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            DisplayBroker broker;
            try
            {
                broker = await registry.GetOrCreateAsync(sessionId, context.RequestAborted).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not open the display for session {SessionId}.", sessionId);
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            await RunAsync(socket, broker, decider, caller, sessionId, options, logger, context.RequestAborted).ConfigureAwait(false);
        });
    }

    private static async Task RunAsync(
        WebSocket socket,
        DisplayBroker broker,
        SessionAccessDecider decider,
        SharingCaller caller,
        string sessionId,
        DisplayOptions options,
        ILogger logger,
        CancellationToken requestAborted)
    {
        var deviceId = caller.DeviceId ?? caller.GitHubLogin ?? "unknown";
        var subscriber = broker.AddSubscriber(new DisplayQuality(broker.Geometry.Width, options.MaxFps, options.JpegQuality));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        var writeLock = new SemaphoreSlim(1, 1);

        try
        {
            // Info first, always: the client cannot map a click to a guest coordinate, or draw the right
            // affordance for who is driving, until it knows the geometry and the holder.
            var current = broker.Arbiter.Current;
            await SendJsonAsync(
                socket, writeLock, DisplayFrameKind.Info,
                new DisplayInfo(broker.Geometry.Width, broker.Geometry.Height, broker.Display.Dpi, current.Holder, current.DeviceId),
                broker.Geometry, cts.Token).ConfigureAwait(false);

            var send = SendLoopAsync(socket, writeLock, broker, subscriber, cts.Token);
            var receive = ReceiveLoopAsync(socket, broker, subscriber, decider, caller, deviceId, sessionId, logger, cts.Token);
            await Task.WhenAny(send, receive).ConfigureAwait(false);
            await cts.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(Swallow(send), Swallow(receive)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            // A client going away is the normal end of a display channel, not an incident.
        }
        finally
        {
            // The channel closing releases any hold this device had: a browser tab that vanished must not
            // keep the agent locked out of its own screen until the idle timer catches up.
            await broker.ReleaseControlForAsync(deviceId, CancellationToken.None).ConfigureAwait(false);
            broker.RemoveSubscriber(subscriber);
            writeLock.Dispose();
        }
    }

    private static async Task SendLoopAsync(
        WebSocket socket, SemaphoreSlim writeLock, DisplayBroker broker, DisplaySubscriber subscriber, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var (damage, notices) = await subscriber.TakeAsync(cancellationToken).ConfigureAwait(false);

            foreach (var notice in notices)
            {
                await SendJsonAsync(socket, writeLock, DisplayFrameKind.Control, notice, broker.Geometry, cancellationToken).ConfigureAwait(false);
            }

            // Damage gating: nothing changed on the guest, nothing goes on the wire. A still screen costs
            // exactly one Info frame for the life of the connection.
            if (damage.IsEmpty)
            {
                continue;
            }

            if (broker.BuildFrame(subscriber, damage) is { } frame)
            {
                await SendFrameAsync(socket, writeLock, frame.Header, frame.Payload, cancellationToken).ConfigureAwait(false);
            }

            // Pace to the rate this subscriber asked for. Anything the guest paints during the wait is
            // unioned into the next frame rather than queued behind this one.
            await Task.Delay(subscriber.FrameInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ReceiveLoopAsync(
        WebSocket socket,
        DisplayBroker broker,
        DisplaySubscriber subscriber,
        SessionAccessDecider decider,
        SharingCaller caller,
        string deviceId,
        string sessionId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxClientMessageBytes];
        var mayWrite = false;
        var mayWriteCheckedAt = DateTimeOffset.MinValue;

        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var received = 0;
            WebSocketReceiveResult result;
            do
            {
                if (received >= buffer.Length)
                {
                    await CloseAsync(socket, "A display message was too large.", cancellationToken).ConfigureAwait(false);
                    return;
                }

                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, received, buffer.Length - received), cancellationToken)
                    .ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                received += result.Count;
            }
            while (!result.EndOfMessage);

            DisplayClientMessage? message;
            try
            {
                message = JsonSerializer.Deserialize<DisplayClientMessage>(
                    new ReadOnlySpan<byte>(buffer, 0, received), DisplayWire.Json);
            }
            catch (JsonException)
            {
                await CloseAsync(socket, "A display message was not valid.", cancellationToken).ConfigureAwait(false);
                return;
            }

            if (message is null)
            {
                await CloseAsync(socket, "A display message was not valid.", cancellationToken).ConfigureAwait(false);
                return;
            }

            // Quality is a property of this subscriber's own stream, not an action on the session, so a
            // view-only watcher may set it.
            if (message is DisplayQuality quality)
            {
                subscriber.SetQuality(quality);
                continue;
            }

            if (DateTimeOffset.UtcNow - mayWriteCheckedAt > WriteDecisionTtl)
            {
                mayWrite = await decider.DecideAsync(sessionId, SessionAccessKind.Prompt, caller, cancellationToken).ConfigureAwait(false);
                mayWriteCheckedAt = DateTimeOffset.UtcNow;
            }

            if (!mayWrite)
            {
                // Watching is allowed, driving is not. Dropped rather than fatal: a view-only client that
                // sends a stray move on a drag should keep watching, not be disconnected.
                continue;
            }

            if (message is DisplayControlRequest control)
            {
                await broker.RequestControlAsync(deviceId, control.Take, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (ToInput(message) is { } input)
            {
                await broker.InjectUserAsync(input, deviceId, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The wire's client messages mapped onto the display seam's input union. Two vocabularies
    /// meet here and nowhere else, which is why the mapping is a single expression.</summary>
    private static DisplayInput? ToInput(DisplayClientMessage message) => message switch
    {
        DisplayPointerMove m => new PointerMove(m.X, m.Y),
        DisplayPointerButton b => new PointerButton(ButtonKind(b.Button), b.Down),
        DisplayPointerScroll s => new PointerScroll(s.X, s.Y, s.Dx, s.Dy),
        DisplayKey k => new KeyPress(k.Key, k.Down),
        _ => null,
    };

    private static PointerButtonKind ButtonKind(int button) => button switch
    {
        1 => PointerButtonKind.Middle,
        2 => PointerButtonKind.Right,
        _ => PointerButtonKind.Left,
    };

    private static async Task SendJsonAsync<T>(
        WebSocket socket, SemaphoreSlim writeLock, DisplayFrameKind kind, T payload, DisplayGeometry geometry, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, DisplayWire.Json);
        var header = new DisplayFrameHeader(
            kind, 0, 0, 0, 0, 0, (ushort)geometry.Width, (ushort)geometry.Height, (uint)json.Length);
        await SendFrameAsync(socket, writeLock, header, json, cancellationToken).ConfigureAwait(false);
    }

    private static async Task SendFrameAsync(
        WebSocket socket, SemaphoreSlim writeLock, DisplayFrameHeader header, byte[] payload, CancellationToken cancellationToken)
    {
        var message = new byte[DisplayFrameHeader.Size + payload.Length];
        header.Write(message);
        payload.CopyTo(message.AsSpan(DisplayFrameHeader.Size));

        // One writer at a time: a Control notice racing a frame would interleave two messages on the wire.
        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(message, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeLock.Release();
        }
    }

    private static Task CloseAsync(WebSocket socket, string reason, CancellationToken cancellationToken)
        => socket.State == WebSocketState.Open
            ? socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, Truncate(reason), cancellationToken)
            : Task.CompletedTask;

    // A close reason is capped at 123 UTF-8 bytes by the protocol; exceeding it throws rather than truncates.
    private static string Truncate(string reason)
        => Encoding.UTF8.GetByteCount(reason) <= 120 ? reason : reason[..60];

    private static async Task Swallow(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
            // The other loop already ended the channel.
        }
    }
}
