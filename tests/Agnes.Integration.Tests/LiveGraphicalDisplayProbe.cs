using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Agnes.Abstractions;
using Agnes.Abstractions.Events;
using Agnes.Client;
using Agnes.Host.Display;
using Agnes.Host.Hosting;
using Agnes.Host.Mcp;
using Agnes.Host.Sessions;
using Agnes.Host.Sharing;
using Agnes.Protocol;
using Agnes.Sandbox;
using Agnes.Sandbox.Incus;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using Xunit.Abstractions;

namespace Agnes.Integration.Tests;

/// <summary>
/// The graphical sandbox, all four layers at once, against real pixels: a real Incus VM with a real X
/// session, captured through the real <see cref="IDisplaySource"/>, brokered by a real host over a real
/// WebSocket to the real <see cref="DisplayChannelClient"/>, and driven by the real <c>computer_*</c> MCP
/// tools.
/// <para>
/// Every layer here has unit tests over a fake display, and every one of them passes on a host where the
/// feature does not work at all: a fake never fails to mode-set, never boots from an image with no X server,
/// never takes 40 seconds to paint its first frame. This is the test that can only pass if the whole thing
/// is true, which is also why it is silent unless explicitly asked for.
/// </para>
/// <para>
/// Silent (and passing) unless <c>AGNES_LIVE_GRAPHICAL=1</c> and Incus answers. It bakes the
/// <c>agnes-graphical</c> image if it is missing — minutes, logged as it goes — and leaves it behind, since
/// that image is the feature's baseline. It creates exactly ONE instance, named with its own
/// <see cref="IncusOptions.InstancePrefix"/> so it can never be confused with a session VM somebody is
/// working in, and deletes that instance (and stops its bus daemon) in a finally.
/// </para>
/// </summary>
public sealed class LiveGraphicalDisplayProbe
{
    private const string SessionId = "sess-live-graphical";
    private const string DeviceToken = "probe-device-token";
    private const string DeviceId = "device-a";

    /// <summary>The guest needs a moment after "ready" for Xorg and the session script to paint something.</summary>
    private static readonly TimeSpan XSessionSettle = TimeSpan.FromSeconds(20);

    private readonly ITestOutputHelper _out;

