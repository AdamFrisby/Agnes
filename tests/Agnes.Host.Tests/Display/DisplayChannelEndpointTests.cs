using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Agnes.Abstractions;
using Agnes.Host.Display;
using Agnes.Host.Hosting;
using Agnes.Host.Sharing;
using Agnes.Protocol;
using Agnes.Sandbox;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Tests.Display;

/// <summary>
/// The display channel end to end over a real socket on a real Kestrel: the auth wall, the authorization
/// decision, the first Info frame, a frame after the guest paints, input arriving from a client, and a
/// person's "take control" locking the agent out.
/// <para>
/// Stood up the way <c>Program</c> stands it up rather than mocked, because everything this guards is
/// wiring: a middleware in the wrong order, an upgrade that happens before the 401, a decision asked of the
/// wrong service. The one substitution is the access decision itself — the sharing stack is exercised by its
/// own tests, so here it is a stub that says yes or no on demand.
/// </para>
/// </summary>
public sealed class DisplayChannelEndpointTests : IAsyncLifetime
{
    private const string Session = "sess-graphical";
    private const string DeviceToken = "device-token";

    private WebApplication _app = null!;
    private StubSessionSource _sessions = null!;
    private StubDisplaySource _source = null!;
    private DisplayBrokerRegistry _registry = null!;
    private StubAccessAuthorizer _access = null!;
    private string _base = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        _sessions = new StubSessionSource();
        _source = DisplayFixture.NewSource(_sessions, Session, width: 64, height: 48);
        var options = DisplayFixture.Options();
        _registry = DisplayFixture.Registry(_sessions, options);
        _access = new StubAccessAuthorizer();

        var devices = new DeviceRegistry(
            DeviceToken, Path.Combine(Path.GetTempPath(), $"agnes-devices-{Guid.NewGuid():n}.json"));

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(_registry);
        builder.Services.AddSingleton(devices);
        builder.Services.AddSingleton<SessionAccessDecider>(_access);

        _app = builder.Build();

