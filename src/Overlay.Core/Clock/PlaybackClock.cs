namespace Overlay.Core.Clock;

public sealed class PlaybackClockOptions
{
    /// <summary>Editor speeds the rate estimate snaps to.</summary>
    public double[] KnownRates { get; set; } = { 0.25, 0.5, 0.75, 1.0 };
    /// <summary>A fitted rate within this distance of a known rate snaps to it.</summary>
    public double SnapTolerance { get; set; } = 0.06;
    /// <summary>A sample further than SeekBaseMs + SeekPerRateMs * rate from the prediction is treated as a seek.</summary>
    public double SeekBaseMs { get; set; } = 40;
    public double SeekPerRateMs { get; set; } = 40;
    /// <summary>How much sample history (wall time) the rate and phase fit use.</summary>
    public double WindowMs { get; set; } = 16000;
    /// <summary>
    /// The phase is the mean of the residuals between these quantiles (0 = most delayed, 1 = least delayed).
    /// Tuned on simulated and recorded tosu data: the least-delayed 15% over a 16 s window is steadiest through
    /// delay bursts (max error 1.7 ms simulated, 1.0 ms wander on the M0 recording, at 100 %).
    /// </summary>
    public double PhaseBandLow { get; set; } = 0.85;
    public double PhaseBandHigh { get; set; } = 1.0;
    /// <summary>Time constant for easing the displayed position onto a refined estimate.</summary>
    public double SmoothingTauMs { get; set; } = 250;
    /// <summary>Easing never changes the displayed speed by more than this fraction, so motion stays monotonic.</summary>
    public double MaxSlewFraction { get; set; } = 0.05;
    /// <summary>
    /// When playback starts, the display starts moving from where it was held and catches up with the song this fast:
    /// time constant, and how much faster than the playback speed it may move (1 = up to double speed).
    /// </summary>
    public double CatchUpTauMs { get; set; } = 100;
    public double CatchUpSlewFraction { get; set; } = 1.0;
    /// <summary>With no samples for this long, stop extrapolating and hold the last position.</summary>
    public double StaleAfterMs { get; set; } = 1000;
}

/// <summary>
/// Turns discrete song-time samples into a smooth, per-frame song position.
/// Feed samples with <see cref="AddSample"/> (any thread) and call <see cref="GetTime"/> once per rendered frame.
/// </summary>
/// <remarks>
/// While playing, the song position is modelled as <c>phase + rate * (wall - segmentStart)</c>. The rate is fitted over
/// the current segment and snapped to a known editor speed; the phase comes from the least-delayed samples at that rate
/// (see <see cref="PlaybackClockOptions.PhaseBandLow"/>), so delivery jitter and delay bursts don't move it. Refinements
/// are eased in without ever reversing motion. A sample far from the prediction starts a new segment (a seek).
/// When paused (reported by the source, or two identical samples in a row), the clock shows the last sample exactly.
/// </remarks>
public sealed class PlaybackClock
{
    private readonly PlaybackClockOptions options;
    private readonly object gate = new();
    private readonly List<TimeSample> segment = new();

    private bool hasSample;
    private TimeSample last;
    private bool frozen = true;
    private double frozenSong;

    private double rate = 1;
    private double segmentStartWall;
    private double targetPhase;   // song time at segmentStartWall, best estimate
    private double displayPhase;  // what is shown; eased toward targetPhase
    private double lastFrameWall = double.NaN;

    public PlaybackClock(PlaybackClockOptions? options = null) => this.options = options ?? new PlaybackClockOptions();

    /// <summary>
    /// Latency compensation in ms of wall time (signed). The prediction is evaluated this far ahead, so it scales with the
    /// playback speed, which is how source and display latency behave. Has no effect while paused.
    /// </summary>
    public double OffsetMs { get; set; }

    public bool HasTime { get { lock (gate) return hasSample; } }

    /// <summary>Number of seeks detected so far (including seeks while paused).</summary>
    public int SeekCount { get; private set; }

    public ClockDebugInfo Debug
    {
        get
        {
            lock (gate)
                return new ClockDebugInfo(last.SongTimeMs, last.WallMs, frozen ? 0 : rate, frozen, LastSeekWallMs, segment.Count);
        }
    }

    public double LastSeekWallMs { get; private set; } = double.NaN;

