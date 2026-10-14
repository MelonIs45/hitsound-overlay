using Overlay.Core.Beatmap;
using Overlay.Core.Timeline;

namespace Overlay.Tests;

public class SkinTests
{
    [Fact]
    public void Parses_the_values_that_affect_hit_objects()
    {
        // Lines as they appear in a real skin.ini (MonkoEdit), with comments and odd spacing.
        var ini = SkinIni.Parse("""
            [General]
            Name:MonkoEdit
            HitCircleOverlayAboveNumber:1
            [Colours]
            Combo1: 164,100,240    // purple
            Combo2: 230, 110, 150  // lilac
            Combo3: 240, 150, 255 // pink
            SliderBorder:100,100,100
            [Fonts]
            HitCirclePrefix: default
            HitCircleOverlap: 14
            [Mania]
            Keys: 4
            """);

        Assert.Equal("default", ini.HitCirclePrefix);
        Assert.Equal(14, ini.HitCircleOverlap);
        Assert.True(ini.HitCircleOverlayAboveNumber);
        Assert.Equal(new[] { new Rgb(164, 100, 240), new Rgb(230, 110, 150), new Rgb(240, 150, 255) }, ini.ComboColours);
    }

    [Fact]
    public void Missing_values_use_osu_defaults()
    {
        var ini = SkinIni.Parse("[General]\nHitCircleOverlayAboveNumer: 0\n[Fonts]\nHitCirclePrefix: numbers\\num\n");
        Assert.Equal("numbers/num", ini.HitCirclePrefix);
        Assert.Equal(-2, ini.HitCircleOverlap);
        Assert.False(ini.HitCircleOverlayAboveNumber); // the old misspelt key still counts
        Assert.Empty(ini.ComboColours);
    }

    [Fact]
    public void Objects_fade_towards_the_timeline_ends()
    {
        double PerPx(double px) => px / 104.0; // strip height at the calibrated size
        Assert.Equal(0, EditorTimeline.EdgeAlpha(PerPx(-50)));
        Assert.Equal(0.3, EditorTimeline.EdgeAlpha(0), 3);
        Assert.Equal(0.5, EditorTimeline.EdgeAlpha(PerPx(70)), 3);
        Assert.Equal(1, EditorTimeline.EdgeAlpha(PerPx(200)));
        double last = -1;
        for (double px = -60; px < 200; px += 5)
        {
            double a = EditorTimeline.EdgeAlpha(PerPx(px));
            Assert.True(a >= last, $"fade should not decrease moving inwards (at {px} px)");
            last = a;
        }
    }
}
