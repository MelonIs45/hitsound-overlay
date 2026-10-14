namespace Overlay.Core.Beatmap;

/// <summary>A red line, with its beat length already clamped the way osu! clamps it.</summary>
public readonly record struct TimingSection(double Time, double BeatLength, int Meter);

/// <summary>
/// Resolved timing: red-line sections (beat length, meter) and slider velocity changes,
/// with osu!'s rules for clamping, red lines resetting SV, and same-time line precedence.
/// </summary>
public sealed class TimingModel
{
    public static readonly TimingModel Empty = new(Array.Empty<TimingSection>(), Array.Empty<(double, double)>());

    private readonly TimingSection[] sections;
    private readonly double[] sectionTimes;
    private readonly double[] svTimes;
    private readonly double[] svValues;

    public IReadOnlyList<TimingSection> Sections => sections;

    private TimingModel(TimingSection[] sections, (double time, double sv)[] sv)
    {
        this.sections = sections;
        sectionTimes = sections.Select(s => s.Time).ToArray();
        svTimes = sv.Select(p => p.time).ToArray();
        svValues = sv.Select(p => p.sv).ToArray();
    }

    /// <param name="points">Timing points in file order.</param>
    public static TimingModel Build(IReadOnlyList<TimingPoint> points)
    {
        var sections = new List<TimingSection>();
        var sv = new List<(double time, double sv)>();

        // Group lines that share a time. Within a group, SV from green lines overrides the
        // SV reset of a red line regardless of file order (matches osu!'s decoder).
        foreach (var group in points.Select((p, i) => (p, i)).GroupBy(x => x.p.Time).OrderBy(g => g.Key))
        {
            double? groupSv = null;
            foreach (var (p, _) in group.OrderBy(x => x.p.Uninherited ? 0 : 1).ThenBy(x => x.i))
            {
                if (p.Uninherited)
                {
                    // A later red line at the same time replaces the earlier one.
                    if (sections.Count > 0 && sections[^1].Time == p.Time) sections.RemoveAt(sections.Count - 1);
                    sections.Add(new TimingSection(p.Time, Math.Clamp(p.BeatLength, 6, 60000), p.Meter));
                }
                groupSv = SliderVelocityOf(p);
            }
            if (groupSv is { } v) sv.Add((group.Key, v));
        }
        return new TimingModel(sections.ToArray(), sv.ToArray());
    }

    /// <summary>SV multiplier a line sets. Red lines reset to 1.</summary>
    private static double SliderVelocityOf(TimingPoint p)
    {
        if (p.Uninherited || !(p.BeatLength < 0)) return 1;
        // osu! clamps in single precision, then divides in double.
        double bpmMultiplier = Math.Clamp((float)-p.BeatLength, 10f, 10000f) / 100.0;
        return 1 / bpmMultiplier;
    }

    /// <summary>The red line in effect at <paramref name="time"/> (the first one if before all of them).</summary>
    public TimingSection SectionAt(double time)
    {
        if (sections.Length == 0) return new TimingSection(0, 1000, 4);
        int i = LastAtOrBefore(sectionTimes, time);
        return sections[Math.Max(i, 0)];
    }

    public double BeatLengthAt(double time) => SectionAt(time).BeatLength;

    /// <summary>Slider velocity multiplier at <paramref name="time"/> (1 before the first line).</summary>
    public double SliderVelocityAt(double time)
    {
        int i = LastAtOrBefore(svTimes, time);
        return i < 0 ? 1 : svValues[i];
    }

    private static int LastAtOrBefore(double[] times, double time)
    {
        int i = Array.BinarySearch(times, time);
        if (i < 0) return ~i - 1;
        // Several entries can share a time; take the last one.
        while (i + 1 < times.Length && times[i + 1] == time) i++;
        return i;
    }
}