        // The same 401 wall Program installs in front of the path, before anything can be upgraded.
        _app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments(DisplayWire.Path)
                && !devices.IsValid(ctx.Request.Query[WireProtocol.TokenParameter].ToString()))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await next();
        });
        _app.UseWebSockets();
        _app.MapDisplayChannel();

        await _app.StartAsync();
        _base = _app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!
            .Addresses.First();
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        await _registry.DisposeAsync();
    }

    /// <summary>Stands in for the real sharing stack: the decision, not how it is reached, is what this
    /// endpoint's tests are about.</summary>
    private sealed class StubAccessAuthorizer : SessionAccessDecider
    {
        public StubAccessAuthorizer()
            : base(null!, null!, null!)
        {
        }

        public bool CanSubscribe { get; set; } = true;
        public bool CanPrompt { get; set; } = true;

        public override SharingCaller CallerFor(string? token) => new("device-a", null, IsOwner: true);

        public override Task<bool> DecideAsync(
            string sessionId, SessionAccessKind kind, SharingCaller caller, CancellationToken cancellationToken = default)
            => Task.FromResult(kind == SessionAccessKind.Subscribe ? CanSubscribe : CanPrompt);
    }

    private Uri SocketUri(string token) =>
        new($"{_base.Replace("http://", "ws://", StringComparison.Ordinal)}{DisplayWire.Path}/{Session}?{WireProtocol.TokenParameter}={token}");

    private async Task<ClientWebSocket> ConnectAsync(string token = DeviceToken)
    {
        var socket = new ClientWebSocket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(SocketUri(token), timeout.Token);
        return socket;
    }

    private static async Task<(DisplayFrameHeader Header, byte[] Payload)> ReadFrameAsync(WebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[DisplayWire.MaxPayloadBytes / 8];
        var received = 0;
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, received, buffer.Length - received), timeout.Token);
            received += result.Count;
        }
        while (!result.EndOfMessage);

        Assert.True(DisplayFrameHeader.TryRead(buffer, out var header), "a host → client message must start with a valid header");
        return (header, buffer[DisplayFrameHeader.Size..received]);
    }

    private static Task SendAsync(WebSocket socket, DisplayClientMessage message)
        => socket.SendAsync(
            JsonSerializer.SerializeToUtf8Bytes(message, DisplayWire.Json),
            WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

    // ---- the wall ----

    [Fact]
    public async Task Without_a_token_the_channel_is_401_before_any_upgrade()
    {
        using var http = new HttpClient { BaseAddress = new Uri(_base) };
        var response = await http.GetAsync($"{DisplayWire.Path}/{Session}");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_bad_token_is_401_too()
    {
        var socket = new ClientWebSocket();
        var failed = await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(SocketUri("not-a-token"), CancellationToken.None));
        Assert.Contains("401", failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_device_with_no_access_to_the_session_is_403()
    {
        _access.CanSubscribe = false;

        var socket = new ClientWebSocket();
        var failed = await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(SocketUri(DeviceToken), CancellationToken.None));
        Assert.Contains("403", failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_session_with_no_display_is_404()
    {
        using var http = new HttpClient { BaseAddress = new Uri(_base) };
        var response = await http.GetAsync($"{DisplayWire.Path}/headless?{WireProtocol.TokenParameter}={DeviceToken}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- the protocol ----

    [Fact]
    public async Task Info_comes_first_and_states_the_geometry_and_holder()
    {
        using var socket = await ConnectAsync();

        var (header, payload) = await ReadFrameAsync(socket);
        Assert.Equal(DisplayFrameKind.Info, header.Kind);

        var info = JsonSerializer.Deserialize<DisplayInfo>(payload, DisplayWire.Json)!;
        Assert.Equal(64, info.Width);
        Assert.Equal(48, info.Height);
        Assert.Equal(DisplayControlHolder.None, info.Holder);
    }

    [Fact]
    public async Task A_joining_client_gets_a_full_frame_then_a_frame_per_repaint()
    {
        using var socket = await ConnectAsync();
        await ReadFrameAsync(socket); // Info

        var (joinHeader, joinPayload) = await ReadFrameAsync(socket);
        Assert.Equal(DisplayFrameKind.Full, joinHeader.Kind);
        Assert.Equal(64, joinHeader.DisplayWidth);
        Assert.True(joinPayload.Length > 0);

        _source.Session.Paint(4, 4, 8, 8);
        var (tileHeader, _) = await ReadFrameAsync(socket);
        Assert.Equal(DisplayFrameKind.Tile, tileHeader.Kind);
        Assert.Equal(4, tileHeader.X);
        Assert.Equal(8, tileHeader.Width);
    }

    [Fact]
    public async Task A_move_from_an_authorized_client_reaches_the_guest()
    {
        using var socket = await ConnectAsync();
        await ReadFrameAsync(socket); // Info

        await SendAsync(socket, new DisplayControlRequest(Take: true));
        await SendAsync(socket, new DisplayPointerMove(11, 22));

        await WaitForAsync(() => _source.Session.Snapshot().Contains(new PointerMove(11, 22)));
    }

    [Fact]
    public async Task A_view_only_client_may_watch_but_not_drive()
    {
        _access.CanPrompt = false;
        using var socket = await ConnectAsync();
        await ReadFrameAsync(socket); // Info
        await ReadFrameAsync(socket); // the join frame — watching works

        await SendAsync(socket, new DisplayControlRequest(Take: true));
        await SendAsync(socket, new DisplayPointerMove(1, 1));

        // Give the receive loop a chance to have done the wrong thing.
        _source.Session.Paint(0, 0, 4, 4);
        await ReadFrameAsync(socket);

        Assert.Empty(_source.Session.Snapshot());
        var broker = await _registry.GetOrCreateAsync(Session);
        Assert.Equal(DisplayControlHolder.None, broker.Arbiter.Holder);
    }

    [Fact]
    public async Task Taking_control_pushes_a_control_frame_and_locks_the_agent_out()
    {
        using var socket = await ConnectAsync();
        await ReadFrameAsync(socket); // Info
        await ReadFrameAsync(socket); // join frame

        await SendAsync(socket, new DisplayControlRequest(Take: true));

        var (header, payload) = await ReadFrameAsync(socket);
        Assert.Equal(DisplayFrameKind.Control, header.Kind);
        var notice = JsonSerializer.Deserialize<DisplayControlNotice>(payload, DisplayWire.Json)!;
        Assert.Equal(DisplayControlHolder.User, notice.Holder);
        Assert.Equal("device-a", notice.DeviceId);

        var broker = await _registry.GetOrCreateAsync(Session);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => broker.InjectAgentAsync([new PointerMove(1, 1)]));
        Assert.Equal(InputArbiter.UserHoldsMessage, refused.Message);
    }

    [Fact]
    public async Task Closing_the_channel_hands_the_display_back()
    {
        var socket = await ConnectAsync();
        await ReadFrameAsync(socket); // Info
        await SendAsync(socket, new DisplayControlRequest(Take: true));

        var broker = await _registry.GetOrCreateAsync(Session);
        await WaitForAsync(() => broker.Arbiter.Holder == DisplayControlHolder.User);

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        socket.Dispose();

        await WaitForAsync(() => broker.Arbiter.Holder == DisplayControlHolder.None);
    }

    [Fact]
    public async Task A_malformed_message_closes_the_channel_with_a_policy_violation()
    {
        using var socket = await ConnectAsync();
        await ReadFrameAsync(socket); // Info

        await socket.SendAsync(
            Encoding.UTF8.GetBytes("{\"t\":\"nonsense\"}"), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[64 * 1024];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, timeout.Token);
        }
        while (result.MessageType != WebSocketMessageType.Close);

        Assert.Equal(WebSocketCloseStatus.PolicyViolation, socket.CloseStatus);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Fail("the condition never became true");
    }
}
