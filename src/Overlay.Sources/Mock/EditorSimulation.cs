using Overlay.Core.Clock;

namespace Overlay.Sources.Mock;

/// <summary>A scripted editor: the true song position over wall time, given play/pause/seek/speed events.</summary>
public sealed class EditorSimulation
{
    public abstract record Event(double AtWallMs);
    public sealed record Play(double AtWallMs) : Event(AtWallMs);
    public sealed record Pause(double AtWallMs) : Event(AtWallMs);
    public sealed record Seek(double AtWallMs, double SongMs) : Event(AtWallMs);
    public sealed record SetRate(double AtWallMs, double Rate) : Event(AtWallMs);

    // Piecewise-linear song position: from each knot on, song = SongMs + Speed * (wall - WallMs).
    private readonly record struct Knot(double WallMs, double SongMs, double Speed, bool Paused, double Rate);
    private readonly List<Knot> knots = new();

    public EditorSimulation(double startSongMs, double rate, bool startPaused, IEnumerable<Event> events)
    {
        var k = new Knot(0, startSongMs, startPaused ? 0 : rate, startPaused, rate);
        knots.Add(k);
        foreach (var e in events.OrderBy(e => e.AtWallMs))
        {
            double song = k.SongMs + k.Speed * (e.AtWallMs - k.WallMs);
            k = e switch
            {
                Play => new Knot(e.AtWallMs, song, k.Rate, false, k.Rate),
                Pause => new Knot(e.AtWallMs, song, 0, true, k.Rate),
                Seek s => new Knot(e.AtWallMs, s.SongMs, k.Speed, k.Paused, k.Rate),
                SetRate r => new Knot(e.AtWallMs, song, k.Paused ? 0 : r.Rate, k.Paused, r.Rate),
                _ => k,
            };
            knots.Add(k);
        }
    }

    private Knot At(double wallMs)
    {
        int i = knots.Count - 1;
        while (i > 0 && knots[i].WallMs > wallMs) i--;
        return knots[i];
    }

    public double SongAt(double wallMs)
    {
        var k = At(wallMs);
        return k.SongMs + k.Speed * (wallMs - k.WallMs);
    }

    public bool PausedAt(double wallMs) => At(wallMs).Paused;
    public double RateAt(double wallMs) => At(wallMs).Rate;
}

/// <summary>Imitates how tosu reports the editor: periodic reads, rounded to whole ms, delivered after a variable delay.</summary>
public sealed class SimulatedTosu
{
    /// <summary>Mean interval between reads (tosu's minimum poll interval is 100 ms; measured ~102 ms).</summary>
    public double PollMs { get; init; } = 102;
    /// <summary>Random variation of each poll interval (± ms).</summary>
    public double PollJitterMs { get; init; } = 2;
    /// <summary>
    /// Delivery delay = DelayMinMs + uniform(0, DelaySpreadMs), plus occasional single-sample spikes and
    /// multi-second bursts of extra delay. Defaults match the M0 recording: core spread ~0–16 ms
    /// (p50 ~10 ms), with a tail to ~40 ms that comes in bursts lasting a few seconds.
    /// </summary>
    public double DelayMinMs { get; init; } = 3;
    public double DelaySpreadMs { get; init; } = 16;
    public double SpikeProbability { get; init; } = 0.03;
    public double SpikeMs { get; init; } = 20;
    /// <summary>Chance per sample that a burst starts.</summary>
    public double BurstProbability { get; init; } = 0.01;
    public int BurstMinSamples { get; init; } = 10;
    public int BurstMaxSamples { get; init; } = 40;
    public double BurstMinMs { get; init; } = 10;
    public double BurstMaxMs { get; init; } = 25;
    public double DropProbability { get; init; }
    /// <summary>Whether samples carry the paused flag (tosu does; a bare time source would not).</summary>
    public bool ReportPaused { get; init; } = true;

    /// <summary>Largest possible delivery delay.</summary>
    public double MaxDelayMs => DelayMinMs + DelaySpreadMs + SpikeMs + BurstMaxMs;

    public List<TimeSample> Generate(EditorSimulation editor, double fromWallMs, double toWallMs, int seed = 1)
    {
        var rng = new Random(seed);
        var samples = new List<TimeSample>();
        int burstLeft = 0;
        double burstMs = 0;
        for (double t = fromWallMs; t < toWallMs; t += PollMs + (rng.NextDouble() * 2 - 1) * PollJitterMs)
        {
            if (burstLeft == 0 && rng.NextDouble() < BurstProbability)
            {
                burstLeft = rng.Next(BurstMinSamples, BurstMaxSamples + 1);
                burstMs = BurstMinMs + rng.NextDouble() * (BurstMaxMs - BurstMinMs);
            }
            double delay = DelayMinMs + rng.NextDouble() * DelaySpreadMs
                + (rng.NextDouble() < SpikeProbability ? SpikeMs : 0)
                + (burstLeft > 0 ? burstMs : 0);
            if (burstLeft > 0) burstLeft--;
            if (rng.NextDouble() < DropProbability) continue;
            double song = Math.Round(editor.SongAt(t));
            samples.Add(new TimeSample(song, t + delay, ReportPaused ? editor.PausedAt(t) : null));
        }
        // Delivery is in order over a single socket.
        samples.Sort((a, b) => a.WallMs.CompareTo(b.WallMs));
        return samples;
    }
}
