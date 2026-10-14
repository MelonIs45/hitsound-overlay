using Overlay.Core.Timeline;

namespace Overlay.Tests;

public class ManiaTests
{
    [Fact]
    public void Colours_step_at_the_switch_columns()
    {
        int[] switches = { 3, 5, 7, 11, 15, 17 };
        Assert.Equal(new[] { 0, 0, 1, 1, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 6, 6 },
            Enumerable.Range(1, 18).Select(c => ManiaPlayfield.ColourIndex(c, switches)));
    }

    [Fact]
    public void Playfield_geometry_matches_the_measured_editor()
    {
        // 18 keys at 2560×1441: the measured black area.
        Assert.Equal(new PixelRect(472, 219, 1593, 1156), ManiaPlayfield.Bounds(new PixelRect(0, 0, 2560, 1441), keys: 18));
        // Fewer keys: same column width, centred.
        var p4 = ManiaPlayfield.Bounds(new PixelRect(0, 0, 2560, 1441), keys: 4);
        Assert.Equal(354, p4.Width);
        Assert.InRange(p4.X + p4.Width / 2.0, 1267.5, 1269.5); // centred, within pixel rounding
    }
}
