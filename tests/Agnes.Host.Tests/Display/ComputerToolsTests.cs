using Agnes.Host.Display;
using Agnes.Host.Mcp;
using Agnes.Host.Tests.Mcp;
using Agnes.Sandbox;
using ModelContextProtocol.Protocol;

namespace Agnes.Host.Tests.Display;

/// <summary>
/// The <c>computer_*</c> tools, driven the way an agent drives them: through a session token, against a real
/// broker over the stub display. The assertions are about what actually reaches the guest and what the model
/// is told back — a screenshot that doesn't say it was scaled is a click in the wrong place.
/// </summary>
public class ComputerToolsTests : IAsyncLifetime
{
    private const string Session = "sess-graphical";

    private StubSessionSource _sessions = null!;
    private StubDisplaySource _source = null!;
    private DisplayBrokerRegistry _registry = null!;
    private AgnesMcpTools _tools = null!;

    public Task InitializeAsync()
    {
        _sessions = new StubSessionSource();
        _source = DisplayFixture.NewSource(_sessions, Session, width: 128, height: 96);
        var options = DisplayFixture.Options();
        _registry = DisplayFixture.Registry(_sessions, options);

        var tokens = new SessionMcpTokens();
        var sessionToken = tokens.Issue(Session);
        _tools = new AgnesMcpTools(
            new FakeAgnesMcpBackend(),
            new FakeMcpAuthenticator("device-token"),
            new FixedTokenSource(sessionToken),
            tokens,
            new BrokerDisplayBackend(_registry, options));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _registry.DisposeAsync();

    private IReadOnlyList<DisplayInput> Injected => _source.Session.Snapshot();

    private static (byte[] Jpeg, string Text) Split(CallToolResult result)
    {
        var image = Assert.IsType<ImageContentBlock>(result.Content[0]);
        Assert.Equal("image/jpeg", image.MimeType);
        var text = Assert.IsType<TextContentBlock>(result.Content[1]);
        return (image.DecodedData.ToArray(), text.Text);
    }

    [Fact]
    public async Task A_headless_session_says_so_rather_than_returning_a_blank_screen()
    {
        var tokens = new SessionMcpTokens();
        var options = DisplayFixture.Options();
        await using var registry = DisplayFixture.Registry(new StubSessionSource(), options);
        var tools = new AgnesMcpTools(
            new FakeAgnesMcpBackend(), new FakeMcpAuthenticator("device-token"),
            new FixedTokenSource(tokens.Issue("headless")), tokens, new BrokerDisplayBackend(registry, options));

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => tools.ComputerScreenshot());
        Assert.Equal("This session has no display.", refused.Message);
    }

    [Fact]
    public async Task A_screenshot_returns_a_jpeg_and_names_both_sizes()
    {
        var (jpeg, text) = Split(await _tools.ComputerScreenshot());

        using var decoded = SkiaSharp.SKBitmap.Decode(jpeg);
        Assert.Equal(128, decoded.Width);
        Assert.Equal(96, decoded.Height);
        Assert.Equal("image 128x96; display 128x96", text);
    }

