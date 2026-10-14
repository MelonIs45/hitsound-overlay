namespace Overlay.Core.Timeline;

/// <summary>
/// Where osu! stable's editor draws its top timeline, and how it maps time to pixels, so std objects can be drawn
/// exactly where the std editor would put them.
/// </summary>
/// <remarks>
/// Measured from screenshots of the stable editor (build b20260924cuttingedge) in a 2560×1441 client area
/// (spikes/timeline): whole-beat ticks fitted within ±0.5 px at TimelineZoom 0.8, 1 and 1.3, in both the std and the
/// mania editor. The scale is exactly proportional to TimelineZoom. Other window sizes are assumed to scale with the
/// client height (osu! stable scales its UI by height); that part is unverified.
/// </remarks>
public static class EditorTimeline
{
    private const double CalibratedHeight = 1441;

    // Calibrated positions, in client pixels at the calibrated height.
    private const double StripLeft = 59, StripRight = 1970, StripTop = 97, StripBottom = 201;
    private const double NowX = 1014.0;
    private const double CircleY = 156.2;
    private const double CircleRadius = 41.5;
    /// <summary>Pixels per millisecond at TimelineZoom 1.</summary>
    private const double PxPerMsAtZoom1 = 0.31851;

    /// <summary>
    /// Skin images are drawn at this many screen pixels per @1x image pixel (measured: the ring of a
    /// hitcircleoverlay with a 61.25 px visible radius appears 41.5 px in radius). Numbers are drawn at 0.8× that.
    /// </summary>
    private const double SkinPxPerImagePx = CircleRadius / 61.25;

    // Objects fade out towards both ends of the timeline. Measured opacity against distance (calibrated px) of the
    // object's centre from the strip's end; beyond the last point they're fully opaque.
    private static readonly (double Distance, double Alpha)[] EdgeFade =
    {
        (-CircleRadius, 0.0), (0, 0.3), (25, 0.38), (70, 0.5), (93, 0.62), (125, 1.0),
    };

    /// <summary>Opacity of an object whose centre is <paramref name="distanceFraction"/> strip heights from the nearest end.</summary>
    public static double EdgeAlpha(double distanceFraction)
    {
        double d = distanceFraction * (StripBottom - StripTop);
        if (d <= EdgeFade[0].Distance) return 0;
        for (int i = 1; i < EdgeFade.Length; i++)
        {
            var (d1, a1) = EdgeFade[i];
            if (d <= d1)
            {
                var (d0, a0) = EdgeFade[i - 1];
                return a0 + (a1 - a0) * (d - d0) / (d1 - d0);
            }
        }
        return 1;
    }

    /// <summary>The timeline strip in screen pixels, for an osu! client area.</summary>
    public static PixelRect StripBounds(PixelRect client)
    {
        double s = client.Height / CalibratedHeight;
        return new PixelRect(
            client.X + (int)Math.Round(StripLeft * s),
            client.Y + (int)Math.Round(StripTop * s),
            (int)Math.Round((StripRight - StripLeft) * s),
            (int)Math.Round((StripBottom - StripTop) * s));
    }

    /// <summary>
    /// Drawing geometry relative to the strip (so it's independent of DPI and of the window's pixel size):
    /// the "now" line as a fraction of the strip width, the circle centre and radius as fractions of the strip height,
    /// and the scale as strip widths per millisecond.
    /// </summary>
    public static EditorTimelineView View(double timelineZoom) => new(
        NowFraction: (NowX - StripLeft) / (StripRight - StripLeft),
        LaneFraction: (CircleY - StripTop) / (StripBottom - StripTop),
        RadiusFraction: CircleRadius / (StripBottom - StripTop),
        WidthsPerMs: PxPerMsAtZoom1 * timelineZoom / (StripRight - StripLeft),
        SkinScaleFraction: SkinPxPerImagePx / (StripBottom - StripTop));
}

/// <summary>See <see cref="EditorTimeline.View"/>. <paramref name="SkinScaleFraction"/>: strip heights per @1x skin image pixel.</summary>
public sealed record EditorTimelineView(double NowFraction, double LaneFraction, double RadiusFraction, double WidthsPerMs, double SkinScaleFraction);
