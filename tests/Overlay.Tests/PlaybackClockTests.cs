using System.Globalization;
using Overlay.Core.Clock;
using Overlay.Sources.Mock;
using Xunit.Abstractions;
using static Overlay.Sources.Mock.EditorSimulation;

namespace Overlay.Tests;

public class PlaybackClockTests(ITestOutputHelper output)
{
    private const double FrameMs = 1000.0 / 144;

    private readonly record struct Frame(double Wall, double Shown, double Truth)
    {
        public double Error => Shown - Truth;
    }

    /// <summary>Renders frames at 144 Hz, delivering each sample when its receive time has passed.</summary>
    private static List<Frame> Run(EditorSimulation editor, SimulatedTosu tosu, PlaybackClock clock, double durationMs, int seed = 1)
    {
        var samples = tosu.Generate(editor, 0, durationMs, seed);
        var frames = new List<Frame>();
        int next = 0;
        for (double f = 0; f < durationMs; f += FrameMs)
        {
            while (next < samples.Count && samples[next].WallMs <= f) clock.AddSample(samples[next++]);
            if (clock.HasTime) frames.Add(new Frame(f, clock.GetTime(f), editor.SongAt(f)));
        }
        return frames;
    }

    /// <summary>
    /// A clock with the latency offset a user would arrive at: calibrated once during steady play at 100 %
    /// (here by measuring the mean error), then used unchanged for every speed and scenario.
    /// </summary>
    private static PlaybackClock CalibratedClock(SimulatedTosu tosu)
    {
        var editor = new EditorSimulation(10_000, 1.0, false, Array.Empty<Event>());
        var frames = Run(editor, tosu, new PlaybackClock(), 30_000, seed: 99);
        double meanError = frames.Where(f => f.Wall > 10_000).Average(f => f.Error);
        return new PlaybackClock { OffsetMs = -meanError };
    }

    private (double rms, double max) Stats(IEnumerable<Frame> frames, string label)
    {
        var e = frames.Select(f => f.Error).ToList();
        double rms = Math.Sqrt(e.Average(x => x * x)), max = e.Max(Math.Abs);
        output.WriteLine($"{label}: rms {rms:F2} ms, max {max:F2} ms over {e.Count} frames");
        return (rms, max);
    }

