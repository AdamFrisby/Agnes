namespace Agnes.App.Desktop.Controls;

/// <summary>
/// How a guest display of one size sits inside a panel of another: a letterbox fit, and the inverse map that
/// turns a click in the panel back into the guest pixel underneath it.
/// </summary>
/// <remarks>
/// This is deliberately a pure struct with no Avalonia types in its signature. Pointer mapping is the part of
/// a remote screen that is silently wrong rather than visibly broken — an off-by-a-letterbox-band click lands
/// somewhere plausible, and the agent's screenshot of the result looks fine — so it is a value that can be
/// unit-tested at its corners rather than glue buried in an event handler.
/// </remarks>
/// <param name="Scale">Guest pixels → panel pixels.</param>
/// <param name="OffsetX">Left edge of the picture inside the panel.</param>
/// <param name="OffsetY">Top edge of the picture inside the panel.</param>
public readonly record struct DisplayFit(double Scale, double OffsetX, double OffsetY, int GuestWidth, int GuestHeight)
{
    /// <summary>The largest centred rectangle of the guest's aspect ratio that fits the panel.</summary>
    public static DisplayFit Compute(int guestWidth, int guestHeight, double panelWidth, double panelHeight)
    {
        if (guestWidth <= 0 || guestHeight <= 0 || panelWidth <= 0 || panelHeight <= 0)
        {
            return new DisplayFit(0, 0, 0, guestWidth, guestHeight);
        }

        var scale = Math.Min(panelWidth / guestWidth, panelHeight / guestHeight);
        var drawnWidth = guestWidth * scale;
        var drawnHeight = guestHeight * scale;
        return new DisplayFit(scale, (panelWidth - drawnWidth) / 2, (panelHeight - drawnHeight) / 2, guestWidth, guestHeight);
    }

    /// <summary>The picture's size inside the panel.</summary>
    public double DrawnWidth => GuestWidth * Scale;

    public double DrawnHeight => GuestHeight * Scale;

    /// <summary>Whether a panel point is over the picture rather than the letterbox bands beside it.</summary>
    public bool Contains(double panelX, double panelY)
        => Scale > 0
        && panelX >= OffsetX && panelX <= OffsetX + DrawnWidth
        && panelY >= OffsetY && panelY <= OffsetY + DrawnHeight;

    /// <summary>
    /// The guest pixel under a panel point, clamped into the display. Clamping rather than rejecting is
    /// deliberate: a drag that strays a few pixels into the letterbox should still move the guest pointer to
    /// the edge, which is what dragging against the edge of a real screen does.
    /// </summary>
    public (int X, int Y) ToGuest(double panelX, double panelY)
    {
        if (Scale <= 0)
        {
            return (0, 0);
        }

        var x = (int)Math.Round((panelX - OffsetX) / Scale);
        var y = (int)Math.Round((panelY - OffsetY) / Scale);
        return (Math.Clamp(x, 0, GuestWidth - 1), Math.Clamp(y, 0, GuestHeight - 1));
    }
}
