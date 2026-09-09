using Agnes.Sandbox.Incus;
using Agnes.Sandbox.Incus.Graphical;
using Agnes.TestKit.Display;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Sandbox.Tests;

public class DisplayKeyMapTests
{
    /// <summary>
    /// Everything Anthropic's computer-use vocabulary can name has to resolve, because a key that
    /// doesn't is a dead end mid-task: the model asked for something reasonable and got an exception.
    /// </summary>
    [Theory]
    [InlineData("Return")]
    [InlineData("Tab")]
    [InlineData("Escape")]
    [InlineData("BackSpace")]
    [InlineData("Delete")]
    [InlineData("Up")]
    [InlineData("Down")]
    [InlineData("Left")]
    [InlineData("Right")]
    [InlineData("Home")]
    [InlineData("End")]
    [InlineData("Page_Up")]
    [InlineData("Page_Down")]
    [InlineData("Insert")]
    [InlineData("ctrl")]
    [InlineData("alt")]
    [InlineData("shift")]
    [InlineData("super")]
    [InlineData("Control_L")]
    [InlineData("Control_R")]
    [InlineData("Shift_R")]
    [InlineData("Alt_R")]
    [InlineData("space")]
    [InlineData("minus")]
    [InlineData("equal")]
    [InlineData("bracketleft")]
    [InlineData("bracketright")]
    [InlineData("semicolon")]
    [InlineData("apostrophe")]
    [InlineData("grave")]
    [InlineData("backslash")]
    [InlineData("comma")]
    [InlineData("period")]
    [InlineData("slash")]
    [InlineData("Caps_Lock")]
    [InlineData("Menu")]
    public void Every_named_key_resolves(string keysym)
    {
        Assert.True(QemuKeyMap.TryResolve(keysym, out var qnum, out var shift));
        Assert.True(qnum > 0);
        Assert.False(shift);
    }

    [Theory]
    [InlineData("F1")]
    [InlineData("F5")]
    [InlineData("F10")]
    [InlineData("F11")]
    [InlineData("F12")]
    [InlineData("0")]
    [InlineData("5")]
    [InlineData("9")]
    [InlineData("a")]
    [InlineData("m")]
    [InlineData("z")]
    [InlineData("KP_0")]
    [InlineData("KP_9")]
    [InlineData("KP_Enter")]
    [InlineData("KP_Add")]
    [InlineData("KP_Decimal")]
    [InlineData("KP_Divide")]
    [InlineData("KP_Multiply")]
    public void Function_digit_letter_and_keypad_keys_resolve(string keysym)
    {
        Assert.True(QemuKeyMap.TryResolve(keysym, out var qnum, out _));
        Assert.True(qnum > 0);
    }

    /// <summary>
    /// The exact numbers, spot-checked, because the whole table is one transcription and a silent
    /// off-by-one in it types the wrong thing rather than failing. These four are the ones that catch a
    /// wrong *numbering*: the letters coincide with evdev codes, the arrows do not.
    /// </summary>
    [Theory]
    [InlineData("a", 0x1E)]      // AT set-1, same as evdev by coincidence
    [InlineData("Return", 0x1C)]
    [InlineData("Up", 0xC8)]     // evdev would say 103 — the escaped set-1 code is the one QEMU wants
    [InlineData("Left", 0xCB)]
    [InlineData("Control_R", 0x9D)]
    [InlineData("F1", 0x3B)]
    [InlineData("F11", 0x57)]    // not contiguous with F10: F11/F12 came later
    [InlineData("1", 0x02)]
    [InlineData("0", 0x0B)]      // 0 sits after 9, not before 1
    public void Key_numbers_are_the_qemu_ones(string keysym, uint expected)
    {
        Assert.True(QemuKeyMap.TryResolve(keysym, out var qnum, out _));
        Assert.Equal(expected, qnum);
    }

