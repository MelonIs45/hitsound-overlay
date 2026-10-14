using Overlay.Core.Beatmap;

namespace Overlay.Core.Timeline;

/// <summary>Finds the hit objects overlapping a time range, including long sliders and spinners that started earlier.</summary>
public sealed class HitObjectIndex
{
    private readonly IReadOnlyList<HitObject> objects;
    private readonly double[] starts;
    private readonly double[] maxEndSoFar; // running maximum of end times, so it is sorted

    public HitObjectIndex(IReadOnlyList<HitObject> objects)
    {
        this.objects = objects;
        starts = objects.Select(o => o.StartTime).ToArray();
        maxEndSoFar = new double[objects.Count];
        double max = double.NegativeInfinity;
        for (int i = 0; i < objects.Count; i++) maxEndSoFar[i] = max = Math.Max(max, objects[i].EndTime);
    }

    public IReadOnlyList<HitObject> Objects => objects;

    /// <summary>Index range [First, End) of objects that may overlap [from, to]: every overlapping object is inside it.</summary>
    public (int First, int End) Query(double from, double to)
    {
        int first = LowerBound(maxEndSoFar, from); // first object whose (running) end reaches the range
        int end = LowerBound(starts, to);
        while (end < starts.Length && starts[end] <= to) end++;
        return (first, Math.Max(first, end));
    }

    private static int LowerBound(double[] a, double value)
    {
        int lo = 0, hi = a.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (a[mid] < value) lo = mid + 1; else hi = mid;
        }
        return lo;
    }
}