    public LiveGraphicalDisplayProbe(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task Drives_a_real_graphical_sandbox_from_the_client_and_from_the_agents_tools()
    {
        if (Environment.GetEnvironmentVariable("AGNES_LIVE_GRAPHICAL") != "1" || !IncusAnswers())
        {
            return;
        }

        var outputDirectory = Environment.GetEnvironmentVariable("AGNES_LIVE_GRAPHICAL_OUT")
            ?? Path.Combine(Path.GetTempPath(), "agnes-live-display");
        Directory.CreateDirectory(outputDirectory);

        using var loggers = LoggerFactory.Create(b => b
            .AddProvider(new TestOutputLoggerProvider(_out))
            .SetMinimumLevel(LogLevel.Information));

        // The live host runs its VMs in the default project on the codeybox-zfs pool over the cb-net bridge
        // (docs/sandbox-live-testing.md); mirror that so the probe talks to the same Incus the daemon does.
        var options = new IncusOptions
        {
            ProjectName = Environment.GetEnvironmentVariable("AGNES_LIVE_INCUS_PROJECT") ?? "default",
            StoragePoolName = Environment.GetEnvironmentVariable("AGNES_LIVE_INCUS_POOL") ?? "codeybox-zfs",
            Bridge = Environment.GetEnvironmentVariable("AGNES_LIVE_INCUS_BRIDGE") ?? "cb-net",
            InstancePrefix = "agnes-probe-",
            // Longer than the daemon's default three minutes. This runs on a developer machine with other
            // VMs on the same pool, and a first boot there is genuinely slower than on a host that exists to
            // run sandboxes; a probe that gives up early reports "the feature is broken" when the truth is
            // "the laptop was busy".
            GuestReadyTimeout = TimeSpan.FromMinutes(8),
        };

        var provider = new IncusSandboxProvider(options, loggers);

        // ---- 1. the image tier ----------------------------------------------------------------------
        // The same manifest SandboxImageManager.EnsureGraphicalAsync bakes: the baseline plus a desktop,
        // under the alias a graphical session launches from.
        var manifest = new SandboxImageManifest().AsGraphical();
        if (!await ((ISandboxImageBuilder)provider).ImageExistsAsync(manifest.Alias))
        {
            _out.WriteLine($"[bake] {manifest.Alias} is missing — baking (this takes minutes)");
            var baked = Stopwatch.StartNew();
            await ((ISandboxImageBuilder)provider).BuildImageAsync(
                manifest, new Progress<string>(line => _out.WriteLine("[bake] " + line)));
            _out.WriteLine($"[bake] {manifest.Alias} ready in {baked.Elapsed.TotalMinutes:F1} min");
        }
        else
        {
            _out.WriteLine($"[bake] {manifest.Alias} already present");
        }

        // ---- 2. one graphical sandbox, built the way SessionManager builds one -----------------------
        // Mirrored rather than reused: OpenSessionCoreAsync also launches an agent, resolves a project and
        // materialises credentials, none of which this probe wants. The spec below is the same spec, minus
        // the host working directory (nothing here reads /work) — see SessionManager.OpenSessionCoreAsync,
        // which passes the graphical image alias and GraphicalDisplay.Default.
        var provisioning = Stopwatch.StartNew();
        var sandbox = await provider.CreateAsync(new SandboxSpec
        {
            ImageReference = manifest.Alias,
            Display = GraphicalDisplay.Default,
        });
        var provisioned = provisioning.Elapsed;
        _out.WriteLine($"[vm] {sandbox.Id} provisioned in {provisioned.TotalSeconds:F0} s");

        try
        {
            Assert.IsAssignableFrom<IDisplaySource>(sandbox);
            var source = (IDisplaySource)sandbox;

            // Xorg and the session script start a few seconds after the guest reports ready; the capture
            // itself would connect before then and wait for a scanout that has not happened yet.
            await Task.Delay(XSessionSettle);

            await using var harness = await ProbeHost.StartAsync(source, loggers);

            // ---- 3. the real client over the real channel --------------------------------------------
            var opened = Stopwatch.StartNew();
            await using var channel = await DisplayChannelClient.ConnectAsync(
                harness.BaseUrl, SessionId, DeviceToken, pinnedFingerprint: null);

            // ---- 4a. the first frame is Info, and it states the guest's real geometry ----------------
            var info = await NextFrameAsync(channel, TimeSpan.FromSeconds(10));
            Assert.Equal(DisplayFrameKind.Info, info.Header.Kind);
            var geometry = JsonSerializer.Deserialize<DisplayInfo>(info.Payload.Span, DisplayWire.Json)!;
            Assert.Equal(1280, geometry.Width);
            Assert.Equal(800, geometry.Height);
            Assert.Equal(DisplayControlHolder.None, geometry.Holder);

            // ---- 4b. and pixels follow, as a JPEG of that screen -------------------------------------
            var first = await NextImageFrameAsync(channel, TimeSpan.FromSeconds(10));
            var toFirstFrame = opened.Elapsed;
            AssertPlausibleJpeg(first, 1280, 800);
            var firstFramePath = Path.Combine(outputDirectory, "first-frame.jpg");
            await File.WriteAllBytesAsync(firstFramePath, first.Payload.ToArray());
            _out.WriteLine($"[frame] first image {first.Payload.Length} bytes -> {firstFramePath}");

            // ---- 4c. a person takes control ----------------------------------------------------------
            await channel.SendAsync(new DisplayControlRequest(Take: true));
            var taken = await WaitForControlAsync(channel, DisplayControlHolder.User, TimeSpan.FromSeconds(5));
            Assert.Equal(DeviceId, taken.DeviceId);

            // ---- 4d. …and drives it: a right-click on the desktop opens openbox's root menu -----------
            // Bottom-right on purpose: the guest session puts a 100x30 xterm at +40+40, so the middle of
            // the screen is inside that window and a right-click there is an xterm selection gesture that
            // paints nothing. The root window is where a right-click is unambiguously visible.
            var clicked = Stopwatch.StartNew();
            await channel.SendAsync(new DisplayPointerMove(1180, 720));
            await channel.SendAsync(new DisplayPointerButton(Button: 2, Down: true));
            await channel.SendAsync(new DisplayPointerButton(Button: 2, Down: false));
            var afterClick = await NextImageFrameAsync(channel, TimeSpan.FromSeconds(2));
            var toClickFrame = clicked.Elapsed;
            AssertPlausibleJpeg(afterClick, 1280, 800);
            var clickFramePath = Path.Combine(outputDirectory, "after-right-click.jpg");
            await File.WriteAllBytesAsync(clickFramePath, afterClick.Payload.ToArray());
            _out.WriteLine($"[frame] post-click image {afterClick.Payload.Length} bytes -> {clickFramePath}");

            // Close the menu again: it grabs the keyboard, and the agent types into that terminal next.
            await channel.SendAsync(new DisplayKey("Escape", Down: true));
            await channel.SendAsync(new DisplayKey("Escape", Down: false));
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            // ---- 4e. and hands it back ---------------------------------------------------------------
            await channel.SendAsync(new DisplayControlRequest(Take: false));
            await WaitForControlAsync(channel, DisplayControlHolder.None, TimeSpan.FromSeconds(5));

            // ---- 5. the agent's own tools, over the same broker --------------------------------------
            var shot = await harness.Tools.ComputerScreenshot();
            var shotImage = Assert.IsType<ImageContentBlock>(shot.Content[0]);
            Assert.Equal("image/jpeg", shotImage.MimeType);
            var shotBytes = shotImage.DecodedData.ToArray();
            AssertJpegHeader(shotBytes, 1280, 800);
            var toolShotPath = Path.Combine(outputDirectory, "computer-screenshot.jpg");
            await File.WriteAllBytesAsync(toolShotPath, shotBytes);
            _out.WriteLine($"[mcp] computer_screenshot {shotBytes.Length} bytes -> {toolShotPath}");

            // Typing goes into the xterm the guest session starts, and lands in a file we can read back
            // through the VM's own channel — pixels prove something changed, this proves WHAT changed.
            var marker = "AGNES" + Random.Shared.Next(100_000, 999_999).ToString(CultureInfo.InvariantCulture);
            await harness.Tools.ComputerType($"echo {marker} > /tmp/agnes-probe.txt\n");

            var sheet = await harness.Tools.ComputerFrames(count: 4, spanMs: 800);
            var sheetImage = Assert.IsType<ImageContentBlock>(sheet.Content[0]);
            Assert.Equal("image/jpeg", sheetImage.MimeType);
            var sheetBytes = sheetImage.DecodedData.ToArray();
            Assert.True(sheetBytes.Length > 1024, "The contact sheet should be a real image.");
            AssertSoi(sheetBytes);
            var sheetPath = Path.Combine(outputDirectory, "computer-frames.jpg");
            await File.WriteAllBytesAsync(sheetPath, sheetBytes);
            _out.WriteLine($"[mcp] computer_frames {sheetBytes.Length} bytes -> {sheetPath}");

            var typed = await ReadGuestFileAsync(sandbox, "/tmp/agnes-probe.txt", TimeSpan.FromSeconds(20));
            Assert.Contains(marker, typed, StringComparison.Ordinal);
            _out.WriteLine($"[guest] the typed line reached the shell: {typed.Trim()}");

            // ---- 6. and stop when a person is driving ------------------------------------------------
            await channel.SendAsync(new DisplayControlRequest(Take: true));
            var retaken = await WaitForControlAsync(channel, DisplayControlHolder.User, TimeSpan.FromSeconds(5));
            Assert.Equal(DeviceId, retaken.DeviceId);

            var refused = await Assert.ThrowsAsync<InvalidOperationException>(
                () => harness.Tools.ComputerClick(100, 100));
            Assert.Equal(InputArbiter.UserHoldsMessage, refused.Message);

            // Looking is still allowed: an agent that has been locked out has to be able to see why.
            var lockedOutShot = await harness.Tools.ComputerScreenshot();
            Assert.IsType<ImageContentBlock>(lockedOutShot.Content[0]);

            _out.WriteLine("---- timings ----");
            _out.WriteLine($"open -> first image frame : {toFirstFrame.TotalMilliseconds:F0} ms");
            _out.WriteLine($"right-click -> next frame : {toClickFrame.TotalMilliseconds:F0} ms");
            _out.WriteLine($"provision (init -> ready) : {provisioned.TotalSeconds:F0} s");
        }
        finally
        {
            // Deletes the VM and stops its bus daemon (GraphicalIncusSandbox.DeleteAsync), and touches
            // nothing else: this is the one instance the probe made, by name.
            await sandbox.DeleteAsync();
            _out.WriteLine($"[vm] {sandbox.Id} deleted");
        }
    }

    // ---- the harness ---------------------------------------------------------------------------------

    /// <summary>
    /// A host with the display channel wired the way <c>Program</c> wires it — the 401 wall in front of the
    /// path, WebSockets, the endpoint over a real broker registry — plus the MCP tools over that same
    /// registry, so the agent and the watcher provably share one capture.
    /// </summary>
    private sealed class ProbeHost : IAsyncDisposable
    {
        private WebApplication _app = null!;
        private DisplayBrokerRegistry _registry = null!;

        public string BaseUrl { get; private set; } = string.Empty;

        public AgnesMcpTools Tools { get; private set; } = null!;

        public static async Task<ProbeHost> StartAsync(IDisplaySource source, ILoggerFactory loggers)
        {
            var host = new ProbeHost();
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");

            var options = new DisplayOptions();
            host._registry = new DisplayBrokerRegistry(
                new SingleSession(source), options, new EventBus(), new DiscardingControlSink(), loggers);

            var devices = new DeviceRegistry(
                DeviceToken, Path.Combine(Path.GetTempPath(), $"agnes-probe-devices-{Guid.NewGuid():n}.json"));

            builder.Services.AddSingleton(options);
            builder.Services.AddSingleton(host._registry);
            builder.Services.AddSingleton(devices);
            builder.Services.AddSingleton<SessionAccessDecider>(new AllowOwner());

            // Stated for fidelity with the host this stands in for: a graphical session is refused outright
            // unless the operator opted in. The gate itself lives in SessionManager (and has its own tests);
            // nothing below it re-checks, which is exactly why the flag belongs at the top.
            builder.Services.AddSingleton(new SessionSecurityOptions { AllowGraphicalSandboxes = true });

            host._app = builder.Build();
            host._app.Use(async (context, next) =>
            {
                if (context.Request.Path.StartsWithSegments(DisplayWire.Path)
                    && !devices.IsValid(context.Request.Query[WireProtocol.TokenParameter].ToString()))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }

                await next();
            });
            host._app.UseWebSockets();
            host._app.MapDisplayChannel();
            await host._app.StartAsync();

            host.BaseUrl = host._app.Services
                .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
                .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!
                .Addresses.First();

            var sessionTokens = new SessionMcpTokens();
            host.Tools = new AgnesMcpTools(
                // The computer_* tools never touch the session backend — that they cannot is the point of
                // passing nothing here; anything they reached for would be a null reference in this test.
                backend: null!,
                new DeviceRegistryMcpAuthenticator(devices),
                new FixedToken(sessionTokens.Issue(SessionId)),
                sessionTokens,
                new BrokerDisplayBackend(host._registry, options));

            return host;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            await _registry.DisposeAsync();
        }
    }