    /// <summary>A character that needs Shift resolves to the unshifted key, plus the shift flag.</summary>
    [Theory]
    [InlineData("A", "a")]
    [InlineData("Z", "z")]
    [InlineData("colon", "semicolon")]
    [InlineData("underscore", "minus")]
    [InlineData("plus", "equal")]
    [InlineData("greater", "period")]
    [InlineData("question", "slash")]
    [InlineData("exclam", "1")]
    [InlineData("parenright", "0")]
    public void Shifted_characters_map_to_their_key_and_ask_for_shift(string shifted, string unshifted)
    {
        Assert.True(QemuKeyMap.TryResolve(shifted, out var shiftedNum, out var needsShift));
        Assert.True(needsShift);
        Assert.True(QemuKeyMap.TryResolve(unshifted, out var plainNum, out var plainShift));
        Assert.False(plainShift);
        Assert.Equal(plainNum, shiftedNum);
    }

    [Fact]
    public void An_unknown_key_is_reported_not_guessed()
    {
        Assert.False(QemuKeyMap.TryResolve("XF86AudioPlay", out _, out _));
        Assert.False(QemuKeyMap.TryResolve(string.Empty, out _, out _));
    }
}

public class DisplayClampTests
{
    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(-40, -1, 0, 0)]
    [InlineData(5000, 5000, 1279, 799)]
    [InlineData(1280, 800, 1279, 799)]   // the size itself is one past the last pixel
    [InlineData(640, 400, 640, 400)]
    public void Coordinates_are_clamped_onto_the_surface(int x, int y, int expectedX, int expectedY)
    {
        var (cx, cy) = new DisplayGeometry(1280, 800, DisplayPixelFormat.Bgrx32).Clamp(x, y);
        Assert.Equal(expectedX, cx);
        Assert.Equal(expectedY, cy);
    }

    [Fact]
    public void A_surface_with_no_size_clamps_to_the_origin()
    {
        Assert.Equal((0, 0), new DisplayGeometry(0, 0, DisplayPixelFormat.Bgrx32).Clamp(100, 100));
    }
}

public class CapturedSurfaceTests
{
    private static byte[] Solid(int width, int height, byte value)
    {
        var pixels = new byte[width * height * 4];
        Array.Fill(pixels, value);
        return pixels;
    }

    [Fact]
    public void A_scanout_defines_the_surface_and_arrives_as_a_full_update()
    {
        var surface = new CapturedSurface();
        var update = surface.ApplyScanout(4, 3, 16, Solid(4, 3, 0x11), DateTimeOffset.UnixEpoch);

        Assert.Equal(new DisplayGeometry(4, 3, DisplayPixelFormat.Bgrx32), surface.Geometry);
        Assert.Equal((0, 0, 4, 3), (update.X, update.Y, update.Width, update.Height));
        Assert.Equal(16, update.Stride);
        Assert.Equal(1, update.Sequence);
        Assert.True(update.IsFullFrame(surface.Geometry!));
        Assert.All(update.Pixels.ToArray(), b => Assert.Equal(0x11, b));
    }

    [Fact]
    public void Updates_compose_onto_the_scanout_and_are_visible_in_the_snapshot()
    {
        var surface = new CapturedSurface();
        surface.ApplyScanout(4, 3, 16, Solid(4, 3, 0x11), DateTimeOffset.UnixEpoch);
        var update = surface.ApplyUpdate(1, 1, 2, 1, 8, Solid(2, 1, 0x99), DateTimeOffset.UnixEpoch);

        Assert.NotNull(update);
        Assert.Equal(2, update!.Sequence);

        var frame = surface.Snapshot();
        Assert.Equal(0x11, frame[0]);                       // (0,0) untouched
        Assert.Equal(0x99, frame[((1 * 4) + 1) * 4]);        // (1,1) written
        Assert.Equal(0x99, frame[((1 * 4) + 2) * 4]);        // (2,1) written
        Assert.Equal(0x11, frame[((1 * 4) + 3) * 4]);        // (3,1) untouched
        Assert.Equal(0x11, frame[((2 * 4) + 1) * 4]);        // (1,2) untouched
    }

