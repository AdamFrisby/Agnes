namespace Agnes.App.Mobile.Controls;

/// <summary>
/// Where the guest's picture lands inside the view, and how a point converts between the two.
///
/// The guest is a fixed grid (1280×800 today) and we never resize it — a phone showing a desktop is a
/// window onto it, not a client that gets to dictate its shape. So the picture is scaled to fit, centred
/// in whatever is left over (the letterbox), then zoomed and panned by the person. Everything the
/// surface draws and every coordinate it sends the host goes through this one transform, which is why
/// it is a pure value rather than a pile of fields on the control: it is the part most likely to be
/// wrong, and the only part that can be tested without a screen.
/// </summary>
/// <param name="Scale">View pixels per guest pixel.</param>
/// <param name="OffsetX">Where the picture's left edge sits in the view.</param>
/// <param name="OffsetY">Where the picture's top edge sits in the view.</param>
public readonly record struct DisplayFit(double Scale, double OffsetX, double OffsetY)
{
    /// <summary>Nothing to draw — no view, or no geometry from the host yet.</summary>
    public static readonly DisplayFit None = new(0, 0, 0);

    public bool IsEmpty => Scale <= 0;

    /// <summary>
    /// Fits <paramref name="guestWidth"/>×<paramref name="guestHeight"/> into the view at
    /// <paramref name="zoom"/>, offset by a pan in <em>view</em> pixels.
    /// </summary>
    /// <remarks>
    /// Two rules hold whatever the pan says. An axis whose picture is smaller than the view is centred
    /// and ignores the pan outright — panning a letterboxed picture into its own black bars is a way to
    /// lose the screen and not know why. An axis larger than the view is clamped to its own edges, so
    /// dragging always stops at the edge of the desktop rather than sliding it away.
    /// </remarks>
    public static DisplayFit Compute(
        double viewWidth,
        double viewHeight,
        int guestWidth,
        int guestHeight,
        double zoom = 1,
        double panX = 0,
        double panY = 0)
    {
        if (viewWidth <= 0 || viewHeight <= 0 || guestWidth <= 0 || guestHeight <= 0 || zoom <= 0)
        {
            return None;
        }

        var scale = Math.Min(viewWidth / guestWidth, viewHeight / guestHeight) * zoom;
        var width = guestWidth * scale;
        var height = guestHeight * scale;

        return new DisplayFit(scale, Place(viewWidth, width, panX), Place(viewHeight, height, panY));
    }

    private static double Place(double view, double picture, double pan)
        => picture <= view
            ? (view - picture) / 2
            : Math.Clamp(((view - picture) / 2) + pan, view - picture, 0);

    /// <summary>A guest pixel's position in the view.</summary>
    public (double X, double Y) ToView(double guestX, double guestY)
        => (OffsetX + (guestX * Scale), OffsetY + (guestY * Scale));

    /// <summary>
    /// The guest pixel under a view point, clamped to the display. Clamping rather than rejecting is
    /// deliberate: a drag that leaves the picture should pin the pointer to the edge the way a real
    /// mouse does against a screen border, not stop reporting.
    /// </summary>
    public (int X, int Y) ToGuest(double viewX, double viewY, int guestWidth, int guestHeight)
    {
        if (IsEmpty)
        {
            return (0, 0);
        }

        var x = (int)Math.Round((viewX - OffsetX) / Scale);
        var y = (int)Math.Round((viewY - OffsetY) / Scale);
        return (Math.Clamp(x, 0, Math.Max(0, guestWidth - 1)), Math.Clamp(y, 0, Math.Max(0, guestHeight - 1)));
    }

    /// <summary>
    /// The pan that brings a guest point back into view, given the pan in force. Used to follow the
    /// trackpad cursor when the person has zoomed in: the cursor moving off-screen with no way to see
    /// where it went is the failure mode of every touch-driven remote desktop.
    /// </summary>
    /// <param name="margin">How close to the edge counts as off-screen, in view pixels.</param>
    public static (double PanX, double PanY) Follow(
        double viewWidth,
        double viewHeight,
        int guestWidth,
        int guestHeight,
        double zoom,
        double panX,
        double panY,
        double guestX,
        double guestY,
        double margin = 48)
    {
        var fit = Compute(viewWidth, viewHeight, guestWidth, guestHeight, zoom, panX, panY);
        if (fit.IsEmpty)
        {
            return (panX, panY);
        }

        var (x, y) = fit.ToView(guestX, guestY);
        return (
            panX + Nudge(x, viewWidth, guestWidth * fit.Scale, margin),
            panY + Nudge(y, viewHeight, guestHeight * fit.Scale, margin));
    }

    private static double Nudge(double position, double view, double picture, double margin)
    {
        if (picture <= view)
        {
            return 0; // letterboxed on this axis: the whole extent is already visible
        }

        var slack = Math.Min(margin, view / 3);
        if (position < slack)
        {
            return slack - position;
        }

        return position > view - slack ? view - slack - position : 0;
    }
}