    /// <summary>One session, one display source, never a turn running — the sweep must not collapse the
    /// broker underneath the probe.</summary>
    private sealed class SingleSession(IDisplaySource source) : IDisplaySessionSource
    {
        public IDisplaySource? DisplaySourceFor(string sessionId)
            => string.Equals(sessionId, SessionId, StringComparison.Ordinal) ? source : null;

        public bool IsTurnActive(string sessionId) => false;
    }

    /// <summary>Control handovers would be appended to the session log by the real sink; there is no log
    /// here, and the assertions read the Control frames on the wire instead.</summary>
    private sealed class DiscardingControlSink : IDisplayControlSink
    {
        public Task AppendAsync(string sessionId, DisplayControlChangedEvent changed, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    /// <summary>The sharing decision is exercised by its own tests; here it is a constant so the probe is
    /// about pixels.</summary>
    private sealed class AllowOwner : SessionAccessDecider
    {
        public AllowOwner()
            : base(null!, null!, null!)
        {
        }

        public override SharingCaller CallerFor(string? token) => new(DeviceId, null, IsOwner: true);

        public override Task<bool> DecideAsync(
            string sessionId, SessionAccessKind kind, SharingCaller caller, CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }

    private sealed class FixedToken(string token) : IMcpCallerTokenSource
    {
        public string? CurrentToken => token;
    }

    private sealed class TestOutputLoggerProvider(ITestOutputHelper output) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Sink(output, categoryName);

        public void Dispose()
        {
        }

        private sealed class Sink(ITestOutputHelper output, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                try
                {
                    output.WriteLine($"[{category.Split('.')[^1]}] {formatter(state, exception)}");
                }
                catch (InvalidOperationException)
                {
                    // The test finished; a late log line from a background task is not a failure.
                }
            }
        }
    }