    [Fact]
    public void A_padded_stride_is_repacked_so_consumers_see_one_rule()
    {
        var surface = new CapturedSurface();
        // 2x2 surface delivered with a 3-pixel stride: the third pixel of each row is padding.
        var padded = new byte[3 * 4 * 2];
        Array.Fill(padded, (byte)0x55);
        for (var row = 0; row < 2; row++)
        {
            Array.Fill(padded, (byte)0xEE, (row * 12) + 8, 4);   // the padding column
        }

        var update = surface.ApplyScanout(2, 2, 12, padded, DateTimeOffset.UnixEpoch);

        Assert.Equal(8, update.Stride);
        Assert.Equal(16, update.Pixels.Length);
        Assert.All(update.Pixels.ToArray(), b => Assert.Equal(0x55, b));   // no padding leaked through
    }

    [Fact]
    public void A_rectangle_hanging_off_the_edge_is_clipped_not_dropped()
    {
        var surface = new CapturedSurface();
        surface.ApplyScanout(4, 4, 16, Solid(4, 4, 0x11), DateTimeOffset.UnixEpoch);

        var update = surface.ApplyUpdate(3, 3, 4, 4, 16, Solid(4, 4, 0x77), DateTimeOffset.UnixEpoch);

        Assert.NotNull(update);
        var frame = surface.Snapshot();
        Assert.Equal(0x77, frame[((3 * 4) + 3) * 4]);   // the one pixel that is actually on the surface
        Assert.Equal(0x11, frame[((3 * 4) + 2) * 4]);   // its neighbour is untouched
    }

    [Fact]
    public void A_malformed_or_short_rectangle_costs_one_update_not_the_session()
    {
        var surface = new CapturedSurface();
        surface.ApplyScanout(4, 4, 16, Solid(4, 4, 0x11), DateTimeOffset.UnixEpoch);

        Assert.Null(surface.ApplyUpdate(0, 0, 0, 2, 0, [], DateTimeOffset.UnixEpoch));
        Assert.Null(surface.ApplyUpdate(0, 0, 4, 4, 8, Solid(4, 4, 0x22), DateTimeOffset.UnixEpoch));
        Assert.Null(surface.ApplyUpdate(0, 0, 4, 4, 16, Solid(4, 1, 0x22), DateTimeOffset.UnixEpoch));
        Assert.Null(surface.ApplyUpdate(-10, -10, 4, 4, 16, Solid(4, 4, 0x22), DateTimeOffset.UnixEpoch));

        Assert.Equal(1, surface.Sequence);              // nothing was published
        Assert.All(surface.Snapshot(), b => Assert.Equal(0x11, b));
    }

    [Fact]
    public void A_scanout_at_a_new_size_resizes_the_surface_in_place()
    {
        var surface = new CapturedSurface();
        surface.ApplyScanout(4, 4, 16, Solid(4, 4, 0x11), DateTimeOffset.UnixEpoch);
        var resized = surface.ApplyScanout(8, 2, 32, Solid(8, 2, 0x33), DateTimeOffset.UnixEpoch);

        Assert.Equal(new DisplayGeometry(8, 2, DisplayPixelFormat.Bgrx32), surface.Geometry);
        Assert.Equal(8, resized.Width);
        Assert.Equal(8 * 2 * 4, surface.Snapshot().Length);
    }
}

