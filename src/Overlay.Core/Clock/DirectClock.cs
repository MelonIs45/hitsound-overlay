namespace Overlay.Core.Clock;

public sealed class DirectClockOptions
{
    /// <summary>Editor speeds; the fitted speed always snaps to the nearest (the editor has no others).</summary>
    public double[] KnownRates { get; set; } = { 0.25, 0.5, 0.75, 1.0 };
    /// <summary>How much recent history (wall time) the speed and phase fit use while playing.</summary>
    public double WindowMs { get; set; } = 500;
    /// <summary>A sample this far from the fitted line (song ms, plus two update gaps at the playback speed) is a seek.</summary>
    public double SeekToleranceMs { get; set; } = 8;
    /// <summary>
    /// With no change for <c>max(MinPauseMs, PauseGapFactor × the usual gap between updates)</c>, playback stopped.
    /// osu! updates the time every frame (every ~2 ms measured in the editor), so a stop shows quickly.
    /// </summary>
    public double MinPauseMs { get; set; } = 25;
    public double PauseGapFactor { get; set; } = 4;
    /// <summary>
    /// Between updates the line is followed for at most <c>max(MinExtrapolateMs, ExtrapolateGaps × usual gap)</c> past
    /// the last change, so a pause overshoots by no more than that before it's noticed.
    /// </summary>
    public double MinExtrapolateMs { get; set; } = 3;
    public double ExtrapolateGaps { get; set; } = 2;
    /// <summary>
    /// Changes needed in a row, each a small step forward at most <see cref="QuickChangeGapMs"/> apart, before a move from
    /// a standstill counts as playback.
    /// </summary>
    public int ChangesToStart { get; set; } = 3;
    public double QuickChangeGapMs { get; set; } = 12;
    /// <summary>
    /// Slower updates (osu! at a low frame rate) count as playback once they've run this long at an editor speed
    /// within this tolerance. Scrolling rarely matches a speed that exactly.
    /// </summary>
    public double SlowStartSpanMs { get; set; } = 100;
    public double SlowStartSpeedTolerance { get; set; } = 0.04;
    /// <summary>The largest step one update can make during playback (one slow osu! frame at full speed).</summary>
    public double MaxPlaybackStepMs { get; set; } = 20;
}

/// <summary>
/// Clock for a fast, fresh time source (osu!'s memory, read every millisecond; the value changes every osu! frame).
/// </summary>
/// <remarks>
/// Paused (no change for a moment) or after a seek, it shows the source's value exactly. Playing, it follows a line
/// fitted over the last half second, with the speed snapped to an editor speed and the phase taken from the
/// least-delayed readings, so motion is smooth between osu!'s updates. It never runs more than a couple of update gaps
/// past the latest value, so a pause shows within a few ms. Unlike <see cref="PlaybackClock"/> it needs no hints from
/// the keyboard or mouse: every change is visible within a frame or two.
/// </remarks>
public sealed class DirectClock : IEditorClock
{
    private readonly DirectClockOptions options;
    private readonly object gate = new();
    private readonly List<TimeSample> fit = new();
    private readonly List<TimeSample> starting = new();
    private readonly List<double> residuals = new();

    private bool hasSample;
    private double lastSong, lastWall;
    private bool playing;
    private double rate = 1, w0, phase;
    private double gapMs = 4;
    private double lastShown = double.NegativeInfinity;

    public DirectClock(DirectClockOptions? options = null) => this.options = options ?? new DirectClockOptions();

    public double OffsetMs { get; set; }
    public int SeekCount { get; private set; }
    public Action<string>? Trace { get; set; }
    public bool HasTime { get { lock (gate) return hasSample; } }

    private double lastSeekWall = double.NaN;

    public ClockDebugInfo Debug
    {
        get { lock (gate) return new ClockDebugInfo(lastSong, lastWall, playing ? rate : 0, !playing, lastSeekWall, fit.Count); }
    }

    public void AddSample(TimeSample s)
    {
        lock (gate)
        {
            if (!hasSample)
            {
                hasSample = true;
                lastSong = s.SongTimeMs;
                lastWall = s.WallMs;
                return;
            }
            if (s.WallMs < lastWall || s.SongTimeMs == lastSong) return; // out of order, or no change
            double delta = s.SongTimeMs - lastSong;
            double dt = s.WallMs - lastWall;
            lastSong = s.SongTimeMs;
            lastWall = s.WallMs;

            if (playing)
            {
                double predicted = phase + rate * (s.WallMs - w0);
                double tolerance = options.SeekToleranceMs + 2 * rate * gapMs;
                if (delta < 0 || Math.Abs(s.SongTimeMs - predicted) > tolerance)
                {
                    // A seek (playing on from elsewhere, or a scroll): show it exactly, and restart from here.
                    Trace?.Invoke($"seek  wall {s.WallMs:F1} song {s.SongTimeMs} (predicted {predicted:F1})");
                    CountSeek(s.WallMs);
                    Stop();
                    starting.Add(s);
                    return;
                }
                gapMs += 0.1 * (Math.Min(dt, 50) - gapMs);
                fit.Add(s);
                while (fit.Count > 2 && s.WallMs - fit[0].WallMs > options.WindowMs) fit.RemoveAt(0);
                Refit();
                return;
            }

            // From a standstill: playback starting, or a seek (scrolling while paused). Shown exactly until a few
            // small steps forward in a row show playback.
            if (delta <= 0 || delta > options.MaxPlaybackStepMs || dt > 100)
            {
                if (Math.Abs(delta) > options.MaxPlaybackStepMs) CountSeek(s.WallMs);
                starting.Clear();
            }
            starting.Add(s);
            if (starting.Count > 16) starting.RemoveAt(0);
            if (!LooksLikePlayback(out var first, out double speed)) return;

            playing = true;
            Trace?.Invoke($"play  wall {first.WallMs:F1} song {first.SongTimeMs} (~{speed:0.00}x)");
            fit.Clear();
            fit.AddRange(starting.SkipWhile(p => p.WallMs < first.WallMs));
            starting.Clear();
            gapMs = Math.Clamp((s.WallMs - first.WallMs) / (fit.Count - 1), 1, 20);
            w0 = first.WallMs;
            rate = Nearest(speed);
            lastShown = double.NegativeInfinity;
            Refit();
        }
    }

