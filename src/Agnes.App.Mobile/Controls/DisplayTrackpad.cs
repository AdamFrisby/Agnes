namespace Agnes.App.Mobile.Controls;

/// <summary>One thing the trackpad decided to do, in guest pixels.</summary>
public abstract record TrackpadAction;

/// <summary>Put the pointer here.</summary>
public sealed record TrackpadMove(int X, int Y) : TrackpadAction;

/// <summary>Press and release <paramref name="Button"/> (0 left, 1 middle, 2 right) at the cursor.</summary>
public sealed record TrackpadClick(int X, int Y, int Button) : TrackpadAction;

/// <summary>One wheel notch at the cursor.</summary>
public sealed record TrackpadScroll(int X, int Y, int Dx, int Dy) : TrackpadAction;

/// <summary>
/// Touch, turned into a mouse.
///
/// A finger is not a mouse and pretending otherwise is what makes touch-driven remote desktops
/// unusable: an absolute mapping puts the pointer under the fingertip, where you cannot see it, at a
/// precision of about 9 mm — which on a 1280-wide desktop is roughly 30 pixels of "somewhere near the
/// thing you meant". So the surface is a <em>trackpad</em>, not a touchscreen:
///
/// <list type="bullet">
///   <item>one finger dragging moves a cursor drawn on top of the picture, relative to where it already
///     was, so the target is never under your hand;</item>
///   <item>a tap — down and up in the same place, quickly — clicks where the cursor is, not where you
///     tapped;</item>
///   <item>holding still right-clicks, which is the gesture Android has trained everyone to expect for
///     "the other menu";</item>
///   <item>two fingers dragging scroll at the cursor.</item>
/// </list>
///
/// It is a pure state machine over positions and timestamps — no Avalonia, no clock of its own — because
/// the interesting bugs here are all about ordering (did the long press fire before the finger lifted?)
/// and those are only reachable in a test if time is an argument.
/// </summary>
public sealed class DisplayTrackpad
{
    /// <summary>How far a finger may travel and still count as a tap, in guest pixels.</summary>
    public const double TapSlop = 14;

    /// <summary>A press longer than this is not a tap any more.</summary>
    public const double TapMilliseconds = 420;

    /// <summary>A press held still for this long is a right-click.</summary>
    public const double LongPressMilliseconds = 550;

    /// <summary>Finger travel that makes one wheel notch, in guest pixels.</summary>
    public const double ScrollStep = 42;

    private readonly Dictionary<int, Finger> _fingers = [];
    private Mode _mode;
    private int _primary = -1;
    private double _scrollX;
    private double _scrollY;

    public DisplayTrackpad(int width, int height)
    {
        Resize(width, height);
        CursorX = width / 2;
        CursorY = height / 2;
    }

    private enum Mode
    {
        Idle,

        /// <summary>One finger down: moving the cursor, and still possibly a tap.</summary>
        Cursor,

        /// <summary>Two fingers down: scrolling.</summary>
        Scroll,

        /// <summary>The gesture already did something irreversible (a long press), or a pinch took it
        /// over. Nothing more happens until every finger is up.</summary>
        Spent,
    }

    public int Width { get; private set; } = 1;

    public int Height { get; private set; } = 1;

    /// <summary>Where the drawn cursor is, in guest pixels. Starts in the middle of the display.</summary>
    public int CursorX { get; private set; }

    public int CursorY { get; private set; }

    /// <summary>True while a finger is down and the gesture could still turn into a click.</summary>
    public bool IsGesturing => _fingers.Count > 0;

    public void Resize(int width, int height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        CursorX = Math.Clamp(CursorX, 0, Width - 1);
        CursorY = Math.Clamp(CursorY, 0, Height - 1);
    }

    /// <summary>Abandons the gesture in flight without emitting anything — used when a pinch takes over,
    /// so zooming never also scrolls or clicks.</summary>
    public void Cancel()
    {
        _mode = _fingers.Count > 0 ? Mode.Spent : Mode.Idle;
        _scrollX = 0;
        _scrollY = 0;
    }

    /// <param name="x">The finger's position in <em>guest</em> pixels — the caller applies the fit.</param>
    public IReadOnlyList<TrackpadAction> Down(int pointerId, double x, double y, double nowMilliseconds)
    {
        _fingers[pointerId] = new Finger(x, y, x, y, nowMilliseconds, 0);
        if (_fingers.Count == 1)
        {
            _primary = pointerId;
            _mode = Mode.Cursor;
        }
        else if (_fingers.Count == 2 && _mode != Mode.Spent)
        {
            // A second finger cancels whatever the first was becoming: two fingers scroll, and a tap
            // that grew a second finger was never a tap.
            _mode = Mode.Scroll;
            _scrollX = 0;
            _scrollY = 0;
        }

        return [];
    }