public class ScriptedDisplaySourceTests
{
    [Fact]
    public async Task The_fake_drives_a_whole_look_click_type_look_loop()
    {
        var source = new ScriptedDisplaySource();
        await using var session = (ScriptedDisplaySession)await source.OpenDisplayAsync();

        Assert.Equal(new DisplayGeometry(1280, 800, DisplayPixelFormat.Bgrx32), session.Geometry);
        Assert.True(session.Updates.TryRead(out var scanout));
        Assert.Equal(1280 * 800 * 4, scanout!.Pixels.Length);   // opening publishes the whole surface

        var before = await session.SnapshotAsync();

        // Click the email field, type, then the password field, then submit.
        await session.InjectAsync(new PointerMove(600, 320));
        await session.InjectAsync(new PointerButton(PointerButtonKind.Left, true));
        await session.InjectAsync(new PointerButton(PointerButtonKind.Left, false));
        foreach (var key in (string[])["a", "l", "i", "c", "e"])
        {
            await session.InjectAsync(new KeyPress(key, true));
            await session.InjectAsync(new KeyPress(key, false));
        }

        Assert.Equal("alice", session.EmailText);

        await session.InjectAsync(new PointerMove(600, 392));
        await session.InjectAsync(new PointerButton(PointerButtonKind.Left, true));
        await session.InjectAsync(new KeyPress("s", true));
        await session.InjectAsync(new KeyPress("BackSpace", true));
        await session.InjectAsync(new KeyPress("x", true));
        Assert.Equal("x", session.PasswordText);

        await session.InjectAsync(new KeyPress("Return", true));
        Assert.True(session.SignedIn);

        var after = await session.SnapshotAsync();
        Assert.NotEqual(before.ToArray(), after.ToArray());     // the screen actually changed

        // Everything injected was recorded, in order, including the ups nothing acted on.
        var injected = session.Injected;
        Assert.Equal(new PointerMove(600, 320), injected[0]);
        Assert.Contains(injected, i => i is KeyPress { Key: "Return", Down: true });
    }

    [Fact]
    public async Task The_fake_publishes_partial_updates_so_consumers_cannot_assume_full_frames()
    {
        var source = new ScriptedDisplaySource();
        await using var session = (ScriptedDisplaySession)await source.OpenDisplayAsync();
        Assert.True(session.Updates.TryRead(out _));   // drop the opening scanout

        session.Tick();

        Assert.True(session.Updates.TryRead(out var update));
        Assert.NotNull(update);
        Assert.True(update.Width < session.Geometry.Width);
        Assert.Equal(update.Width * 4, update.Stride);
        Assert.Equal(update.Stride * update.Height, update.Pixels.Length);
    }

    [Fact]
    public async Task Pointer_coordinates_are_clamped_by_the_session()
    {
        var source = new ScriptedDisplaySource();
        await using var session = (ScriptedDisplaySession)await source.OpenDisplayAsync();

        await session.InjectAsync(new PointerMove(99999, -5));

        Assert.Equal((1279, 0), session.Pointer);
    }
}

public class GraphicalProvisioningTests
{
    /// <summary>A dbus-daemon stand-in, so provisioning can be exercised without starting a real bus.</summary>
    private static IncusOptions Options(string runtimeDirectory) => new()
    {
        DisplayRuntimeDirectory = runtimeDirectory,
        DbusDaemonPath = "/bin/true",
    };

