using Agnes.App.Mobile.Controls;

namespace Agnes.Mobile.Tests;

/// <summary>
/// The gesture rules, which are the whole difference between a usable remote desktop on a phone and a
/// toy. They are stated once here, as a table anyone can read:
///
///   drag → the cursor moves, relative · tap → click where the cursor is · hold still → right-click ·
///   two fingers → scroll at the cursor · pinch → cancel, the view is zooming.
///
/// Time is an argument rather than a clock, so the orderings that matter (did the hold fire before the
/// finger lifted?) are reachable at all.
/// </summary>
public sealed class DisplayTrackpadTests
{
    private const int Width = 1280;
    private const int Height = 800;

    private static DisplayTrackpad Pad() => new(Width, Height);

    [Fact]
    public void It_starts_in_the_middle_of_the_display()
    {
        var pad = Pad();

        Assert.Equal(Width / 2, pad.CursorX);
        Assert.Equal(Height / 2, pad.CursorY);
    }

    [Fact]
    public void A_drag_moves_the_cursor_relative_to_where_it_already_was()
    {
        var pad = Pad();
        var startX = pad.CursorX;

        pad.Down(1, 100, 100, 0);
        var actions = pad.Move(1, 160, 130, 30);

        // Not "the cursor jumped to 160" — the finger travelled 60, so the cursor did.
        var move = Assert.IsType<TrackpadMove>(Assert.Single(actions));
        Assert.Equal(startX + 60, move.X);
        Assert.Equal((Height / 2) + 30, move.Y);
        Assert.Equal(move.X, pad.CursorX);
    }

    [Fact]
    public void The_cursor_stops_at_the_edge_of_the_display()
    {
        var pad = Pad();

        pad.Down(1, 0, 0, 0);
        pad.Move(1, 10_000, 10_000, 50);

        Assert.Equal(Width - 1, pad.CursorX);
        Assert.Equal(Height - 1, pad.CursorY);
    }

    [Fact]
    public void A_tap_clicks_at_the_cursor_not_where_you_tapped()
    {
        var pad = Pad();

        pad.Down(1, 40, 40, 0);
        var actions = pad.Up(1, 120);

        var click = Assert.IsType<TrackpadClick>(Assert.Single(actions));
        Assert.Equal(0, click.Button);
        Assert.Equal(Width / 2, click.X);   // the cursor never moved
        Assert.Equal(Height / 2, click.Y);
    }

    [Fact]
    public void A_drag_is_not_a_tap()
    {
        var pad = Pad();

        pad.Down(1, 40, 40, 0);
        pad.Move(1, 40 + (DisplayTrackpad.TapSlop * 3), 40, 40);
        var actions = pad.Up(1, 80);

        Assert.Empty(actions);
    }

    [Fact]
    public void A_slow_press_is_not_a_tap_either()
    {
        var pad = Pad();

        pad.Down(1, 40, 40, 0);
        var actions = pad.Up(1, DisplayTrackpad.TapMilliseconds + 50);

        Assert.Empty(actions);
    }

    [Fact]
    public void Holding_still_right_clicks_once_and_then_the_lift_does_nothing()
    {
        var pad = Pad();
        pad.Down(1, 40, 40, 0);

        Assert.Empty(pad.Tick(DisplayTrackpad.LongPressMilliseconds - 10));

        var fired = pad.Tick(DisplayTrackpad.LongPressMilliseconds + 1);
        var click = Assert.IsType<TrackpadClick>(Assert.Single(fired));
        Assert.Equal(2, click.Button);

        // The finger is still down. It must not fire again, and lifting must not add a left click on top.
        Assert.Empty(pad.Tick(DisplayTrackpad.LongPressMilliseconds + 500));
        Assert.Empty(pad.Up(1, DisplayTrackpad.LongPressMilliseconds + 600));
    }

    [Fact]
    public void Moving_cancels_the_hold()
    {
        var pad = Pad();
        pad.Down(1, 40, 40, 0);
        pad.Move(1, 40 + (DisplayTrackpad.TapSlop * 2), 40, 20);

        Assert.Empty(pad.Tick(DisplayTrackpad.LongPressMilliseconds + 100));
    }

    [Fact]
    public void Two_fingers_scroll_at_the_cursor_in_the_direction_the_content_should_go()
    {
        var pad = Pad();
        pad.Down(1, 100, 400, 0);
        pad.Down(2, 200, 400, 5);

        // Fingers up the screen = content up = a wheel-down notch.
        var actions = pad.Move(1, 100, 400 - (DisplayTrackpad.ScrollStep * 2), 40);

        Assert.Equal(2, actions.Count);
        foreach (var action in actions)
        {
            var scroll = Assert.IsType<TrackpadScroll>(action);
            Assert.Equal(1, scroll.Dy);
            Assert.Equal(0, scroll.Dx);
            Assert.Equal(pad.CursorX, scroll.X);
            Assert.Equal(pad.CursorY, scroll.Y);
        }
    }

    [Fact]
    public void A_second_finger_cancels_the_tap_the_first_was_becoming()
    {
        var pad = Pad();
        pad.Down(1, 100, 400, 0);
        pad.Down(2, 200, 400, 5);

        Assert.Empty(pad.Up(1, 60));
        Assert.Empty(pad.Up(2, 70));
    }

    [Fact]
    public void Scrolling_never_moves_the_cursor()
    {
        var pad = Pad();
        var before = (pad.CursorX, pad.CursorY);

        pad.Down(1, 100, 400, 0);
        pad.Down(2, 200, 400, 5);
        pad.Move(1, 100, 100, 40);

        Assert.Equal(before, (pad.CursorX, pad.CursorY));
    }

    [Fact]
    public void A_pinch_cancels_whatever_the_fingers_were_doing()
    {
        var pad = Pad();
        pad.Down(1, 100, 400, 0);
        pad.Down(2, 200, 400, 5);

        pad.Cancel(); // the pinch recognizer took over

        Assert.Empty(pad.Move(1, 100, 100, 40));
        Assert.Empty(pad.Up(1, 60));
    }

    [Fact]
    public void Lifting_one_of_two_fingers_does_not_promote_the_other_to_the_cursor()
    {
        var pad = Pad();
        pad.Down(1, 100, 400, 0);
        pad.Down(2, 200, 400, 5);
        pad.Up(2, 40);

        // The remaining finger is mid-scroll; treating it as a fresh drag would jump the pointer.
        Assert.Empty(pad.Move(1, 400, 400, 60));
        Assert.Equal(Width / 2, pad.CursorX);
    }

    [Fact]
    public void Resizing_keeps_the_cursor_on_the_display()
    {
        var pad = Pad();
        pad.Down(1, 0, 0, 0);
        pad.Move(1, 5000, 5000, 30);
        pad.Up(1, 60);

        pad.Resize(640, 480);

        Assert.Equal(639, pad.CursorX);
        Assert.Equal(479, pad.CursorY);
    }
}