    public void AddSample(TimeSample s)
    {
        lock (gate)
        {
            if (hasSample && s.WallMs < last.WallMs) return; // out of order
            bool stalled = hasSample && s.SongTimeMs == last.SongTimeMs;
            bool first = !hasSample;
            double sinceLastSample = first ? 0 : s.WallMs - last.WallMs;
            double? resumeFrom = null; // set when this sample confirms that playback started
            hasSample = true;
            last = s;

            if (s.Paused == true || stalled)
            {
                // While paused every position change is shown exactly. Count real seeks for the debug readout,
                // but not the few-ms nudges osu! makes when pausing.
                if (frozen && !first && Math.Abs(s.SongTimeMs - frozenSong) > options.SeekBaseMs)
                {
                    SeekCount++;
                    LastSeekWallMs = s.WallMs;
                }
                frozen = true;
                frozenSong = s.SongTimeMs;
                segment.Clear();
                pendingResume = null; // a forward move followed by a standstill was a seek
                return;
            }

            if (frozen && !first && s.SongTimeMs < frozenSong)
            {
                // Playback never moves backwards, so this is a seek (or osu!'s small nudge on pause) while still paused.
                if (frozenSong - s.SongTimeMs > options.SeekBaseMs) { SeekCount++; LastSeekWallMs = s.WallMs; }
                frozenSong = s.SongTimeMs;
                pendingResume = null;
                return;
            }

            if (frozen || first)
            {
                // Moved forward from a standstill: either playback started, or a seek made while paused (tosu reports
                // the sample after a seek as unpaused, so the flag can't tell them apart). Show the new position exactly
                // and hold it; playback resumes only once the next sample has advanced at a playback speed, starting
                // from this sample so no time is lost. Otherwise a forward seek would drift ahead and snap back.
                if (pendingResume is { } start && IsPlaybackSpeed(start, s))
                {
                    resumeFrom = frozenSong;
                    frozen = false;
                    pendingResume = null;
                    StartSegment(start);
                    // Fall through: s is the new segment's second sample.
                }
                else
                {
                    // A jump further than playback could have moved since the last sample is certainly a seek.
                    if (!first && s.SongTimeMs - frozenSong > SeekThreshold + rate * sinceLastSample)
                    {
                        SeekCount++;
                        LastSeekWallMs = s.WallMs;
                    }
                    frozen = true;
                    frozenSong = s.SongTimeMs;
                    pendingResume = s;
                    return;
                }
            }

            double predicted = targetPhase + rate * (s.WallMs - segmentStartWall);
            double impliedSpeed = (s.SongTimeMs - segment[^1].SongTimeMs) / Math.Max(1, s.WallMs - segment[^1].WallMs);
            double error = s.SongTimeMs - predicted;
            if (error < -SeekThreshold && impliedSpeed >= 0 && impliedSpeed < 0.5 * rate)
            {
                // Fell well behind while barely moving: playback stopped between samples (a pause the source
                // didn't flag). Hold here; if it was a slow-down instead, the next sample restarts playback.
                frozen = true;
                frozenSong = s.SongTimeMs;
                segment.Clear();
                return;
            }

            bool plausibleNewSpeed = segment.Count == 1 && impliedSpeed > 0.1 && impliedSpeed < 1.6;
            if (Math.Abs(error) > SeekThreshold && !plausibleNewSpeed)
            {
                SeekCount++;
                LastSeekWallMs = s.WallMs;
                StartSegment(s);
                return;
            }

            segment.Add(s);
            while (segment.Count > 2 && s.WallMs - segment[0].WallMs > options.WindowMs) segment.RemoveAt(0);
            Refit();

            if (resumeFrom is { } held)
            {
                // Playback just started: rather than jumping ahead to where the song already is, start moving from
                // the held position and catch up quickly (GetTime eases displayPhase towards targetPhase).
                if (!double.IsNaN(lastFrameWall))
                {
                    displayPhase = Math.Min(targetPhase, held - rate * (lastFrameWall + OffsetMs - segmentStartWall));
                    catchingUp = displayPhase < targetPhase;
                }
            }
        }
    }

    /// <summary>The display is catching up after playback started (eases faster than usual).</summary>
    private bool catchingUp;

    private double SeekThreshold => options.SeekBaseMs + options.SeekPerRateMs * rate;

    /// <summary>A forward move from a standstill, waiting for the next sample to show whether playback started.</summary>
    private TimeSample? pendingResume;

    /// <summary>Whether the song advanced between two samples at a rate the editor can play at.</summary>
    private static bool IsPlaybackSpeed(TimeSample a, TimeSample b)
    {
        double speed = (b.SongTimeMs - a.SongTimeMs) / Math.Max(1, b.WallMs - a.WallMs);
        return speed > 0.1 && speed < 1.6;
    }

