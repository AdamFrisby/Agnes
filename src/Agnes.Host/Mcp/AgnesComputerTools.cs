using System.ComponentModel;
using System.Globalization;
using Agnes.Host.Display;
using Agnes.Sandbox;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Agnes.Host.Mcp;

/// <summary>
/// The <c>computer_*</c> half of Agnes-as-an-MCP-server: what an agent running in a graphical sandbox uses to
/// see and drive its own screen.
/// <para>
/// One tool per action, named and shaped the way the 2026 computer-use toolsets are, because that shape is
/// what the models were trained against — a single <c>computer(action=…)</c> tool would be a novel interface
/// every model would have to be taught in the prompt. Every coordinate is a <b>guest</b> pixel; when a
/// screenshot was scaled down for the model's benefit, the text block says so and says both sizes, and clicks
/// are still in display coordinates. Scaling the picture must never silently move the mouse.
/// </para>
/// <para>Authorization is <see cref="RequireActingSession"/>, exactly as <c>send_user_file</c>: an agent's
/// session token can only ever drive its own session's screen, whatever <c>sessionId</c> it passes.</para>
/// </summary>
public sealed partial class AgnesMcpTools
{
    /// <summary>Resolves the session a computer tool may act on and asserts it actually has a screen.</summary>
    /// <exception cref="InvalidOperationException">The session is headless. Stated plainly so a model that
    /// guessed wrong stops guessing rather than retrying with different arguments.</exception>
    private string RequireDisplaySession(string? sessionId)
    {
        var target = RequireActingSession(sessionId);
        if (!_display.HasDisplay(target))
        {
            throw new InvalidOperationException("This session has no display.");
        }

        return target;
    }

    [McpServerTool(Name = "computer_screenshot", ReadOnly = true)]
    [Description("Take a screenshot of the session's screen and look at it. This is how you see what is on the "
        + "display: take one before you act, and take another afterwards to check what happened. Coordinates "
        + "everywhere else are the display's own pixels, whatever size the returned image is.")]
    public async Task<CallToolResult> ComputerScreenshot(
        [Description("Scale the returned image down to at most this many pixels wide. Omit for the display's real size.")] int? maxWidth = null,
        [Description("Omit when called by the agent itself; required with a device token.")] string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        var target = RequireDisplaySession(sessionId);
        var shot = await _display.ScreenshotAsync(target, Sane(maxWidth), cancellationToken).ConfigureAwait(false);
        return Image(
            shot.Jpeg,
            Describe(shot.Width, shot.Height, shot.DisplayWidth, shot.DisplayHeight));
    }

