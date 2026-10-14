using Overlay.Core.Beatmap;
using Overlay.Core.Timeline;

namespace Overlay.Tests;

public class TimelineTests
{
    [Fact]
    public void Layout_maps_time_to_x_around_the_now_line()
    {
        var layout = new TimelineLayout(Width: 1000, NowX: 250, PxPerMs: 0.5);
        Assert.Equal(250, layout.TimeToX(10_000, now: 10_000));
        Assert.Equal(300, layout.TimeToX(10_100, now: 10_000));
        Assert.Equal(10_100, layout.XToTime(300, now: 10_000));
        Assert.Equal((9_500.0, 11_500.0), layout.VisibleRange(10_000));
        Assert.Equal((9_480.0, 11_520.0), layout.VisibleRange(10_000, marginPx: 10));
    }

    [Fact]
    public void Placement_follows_the_osu_window_and_round_trips()
    {
        var client = new PixelRect(2560, 0, 2560, 1440);
        var bounds = OverlayPlacement.Default.ToScreen(client, dpiScale: 1.5);
        Assert.Equal(new PixelRect(2560 + 512, 86, 1536, 165), bounds);

        // osu! moved and shrunk: same relative spot, same physical height.
        var smaller = new PixelRect(100, 50, 1280, 720);
        Assert.Equal(new PixelRect(100 + 256, 50 + 43, 768, 165), OverlayPlacement.Default.ToScreen(smaller, 1.5));

        var dragged = new PixelRect(2560 + 100, 300, 2000, 150);
        var placement = OverlayPlacement.FromScreen(dragged, client, 1.5);
        Assert.Equal(dragged, placement.ToScreen(client, 1.5));
        Assert.Equal(100, placement.HeightDip, 6);
    }

    /// <summary>
    /// The editor-timeline geometry reproduces what was measured in osu!'s editor (spikes/timeline, 2560×1441):
    /// "now" at x 1014, circles at y 156, and whole-beat ticks at 0.41409 px/ms for TimelineZoom 1.3
    /// (0.25476 for 0.8).
    /// </summary>
    [Theory]
    [InlineData(1.3, 0.41409)]
    [InlineData(1.0, 0.31855)]
    [InlineData(0.8, 0.25476)]
    public void Editor_timeline_matches_measured_positions(double zoom, double measuredPxPerMs)
    {
        var client = new PixelRect(0, 0, 2560, 1441);
        var strip = EditorTimeline.StripBounds(client);
        var view = EditorTimeline.View(zoom);
        double ScreenX(double t, double now) => strip.X + strip.Width * (view.NowFraction + (t - now) * view.WidthsPerMs);

        Assert.Equal(1014.0, ScreenX(64_857, 64_857), 1);
        double expectedAt2s = 1014.0 + measuredPxPerMs * 2000;
        Assert.InRange(ScreenX(66_857, 64_857), expectedAt2s - 0.5, expectedAt2s + 0.5); // within a pixel over 2 s
        Assert.Equal(156.2, strip.Y + strip.Height * view.LaneFraction, 1);
        Assert.Equal(41.5, strip.Height * view.RadiusFraction, 1);
    }

    [Fact]
    public void Editor_timeline_follows_the_osu_window()
    {
        var moved = EditorTimeline.StripBounds(new PixelRect(2560, 0, 2560, 1441));
        Assert.Equal(new PixelRect(2560 + 59, 97, 1911, 104), moved);
    }

    [Fact]
    public void Zoom_steps_are_clamped()
    {
        Assert.True(Zoom.Step(0.3, 1) > 0.3);
        Assert.Equal(Zoom.Max, Zoom.Step(2.9, 10));
        Assert.Equal(Zoom.Min, Zoom.Step(0.03, -10));
    }

    private static HitObject Obj(double start, double end) => new(HitObjectKind.Slider, start, end, 1, false, 1, 1);

    [Fact]
    public void Index_includes_long_objects_that_started_before_the_range()
    {
        var objects = new[] { Obj(0, 100), Obj(200, 5_000), Obj(300, 300), Obj(1_000, 1_000), Obj(6_000, 6_000) };
        var index = new HitObjectIndex(objects);

        var (first, end) = index.Query(2_000, 3_000);
        var hits = objects[first..end].Where(o => o.EndTime >= 2_000 && o.StartTime <= 3_000).ToList();
        Assert.Equal(new[] { objects[1] }, hits); // the long slider is still on screen

        Assert.Equal((0, 1), index.Query(-500, 50));
        Assert.Equal((4, 5), index.Query(5_500, 6_000)); // object exactly at the right edge is included
        var (f, e) = index.Query(7_000, 8_000);
        Assert.Equal(f, e);
    }

    private static TimingModel Timing(params TimingPoint[] points) => TimingModel.Build(points);

    [Fact]
    public void Grid_ticks_have_snap_denominators_and_bar_lines()
    {
        var timing = Timing(new TimingPoint(1_000, 400, 4, true));
        var ticks = BeatGrid.Ticks(timing, 1_000, 1_400, 4).ToList();

        Assert.Equal(new[] { 1_000.0, 1_100, 1_200, 1_300, 1_400 }, ticks.Select(t => t.Time));
        Assert.Equal(new[] { 1, 4, 2, 4, 1 }, ticks.Select(t => t.Denominator));
        Assert.True(ticks[0].IsBarLine);
        Assert.False(ticks[4].IsBarLine); // beat 2 of the bar
        Assert.True(BeatGrid.Ticks(timing, 2_600, 2_600, 1).Single().IsBarLine); // 4 beats later

        var thirds = BeatGrid.Ticks(timing, 1_000, 1_400, 6).Select(t => t.Denominator);
        Assert.Equal(new[] { 1, 6, 3, 2, 3, 6, 1 }, thirds);
    }

    [Fact]
    public void Grid_restarts_at_each_red_line_and_extends_before_the_first()
    {
        var timing = Timing(new TimingPoint(1_000, 500, 4, true), new TimingPoint(2_250, 300, 3, true));
        var ticks = BeatGrid.Ticks(timing, 0, 2_900, 1).ToList();

        Assert.Equal(new[] { 0.0, 500, 1_000, 1_500, 2_000, 2_250, 2_550, 2_850 }, ticks.Select(t => t.Time));
        Assert.False(ticks[0].IsBarLine); // 2 beats before the first red line
        Assert.True(ticks[2].IsBarLine);  // the first red line
        Assert.True(ticks[5].IsBarLine);  // a red line starts a bar
        Assert.False(ticks[6].IsBarLine);
    }

    [Fact]
    public void Readable_divisor_drops_detail_when_ticks_get_too_close()
    {
        Assert.Equal(4, BeatGrid.Readable(4, beatLengthMs: 500, pxPerMs: 0.3));  // 37.5 px apart
        Assert.Equal(2, BeatGrid.Readable(4, beatLengthMs: 500, pxPerMs: 0.03)); // 1/4 would be 3.75 px apart
        Assert.Equal(1, BeatGrid.Readable(3, beatLengthMs: 500, pxPerMs: 0.02));
        Assert.Equal(6, BeatGrid.Readable(6, beatLengthMs: 300, pxPerMs: 0.3));
        Assert.Equal(5, BeatGrid.Readable(5, beatLengthMs: 500, pxPerMs: 0.3)); // unusual divisors still draw
        Assert.Equal(1, BeatGrid.Readable(5, beatLengthMs: 500, pxPerMs: 0.03));
    }
}