    private void StartSegment(TimeSample s)
    {
        segment.Clear();
        segment.Add(s);
        segmentStartWall = s.WallMs;
        targetPhase = displayPhase = s.SongTimeMs;
        catchingUp = false; // a new segment (e.g. a seek) is shown exactly
    }

    private void Refit()
    {
        int n = segment.Count;
        double w0 = segmentStartWall;

        // Least-squares slope over the segment.
        double mx = 0, my = 0;
        foreach (var p in segment) { mx += p.WallMs - w0; my += p.SongTimeMs; }
        mx /= n; my /= n;
        double sxy = 0, sxx = 0;
        foreach (var p in segment)
        {
            double dx = p.WallMs - w0 - mx;
            sxy += dx * (p.SongTimeMs - my);
            sxx += dx * dx;
        }
        if (sxx <= 0) return;
        double slope = sxy / sxx;

        double newRate = rate;
        double nearest = Nearest(slope);
        double span = segment[^1].WallMs - segment[0].WallMs;
        if (n == 2)
        {
            // One interval is too noisy to trust: only switch when clearly different from the assumed speed.
            if (Math.Abs(slope - rate) > 0.15) newRate = nearest;
        }
        else if (span < 300)
        {
            // A few samples are enough to tell the editor speeds apart, though not to measure an unusual one.
            newRate = nearest;
        }
        else
        {
            newRate = Math.Abs(slope - nearest) <= options.SnapTolerance ? nearest : Math.Max(slope, 0.01);
        }

        double phase = EstimatePhase(newRate, w0);

        if (newRate != rate)
        {
            rate = newRate;
            targetPhase = displayPhase = phase; // speed change: show the new estimate immediately
        }
        else targetPhase = phase;
    }

    private readonly List<double> residuals = new();

    /// <summary>
    /// Song time at <paramref name="w0"/> for the given rate. Delivery delays only ever make a sample look late
    /// (it is stamped when received, after it was read), and they come in bursts. So rather than the mean residual,
    /// this averages the upper band of residuals: the least-delayed samples, which stay steady through delay bursts.
    /// </summary>
    private double EstimatePhase(double rate, double w0)
    {
        residuals.Clear();
        foreach (var p in segment) residuals.Add(p.SongTimeMs - rate * (p.WallMs - w0));
        residuals.Sort();
        int n = residuals.Count;
        int lo = (int)(n * options.PhaseBandLow), hi = (int)Math.Ceiling(n * options.PhaseBandHigh);
        if (hi - lo < 1) return residuals[n / 2];
        double sum = 0;
        for (int i = lo; i < hi; i++) sum += residuals[i];
        return sum / (hi - lo);
    }

    private double Nearest(double slope)
    {
        double best = options.KnownRates[0];
        foreach (var r in options.KnownRates)
            if (Math.Abs(r - slope) < Math.Abs(best - slope)) best = r;
        return best;
    }

    /// <summary>Song position to display at <paramref name="wallMs"/>. Call once per frame, with increasing times.</summary>
    public double GetTime(double wallMs)
    {
        lock (gate)
        {
            if (!hasSample) return 0;
            if (frozen) { lastFrameWall = wallMs; return frozenSong; }

            if (double.IsNaN(lastFrameWall)) displayPhase = targetPhase; // nothing shown yet, so nothing to ease from
            double dt = double.IsNaN(lastFrameWall) ? 0 : Math.Max(0, wallMs - lastFrameWall);
            lastFrameWall = wallMs;

            double diff = targetPhase - displayPhase;
            if (catchingUp && Math.Abs(diff) < 0.5) catchingUp = false;
            if (diff != 0 && dt > 0)
            {
                double tau = catchingUp ? options.CatchUpTauMs : options.SmoothingTauMs;
                double slew = catchingUp ? options.CatchUpSlewFraction : options.MaxSlewFraction;
                double step = diff * (1 - Math.Exp(-dt / tau));
                double maxStep = slew * rate * dt;
                displayPhase += Math.Clamp(step, -maxStep, maxStep);
            }

            // Stop extrapolating if the source went quiet.
            double t = Math.Min(wallMs + OffsetMs, last.WallMs + options.StaleAfterMs);
            return displayPhase + rate * (t - segmentStartWall);
        }
    }
}

public readonly record struct ClockDebugInfo(
    double LastSampleSongMs, double LastSampleWallMs, double Rate, bool Paused, double LastSeekWallMs, int SegmentSamples);
