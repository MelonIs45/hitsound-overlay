using Overlay.Core.Clock;
using Xunit;
using Xunit.Abstractions;

namespace Overlay.Tests;

/// <summary>
/// The clock for osu!'s memory: osu! writes the song time (whole ms) every frame, about every 2 ms in the editor; the
/// reader polls every millisecond and reports changes.
/// </summary>
public class DirectClockTests
{
    private const double FrameMs = 1000.0 / 240;
    private readonly ITestOutputHelper output;

    public DirectClockTests(ITestOutputHelper output) => this.output = output;

    /// <summary>The editor's song position over wall time: a list of (from wall, song at that moment, rate) pieces.</summary>
    private sealed class Editor
    {
        private readonly List<(double Wall, double Song, double Rate)> pieces = new();

        public Editor(double song, double rate) => pieces.Add((0, song, rate));

        public Editor Then(double wall, double? song = null, double? rate = null)
        {
            double at = SongAt(wall);
            pieces.Add((wall, song ?? at, rate ?? pieces[^1].Rate));
            return this;
        }

        public double SongAt(double wall)
        {
            var p = pieces.Last(x => x.Wall <= wall);
            return p.Song + p.Rate * (wall - p.Wall);
        }
    }

    /// <summary>What the memory reader reports: osu!'s per-frame whole-ms value, as first seen by a 1 ms poll.</summary>
    private static List<TimeSample> Read(Editor editor, double untilMs, int seed = 1)
    {
        var rng = new Random(seed);
        var samples = new List<TimeSample>();
        double osuFrame = 0;
        int? last = null;
        while (osuFrame < untilMs)
        {
            int value = (int)Math.Floor(editor.SongAt(osuFrame));
            if (value != last)
            {
                double seen = Math.Ceiling(osuFrame + 0.05) + rng.NextDouble() * 0.3; // next poll after the write
                samples.Add(new TimeSample(value, seen, null));
                last = value;
            }
            osuFrame += 1.6 + rng.NextDouble() * 0.8; // osu! frames ~2 ms apart
        }
        return samples;
    }

    private static List<(double Wall, double Shown, double Truth)> Run(DirectClock clock, Editor editor, double untilMs, int seed = 1)
    {
        var samples = Read(editor, untilMs, seed);
        var frames = new List<(double, double, double)>();
        int next = 0;
        for (double f = 0; f < untilMs; f += FrameMs)
        {
            while (next < samples.Count && samples[next].WallMs <= f) clock.AddSample(samples[next++]);
            if (clock.HasTime) frames.Add((f, clock.GetTime(f), editor.SongAt(f)));
        }
        return frames;
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.75)]
    [InlineData(0.5)]
    [InlineData(0.25)]
    public void Steady_play_tracks_within_2ms_and_never_goes_backwards(double rate)
    {
        var frames = Run(new DirectClock(), new Editor(10_000, rate), 10_000);
        var settled = frames.Where(f => f.Wall > 1000).ToList();
        double worst = settled.Max(f => Math.Abs(f.Shown - f.Truth));
        output.WriteLine($"{rate}x: worst {worst:F2} ms, mean {settled.Average(f => f.Shown - f.Truth):F2} ms");
        Assert.True(worst < 2, $"worst error {worst:F2} ms");
        foreach (var (a, b) in frames.Zip(frames.Skip(1)).Where(p => p.First.Wall > 100))
            Assert.True(b.Shown >= a.Shown, $"went back at {b.Wall:F1} ms");
    }

    [Fact]
    public void Pause_shows_the_exact_position_quickly_with_a_tiny_overshoot()
    {
        var editor = new Editor(10_000, 1.0).Then(3000, rate: 0);
        var frames = Run(new DirectClock(), editor, 4000);
        double paused = editor.SongAt(3000);
        double overshoot = frames.Where(f => f.Wall > 3000).Max(f => f.Shown - paused);
        output.WriteLine($"overshoot {overshoot:F2} ms");
        Assert.True(overshoot < 5, $"overshoot {overshoot:F2} ms");
        Assert.All(frames.Where(f => f.Wall > 3040), f => Assert.Equal(Math.Floor(paused), f.Shown, 3));
    }

    [Fact]
    public void Playback_start_follows_within_a_few_ms()
    {
        var editor = new Editor(10_000, 0).Then(2000, rate: 1.0);
        var frames = Run(new DirectClock(), editor, 4000);
        Assert.All(frames.Where(f => f.Wall > 2020), f => Assert.InRange(f.Shown - f.Truth, -4, 2));
        Assert.All(frames.Where(f => f.Wall > 2300), f => Assert.InRange(f.Shown - f.Truth, -2, 2));
    }

    [Theory]
    [InlineData(-41)]
    [InlineData(41)]
    [InlineData(-3000)]
    [InlineData(3000)]
    public void Seeks_while_playing_are_followed_at_once(double jump)
    {
        var editor = new Editor(10_000, 1.0).Then(3000, song: 13_000 + jump);
        var clock = new DirectClock();
        var frames = Run(clock, editor, 5000);
        Assert.All(frames.Where(f => f.Wall > 3015), f => Assert.InRange(f.Shown - f.Truth, -4, 2));
        Assert.All(frames.Where(f => f.Wall > 3300), f => Assert.InRange(f.Shown - f.Truth, -2, 2));
        Assert.Equal(1, clock.SeekCount);
    }

    [Fact]
    public void Scrolling_while_paused_shows_each_step_exactly()
    {
        // Snap steps every 30 ms (a quick scroll), small and large, both ways.
        var editor = new Editor(10_000, 0);
        double song = 10_000;
        var steps = new[] { 19, 19, 19, 19, -19, -19, 150, 150, -300, 19 };
        for (int i = 0; i < steps.Length; i++) editor.Then(1000 + i * 30, song: song += steps[i], rate: 0);
        var frames = Run(new DirectClock(), editor, 2000);
        foreach (var (wall, shown, truth) in frames.Where(f => f.Wall > 1000))
        {
            // Exactly a value osu! had in the last few ms (its next frame writes it, the next poll reads it).
            bool exact = Enumerable.Range(0, 9).Any(k => Math.Abs(shown - Math.Floor(editor.SongAt(wall - k * 0.5))) < 0.001);
            Assert.True(exact, $"at {wall:F1} ms showed {shown}, osu! at {truth}");
        }
    }

    [Theory]
    [InlineData(1.0, 0.5)]
    [InlineData(0.25, 1.0)]
    public void Speed_change_is_picked_up(double from, double to)
    {
        var editor = new Editor(10_000, from).Then(3000, rate: to);
        var frames = Run(new DirectClock(), editor, 6000);
        Assert.All(frames.Where(f => f.Wall > 3600), f => Assert.InRange(f.Shown - f.Truth, -2, 2));
    }

    [Fact]
    public void Offset_shifts_by_real_time()
    {
        var clock = new DirectClock { OffsetMs = 8 };
        var frames = Run(clock, new Editor(10_000, 0.5), 5000);
        double mean = frames.Where(f => f.Wall > 1000).Average(f => f.Shown - f.Truth);
        Assert.InRange(mean, 4 - 1, 4 + 1);
    }
}
