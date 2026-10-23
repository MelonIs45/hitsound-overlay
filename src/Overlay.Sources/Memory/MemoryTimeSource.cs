using System.Diagnostics;
using Overlay.Core.Clock;

namespace Overlay.Sources.Memory;

/// <summary>
/// Reads the editor's song time and osu!'s game state straight from osu! stable's memory (read-only), polling every
/// millisecond: a sample for every change (osu! updates the time every frame, ~2 ms apart in the editor). The open
/// beatmap still comes from another source (tosu), if given.
/// </summary>
public sealed class MemoryTimeSource : ITimeSource, IDisposable
{
    private readonly Stopwatch wall;
    private readonly ITimeSource? beatmaps;
    private readonly Thread thread;
    private volatile bool stopping;
    private SourceStatus status = SourceStatus.Disconnected;

    public event Action<TimeSample>? Sample;
    public event Action<BeatmapInfo>? BeatmapChanged;
    public event Action<SourceStatus>? StatusChanged;

    /// <summary>Why osu!'s values can't be read right now, or null.</summary>
    public string? Problem { get; private set; }

    public SourceStatus Status => status;

    /// <param name="wall">The stopwatch the renderer uses for frame times, so sample and frame times line up.</param>
    /// <param name="beatmaps">Where the open beatmap comes from (its BeatmapChanged is passed on).</param>
    public MemoryTimeSource(Stopwatch wall, ITimeSource? beatmaps = null)
    {
        this.wall = wall;
        this.beatmaps = beatmaps;
        if (beatmaps != null) beatmaps.BeatmapChanged += OnBeatmapChanged;
        thread = new Thread(Run) { IsBackground = true, Name = "osu! memory reader", Priority = ThreadPriority.AboveNormal };
    }

    private void OnBeatmapChanged(BeatmapInfo info) => BeatmapChanged?.Invoke(info);

    public void Start() => thread.Start();

    private void Run()
    {
        OsuMemory? osu = null;
        long nextAttach = 0;
        int lastTime = int.MinValue;
        long nextStatus = 0;
        while (!stopping)
        {
            try
            {
                if (osu == null)
                {
                    if (Environment.TickCount64 < nextAttach) { Thread.Sleep(50); continue; }
                    nextAttach = Environment.TickCount64 + 2000;
                    osu = Attach();
                    lastTime = int.MinValue;
                    if (osu == null) { SetStatus(SourceStatus.Disconnected); continue; }
                }

                if (!osu.TryReadTime(out int time))
                {
                    // osu! closed (or its memory can't be read any more): find it again.
                    osu.Dispose();
                    osu = null;
                    SetStatus(SourceStatus.Disconnected);
                    continue;
                }
                double readAt = wall.Elapsed.TotalMilliseconds;

                if (Environment.TickCount64 >= nextStatus)
                {
                    nextStatus = Environment.TickCount64 + 20;
                    if (osu.TryReadStatus(out int state)) SetStatus(new SourceStatus(true, state, OsuMemory.StateName(state)));
                }
                if (time != lastTime)
                {
                    lastTime = time;
                    Sample?.Invoke(new TimeSample(time, readAt, null));
                }
            }
            catch (Exception e)
            {
                // A failing handler must not end the reader.
                System.Diagnostics.Trace.WriteLine($"memory reader: {e}");
                Error?.Invoke(e);
            }
            Thread.Sleep(1); // ~1 ms with the 1 ms timer resolution the app sets
        }
        osu?.Dispose();
    }

    /// <summary>An unexpected error while reading or handling a sample; the reader carries on. Background thread.</summary>
    public event Action<Exception>? Error;

    private OsuMemory? Attach()
    {
        var procs = Process.GetProcessesByName("osu!");
        try
        {
            if (procs.Length == 0) { Problem = null; return null; }
            var osu = OsuMemory.TryAttach(procs[0], out string problem);
            Problem = osu == null ? problem : null;
            return osu;
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
    }

    private void SetStatus(SourceStatus s)
    {
        if (s == status) return;
        status = s;
        StatusChanged?.Invoke(s);
    }

    public void Dispose()
    {
        stopping = true;
        if (beatmaps != null) beatmaps.BeatmapChanged -= OnBeatmapChanged;
        if (thread.IsAlive) thread.Join(1000);
    }
}