    [McpServerTool(Name = "computer_frames", ReadOnly = true)]
    [Description("Watch the screen over a short span: takes several screenshots and returns them as ONE image, "
        + "tiled left to right then top to bottom in time order. Use it when a single screenshot cannot answer "
        + "the question — is it still loading, did the animation settle, did the click register — because one "
        + "picture of the sequence is far easier to judge than several separate ones.")]
    public async Task<CallToolResult> ComputerFrames(
        [Description("How many frames to capture (1-12).")] int count = 5,
        [Description("The total time to spread them over, in milliseconds.")] int spanMs = 500,
        [Description("Scale each frame down to at most this many pixels wide.")] int? maxWidth = null,
        [Description("Omit when called by the agent itself; required with a device token.")] string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        var target = RequireDisplaySession(sessionId);
        var span = TimeSpan.FromMilliseconds(Math.Clamp(spanMs, 0, 10_000));
        var sheet = await _display.ContactSheetAsync(target, Math.Clamp(count, 1, 12), span, Sane(maxWidth), cancellationToken)
            .ConfigureAwait(false);

        var every = sheet.Frames <= 1 ? sheet.SpanMs : sheet.SpanMs / (sheet.Frames - 1);
        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"{sheet.Frames} frames over {sheet.SpanMs} ms (one every ~{every} ms), tiled {sheet.Columns}x{sheet.Rows} "
            + $"left to right then top to bottom, oldest first; each cell is {sheet.CellWidth}x{sheet.CellHeight}; "
            + $"display {sheet.DisplayWidth}x{sheet.DisplayHeight}");
        return Image(sheet.Jpeg, text);
    }

    [McpServerTool(Name = "computer_move")]
    [Description("Move the pointer to a point on the display, without pressing anything. Use it to hover — to "
        + "reveal a tooltip or open a menu — then take a screenshot to see the result.")]
    public async Task<string> ComputerMove(
        [Description("X in display pixels, measured from the left edge.")] int x,
        [Description("Y in display pixels, measured from the top edge.")] int y,
        [Description("Omit when called by the agent itself; required with a device token.")] string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        var target = RequireDisplaySession(sessionId);
        await _display.InjectAsync(target, [new PointerMove(x, y)], cancellationToken).ConfigureAwait(false);
        return $"Pointer moved to {x},{y}.";
    }

    [McpServerTool(Name = "computer_click")]
    [Description("Click at a point on the display. The pointer moves there first, so you do not need a separate "
        + "move. Set count to 2 for a double click. Take a screenshot afterwards — a click that did nothing and "
        + "a click that opened a dialog look identical from here.")]
    public async Task<string> ComputerClick(
        [Description("X in display pixels.")] int x,
        [Description("Y in display pixels.")] int y,
        [Description("Which button: left, middle or right.")] string button = "left",
        [Description("How many clicks (2 for a double click).")] int count = 1,
        [Description("Modifiers to hold during the click, e.g. 'ctrl' or 'ctrl+shift'.")] string? modifiers = null,
        [Description("Omit when called by the agent itself; required with a device token.")] string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        var target = RequireDisplaySession(sessionId);
        var kind = ButtonKind(button);
        var clicks = Math.Clamp(count, 1, 3);
        var held = ModifierKeys(modifiers);

        var inputs = new List<DisplayInput> { new PointerMove(x, y) };
        inputs.AddRange(held.Select(m => new KeyPress(m, Down: true)));
        for (var i = 0; i < clicks; i++)
        {
            inputs.Add(new PointerButton(kind, Down: true));
            inputs.Add(new PointerButton(kind, Down: false));
        }

        for (var i = held.Count - 1; i >= 0; i--)
        {
            inputs.Add(new KeyPress(held[i], Down: false));
        }

        await _display.InjectAsync(target, inputs, cancellationToken).ConfigureAwait(false);
        return $"Clicked {button} {clicks}x at {x},{y}.";
    }

    [McpServerTool(Name = "computer_drag")]
    [Description("Press the button at one point, drag to another, and release: for selecting text, moving a "
        + "window, or dragging a slider. The pointer is moved through the midpoint, because some applications "
        + "ignore a drag that teleports.")]
    public async Task<string> ComputerDrag(
        [Description("X to start from, in display pixels.")] int fromX,
        [Description("Y to start from, in display pixels.")] int fromY,
        [Description("X to finish at.")] int toX,
        [Description("Y to finish at.")] int toY,
        [Description("Which button to drag with: left, middle or right.")] string button = "left",
        [Description("Omit when called by the agent itself; required with a device token.")] string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        var target = RequireDisplaySession(sessionId);
        var kind = ButtonKind(button);
        await _display.InjectAsync(
            target,
            [
                new PointerMove(fromX, fromY),
                new PointerButton(kind, Down: true),
                new PointerMove((fromX + toX) / 2, (fromY + toY) / 2),
                new PointerMove(toX, toY),
                new PointerButton(kind, Down: false),
            ],
            cancellationToken).ConfigureAwait(false);
        return $"Dragged {button} from {fromX},{fromY} to {toX},{toY}.";
    }

    [McpServerTool(Name = "computer_key")]
    [Description("Press a key or a key combination, written the way xdotool writes it: 'Return', 'Tab', "
        + "'Escape', 'BackSpace', 'ctrl+shift+t', 'alt+F4', 'ctrl+a'. Modifiers are held for the key and "
        + "released after. Use this for shortcuts and named keys; use computer_type for ordinary text.")]
    public async Task<string> ComputerKey(
        [Description("The key or combination, e.g. 'Return' or 'ctrl+shift+t'.")] string keys,
        [Description("How many times to press it.")] int count = 1,
        [Description("Omit when called by the agent itself; required with a device token.")] string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        var target = RequireDisplaySession(sessionId);
        var chord = KeyChord.Parse(keys);
        await _display.PressChordAsync(target, chord, Math.Clamp(count, 1, 16), cancellationToken).ConfigureAwait(false);
        return $"Pressed {keys}{(count > 1 ? $" {count}x" : string.Empty)}.";
    }

    [McpServerTool(Name = "computer_type")]
    [Description("Type text into whatever has keyboard focus, as individual key presses. Click the field first "
        + "— this types where the focus already is, it does not choose a target. ASCII only: for anything else, "
        + "write the text to a file in the working directory and open it in the guest.")]
    public async Task<string> ComputerType(
        [Description("The text to type. Newlines are typed as Return.")] string text,
        [Description("Omit when called by the agent itself; required with a device token.")] string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        var target = RequireDisplaySession(sessionId);
        var bytes = TypedText.ByteLength(text);
        if (bytes > _display.Options.MaxTypeBytes)
        {
            throw new ArgumentException(
                $"That is {bytes} bytes of text; computer_type accepts {_display.Options.MaxTypeBytes} at a time. "
                + "Type it in smaller pieces, or write it to a file and open that in the guest.",
                nameof(text));
        }

        // Typing goes through its own path: budgeted per keystroke and bounded by MaxTypeBytes above, rather
        // than by the per-call event ceiling that governs chords and drags — see IAgnesDisplayBackend.TypeAsync.
        await _display.TypeAsync(target, text, cancellationToken).ConfigureAwait(false);
        return $"Typed {text.Length} characters.";
    }

    [McpServerTool(Name = "computer_scroll")]
    [Description("Scroll the wheel at a point on the display. The pointer moves there first, so the scroll "
        + "lands on whatever is under that point rather than wherever focus happens to be.")]
    public async Task<string> ComputerScroll(
        [Description("X in display pixels — scroll happens over whatever is here.")] int x,
        [Description("Y in display pixels.")] int y,
        [Description("Which way: up, down, left or right.")] string direction = "down",
        [Description("How many wheel clicks.")] int amount = 3,
        [Description("Omit when called by the agent itself; required with a device token.")] string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        var target = RequireDisplaySession(sessionId);
        var clicks = Math.Clamp(amount, 1, 10);
        var (dx, dy) = direction.ToLowerInvariant() switch
        {
            "up" => (0, -1),
            "left" => (-1, 0),
            "right" => (1, 0),
            "down" => (0, 1),
            _ => throw new ArgumentException($"'{direction}' is not a scroll direction. Use up, down, left or right.", nameof(direction)),
        };

        var inputs = new List<DisplayInput> { new PointerMove(x, y) };
        for (var i = 0; i < clicks; i++)
        {
            inputs.Add(new PointerScroll(x, y, dx, dy));
        }

        await _display.InjectAsync(target, inputs, cancellationToken).ConfigureAwait(false);
        return $"Scrolled {direction} {clicks}x at {x},{y}.";
    }

    [McpServerTool(Name = "computer_hold_key")]
    [Description("Hold one key down for a while and then release it — for key repeat (holding an arrow to scroll "
        + "a long way) or for a modifier an application only reacts to while it is held. The release is "
        + "guaranteed: the key never stays down.")]
    public async Task<string> ComputerHoldKey(
        [Description("The key to hold, e.g. 'Down', 'shift' or 'a'. One key, not a combination.")] string key,
        [Description("How long to hold it, in milliseconds (up to 5000).")] int downMs = 500,
        [Description("Omit when called by the agent itself; required with a device token.")] string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        var target = RequireDisplaySession(sessionId);
        var chord = KeyChord.Parse(key);
        if (chord.Modifiers.Count > 0)
        {
            throw new ArgumentException(
                $"computer_hold_key holds one key; '{key}' is a combination. Use computer_key for a chord.", nameof(key));
        }

        var hold = TimeSpan.FromMilliseconds(Math.Clamp(downMs, 1, 5000));
        await _display.HoldKeyAsync(target, chord, hold, cancellationToken).ConfigureAwait(false);
        return $"Held {key} for {hold.TotalMilliseconds:0} ms.";
    }

    [McpServerTool(Name = "computer_wait", ReadOnly = true)]
    [Description("Wait, doing nothing, then carry on — for letting an application finish reacting before you "
        + "look again. Prefer computer_frames when what you actually want is to WATCH something happen.")]
    public async Task<string> ComputerWait(
        [Description("How long to wait, in milliseconds (up to 10000).")] int ms = 500,
        [Description("Omit when called by the agent itself; required with a device token.")] string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        RequireDisplaySession(sessionId);
        var wait = Math.Clamp(ms, 0, _display.Options.MaxWaitMs);
        await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
        return $"Waited {wait} ms.";
    }

    [McpServerTool(Name = "computer_cursor_position", ReadOnly = true)]
    [Description("Where the pointer currently is, in display pixels.")]
    public async Task<string> ComputerCursorPosition(
        [Description("Omit when called by the agent itself; required with a device token.")] string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        var target = RequireDisplaySession(sessionId);
        var (x, y) = await _display.PointerPositionAsync(target, cancellationToken).ConfigureAwait(false);
        var display = await _display.GeometryAsync(target, cancellationToken).ConfigureAwait(false);
        return display is null
            ? $"Pointer at {x},{y}."
            : $"Pointer at {x},{y}; display {display.Width}x{display.Height}.";
    }

    // ---- shared shaping ----

    /// <summary>An image result: the JPEG the model looks at, and one line of text telling it what it is
    /// looking at. Both matter — without the sizes, a scaled screenshot silently invites clicks in the wrong
    /// coordinate space.</summary>
    private static CallToolResult Image(byte[] jpeg, string text) => new()
    {
        Content =
        [
            ImageContentBlock.FromBytes(jpeg, "image/jpeg"),
            new TextContentBlock { Text = text },
        ],
    };

    private static string Describe(int width, int height, int displayWidth, int displayHeight)
        => width == displayWidth && height == displayHeight
            ? $"image {width}x{height}; display {displayWidth}x{displayHeight}"
            : $"image {width}x{height}; display {displayWidth}x{displayHeight}; the image is scaled — give every "
              + $"coordinate in DISPLAY pixels ({displayWidth}x{displayHeight}), not image pixels";

    private static PointerButtonKind ButtonKind(string button) => button.ToLowerInvariant() switch
    {
        "left" => PointerButtonKind.Left,
        "middle" => PointerButtonKind.Middle,
        "right" => PointerButtonKind.Right,
        _ => throw new ArgumentException($"'{button}' is not a button. Use left, middle or right.", nameof(button)),
    };

    /// <summary>Parses a modifier-only string like <c>ctrl+shift</c>. Written as a chord and then split, so
    /// modifier spellings are recognized in exactly one place.</summary>
    private static IReadOnlyList<string> ModifierKeys(string? modifiers)
    {
        if (string.IsNullOrWhiteSpace(modifiers))
        {
            return [];
        }

        // "ctrl+shift" parses as modifiers [ctrl] + key "shift"; both halves are modifiers here.
        var chord = KeyChord.Parse(modifiers);
        return [.. chord.Modifiers, chord.Key];
    }

    /// <summary>Clamps a caller-supplied max width to something an encoder can use, or null for "no cap".</summary>
    private static int? Sane(int? maxWidth) => maxWidth is { } w && w > 0 ? Math.Clamp(w, 64, 8192) : null;
}