    private sealed class RecordingRunner : IIncusCliRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
            IReadOnlyList<string> argv, string? stdin = null,
            Action<string>? stdoutChunk = null, Action<string>? stderrChunk = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(argv);
            return Task.FromResult((0, string.Empty, string.Empty));
        }

        public Task RunCheckedAsync(string what, IReadOnlyList<string> argv, string? stdin = null, CancellationToken cancellationToken = default)
        {
            Calls.Add(argv);
            return Task.CompletedTask;
        }

        public bool Any(Func<string, bool> predicate) => Calls.Any(c => c.Any(predicate));
    }

    [Fact]
    public async Task A_headless_sandbox_gets_no_display_configuration_at_all()
    {
        var runner = new RecordingRunner();
        var provider = new IncusSandboxProvider(Options(Path.Combine(Path.GetTempPath(), "agnes-display-test-headless")), NullLoggerFactory.Instance, runner);

        var sandbox = await provider.CreateAsync(new SandboxSpec());

        Assert.False(runner.Any(a => a.StartsWith("raw.qemu=", StringComparison.Ordinal)));
        Assert.False(runner.Any(a => a.StartsWith("raw.apparmor=", StringComparison.Ordinal)));
        Assert.IsNotAssignableFrom<IDisplaySource>(sandbox);   // the capability is absent, not throwing
    }

    [Fact]
    public async Task A_display_adds_raw_qemu_raw_apparmor_the_graphical_image_and_a_bigger_disk()
    {
        var directory = Path.Combine(Path.GetTempPath(), "agnes-display-test-" + Guid.NewGuid().ToString("N")[..8]);
        var runner = new RecordingRunner();
        var options = Options(directory);
        var provider = new IncusSandboxProvider(options, NullLoggerFactory.Instance, runner);

        try
        {
            var sandbox = await provider.CreateAsync(new SandboxSpec { Display = new GraphicalDisplay(1280, 800) });

            Assert.IsAssignableFrom<IDisplaySource>(sandbox);
            Assert.True(runner.Any(a => a.StartsWith("raw.qemu=-display dbus,addr=unix:path=" + directory, StringComparison.Ordinal)));
            Assert.True(runner.Any(a => a.Contains("dbus (send, receive, bind) bus=session,", StringComparison.Ordinal)));
            Assert.True(runner.Any(a => a == options.GraphicalImage));
            // 24 GiB, the graphical floor — Incus refuses to launch an image bigger than its volume.
            Assert.True(runner.Any(a => a == "root,size=25769803776B"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void The_bus_policy_admits_only_qemu_root_and_us()
    {
        var bus = new DisplayBus(Options("/tmp/agnes-display-test"), NullLogger.Instance);
        var config = bus.RenderBusConfig("/tmp/agnes-display-test/x/bus");

        Assert.Contains("<deny user=\"*\"/>", config, StringComparison.Ordinal);
        Assert.Contains("<allow user=\"root\"/>", config, StringComparison.Ordinal);   // QEMU connects before dropping privileges
        Assert.Contains("<allow user=\"incus\"/>", config, StringComparison.Ordinal);
        Assert.Contains("<allow user=\"" + Environment.UserName + "\"/>", config, StringComparison.Ordinal);
        Assert.Contains("<listen>unix:path=/tmp/agnes-display-test/x/bus</listen>", config, StringComparison.Ordinal);
    }

    [Fact]
    public void The_graphical_image_tier_is_the_baseline_plus_a_desktop()
    {
        var baseline = new SandboxImageManifest { AptPackages = ["git", "curl"] };
        var graphical = baseline.AsGraphical();

        Assert.Equal("agnes-graphical", graphical.Alias);
        Assert.Contains("git", graphical.AptPackages);
        Assert.Contains("xserver-xorg-core", graphical.AptPackages);
        Assert.Contains("openbox", graphical.AptPackages);
        Assert.DoesNotContain("xserver-xorg-core", baseline.AptPackages);   // the headless tier is untouched
        Assert.NotEqual(baseline.Fingerprint(), graphical.Fingerprint());
    }

    [Fact]
    public void The_graphical_guest_units_carry_the_requested_size_and_start_at_boot()
    {
        var cloudInit = IncusGuest.CloudInit(new IncusOptions(), new GraphicalDisplay(1600, 900));

        Assert.Contains("1600 900", cloudInit, StringComparison.Ordinal);          // the geometry file
        Assert.Contains("Modes   \"1600x900\"", cloudInit, StringComparison.Ordinal);
        Assert.Contains("enable, --now, agnes-x.service", cloudInit, StringComparison.Ordinal);
        Assert.Contains("enable, --now, agnes-desktop.service", cloudInit, StringComparison.Ordinal);

        var headless = IncusGuest.CloudInit(new IncusOptions());
        Assert.DoesNotContain("agnes-x.service", headless, StringComparison.Ordinal);
    }
}