    [Fact]
    public async Task A_scaled_screenshot_tells_the_model_to_keep_using_display_coordinates()
    {
        var (jpeg, text) = Split(await _tools.ComputerScreenshot(maxWidth: 64));

        using var decoded = SkiaSharp.SKBitmap.Decode(jpeg);
        Assert.Equal(64, decoded.Width);
        Assert.Contains("image 64x48", text, StringComparison.Ordinal);
        Assert.Contains("display 128x96", text, StringComparison.Ordinal);
        Assert.Contains("DISPLAY pixels", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Frames_returns_one_contact_sheet_and_names_the_grid_and_timing()
    {
        var (jpeg, text) = Split(await _tools.ComputerFrames(count: 4, spanMs: 40));

        using var decoded = SkiaSharp.SKBitmap.Decode(jpeg);
        Assert.Equal(128 * 2 + 2, decoded.Width);
        Assert.Equal(96 * 2 + 2, decoded.Height);
        Assert.Contains("4 frames over 40 ms", text, StringComparison.Ordinal);
        Assert.Contains("tiled 2x2", text, StringComparison.Ordinal);
        Assert.Contains("oldest first", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_click_moves_first_then_presses_and_releases()
    {
        await _tools.ComputerClick(10, 20);

        Assert.Equal(
            new DisplayInput[]
            {
                new PointerMove(10, 20),
                new PointerButton(PointerButtonKind.Left, true),
                new PointerButton(PointerButtonKind.Left, false),
            },
            Injected);
    }

    [Fact]
    public async Task A_click_with_modifiers_holds_them_around_the_press()
    {
        await _tools.ComputerClick(1, 2, button: "right", count: 2, modifiers: "ctrl+shift");

        Assert.Equal(
            new DisplayInput[]
            {
                new PointerMove(1, 2),
                new KeyPress("ctrl", true),
                new KeyPress("shift", true),
                new PointerButton(PointerButtonKind.Right, true),
                new PointerButton(PointerButtonKind.Right, false),
                new PointerButton(PointerButtonKind.Right, true),
                new PointerButton(PointerButtonKind.Right, false),
                new KeyPress("shift", false),
                new KeyPress("ctrl", false),
            },
            Injected);
    }

    [Fact]
    public async Task A_drag_passes_through_the_midpoint()
    {
        await _tools.ComputerDrag(0, 0, 20, 40);

        Assert.Equal(
            new DisplayInput[]
            {
                new PointerMove(0, 0),
                new PointerButton(PointerButtonKind.Left, true),
                new PointerMove(10, 20),
                new PointerMove(20, 40),
                new PointerButton(PointerButtonKind.Left, false),
            },
            Injected);
    }

    [Fact]
    public async Task A_key_chord_reaches_the_guest_as_hardware()
    {
        await _tools.ComputerKey("ctrl+shift+t");

        Assert.Equal(
            new DisplayInput[]
            {
                new KeyPress("ctrl", true),
                new KeyPress("shift", true),
                new KeyPress("t", true),
                new KeyPress("t", false),
                new KeyPress("shift", false),
                new KeyPress("ctrl", false),
            },
            Injected);
    }

    [Fact]
    public async Task A_blocked_chord_never_reaches_the_guest()
    {
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => _tools.ComputerKey("ctrl+alt+F2"));
        Assert.Contains("blocked on this host", refused.Message, StringComparison.Ordinal);
        Assert.Empty(Injected);
    }

    [Fact]
    public async Task Typing_becomes_key_presses()
    {
        await _tools.ComputerType("ab");

        Assert.Equal(
            new DisplayInput[]
            {
                new KeyPress("a", true), new KeyPress("a", false),
                new KeyPress("b", true), new KeyPress("b", false),
            },
            Injected);
    }

    /// <summary>
    /// Typing is bounded by its own byte ceiling, not by the per-call event ceiling. A character is two key
    /// events (four shifted), so charging typing like a chord would cap <c>computer_type</c> at sixteen
    /// characters — shorter than a URL, a filename or any shell command worth typing.
    /// </summary>
    [Fact]
    public async Task Typing_a_whole_command_is_not_capped_by_the_per_call_event_ceiling()
    {
        const string Command = "echo hello > /tmp/agnes-typed.txt\n";   // 34 chars, 70+ key events

        await _tools.ComputerType(Command);

        Assert.Equal(new KeyPress("e", true), Injected[0]);
        Assert.Equal(new KeyPress("Return", false), Injected[^1]);
        Assert.True(Injected.Count > DisplayFixture.Options().InputEventsPerToolCall);
    }

    /// <summary>…but it is still budgeted, per keystroke: the resource is the guest's input queue, and one
    /// character is one thing the person or the model meant to do.</summary>
    [Fact]
    public async Task Typing_is_charged_one_keystroke_per_character_against_the_minute_budget()
    {
        var sessions = new StubSessionSource();
        var source = DisplayFixture.NewSource(sessions, "budgeted", width: 64, height: 48);
        var options = DisplayFixture.Options() with { InputEventsPerMinute = 10 };
        await using var registry = DisplayFixture.Registry(sessions, options);
        var tokens = new SessionMcpTokens();
        var tools = new AgnesMcpTools(
            new FakeAgnesMcpBackend(), new FakeMcpAuthenticator("device-token"),
            new FixedTokenSource(tokens.Issue("budgeted")), tokens, new BrokerDisplayBackend(registry, options));

        await tools.ComputerType("eight ch");   // 8 characters = 8 of the minute's 10, not 16 events

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => tools.ComputerType("more"));
        Assert.Contains("minute", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(16, source.Session.Snapshot().Count);   // the refused call typed nothing
    }

    [Fact]
    public async Task Scrolling_moves_the_pointer_then_turns_the_wheel()
    {
        await _tools.ComputerScroll(5, 6, direction: "up", amount: 2);

        Assert.Equal(
            new DisplayInput[]
            {
                new PointerMove(5, 6),
                new PointerScroll(5, 6, 0, -1),
                new PointerScroll(5, 6, 0, -1),
            },
            Injected);
    }

    [Fact]
    public async Task A_held_key_goes_down_and_comes_back_up()
    {
        await _tools.ComputerHoldKey("Down", downMs: 10);

        Assert.Equal(
            new DisplayInput[] { new KeyPress("Down", true), new KeyPress("Down", false) },
            Injected);
    }

    [Fact]
    public async Task Hold_key_refuses_a_combination()
    {
        var refused = await Assert.ThrowsAsync<ArgumentException>(() => _tools.ComputerHoldKey("ctrl+a"));
        Assert.Contains("holds one key", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_cursor_position_is_where_it_was_last_put()
    {
        await _tools.ComputerMove(33, 44);
        Assert.Equal("Pointer at 33,44; display 128x96.", await _tools.ComputerCursorPosition());
    }

    [Fact]
    public async Task A_person_holding_the_display_stops_every_input_tool()
    {
        var broker = await _registry.GetOrCreateAsync(Session);
        await broker.RequestControlAsync("device-a", take: true);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => _tools.ComputerClick(1, 1));
        Assert.Equal(InputArbiter.UserHoldsMessage, refused.Message);
        Assert.Empty(Injected);

        // Looking is still allowed: a locked-out agent must still be able to see why.
        var (_, text) = Split(await _tools.ComputerScreenshot());
        Assert.Contains("display 128x96", text, StringComparison.Ordinal);
    }
}
