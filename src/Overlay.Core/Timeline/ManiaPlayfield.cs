namespace Overlay.Core.Timeline;

/// <summary>Where osu! stable's mania editor draws its playfield, so its columns can be tinted.</summary>
/// <remarks>
/// Measured from screenshots of the 18-key mania editor in a 2560×1441 client area (spikes/mania): the playfield's
/// black area spans x 472–2065 and y 219–1375, i.e. 18 columns of 88.5 px. Other key counts are assumed to keep that
/// column width, centred (unverified), and other window sizes to scale with the client height (unverified).
/// </remarks>
public static class ManiaPlayfield
{
    private const double CalibratedHeight = 1441;
    private const int CalibratedKeys = 18;
    private const double Left = 472, Right = 2065, Top = 219, Bottom = 1375;

    private const double ColumnWidth = (Right - Left) / CalibratedKeys;
    private const double CentreX = (Left + Right) / 2;

    /// <summary>The playfield in screen pixels, for an osu! client area and a key count.</summary>
    public static PixelRect Bounds(PixelRect client, int keys)
    {
        double s = client.Height / CalibratedHeight;
        double width = Math.Clamp(keys, 1, 18) * ColumnWidth;
        return new PixelRect(
            client.X + (int)Math.Round((CentreX - width / 2) * s),
            client.Y + (int)Math.Round(Top * s),
            (int)Math.Round(width * s),
            (int)Math.Round((Bottom - Top) * s));
    }

    /// <summary>
    /// Which combo colour a column (1 = leftmost) gets: one step further for every switch column at or before it.
    /// E.g. with switches 3, 5, 7, 11, 15, 17: columns 1–2 → 0, 3–4 → 1, 5–6 → 2, 7–10 → 3, 11–14 → 4, 15–16 → 5, 17+ → 6.
    /// </summary>
    public static int ColourIndex(int column, IReadOnlyCollection<int> switchColumns) => switchColumns.Count(s => s <= column);
}
