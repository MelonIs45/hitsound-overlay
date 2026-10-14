namespace Overlay.Core.Timeline;

/// <summary>Maps song time to x on a horizontal strip with a fixed "now" line.</summary>
/// <param name="Width">Strip width in device-independent pixels.</param>
/// <param name="NowX">x position of the "now" line.</param>
/// <param name="PxPerMs">Zoom: pixels per millisecond of song time.</param>
public readonly record struct TimelineLayout(double Width, double NowX, double PxPerMs)
{
    public double TimeToX(double time, double now) => NowX + (time - now) * PxPerMs;

    public double XToTime(double x, double now) => now + (x - NowX) / PxPerMs;

    /// <summary>Song-time range covered by the strip, widened by <paramref name="marginPx"/> on both sides.</summary>
    public (double From, double To) VisibleRange(double now, double marginPx = 0) =>
        (XToTime(-marginPx, now), XToTime(Width + marginPx, now));
}

/// <summary>Zoom steps, stored as pixels per ms.</summary>
public static class Zoom
{
    public const double Min = 0.02, Max = 3.0, Default = 0.3;

    /// <summary>One zoom step in or out (about 12% per step, like a mouse wheel notch).</summary>
    public static double Step(double pxPerMs, int steps) => Math.Clamp(pxPerMs * Math.Pow(1.12, steps), Min, Max);
}
