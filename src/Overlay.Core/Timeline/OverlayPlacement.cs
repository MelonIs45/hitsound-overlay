namespace Overlay.Core.Timeline;

/// <summary>A rectangle in physical screen pixels.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height);

/// <summary>
/// Where the overlay sits relative to osu!'s client area: position and width as fractions of it, so the overlay
/// keeps its place when osu! is moved, resized or switched between windowed and borderless; height in DIPs,
/// so the strip stays the same physical size.
/// </summary>
public sealed record OverlayPlacement(double XFraction, double YFraction, double WidthFraction, double HeightDip)
{
    public static readonly OverlayPlacement Default = new(0.2, 0.06, 0.6, 110);

    public PixelRect ToScreen(PixelRect client, double dpiScale)
    {
        int w = Math.Max(50, (int)Math.Round(client.Width * WidthFraction));
        int h = Math.Max(30, (int)Math.Round(HeightDip * dpiScale));
        int x = client.X + (int)Math.Round(client.Width * XFraction);
        int y = client.Y + (int)Math.Round(client.Height * YFraction);
        return new PixelRect(x, y, w, h);
    }

    public static OverlayPlacement FromScreen(PixelRect overlay, PixelRect client, double dpiScale) =>
        client.Width <= 0 || client.Height <= 0 ? Default : new(
            (overlay.X - client.X) / (double)client.Width,
            (overlay.Y - client.Y) / (double)client.Height,
            overlay.Width / (double)client.Width,
            overlay.Height / dpiScale);
}
