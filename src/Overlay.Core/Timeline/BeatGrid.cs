using Overlay.Core.Beatmap;

namespace Overlay.Core.Timeline;

/// <param name="Denominator">Snap of this tick in lowest terms: 1 = whole beat, 2 = 1/2, 3 = 1/3, 4 = 1/4 ...</param>
/// <param name="IsBarLine">First beat of a measure.</param>
public readonly record struct BeatTick(double Time, int Denominator, bool IsBarLine);

public static class BeatGrid
{
    /// <summary>Beat divisors the editor offers.</summary>
    public static readonly int[] Divisors = { 1, 2, 3, 4, 6, 8, 12, 16 };

    /// <summary>
    /// Ticks at 1/<paramref name="divisor"/> of a beat between <paramref name="from"/> and <paramref name="to"/>.
    /// Each red line restarts the grid; the first one also extends backwards to cover time before it.
    /// </summary>
    public static IEnumerable<BeatTick> Ticks(TimingModel timing, double from, double to, int divisor)
    {
        var sections = timing.Sections;
        if (sections.Count == 0 || divisor < 1 || to < from) yield break;

        for (int s = 0; s < sections.Count; s++)
        {
            var sec = sections[s];
            double sectionStart = s == 0 ? double.NegativeInfinity : sec.Time;
            double sectionEnd = s + 1 < sections.Count ? sections[s + 1].Time : double.PositiveInfinity;
            double lo = Math.Max(from, sectionStart), hi = Math.Min(to, sectionEnd);
            if (lo > hi) continue;

            double step = sec.BeatLength / divisor;
            long k = (long)Math.Ceiling((lo - sec.Time) / step - 1e-9);
            int perBar = divisor * Math.Max(1, sec.Meter);
            for (double t = sec.Time + k * step; t <= hi; k++, t = sec.Time + k * step)
            {
                if (t >= sectionEnd) break; // the next red line starts its own grid
                int r = (int)(((k % divisor) + divisor) % divisor);
                int denominator = r == 0 ? 1 : divisor / Gcd(r, divisor);
                bool bar = ((k % perBar) + perBar) % perBar == 0;
                yield return new BeatTick(t, denominator, bar);
            }
        }
    }

    /// <summary>Largest offered divisor not above <paramref name="divisor"/> whose ticks are at least <paramref name="minPx"/> apart.</summary>
    public static int Readable(int divisor, double beatLengthMs, double pxPerMs, double minPx = 5)
    {
        int best = 1;
        // The divisor itself counts even if it isn't one of the usual ones (other editors allow e.g. 1/5).
        foreach (var d in Divisors.Append(divisor))
            if (d <= divisor && divisor % d == 0 && d > best && beatLengthMs / d * pxPerMs >= minPx) best = d;
        return best;
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a;
    }
}
