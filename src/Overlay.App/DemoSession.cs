using Overlay.Core.Beatmap;
using Overlay.Sources.Mock;
using static Overlay.Sources.Mock.EditorSimulation;

namespace Overlay.App;

/// <summary>
/// A scripted editor session over a beatmap, for running the overlay without osu!: plays at each editor speed,
/// pauses, seeks while paused and while playing, and wraps back to the start at the end of the map.
/// </summary>
public static class DemoSession
{
    public static EditorSimulation Create(OsuBeatmap map, double durationMs)
    {
        double start = map.HitObjects.Count > 0 ? map.HitObjects[0].StartTime : 0;
        double end = map.HitObjects.Count > 0 ? map.HitObjects[^1].EndTime : 60_000;
        start = Math.Max(0, start - 1_000);

        // (what, seconds of wall time, argument)
        var cycle = new (string op, double secs, double arg)[]
        {
            ("rate", 8, 1.0),
            ("pause", 2, 0), ("seekBy", 1.5, -2_000), ("seekBy", 1.5, 500), ("play", 0, 0),
            ("rate", 8, 0.75),
            ("seekBy", 6, 4_000),   // seek while playing
            ("rate", 8, 0.5),
            ("pause", 1.5, 0), ("seekBy", 1, 1_000), ("play", 0, 0),
            ("rate", 10, 0.25),
            ("rate", 6, 1.0),
            ("seekBy", 8, -3_000),
        };

        var events = new List<Event>();
        double t = 2_000, song = start, rate = 1.0;
        bool paused = false;
        events.Add(new Seek(0, start));
        while (t < durationMs)
        {
            foreach (var (op, secs, arg) in cycle)
            {
                switch (op)
                {
                    case "rate": rate = arg; events.Add(new SetRate(t, arg)); break;
                    case "pause": paused = true; events.Add(new Pause(t)); break;
                    case "play": paused = false; events.Add(new Play(t)); break;
                    case "seekBy":
                        song = Math.Max(0, song + arg);
                        events.Add(new Seek(t, song));
                        break;
                }
                double ms = secs * 1000;
                if (!paused) song += rate * ms;
                t += ms;
                if (song > end)
                {
                    song = start;
                    events.Add(new Seek(t, song));
                }
            }
        }
        return new EditorSimulation(start, 1.0, startPaused: true, events.Prepend(new Play(1_000)));
    }
}