    // ---- helpers -------------------------------------------------------------------------------------

    private static bool IncusAnswers()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("incus", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            if (process is null || !process.WaitForExit(10_000))
            {
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task<DisplayFrame> NextFrameAsync(IDisplayChannel channel, TimeSpan within)
    {
        using var timeout = new CancellationTokenSource(within);
        return await channel.Frames.ReadAsync(timeout.Token);
    }

    private static async Task<DisplayFrame> NextImageFrameAsync(IDisplayChannel channel, TimeSpan within)
    {
        using var timeout = new CancellationTokenSource(within);
        while (true)
        {
            var frame = await channel.Frames.ReadAsync(timeout.Token);
            if (frame.IsImage)
            {
                return frame;
            }
        }
    }

    /// <summary>
    /// Reads until control is announced to be where it was asked to go.
    /// <para>
    /// Every handover is pushed to every watcher, including the ones this client did not cause — the agent's
    /// first input of a turn claims the display and publishes a <c>Control</c> frame of its own. So "the next
    /// Control frame" is not the answer to "did my take-control land"; the answer is the first frame that says
    /// the expected holder, and anything before it is somebody else's handover in flight.
    /// </para>
    /// </summary>
    private static async Task<DisplayControlNotice> WaitForControlAsync(
        IDisplayChannel channel, DisplayControlHolder expected, TimeSpan within)
    {
        using var timeout = new CancellationTokenSource(within);
        while (true)
        {
            var frame = await channel.Frames.ReadAsync(timeout.Token);
            if (frame.Header.Kind != DisplayFrameKind.Control)
            {
                continue;
            }

            var notice = JsonSerializer.Deserialize<DisplayControlNotice>(frame.Payload.Span, DisplayWire.Json)!;
            if (notice.Holder == expected)
            {
                return notice;
            }
        }
    }

    private static void AssertPlausibleJpeg(DisplayFrame frame, int displayWidth, int displayHeight)
    {
        Assert.Equal(displayWidth, frame.Header.DisplayWidth);
        Assert.Equal(displayHeight, frame.Header.DisplayHeight);
        Assert.Equal((uint)frame.Payload.Length, frame.Header.PayloadLength);
        Assert.True(frame.Payload.Length > 2048, $"A 1280x800 desktop frame should not be {frame.Payload.Length} bytes.");
        AssertSoi(frame.Payload.Span);

        // A Full frame is the whole screen; a Tile is a region of it, and its own header states the size.
        var expectedWidth = frame.Header.Kind == DisplayFrameKind.Full ? displayWidth : frame.Header.Width;
        var expectedHeight = frame.Header.Kind == DisplayFrameKind.Full ? displayHeight : frame.Header.Height;
        AssertJpegHeader(frame.Payload.Span, expectedWidth, expectedHeight);
    }

    private static void AssertSoi(ReadOnlySpan<byte> jpeg)
    {
        Assert.True(jpeg.Length > 4, "Not a JPEG: too short.");
        Assert.Equal(0xFF, jpeg[0]);
        Assert.Equal(0xD8, jpeg[1]);
    }

    /// <summary>Decodes the JPEG's own start-of-frame geometry — the picture has to be the size the frame
    /// header claims, or a client would map clicks through the wrong transform.</summary>
    private static void AssertJpegHeader(ReadOnlySpan<byte> jpeg, int width, int height)
    {
        AssertSoi(jpeg);
        using var decoded = SkiaSharp.SKBitmap.Decode(jpeg.ToArray());
        Assert.NotNull(decoded);
        Assert.Equal(width, decoded.Width);
        Assert.Equal(height, decoded.Height);
    }

    private static async Task<string> ReadGuestFileAsync(ISandbox sandbox, string path, TimeSpan within)
    {
        var deadline = DateTimeOffset.UtcNow + within;
        var last = string.Empty;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var result = await sandbox.ExecAsync(new SandboxExec { Argv = ["cat", path] });
            if (result.Success && result.Stdout.Trim().Length > 0)
            {
                return result.Stdout;
            }

            last = result.Stderr;
            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        return last;
    }
}
