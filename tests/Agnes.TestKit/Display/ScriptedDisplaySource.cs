using System.Threading.Channels;
using Agnes.Sandbox;

namespace Agnes.TestKit.Display;

/// <summary>
/// A graphical sandbox with no sandbox: renders a synthetic 1280x800 surface, accepts input, and
/// records everything injected — so the host broker, the tools that drive a screen, and every client
/// can be tested end to end without an Incus VM, a GPU or a display server.
/// </summary>
/// <remarks>
/// It is a <em>behaving</em> fake, not a pixel generator. The surface is a login page modelled on
/// CodeyBox's demo-login fixture: click a field to focus it, type into it, press the button (or
/// Return) and the page changes to a signed-in state. That matters because the thing under test is
/// usually a loop — look, decide, click, look again — and a fake that never changes in response to
/// input lets that loop pass while being completely broken.
/// </remarks>
public sealed class ScriptedDisplaySource : IDisplaySource
{
    private readonly GraphicalDisplay _display;

    public ScriptedDisplaySource(GraphicalDisplay? display = null)
        => _display = display ?? new GraphicalDisplay(1280, 800);

    /// <summary>The session handed out by the last <see cref="OpenDisplayAsync"/>, for assertions.</summary>
    public ScriptedDisplaySession? Current { get; private set; }

    public Task<IDisplaySession> OpenDisplayAsync(CancellationToken cancellationToken = default)
    {
        var session = new ScriptedDisplaySession(_display);
        Current = session;
        return Task.FromResult<IDisplaySession>(session);
    }
}

/// <summary>The live half of <see cref="ScriptedDisplaySource"/>. See its remarks.</summary>
public sealed class ScriptedDisplaySession : IDisplaySession
{
    // Byte order is B,G,R,x — the one every DisplayUpdate uses.
    private static readonly byte[] Background = [0x2b, 0x1d, 0x1b, 0xff];
    private static readonly byte[] Panel = [0x3a, 0x2e, 0x2a, 0xff];
    private static readonly byte[] Field = [0x18, 0x14, 0x12, 0xff];
    private static readonly byte[] FieldFocused = [0x60, 0x3a, 0x2a, 0xff];
    private static readonly byte[] Ink = [0xf0, 0xdc, 0xd8, 0xff];
    private static readonly byte[] Button = [0x9c, 0x4a, 0x7a, 0xff];
    private static readonly byte[] Welcome = [0x60, 0xc0, 0x60, 0xff];
    private static readonly byte[] Spinner = [0x20, 0xa0, 0xf0, 0xff];

    private static readonly Rect EmailField = new(360, 300, 560, 44);
    private static readonly Rect PasswordField = new(360, 372, 560, 44);
    private static readonly Rect SubmitButton = new(360, 444, 180, 48);
    private static readonly Rect Spinner1 = new(360, 540, 560, 40);

    private readonly DisplaySurface _surface;
    private readonly Channel<DisplayUpdate> _updates = Channel.CreateUnbounded<DisplayUpdate>();
    private readonly List<DisplayInput> _injected = [];
    private readonly Lock _gate = new();

    private int _pointerX;
    private int _pointerY;
    private int _tick;
    private Field _focus = Display.Field.None;

    internal ScriptedDisplaySession(GraphicalDisplay display)
    {
        Geometry = new DisplayGeometry(display.Width, display.Height, display.Dpi);
        _surface = new DisplaySurface(display.Dpi);
        var frame = new byte[display.Width * display.Height * 4];
        PaintPage(frame, display.Width);
        _updates.Writer.TryWrite(_surface.ApplyScanout(
            display.Width, display.Height, display.Width * 4, DisplayPixelFormat.Bgrx8888, frame, DateTimeOffset.UtcNow));
    }

    public DisplayGeometry Geometry { get; }

    public ChannelReader<DisplayUpdate> Updates => _updates.Reader;

    /// <summary>Every input handed to <see cref="InjectAsync"/>, in order.</summary>
    public IReadOnlyList<DisplayInput> Injected
    {
        get
        {
            lock (_gate)
            {
                return [.. _injected];
            }
        }
    }

    /// <summary>Where the pointer currently is, after clamping.</summary>
    public (int X, int Y) Pointer
    {
        get
        {
            lock (_gate)
            {
                return (_pointerX, _pointerY);
            }
        }
    }

    public string EmailText { get; private set; } = string.Empty;

    public string PasswordText { get; private set; } = string.Empty;

    public bool SignedIn { get; private set; }

    /// <summary>
    /// Advances the animation one step and publishes a <em>partial</em> update. Worth having because a
    /// real QEMU capture only ever sends whole-surface rectangles: without this, a consumer that
    /// silently assumes every update is full-surface would pass every test and fail on any other
    /// backend.
    /// </summary>
    public void Tick()
    {
        _tick++;
        Publish(Spinner1);
    }

    public Task InjectAsync(DisplayInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        lock (_gate)
        {
            _injected.Add(input);
        }

        switch (input)
        {
            case PointerMove move:
                SetPointer(move.X, move.Y);
                break;

            case PointerScroll scroll:
                SetPointer(scroll.X, scroll.Y);
                break;

            case PointerButton { Button: PointerButtonKind.Left, Down: true }:
                Click();
                break;

            case KeyPress { Down: true } key:
                TypeKey(key.Key);
                break;

            default:
                break;
        }

        return Task.CompletedTask;
    }

