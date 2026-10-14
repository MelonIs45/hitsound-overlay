using System.Diagnostics;
using Overlay.Core.Clock;

namespace Overlay.Sources.Mock;

/// <summary>
/// Plays a scripted editor session in real time, emitting samples the way tosu would.
/// Used to drive the overlay without osu! running (demos, M3 renderer work).
/// </summary>
public sealed class MockTimeSource : ITimeSource, IDisposable
{
    private readonly EditorSimulation editor;
    private readonly SimulatedTosu tosu;
    private readonly Stopwatch clock;
    private readonly double durationMs;
    private CancellationTokenSource? cts;

    public event Action<TimeSample>? Sample;
    public event Action<BeatmapInfo>? BeatmapChanged;
    public event Action<SourceStatus>? StatusChanged;

    /// <param name="clock">The stopwatch the renderer also uses for frame times, so wall times line up.</param>
    public MockTimeSource(EditorSimulation editor, SimulatedTosu tosu, Stopwatch clock, double durationMs)
    {
        this.editor = editor;
        this.tosu = tosu;
        this.clock = clock;
        this.durationMs = durationMs;
    }

    public EditorSimulation Editor => editor;

    public void Start(BeatmapInfo? beatmap = null)
    {
        cts = new CancellationTokenSource();
        var token = cts.Token;
        StatusChanged?.Invoke(new SourceStatus(true, SourceStatus.EditorState, "edit (simulated)"));
        if (beatmap != null) BeatmapChanged?.Invoke(beatmap);
        double origin = clock.Elapsed.TotalMilliseconds;
        var samples = tosu.Generate(editor, 0, durationMs);
        _ = Task.Run(async () =>
        {
            foreach (var s in samples)
            {
                double wait = origin + s.WallMs - clock.Elapsed.TotalMilliseconds;
                if (wait > 1) await Task.Delay(TimeSpan.FromMilliseconds(wait), token).ConfigureAwait(false);
                if (token.IsCancellationRequested) return;
                // Stamp with the actual receive time, as the real source does.
                Sample?.Invoke(s with { WallMs = clock.Elapsed.TotalMilliseconds });
            }
        }, token);
    }

    public void Dispose() => cts?.Cancel();
}
