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
    /// <summary>
    /// Forward moves within this long of a "paused" report are seeks (scrolling), not playback. Measured: while
    /// scrolling, tosu reports paused every 3–4 samples; during playback never.
    /// </summary>
    public double PausedFlagQuietMs { get; set; } = 450;
    /// <summary>
    /// How long osu! keeps playing after the pause key before it stops (measured 31, 36, 41 ms on the dev laptop);
    /// the display keeps moving for this long so it stops where osu! does.
    /// </summary>
    public double PauseKeyLatencyMs { get; set; } = 36;
    /// <summary>After a scroll-wheel event, how long samples may still carry the resulting seek (a sample or three).</summary>
    public double ScrollSeekWindowMs { get; set; } = 350;
    /// <summary>
    /// Within that window, a sample this far from the prediction (× playback speed, plus 15 ms) is shown as a jump;
    /// smaller differences are eased in. Kept above typical delivery jitter (~10–25 ms at 100 %).
    /// </summary>
    public double ScrollSeekToleranceMs { get; set; } = 20;
    /// <summary>After the play/pause key, how long samples that don't show the change yet are ignored.</summary>
    public double KeyHintGraceMs { get; set; } = 300;
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

    /// <summary>Optional diagnostics: called with a line for every sample and play/pause key event.</summary>
    public Action<string>? Trace { get; set; }

    public void AddSample(TimeSample s)
    {
        lock (gate)
        {
            Trace?.Invoke($"sample wall {s.WallMs:F1} song {s.SongTimeMs} paused {s.Paused} (before: frozen {frozen} hint {keyHint})");
            if (hasSample && s.WallMs < last.WallMs) return; // out of order
            bool stalled = hasSample && s.SongTimeMs == last.SongTimeMs;
            bool first = !hasSample;
            double sinceLastSample = first ? 0 : s.WallMs - last.WallMs;
            double? resumeFrom = null; // set when this sample confirms that playback started
            hasSample = true;
            last = s;
            if (s.Paused == true) lastPausedFlagWall = s.WallMs;

            // After the play/pause key, samples read before osu! reacted are stale for a moment.
            if (keyHint != KeyHint.None && s.WallMs - keyHintWall > options.KeyHintGraceMs) keyHint = KeyHint.None;
            if (keyHint == KeyHint.Play)
            {
                // Still the position from before playback started: keep running from the key press.
                if (s.Paused == true || s.SongTimeMs <= keyHintSong + 1) return;
                // The first sample since playback started takes over; ease out any small difference.
                keyHint = KeyHint.None;
                double shown = ShownAt(lastFrameWall);
                StartSegment(s);
                ContinueFrom(shown);
                return;
            }
            if (keyHint == KeyHint.Pause)
            {
                if (s.Paused != true && !stalled) return; // still moving: read before the pause took effect
                keyHint = KeyHint.None; // the paused position: shown exactly below
            }

            // Just after a scroll-wheel seek, any move away from the prediction is that seek, however small.
            if (s.WallMs <= scrollSeekUntil && s.Paused != true && !stalled)
            {
                if (frozen)
                {
                    // Scrolling while paused: snap, and don't take the moves for playback starting.
                    if (Math.Abs(s.SongTimeMs - frozenSong) > options.SeekBaseMs) { SeekCount++; LastSeekWallMs = s.WallMs; }
                    frozenSong = s.SongTimeMs;
                    pendingResume = null;
                    return;
                }
                // Scrolling while playing: start again from this sample, so even a seek too small to tell from
                // delivery jitter can't linger in the estimate. A clear jump snaps (that's the seek); a small
                // difference eases in, so jitter never shows as a jump.
                double expected = targetPhase + rate * (s.WallMs - segmentStartWall);
                bool jumped = Math.Abs(s.SongTimeMs - expected) > options.ScrollSeekToleranceMs * Math.Max(rate, 0.25) + 15;
                double shown = ShownAt(lastFrameWall);
                if (jumped) { SeekCount++; LastSeekWallMs = s.WallMs; }
                StartSegment(s);
                if (!jumped) ContinueFrom(shown);
                fastEaseUntilWall = s.WallMs + 1500; // the fresh estimate is refined quickly over the next samples
                return;
            }

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
                pauseAtWall = null;   // the exact paused position is known now
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
                // While scrolling, the source still reports "paused" on the standstill samples between scroll steps
                // (every few samples), but never during playback: so recently-paused means these moves are seeks.
                bool recentlyPaused = s.WallMs - lastPausedFlagWall < options.PausedFlagQuietMs;
                if (pendingResume is { } start && IsPlaybackSpeed(start, s) && !recentlyPaused)
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

            if (segment.Count == 0)
            {
                // Running since the play key, but no sample confirmed it within the grace period (e.g. osu! jumped back
                // as playback started): carry on from this sample, snapping if it's a jump.
                double shown = ShownAt(lastFrameWall);
                bool jumped = !(Math.Abs(s.SongTimeMs - shown) <= SeekThreshold);
                if (jumped) { SeekCount++; LastSeekWallMs = s.WallMs; }
                StartSegment(s);
                if (!jumped) ContinueFrom(shown);
                return;
            }

            double predicted = targetPhase + rate * (s.WallMs - segmentStartWall);
            double impliedSpeed = (s.SongTimeMs - segment[^1].SongTimeMs) / Math.Max(1, s.WallMs - segment[^1].WallMs);
            double error = s.SongTimeMs - predicted;
            if (s.Paused == null && error < -SeekThreshold && impliedSpeed >= 0 && impliedSpeed < 0.5 * rate)
            {
                // Fell well behind while barely moving: playback stopped between samples (a pause the source
                // didn't flag). Hold here; if it was a slow-down instead, the next sample restarts playback.
                // Only for sources without a pause flag: with one, this pattern is a small backward scroll while
                // playing, and the pause itself is reported.
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

            // Playback just started: rather than jumping ahead to where the song already is, start moving from the
            // held position and catch up quickly.
            if (resumeFrom is { } held) ContinueFrom(held);
        }
    }

    /// <summary>
    /// The play/pause key (Space in the editor) was pressed at <paramref name="wallMs"/>. Acts on it at once instead of
    /// waiting for the time source to show the change: starts moving from the shown position, or stops there. For a
    /// short grace period samples that don't show the change yet are ignored; if the key didn't actually toggle
    /// playback (e.g. it was typed into a text box), samples correct the display once the grace period ends.
    /// </summary>
    public void PlayPauseKeyPressed(double wallMs)
    {
        lock (gate)
        {
            if (!hasSample) return;
            Trace?.Invoke($"key   wall {wallMs:F1} -> {(frozen ? "play" : "pause")} at shown {ShownAt(wallMs):F1}");
            keyHintWall = wallMs;
            pendingResume = null;
            if (pauseAtWall != null)
            {
                // Play again before the pause took effect: carry on as if it never happened.
                pauseAtWall = null;
                keyHint = KeyHint.None;
                return;
            }
            if (frozen)
            {
                keyHint = KeyHint.Play;
                keyHintSong = frozenSong;
                frozen = false;
                segment.Clear();
                segmentStartWall = wallMs;
                // Continuous with the held position: shown(t) = held + rate * (t - wallMs).
                targetPhase = displayPhase = frozenSong - rate * OffsetMs;
                catchingUp = false;
            }
            else
            {
                // osu! keeps playing briefly after the key before it actually stops (measured 31–41 ms), so keep moving
                // until then (GetTime freezes at that moment), rather than stopping short and snapping forward.
                keyHint = KeyHint.Pause;
                pauseAtWall = wallMs + options.PauseKeyLatencyMs;
            }
        }
    }

    /// <summary>After the pause key: when to stop (osu! stops a little after the key).</summary>
    private double? pauseAtWall;

    /// <summary>Samples up to this wall time may carry a scroll-wheel seek.</summary>
    private double scrollSeekUntil = double.NegativeInfinity;
    /// <summary>Until this wall time, refinements of the estimate are eased in quickly (after a scroll seek).</summary>
    private double fastEaseUntilWall = double.NegativeInfinity;

    /// <summary>
    /// The editor's timeline was scrolled (mouse wheel) at <paramref name="wallMs"/>, which seeks. For a short while,
    /// samples that differ from the prediction are taken as that seek even when the jump is too small to tell from
    /// delivery jitter: while playing, playback carries on from the new position; while paused, it snaps there.
    /// </summary>
    public void ScrollWheelMoved(double wallMs)
    {
        lock (gate)
        {
            Trace?.Invoke($"wheel wall {wallMs:F1}");
            scrollSeekUntil = Math.Max(scrollSeekUntil, wallMs + options.ScrollSeekWindowMs);
        }
    }

    private enum KeyHint { None, Play, Pause }
    private KeyHint keyHint;
    private double keyHintWall, keyHintSong;

    /// <summary>The position the display shows at <paramref name="wallMs"/> (without advancing the easing).</summary>
    private double ShownAt(double wallMs)
    {
        if (frozen) return frozenSong;
        return displayPhase + rate * (ExtrapolateTo(wallMs) - segmentStartWall);
    }

    /// <summary>
    /// The wall time to extrapolate to: <paramref name="wallMs"/> plus the offset, but no further than a while after the
    /// latest information (a sample, or the play key starting this segment), so the display stops if the source goes
    /// quiet. Never before the segment start, which would run the display backwards.
    /// </summary>
    private double ExtrapolateTo(double wallMs) =>
        Math.Min(wallMs + OffsetMs, Math.Max(last.WallMs, segmentStartWall) + options.StaleAfterMs);

    /// <summary>Keeps the display continuous at <paramref name="shown"/> and eases it onto the new estimate quickly.</summary>
    private void ContinueFrom(double shown)
    {
        if (double.IsNaN(lastFrameWall)) return;
        displayPhase = shown - rate * (lastFrameWall + OffsetMs - segmentStartWall);
        catchingUp = displayPhase != targetPhase;
    }

    /// <summary>The display is catching up after playback started (eases faster than usual).</summary>
    private bool catchingUp;

    private double SeekThreshold => options.SeekBaseMs + options.SeekPerRateMs * rate;

    /// <summary>A forward move from a standstill, waiting for the next sample to show whether playback started.</summary>
    private TimeSample? pendingResume;
    /// <summary>When the source last reported "paused".</summary>
    private double lastPausedFlagWall = double.NegativeInfinity;

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
            if (!frozen && pauseAtWall is { } pauseAt && wallMs >= pauseAt)
            {
                // The pause key's moment has come: stop where the song is at that time.
                frozenSong = ShownAt(pauseAt);
                frozen = true;
                segment.Clear();
                pauseAtWall = null;
            }
            if (frozen) { lastFrameWall = wallMs; return frozenSong; }

            if (double.IsNaN(lastFrameWall)) displayPhase = targetPhase; // nothing shown yet, so nothing to ease from
            double dt = double.IsNaN(lastFrameWall) ? 0 : Math.Max(0, wallMs - lastFrameWall);
            lastFrameWall = wallMs;

            double diff = targetPhase - displayPhase;
            if (catchingUp && Math.Abs(diff) < 0.5) catchingUp = false;
            if (diff != 0 && dt > 0)
            {
                bool fast = catchingUp || wallMs < fastEaseUntilWall;
                double tau = fast ? options.CatchUpTauMs : options.SmoothingTauMs;
                double slew = fast ? options.CatchUpSlewFraction : options.MaxSlewFraction;
                double step = diff * (1 - Math.Exp(-dt / tau));
                double maxStep = slew * rate * dt;
                displayPhase += Math.Clamp(step, -maxStep, maxStep);
            }

            // Stop extrapolating if the source went quiet.
            return displayPhase + rate * (ExtrapolateTo(wallMs) - segmentStartWall);
        }
    }
}

public readonly record struct ClockDebugInfo(
    double LastSampleSongMs, double LastSampleWallMs, double Rate, bool Paused, double LastSeekWallMs, int SegmentSamples);