    public Task<ReadOnlyMemory<byte>> SnapshotAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<ReadOnlyMemory<byte>>(_surface.Snapshot());

    public ValueTask DisposeAsync()
    {
        _updates.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    private void SetPointer(int x, int y)
    {
        lock (_gate)
        {
            (_pointerX, _pointerY) = Geometry.Clamp(x, y);
        }
    }

    private void Click()
    {
        var (x, y) = Pointer;
        if (SignedIn)
        {
            return;
        }

        if (EmailField.Contains(x, y))
        {
            _focus = Display.Field.Email;
            Publish(EmailField, PasswordField);
        }
        else if (PasswordField.Contains(x, y))
        {
            _focus = Display.Field.Password;
            Publish(EmailField, PasswordField);
        }
        else if (SubmitButton.Contains(x, y))
        {
            Submit();
        }
        else
        {
            _focus = Display.Field.None;
            Publish(EmailField, PasswordField);
        }
    }

    private void TypeKey(string keysym)
    {
        if (SignedIn)
        {
            return;
        }

        if (keysym == "Return")
        {
            Submit();
            return;
        }

        if (_focus == Display.Field.None)
        {
            return;
        }

        var text = _focus == Display.Field.Email ? EmailText : PasswordText;
        text = keysym switch
        {
            "BackSpace" => text.Length > 0 ? text[..^1] : text,
            "space" => text + ' ',
            "period" => text + '.',
            "at" => text + '@',
            _ when keysym.Length == 1 => text + keysym,
            _ => text,
        };

        if (_focus == Display.Field.Email)
        {
            EmailText = text;
        }
        else
        {
            PasswordText = text;
        }

        Publish(_focus == Display.Field.Email ? EmailField : PasswordField);
    }

    private void Submit()
    {
        SignedIn = EmailText.Length > 0 && PasswordText.Length > 0;
        // The whole panel, because signing in changes every part of it — a damage rectangle that
        // understated the change would leave the fake showing pixels the model no longer describes.
        Publish(new Rect(280, 240, 720, 380));
    }

    /// <summary>Repaints the page and publishes one update per damaged rectangle.</summary>
    private void Publish(params Rect[] damaged)
    {
        var frame = new byte[Geometry.Width * Geometry.Height * 4];
        PaintPage(frame, Geometry.Width);
        foreach (var rect in damaged)
        {
            var pixels = new byte[rect.Width * rect.Height * 4];
            for (var row = 0; row < rect.Height; row++)
            {
                var source = (((rect.Y + row) * Geometry.Width) + rect.X) * 4;
                frame.AsSpan(source, rect.Width * 4).CopyTo(pixels.AsSpan(row * rect.Width * 4));
            }

            var update = _surface.ApplyUpdate(
                rect.X, rect.Y, rect.Width, rect.Height, rect.Width * 4,
                DisplayPixelFormat.Bgrx8888, pixels, DateTimeOffset.UtcNow, mayAdoptPixels: true);
            if (update is not null)
            {
                _updates.Writer.TryWrite(update);
            }
        }
    }

    /// <summary>
    /// Paints the whole page. Text is drawn as one block per character rather than with a font: the
    /// consumers this fake exists for compare and transport pixels, they do not read them, and a
    /// bitmap font would be more code than everything else here put together.
    /// </summary>
    private void PaintPage(byte[] frame, int width)
    {
        Fill(frame, width, new Rect(0, 0, Geometry.Width, Geometry.Height), Background);
        Fill(frame, width, new Rect(280, 240, 720, 380), Panel);

        if (SignedIn)
        {
            Fill(frame, width, new Rect(360, 300, 560, 44), Welcome);
            return;
        }

        Fill(frame, width, EmailField, _focus == Display.Field.Email ? FieldFocused : Field);
        Fill(frame, width, PasswordField, _focus == Display.Field.Password ? FieldFocused : Field);
        Fill(frame, width, SubmitButton, Button);
        DrawText(frame, width, EmailField, EmailText.Length);
        DrawText(frame, width, PasswordField, PasswordText.Length);

        // The spinner: one block sliding along its track, so consecutive frames differ even when
        // nothing was typed.
        Fill(frame, width, Spinner1, Panel);
        Fill(frame, width, new Rect(Spinner1.X + ((_tick * 24) % (Spinner1.Width - 40)), Spinner1.Y + 8, 40, 24), Spinner);
    }

    private static void DrawText(byte[] frame, int width, Rect field, int characters)
    {
        for (var i = 0; i < Math.Min(characters, 40); i++)
        {
            Fill(frame, width, new Rect(field.X + 12 + (i * 13), field.Y + 14, 9, 16), Ink);
        }
    }

    private static void Fill(byte[] frame, int width, Rect rect, byte[] colour)
    {
        for (var row = 0; row < rect.Height; row++)
        {
            var offset = (((rect.Y + row) * width) + rect.X) * 4;
            for (var column = 0; column < rect.Width; column++)
            {
                colour.CopyTo(frame.AsSpan(offset + (column * 4)));
            }
        }
    }

    private readonly record struct Rect(int X, int Y, int Width, int Height)
    {
        internal bool Contains(int px, int py) => px >= X && px < X + Width && py >= Y && py < Y + Height;
    }
}

internal enum Field
{
    None,
    Email,
    Password,
}
