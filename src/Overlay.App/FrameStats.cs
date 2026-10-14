namespace Overlay.App;

/// <summary>Frame rate and timing over the last second, for the status readout.</summary>
public sealed class FrameStats
{
    private readonly object gate = new();
    private double windowStart = double.NaN, lastFrame = double.NaN;
    private int frames;
    private double renderSum, renderMax, gapMax;

    public int Fps { get; private set; }
    /// <summary>Longest gap between frames in the last second (a hitch shows up here).</summary>
    public double MaxFrameGapMs { get; private set; }
    public double AvgRenderMs { get; private set; }
    public double MaxRenderMs { get; private set; }

    public void Record(double wallMs, double renderMs)
    {
        lock (gate)
        {
            if (double.IsNaN(windowStart)) windowStart = wallMs;
            if (!double.IsNaN(lastFrame)) gapMax = Math.Max(gapMax, wallMs - lastFrame);
            lastFrame = wallMs;
            frames++;
            renderSum += renderMs;
            renderMax = Math.Max(renderMax, renderMs);
            if (wallMs - windowStart >= 1000)
            {
                Fps = (int)Math.Round(frames * 1000 / (wallMs - windowStart));
                AvgRenderMs = renderSum / frames;
                MaxRenderMs = renderMax;
                MaxFrameGapMs = gapMax;
                windowStart = wallMs;
                frames = 0;
                renderSum = renderMax = gapMax = 0;
            }
        }
    }
}