    /// <summary>
    /// Whether the recent small forward steps from a standstill are playback rather than scrolling. Playback changes the
    /// value every osu! frame (a few ms apart); wheel steps come tens of ms apart. So: a few changes in quick succession
    /// at a playback speed; or, for an osu! running at a low frame rate, a longer run at exactly an editor speed.
    /// </summary>
    private bool LooksLikePlayback(out TimeSample first, out double speed)
    {
        first = default;
        speed = 0;
        int n = starting.Count;
        if (n < options.ChangesToStart) return false;

        bool quick = true;
        for (int i = n - options.ChangesToStart + 1; i < n; i++)
            if (starting[i].WallMs - starting[i - 1].WallMs > options.QuickChangeGapMs) quick = false;
        if (quick)
        {
            first = starting[n - options.ChangesToStart];
            speed = (starting[^1].SongTimeMs - first.SongTimeMs) / Math.Max(1, starting[^1].WallMs - first.WallMs);
            if (speed > 0.1 && speed < 1.6) return true;
        }

        first = starting[0];
        double span = starting[^1].WallMs - first.WallMs;
        if (n < 4 || span < options.SlowStartSpanMs) return false;
        speed = (starting[^1].SongTimeMs - first.SongTimeMs) / span;
        return Math.Abs(speed - Nearest(speed)) <= options.SlowStartSpeedTolerance;
    }

    private void CountSeek(double wallMs)
    {
        SeekCount++;
        lastSeekWall = wallMs;
    }

    private void Stop()
    {
        playing = false;
        fit.Clear();
        starting.Clear();
    }

    /// <summary>Speed (snapped) and phase from the fit window.</summary>
    private void Refit()
    {
        int n = fit.Count;
        double span = fit[^1].WallMs - fit[0].WallMs;
        if (n >= 3 && span >= 20)
        {
            double mx = 0, my = 0;
            foreach (var p in fit) { mx += p.WallMs - w0; my += p.SongTimeMs; }
            mx /= n; my /= n;
            double sxy = 0, sxx = 0;
            foreach (var p in fit)
            {
                double dx = p.WallMs - w0 - mx;
                sxy += dx * (p.SongTimeMs - my);
                sxx += dx * dx;
            }
            if (sxx > 0) rate = Nearest(sxy / sxx);
        }

        // Readings are only ever late (osu! wrote the value at its frame, we read it up to a poll interval later), and the
        // value is whole ms: the line runs along the top of the readings, so average the least-delayed quarter.
        residuals.Clear();
        foreach (var p in fit) residuals.Add(p.SongTimeMs - rate * (p.WallMs - w0));
        residuals.Sort();
        int from = Math.Min(n - 1, (int)(n * 0.75));
        double sum = 0;
        for (int i = from; i < n; i++) sum += residuals[i];
        phase = sum / (n - from);
    }

    private double Nearest(double speed)
    {
        double best = options.KnownRates[0];
        foreach (var r in options.KnownRates)
            if (Math.Abs(r - speed) < Math.Abs(best - speed)) best = r;
        return best;
    }

    public double GetTime(double wallMs)
    {
        lock (gate)
        {
            if (!hasSample) return 0;
            if (playing)
            {
                double quiet = wallMs - lastWall;
                if (quiet > Math.Max(options.MinPauseMs, options.PauseGapFactor * gapMs))
                {
                    Trace?.Invoke($"pause wall {wallMs:F1} song {lastSong} (quiet {quiet:F1} ms)");
                    Stop();
                }
            }
            if (!playing) return lastSong;

            double shown = phase + rate * (wallMs + OffsetMs - w0);
            // Never run far past the latest value: if osu! stopped, this is as far as the display gets.
            double reach = Math.Min(Math.Max(0, wallMs - lastWall), Math.Max(options.MinExtrapolateMs, options.ExtrapolateGaps * gapMs));
            shown = Math.Min(shown, lastSong + 1 + rate * (reach + OffsetMs));
            // Refinements never move the display backwards while playing.
            shown = Math.Max(shown, lastShown);
            lastShown = shown;
            return shown;
        }
    }

    public void PlayPauseKeyPressed(double wallMs) { }
    public void ScrollWheelMoved(double wallMs) { }
}