    [Theory]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(0.75)]
    [InlineData(1.0)]
    public void Steady_play_error_below_2ms_and_motion_monotonic(double rate)
    {
        var editor = new EditorSimulation(10_000, rate, startPaused: false, Array.Empty<Event>());
        var tosu = new SimulatedTosu();
        var clock = CalibratedClock(tosu);
        var frames = Run(editor, tosu, clock, 40_000);

        var (_, max) = Stats(frames.Where(f => f.Wall > 5_000), $"rate {rate}");
        Assert.True(max < 2, $"steady-state error {max:F2} ms");
        Assert.Equal(0, clock.SeekCount);
        Assert.Equal(rate, clock.Debug.Rate);

        // Frame-to-frame motion: always forward, and never more than 10% off the playback speed.
        foreach (var (a, b) in frames.Zip(frames.Skip(1)).Where(p => p.First.Wall > 1_000))
        {
            double speed = (b.Shown - a.Shown) / (b.Wall - a.Wall);
            Assert.InRange(speed, rate * 0.9, rate * 1.1);
        }
    }

    [Theory]
    [InlineData(0.25)]
    [InlineData(1.0)]
    public void Seek_while_playing_snaps_on_first_frame_then_settles(double rate)
    {
        var editor = new EditorSimulation(10_000, rate, false, new Event[] { new Seek(15_000, 60_000) });
        var tosu = new SimulatedTosu();
        var clock = CalibratedClock(tosu);
        var frames = Run(editor, tosu, clock, 30_000);

        var firstAfter = frames.First(f => f.Wall >= clock.LastSeekWallMs);
        double bound = rate * tosu.MaxDelayMs + 2; // one raw sample's worth of delay
        output.WriteLine($"first frame after seek: error {firstAfter.Error:F2} ms (bound {bound:F1})");
        Assert.Equal(1, clock.SeekCount);
        Assert.InRange(firstAfter.Error, -bound, bound);
        Assert.True(Stats(frames.Where(f => f.Wall > 21_000), "after seek").max < 2);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)] // source without a paused flag: pause is detected from repeated samples
    public void Pause_and_seek_while_paused_show_exact_position(bool reportPaused)
    {
        var editor = new EditorSimulation(10_000, 1.0, false, new Event[]
        {
            new Pause(5_000), new Seek(7_000, 30_000), new Seek(8_000, 29_917), new Play(10_000),
        });
        var tosu = new SimulatedTosu { ReportPaused = reportPaused };
        var clock = CalibratedClock(tosu);
        var frames = Run(editor, tosu, clock, 20_000);

        // Once the source has had time to report it (worst case two polls plus delivery), the paused position is exact.
        double settle = 2 * tosu.PollMs + tosu.MaxDelayMs + 5;
        foreach (var at in new[] { 5_000.0, 7_000, 8_000 })
        {
            var held = frames.Where(f => f.Wall > at + settle && f.Wall < at + 900).ToList();
            Assert.NotEmpty(held);
            Assert.All(held, f => Assert.InRange(f.Error, -0.5, 0.5));
        }
        Assert.Equal(2, clock.SeekCount);

        // Resume: playback starts from the held position (so briefly behind by up to a sample interval plus delay,
        // never ahead beyond jitter), catches up within a second, then is precise.
        var resumed = frames.Where(f => f.Wall > 10_000 + settle && f.Wall < 10_500).ToList();
        Assert.All(resumed, f => Assert.InRange(f.Error, -2 * tosu.PollMs - tosu.MaxDelayMs, 60));
        Assert.All(frames.Where(f => f.Wall > 11_000 && f.Wall < 12_000), f => Assert.InRange(f.Error, -60, 60));
        Assert.True(Stats(frames.Where(f => f.Wall > 15_000), "after resume").max < 2);
    }

    [Theory]
    [InlineData(1.0, 0.5)]
    [InlineData(0.5, 0.25)]
    [InlineData(0.25, 1.0)]
    public void Speed_change_while_playing_is_picked_up(double from, double to)
    {
        var editor = new EditorSimulation(10_000, from, false, new Event[] { new SetRate(10_000, to) });
        var tosu = new SimulatedTosu();
        var clock = CalibratedClock(tosu);
        var frames = Run(editor, tosu, clock, 25_000);

        Assert.Equal(to, clock.Debug.Rate);
        var (_, maxEarly) = Stats(frames.Where(f => f.Wall > 11_000 && f.Wall < 15_000), $"{from}->{to}, 1-5 s after");
        Assert.True(maxEarly < 25, $"error soon after speed change {maxEarly:F1} ms");
        Assert.True(Stats(frames.Where(f => f.Wall > 17_000), $"{from}->{to}, settled").max < 2);
    }

    [Fact]
    public void Dropped_and_jittery_samples_cause_no_false_seeks()
    {
        var editor = new EditorSimulation(10_000, 1.0, false, Array.Empty<Event>());
        var tosu = new SimulatedTosu { DropProbability = 0.2, PollJitterMs = 10, SpikeProbability = 0.1, SpikeMs = 40 };
        var clock = CalibratedClock(tosu);
        var frames = Run(editor, tosu, clock, 40_000, seed: 7);

        Assert.Equal(0, clock.SeekCount);
        Assert.True(Stats(frames.Where(f => f.Wall > 8_000), "20% dropped, heavy jitter").max < 3);
    }

    /// <summary>
    /// Scrolling the timeline while paused, as tosu reports it: the sample after each seek says "not paused" (seen in
    /// every scroll in the M0 recording), the next ones "paused". Forward and backward scrolls must both snap to the
    /// new position and stay there, without drifting ahead as if playback had started.
    /// </summary>
    [Fact]
    public void Scrolling_while_paused_snaps_both_ways_without_drift()
    {
        var clock = new PlaybackClock();
        double wall = 0;
        var shown = new List<(double Wall, double Shown, double Expected)>();
        void Sample(double song, bool paused)
        {
            clock.AddSample(new TimeSample(song, wall, paused));
            // Frames until the next sample, 100 ms later.
            for (double f = wall; f < wall + 100; f += FrameMs) shown.Add((f, clock.GetTime(f), song));
            wall += 100;
        }

        for (int i = 0; i < 5; i++) Sample(10_000, paused: true);
        foreach (double target in new[] { 10_083, 10_167, 10_250, 10_167, 10_083, 10_250, 10_333 })
        {
            Sample(target, paused: false); // the jump sample, flagged as unpaused
            Sample(target, paused: true);
            Sample(target, paused: true);
        }

        foreach (var (f, s, expected) in shown) Assert.True(Math.Abs(s - expected) < 0.001, $"at {f} ms showed {s}, expected {expected}");
    }

    /// <summary>
    /// Scrolling forward repeatedly while paused, as recorded from tosu: steps of one snap (83 ms) every sample, so it
    /// advances at a believable 0.83x; the moving samples say "not paused", the standstill between bursts "paused".
    /// It must snap to each position and never be mistaken for playback.
    /// </summary>
    [Fact]
    public void Repeated_forward_scrolling_is_not_mistaken_for_playback()
    {
        var clock = new PlaybackClock();
        double wall = 0, song = 8_000;
        var shown = new List<(double Wall, double Shown, double Expected)>();
        void Sample(bool paused)
        {
            clock.AddSample(new TimeSample(song, wall, paused));
            for (double f = wall; f < wall + 100; f += FrameMs) shown.Add((f, clock.GetTime(f), song));
            wall += 100;
        }

        for (int i = 0; i < 5; i++) Sample(paused: true);
        for (int burst = 0; burst < 8; burst++)
        {
            for (int step = 0; step < 3; step++) { song += 83; Sample(paused: false); }
            Sample(paused: true); // the standstill between scroll bursts
        }

        foreach (var (f, s, expected) in shown) Assert.True(Math.Abs(s - expected) < 0.001, $"at {f} ms showed {s}, expected {expected}");
    }

    [Fact]
    public void Playback_starts_smoothly_from_the_held_position_then_tracks()
    {
        var clock = new PlaybackClock();
        var frames = new List<(double Wall, double Shown)>();
        int next = 0;
        // Paused at 10 000 ms; play pressed at wall 495, after which the song advances at 1x.
        var samples = Enumerable.Range(0, 40).Select(i => i < 5
            ? new TimeSample(10_000, i * 100, true)
            : new TimeSample(10_000 + (i * 100 - 495), i * 100, false)).ToList();
        for (double f = 0; f < 3900; f += FrameMs)
        {
            while (next < samples.Count && samples[next].WallMs <= f) clock.AddSample(samples[next++]);
            frames.Add((f, clock.GetTime(f)));
        }

        // Never backwards. Without the play key, playback is only recognised once the source hasn't said "paused" for a
        // while (last said at wall 400, so from 850): until then the display steps with the samples like a seek, and
        // from then on it moves smoothly, at most double speed while catching up.
        foreach (var (a, b) in frames.Zip(frames.Skip(1)))
        {
            double speed = (b.Shown - a.Shown) / (b.Wall - a.Wall);
            Assert.True(speed >= 0, $"moved backwards at {b.Wall} ms");
            if (a.Wall > 1000) Assert.InRange(speed, 0, 2.0 + 1e-6);
        }
        // Caught up within half a second of playback being confirmed, then tracks the song.
        foreach (var (f, shown) in frames.Where(x => x.Wall > 1200))
            Assert.InRange(shown, 10_000 + (f - 495) - 5, 10_000 + (f - 495) + 5);
        Assert.Equal(1.0, clock.Debug.Rate);
        Assert.Equal(0, clock.SeekCount);
    }

    /// <summary>Feeds samples and the play/pause key at their wall times, recording what's shown every frame.</summary>
    private static List<(double Wall, double Shown)> RunWithKeys(PlaybackClock clock, IList<TimeSample> samples, double[] keyPresses, double untilMs)
    {
        var frames = new List<(double Wall, double Shown)>();
        int next = 0, nextKey = 0;
        for (double f = 0; f < untilMs; f += FrameMs)
        {
            while (nextKey < keyPresses.Length && keyPresses[nextKey] <= f) clock.PlayPauseKeyPressed(keyPresses[nextKey++]);
            while (next < samples.Count && samples[next].WallMs <= f) clock.AddSample(samples[next++]);
            frames.Add((f, clock.GetTime(f)));
        }
        return frames;
    }

    [Fact]
    public void Play_key_starts_motion_at_once_and_samples_take_over_smoothly()
    {
        // Paused at 10 000. Space at wall 1000; osu! starts playing then, and tosu shows it from its read at 1060
        // (delivered at 1080), so the samples at 1000 and earlier still show the paused position.
        var samples = new List<TimeSample>();
        for (double w = 0; w <= 1000; w += 100) samples.Add(new TimeSample(10_000, w + 20, w < 1000 ? true : false));
        for (double w = 1060; w < 4000; w += 100) samples.Add(new TimeSample(10_000 + (w - 1000), w + 20, false));
        var frames = RunWithKeys(new PlaybackClock { OffsetMs = 20 }, samples, new[] { 1000.0 }, 4000);

        // Moving from the very first frame after the key, with no jump at any point.
        var afterKey = frames.Where(f => f.Wall > 1000).ToList();
        Assert.True(afterKey[1].Shown > 10_000, "should move right after the key press");
        var withData = frames.Where(f => f.Wall > 100).ToList(); // before the first sample there is nothing to show
        foreach (var (a, b) in withData.Zip(withData.Skip(1)))
            Assert.InRange((b.Shown - a.Shown) / (b.Wall - a.Wall), 0, 2.0 + 1e-6);
        // And in step with the song (truth: 10 000 + (t - 1000)).
        Assert.All(frames.Where(f => f.Wall > 1000 && f.Wall < 4000), f => Assert.InRange(f.Shown - (10_000 + f.Wall - 1000), -25, 25));
        Assert.All(frames.Where(f => f.Wall > 2000 && f.Wall < 4000), f => Assert.InRange(f.Shown - (10_000 + f.Wall - 1000), -3, 3));
    }

    [Fact]
    public void Pause_key_stops_at_once_and_the_paused_sample_sets_the_exact_position()
    {
        // Playing at 1x from 10 000 (wall 0). Space at wall 3000; tosu still reports motion until its read at 3060.
        var samples = new List<TimeSample>();
        for (double w = 0; w < 3060; w += 100) samples.Add(new TimeSample(10_000 + w, w + 20, false));
        for (double w = 3060; w < 4500; w += 100) samples.Add(new TimeSample(13_000, w + 20, true));
        var frames = RunWithKeys(new PlaybackClock { OffsetMs = 20 }, samples, new[] { 3000.0 }, 4500);

        var stopped = frames.Where(f => f.Wall >= 3000 && f.Wall < 3080).Select(f => f.Shown).Distinct().ToList();
        Assert.Single(stopped); // holds still from the key press, ignoring the late moving sample
        Assert.InRange(stopped[0], 13_000 - 15, 13_000 + 15);
        Assert.All(frames.Where(f => f.Wall > 3100), f => Assert.Equal(13_000, f.Shown, 3)); // then exact
    }

    [Fact]
    public void Play_key_that_did_not_start_playback_is_corrected_after_the_grace_period()
    {
        // Space typed somewhere osu! didn't treat as play: the time never moves.
        var samples = Enumerable.Range(0, 30).Select(i => new TimeSample(10_000, i * 100 + 20, true)).ToList();
        var frames = RunWithKeys(new PlaybackClock(), samples, new[] { 1000.0 }, 3000);
        Assert.All(frames.Where(f => f.Wall > 1500), f => Assert.Equal(10_000, f.Shown, 3));
    }

    [Fact]
    public void Holds_position_when_the_source_goes_quiet()
    {
        var clock = new PlaybackClock();
        for (int i = 0; i < 20; i++) clock.AddSample(new TimeSample(1000 + i * 100, i * 100, false));
        double atStale = clock.GetTime(1900 + 1000);
        Assert.Equal(atStale, clock.GetTime(10_000), 6);
    }

    /// <summary>
    /// Replays the real tosu recording from M0 at 144 Hz. osu!'s audio clock runs at exactly the editor speed
    /// (fitted slopes 0.75007, 0.49997, 0.25001), so the true position is a straight line with that slope.
    /// Its constant offset is what the user's latency calibration absorbs, so this measures how much the clock's
    /// error wanders around its mean: that is what would show as objects drifting against the playhead.
    /// </summary>
    [Theory]
    [InlineData(48_900, 60_200, 1.0)]
    [InlineData(78_000, 95_400, 0.75)]
    [InlineData(101_200, 112_500, 0.5)]
    [InlineData(120_700, 128_300, 0.25)]
    public void Real_tosu_recording_tracks_offline_fit(double fromMs, double toMs, double rate)
    {
        var inv = CultureInfo.InvariantCulture;
        // The recording is personal data and isn't in the repository; record your own with spikes/m0/TosuProbe.
        string recording = Path.Combine(AppContext.BaseDirectory, "TestData", "tosu-run1.csv");
        if (!File.Exists(recording))
        {
            output.WriteLine("Skipped: TestData/tosu-run1.csv (a tosu recording) is not present.");
            return;
        }
        var samples = File.ReadLines(recording).Skip(1)
            .Select(l => l.Split(','))
            .Where(p => p[1] == "v2" && p[2] == "1")
            .Select(p => new TimeSample(double.Parse(p[3], inv), double.Parse(p[0], inv), p[4] == "1"))
            .ToList();

        var stretch = samples.Where(s => s.WallMs >= fromMs && s.WallMs <= toMs).ToList();
        double mx = stretch.Average(s => s.WallMs), my = stretch.Average(s => s.SongTimeMs);
        double fitted = stretch.Sum(s => (s.WallMs - mx) * (s.SongTimeMs - my)) / stretch.Sum(s => (s.WallMs - mx) * (s.WallMs - mx));
        Assert.InRange(fitted, rate - 0.01, rate + 0.01);
        double slope = rate;

        var clock = new PlaybackClock();
        var frames = new List<Frame>();
        int next = 0;
        for (double f = fromMs - 30_000; f < toMs; f += FrameMs)
        {
            while (next < samples.Count && samples[next].WallMs <= f) clock.AddSample(samples[next++]);
            if (!clock.HasTime) continue;
            double shown = clock.GetTime(f); // every frame, as the renderer does
            if (f > fromMs + 5_000) frames.Add(new Frame(f, shown, my + slope * (f - mx)));
        }
        double mean = frames.Average(f => f.Error);
        double std = Math.Sqrt(frames.Average(f => (f.Error - mean) * (f.Error - mean)));
        double wander = frames.Max(f => Math.Abs(f.Error - mean));
        output.WriteLine($"real data at {rate}: constant part {mean:F2} ms, std {std:F2} ms, max wander {wander:F2} ms");
        Assert.Equal(rate, clock.Debug.Rate);
        Assert.True(std < 2, $"std {std:F2} ms");
    }
}
