using Agnes.App.Mobile.Controls;

namespace Agnes.Mobile.Tests;

/// <summary>
/// The transform between the phone's panel and the guest's pixels.
///
/// Everything the screen segment does — where the picture lands, which pixel a finger is over, where the
/// cursor is drawn — goes through this one value, and getting it wrong is invisible in a screenshot and
/// obvious the moment someone tries to click a button. So it is tested at the places that can't be
/// fudged: the four corners, the centre, and the letterbox.
/// </summary>
public sealed class DisplayFitTests
{
    private const int GuestWidth = 1280;
    private const int GuestHeight = 800;

    // A phone panel: much wider aspect than tall relative to the guest, so the fit is width-limited and
    // the letterbox is top and bottom.
    private const double ViewWidth = 400;
    private const double ViewHeight = 700;

    [Fact]
    public void A_wide_guest_in_a_tall_view_is_fitted_to_the_width_and_letterboxed_vertically()
    {
        var fit = DisplayFit.Compute(ViewWidth, ViewHeight, GuestWidth, GuestHeight);

        Assert.Equal(ViewWidth / GuestWidth, fit.Scale, 6);
        Assert.Equal(0, fit.OffsetX, 6);                       // fills the width exactly
        Assert.True(fit.OffsetY > 0, "letterboxed vertically");
        Assert.Equal((ViewHeight - (GuestHeight * fit.Scale)) / 2, fit.OffsetY, 6);
    }

    [Fact]
    public void The_corners_and_the_centre_round_trip()
    {
        var fit = DisplayFit.Compute(ViewWidth, ViewHeight, GuestWidth, GuestHeight);

        foreach (var (gx, gy) in new[] { (0, 0), (GuestWidth - 1, 0), (0, GuestHeight - 1), (GuestWidth - 1, GuestHeight - 1), (GuestWidth / 2, GuestHeight / 2) })
        {
            var (vx, vy) = fit.ToView(gx, gy);
            var back = fit.ToGuest(vx, vy, GuestWidth, GuestHeight);

            Assert.Equal(gx, back.X);
            Assert.Equal(gy, back.Y);
        }
    }

    [Fact]
    public void The_top_left_of_the_picture_is_guest_zero_zero()
    {
        var fit = DisplayFit.Compute(ViewWidth, ViewHeight, GuestWidth, GuestHeight);

        Assert.Equal((0, 0), fit.ToGuest(fit.OffsetX, fit.OffsetY, GuestWidth, GuestHeight));
    }

    [Fact]
    public void A_point_outside_the_picture_pins_to_the_edge_rather_than_being_rejected()
    {
        // A drag that runs off the letterbox should behave like a mouse against a screen border.
        var fit = DisplayFit.Compute(ViewWidth, ViewHeight, GuestWidth, GuestHeight);

        Assert.Equal((0, 0), fit.ToGuest(-500, -500, GuestWidth, GuestHeight));
        Assert.Equal((GuestWidth - 1, GuestHeight - 1), fit.ToGuest(9999, 9999, GuestWidth, GuestHeight));
    }

    [Fact]
    public void Zooming_scales_about_the_centre_until_the_pan_moves_it()
    {
        var fitted = DisplayFit.Compute(ViewWidth, ViewHeight, GuestWidth, GuestHeight);
        var zoomed = DisplayFit.Compute(ViewWidth, ViewHeight, GuestWidth, GuestHeight, zoom: 2);

        Assert.Equal(fitted.Scale * 2, zoomed.Scale, 6);

        // The guest pixel in the middle of the display stays in the middle of the view.
        var (cx, cy) = zoomed.ToView(GuestWidth / 2.0, GuestHeight / 2.0);
        Assert.Equal(ViewWidth / 2, cx, 3);
        Assert.Equal(ViewHeight / 2, cy, 3);
    }

    [Fact]
    public void A_letterboxed_axis_ignores_the_pan()
    {
        // At fit-to-view the picture is narrower than nothing on X and shorter on Y; dragging must not be
        // able to slide the desktop into its own black bars.
        var still = DisplayFit.Compute(ViewWidth, ViewHeight, GuestWidth, GuestHeight, zoom: 1, panX: 300, panY: -400);
        var none = DisplayFit.Compute(ViewWidth, ViewHeight, GuestWidth, GuestHeight);

        Assert.Equal(none.OffsetX, still.OffsetX, 6);
        Assert.Equal(none.OffsetY, still.OffsetY, 6);
    }

    [Fact]
    public void A_pan_past_the_edge_of_a_zoomed_picture_stops_at_the_edge()
    {
        var fit = DisplayFit.Compute(ViewWidth, ViewHeight, GuestWidth, GuestHeight, zoom: 3, panX: 100_000, panY: 100_000);

        // Dragging right as far as you like still puts the picture's left edge at the view's left edge.
        Assert.Equal(0, fit.OffsetX, 6);
        Assert.Equal(0, fit.OffsetY, 6);
    }

    [Fact]
    public void No_view_and_no_geometry_are_both_nothing_to_draw()
    {
        Assert.True(DisplayFit.Compute(0, 700, GuestWidth, GuestHeight).IsEmpty);
        Assert.True(DisplayFit.Compute(ViewWidth, ViewHeight, 0, 0).IsEmpty);
    }

    [Fact]
    public void Following_the_cursor_only_moves_an_axis_that_is_actually_scrolled()
    {
        // Zoomed in enough that both axes overflow, with the cursor at the far right.
        var (panX, panY) = DisplayFit.Follow(
            ViewWidth, ViewHeight, GuestWidth, GuestHeight, zoom: 3, panX: 0, panY: 0,
            guestX: GuestWidth - 1, guestY: GuestHeight / 2.0);

        Assert.True(panX < 0, "the view scrolls right to reveal the cursor");

        // …and at fit-to-view, where everything is already visible, it does nothing at all.
        var (stillX, stillY) = DisplayFit.Follow(
            ViewWidth, ViewHeight, GuestWidth, GuestHeight, zoom: 1, panX: 0, panY: 0,
            guestX: GuestWidth - 1, guestY: 0);

        Assert.Equal(0, stillX, 6);
        Assert.Equal(0, stillY, 6);
        Assert.Equal(0, panY, 6);
    }
}
