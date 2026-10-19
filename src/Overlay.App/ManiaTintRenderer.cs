using Overlay.Core.Beatmap;
using Overlay.Core.Settings;
using Overlay.Core.Timeline;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace Overlay.App;

/// <summary>
/// Tints the columns of osu!'s mania playfield with the std map's combo colours, stepping to the next colour at the
/// configured column numbers. Drawn into a click-through window that covers exactly the playfield.
/// </summary>
public sealed class ManiaTintRenderer : IOverlayRenderer
{
    private readonly OverlaySettings settings;
    private readonly Dictionary<uint, ID2D1SolidColorBrush> brushes = new();
    private ID2D1DeviceContext? brushOwner;

    public ManiaTintRenderer(OverlaySettings settings) => this.settings = settings;

    /// <summary>Key count of the open mania map (0: none open).</summary>
    public volatile int Keys;
    /// <summary>Colours to step through: the std map's combo colours (or the skin's, as osu! would use).</summary>
    public volatile IReadOnlyList<Rgb>? Colours;
    public bool ShowLayoutFrame { get; set; }

    /// <summary>What the last frame showed; the tint is static, so frames are only drawn when this changes.</summary>
    private (int Keys, IReadOnlyList<Rgb>? Colours, double Opacity, string Switches, float W, float H) drawn;

    private (int, IReadOnlyList<Rgb>?, double, string, float, float) State(float w, float h) =>
        (Keys, Colours, settings.ManiaColumnTintOpacity, string.Join(",", settings.ManiaColumnColourSwitches), w, h);

    public bool NeedsRedraw(float w, float h) => State(w, h) != drawn;

    public void Render(ID2D1DeviceContext dc, float w, float h)
    {
        if (!ReferenceEquals(brushOwner, dc)) { DisposeBrushes(); brushOwner = dc; }
        drawn = State(w, h);
        dc.Clear(new Color4(0, 0, 0, 0));

        int keys = Keys;
        var colours = Colours;
        if (keys <= 0 || colours == null || colours.Count == 0) return;

        byte alpha = (byte)Math.Round(255 * Math.Clamp(settings.ManiaColumnTintOpacity, 0, 1));
        var switches = settings.ManiaColumnColourSwitches;
        for (int column = 1; column <= keys; column++)
        {
            float x0 = w * (column - 1) / keys, x1 = w * column / keys;
            var c = colours[ManiaPlayfield.ColourIndex(column, switches) % colours.Count];
            dc.FillRectangle(new Rect(x0, 0, x1 - x0, h), Brush(c, alpha));
        }
    }

    private ID2D1SolidColorBrush Brush(Rgb c, byte a)
    {
        uint key = (uint)(a << 24 | c.R << 16 | c.G << 8 | c.B);
        if (!brushes.TryGetValue(key, out var brush))
        {
            brush = brushOwner!.CreateSolidColorBrush(new Color4(c.R / 255f, c.G / 255f, c.B / 255f, a / 255f));
            brushes[key] = brush;
        }
        return brush;
    }

    private void DisposeBrushes()
    {
        foreach (var b in brushes.Values) b.Dispose();
        brushes.Clear();
    }

    public void Dispose() => DisposeBrushes();
}

/// <summary>What an <see cref="OverlayHost"/> draws each frame.</summary>
public interface IOverlayRenderer : IDisposable
{
    /// <summary>Draws a frame; <paramref name="w"/> and <paramref name="h"/> are the window's size in DIPs.</summary>
    void Render(ID2D1DeviceContext dc, float w, float h);
    bool ShowLayoutFrame { get; set; }
    /// <summary>Whether the content would differ from the last frame drawn (static content can skip frames).</summary>
    bool NeedsRedraw(float w, float h) => true;
}