    public IReadOnlyList<TrackpadAction> Move(int pointerId, double x, double y, double nowMilliseconds)
    {
        if (!_fingers.TryGetValue(pointerId, out var finger))
        {
            return [];
        }

        var dx = x - finger.LastX;
        var dy = y - finger.LastY;
        var travel = finger.Travel + Math.Sqrt((dx * dx) + (dy * dy));
        _fingers[pointerId] = finger with { LastX = x, LastY = y, Travel = travel };

        switch (_mode)
        {
            case Mode.Cursor when pointerId == _primary:
                // 1:1 in guest space, no acceleration. Predictability beats reach — the answer to "that
                // target is too small" is the pinch zoom, not a curve nobody can learn.
                var moved = SetCursor(CursorX + dx, CursorY + dy);
                return moved ? [new TrackpadMove(CursorX, CursorY)] : [];

            case Mode.Scroll:
                _scrollX += dx;
                _scrollY += dy;
                return DrainScroll();

            default:
                return [];
        }
    }

    /// <summary>Called on a timer while a finger is down; this is the only thing that can fire a long
    /// press, because a finger held still produces no events of its own.</summary>
    public IReadOnlyList<TrackpadAction> Tick(double nowMilliseconds)
    {
        if (_mode != Mode.Cursor || _fingers.Count != 1 || !_fingers.TryGetValue(_primary, out var finger))
        {
            return [];
        }

        if (finger.Travel > TapSlop || nowMilliseconds - finger.DownAt < LongPressMilliseconds)
        {
            return [];
        }

        _mode = Mode.Spent;
        return [new TrackpadClick(CursorX, CursorY, 2)];
    }

    public IReadOnlyList<TrackpadAction> Up(int pointerId, double nowMilliseconds)
    {
        if (!_fingers.Remove(pointerId, out var finger))
        {
            return [];
        }

        IReadOnlyList<TrackpadAction> actions = [];
        if (_mode == Mode.Cursor
            && pointerId == _primary
            && finger.Travel <= TapSlop
            && nowMilliseconds - finger.DownAt <= TapMilliseconds)
        {
            actions = [new TrackpadClick(CursorX, CursorY, 0)];
        }

        if (_fingers.Count == 0)
        {
            _mode = Mode.Idle;
            _primary = -1;
            _scrollX = 0;
            _scrollY = 0;
        }
        else
        {
            // Lifting one of two fingers must not silently promote the other back to cursor duty: the
            // remaining finger is mid-scroll and would jump the pointer across the desktop.
            _mode = Mode.Spent;
        }

        return actions;
    }

    /// <summary>Places the cursor outright (a fresh Info, or a jump requested by the view).</summary>
    public void PlaceCursor(int x, int y) => SetCursor(x, y);

    private bool SetCursor(double x, double y)
    {
        var nx = Math.Clamp((int)Math.Round(x), 0, Width - 1);
        var ny = Math.Clamp((int)Math.Round(y), 0, Height - 1);
        if (nx == CursorX && ny == CursorY)
        {
            return false;
        }

        CursorX = nx;
        CursorY = ny;
        return true;
    }

    private List<TrackpadAction> DrainScroll()
    {
        var actions = new List<TrackpadAction>();

        // Direct manipulation: dragging the fingers up pushes the content up, which is a wheel-down
        // notch. Hence the negation — the axis the finger moves on and the axis the wheel reports are
        // opposites, and getting this backwards is the classic "scrolling feels wrong" bug.
        while (Math.Abs(_scrollY) >= ScrollStep)
        {
            var step = Math.Sign(_scrollY);
            _scrollY -= step * ScrollStep;
            actions.Add(new TrackpadScroll(CursorX, CursorY, 0, -step));
        }

        while (Math.Abs(_scrollX) >= ScrollStep)
        {
            var step = Math.Sign(_scrollX);
            _scrollX -= step * ScrollStep;
            actions.Add(new TrackpadScroll(CursorX, CursorY, -step, 0));
        }

        return actions;
    }

    private readonly record struct Finger(
        double StartX, double StartY, double LastX, double LastY, double DownAt, double Travel);
}
