using System.Diagnostics;
using System.Numerics;
using Overlay.Core.Clock;
using Overlay.Core.Settings;
using Overlay.Core.Timeline;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using Overlay.Core.Beatmap;

namespace Overlay.App;

/// <summary>Draws the std timeline (beat grid, objects, "now" line) with Direct2D. Used from the render thread only,
/// except for the properties the UI sets, which are simple reference/value swaps.</summary>
public sealed class TimelineRenderer : IOverlayRenderer
{
    private readonly Stopwatch wall;
    private readonly OverlaySettings settings;
    private readonly IDWriteFactory dwrite = DWrite.DWriteCreateFactory<IDWriteFactory>();
    private readonly Dictionary<uint, BrushEntry> brushes = new();
    private ID2D1DeviceContext? brushOwner;
    private float brushOpacity = 1;
    /// <summary>Fade of the object being drawn (1 outside the object loop).</summary>
    private float objectAlpha = 1;
    private float ObjectOpacity => brushOpacity * objectAlpha;
    private IDWriteTextFormat? numberFormat, labelFormat;
    private float numberFormatSize;
    private static readonly string[] Numbers = Enumerable.Range(0, 1000).Select(i => i.ToString()).ToArray();

    private volatile Scene? scene;

    private sealed record Scene(OsuBeatmap Beatmap, HitObjectIndex Index);

    public TimelineRenderer(Stopwatch wall, OverlaySettings settings)
    {
        this.wall = wall;
        this.settings = settings;
    }

    public volatile PlaybackClock? Clock;
    /// <summary>For demos: the true song position at a wall time, to show the clock's error.</summary>
    public volatile Func<double, double>? TruthAt;
    private volatile bool showLayoutFrame;
    public bool ShowLayoutFrame { get => showLayoutFrame; set => showLayoutFrame = value; }
    /// <summary>
    /// When set, draw only the objects, onto osu!'s own editor timeline (the window then covers exactly that strip);
    /// when null, draw the overlay's own strip.
    /// </summary>
    public volatile EditorTimelineView? EditorView;
    /// <summary>The user's skin, for drawing objects with its images (null: plain drawn circles).</summary>
    public volatile SkinImages? Skin;
    private SkinImages? activeSkin;
    /// <summary>Beat snap for the grid: the editor's, as saved in the open map.</summary>
    public volatile int BeatSnap = 4;
    public volatile string? Message;

    public FrameStats Stats { get; } = new();
    /// <summary>Optional diagnostics: called with (wall time, shown song time) for every frame drawn.</summary>
    public Action<double, double>? FrameTrace { get; set; }
    public double LastShownTime { get; private set; }
    public double LastError { get; private set; } = double.NaN;

    public OsuBeatmap? Beatmap
    {
        get => scene?.Beatmap;
        set => scene = value == null ? null : new Scene(value, new HitObjectIndex(value.HitObjects));
    }

    public void Render(ID2D1DeviceContext dc, float w, float h)
    {
        long renderStart = Stopwatch.GetTimestamp();
        if (!ReferenceEquals(brushOwner, dc)) { DisposeBrushes(); brushOwner = dc; }
        float opacity = (float)Math.Clamp(settings.Opacity, 0, 1);
        brushOpacity = opacity; // brushes pick it up when next used
        dc.Clear(new Color4(0, 0, 0, 0));

        var editor = EditorView;
        var s = scene;
        var clock = Clock;
        bool ready = s != null && clock != null && clock.HasTime;

        // Geometry: either osu!'s own timeline (only objects, placed as the std editor would), or the overlay's strip.
        TimelineLayout layout;
        float laneY, r;
        if (editor != null)
        {
            layout = new TimelineLayout(w, w * editor.NowFraction, w * editor.WidthsPerMs);
            laneY = h * (float)editor.LaneFraction;
            r = h * (float)editor.RadiusFraction;
        }
        else
        {
            float bgAlpha = (float)Math.Clamp(settings.BackgroundOpacity, 0, 1);
            dc.FillRectangle(new Rect(0, 0, w, h), Brush(16, 16, 22, (byte)(bgAlpha * 255)));
            if (ShowLayoutFrame) dc.DrawRectangle(new Rect(1, 1, w - 2, h - 2), Brush(255, 204, 34), 2);
            layout = new TimelineLayout(w, MathF.Round(w * (float)Math.Clamp(settings.NowFraction, 0, 1)), settings.PxPerMs);
            laneY = MathF.Round(h * 0.52f);
            r = MathF.Max(4, MathF.Round(h * 0.26f));
        }
        var nowLine = (new Vector2((float)layout.NowX, 0), new Vector2((float)layout.NowX, h));

        if (!ready)
        {
            if (editor == null)
            {
                dc.DrawLine(nowLine.Item1, nowLine.Item2, Brush(255, 204, 34), 2);
                DrawLabel(dc, Message ?? (s == null ? "No standard difficulty loaded" : "Waiting for the editor..."), 6, 4);
            }
            Stats.Record(wall.Elapsed.TotalMilliseconds, Stopwatch.GetElapsedTime(renderStart).TotalMilliseconds);
            return;
        }

        double wallMs = wall.Elapsed.TotalMilliseconds;
        clock!.OffsetMs = settings.OffsetMs;
        double now = clock.GetTime(wallMs);
        LastShownTime = now;
        FrameTrace?.Invoke(wallMs, now);
        var (from, to) = layout.VisibleRange(now, marginPx: r * 2);

        if (editor == null && settings.ShowGrid) DrawGrid(dc, s!, layout, now, from, to, h);
        // Skin images: osu!'s timeline scale in that style; in the strip, scaled to its circle size.
        var skin = Skin;
        if (!ReferenceEquals(skin, activeSkin)) { activeSkin?.Dispose(); activeSkin = skin; } // GPU objects die on this thread
        double skinScale = editor != null ? h * editor.SkinScaleFraction : r / 61.25;
        DrawObjects(dc, s!, layout, now, from, to, laneY, r, w, h, fadeAtEnds: editor != null, skin, skinScale);
        if (editor == null) dc.DrawLine(nowLine.Item1, nowLine.Item2, Brush(255, 204, 34), 2);

        var truth = TruthAt;
        if (truth != null) LastError = now - truth(wallMs);
        if (settings.ShowDebug)
        {
            var d = clock.Debug;
            string err = double.IsNaN(LastError) ? "" : $"   error {LastError,6:+0.0;-0.0} ms";
            DrawLabel(dc, $"{now,9:F1} ms   {(d.Paused ? "paused" : $"{d.Rate:0.##}x")}   {Stats.Fps,3} fps{err}", 6, 4);
        }

        Stats.Record(wallMs, Stopwatch.GetElapsedTime(renderStart).TotalMilliseconds);
    }
    // ---- beat grid ----

    private static (byte r, byte g, byte b, float width) SnapStyle(int denominator) => denominator switch
    {
        1 => (255, 255, 255, 1.5f),
        2 => (237, 60, 60, 1.2f),
        3 => (170, 90, 255, 1.2f),
        4 => (70, 130, 255, 1.2f),
        6 => (255, 70, 200, 1.2f),
        8 => (255, 210, 40, 1.2f),
        12 => (160, 160, 160, 1f),
        _ => (110, 110, 110, 1f),
    };

    private void DrawGrid(ID2D1DeviceContext dc, Scene s, TimelineLayout layout, double now, double from, double to, float h)
    {
        int divisor = BeatGrid.Readable(BeatSnap, s.Beatmap.Timing.BeatLengthAt(now), layout.PxPerMs);
        var barLine = Brush(255, 255, 255, 70);
        foreach (var tick in BeatGrid.Ticks(s.Beatmap.Timing, from, to, divisor))
        {
            float x = MathF.Round((float)layout.TimeToX(tick.Time, now)) + 0.5f;
            if (tick.IsBarLine) dc.DrawLine(new Vector2(x, 0), new Vector2(x, h), barLine, 1);
            float len = tick.IsBarLine ? h * 0.42f : tick.Denominator switch { 1 => h * 0.32f, 2 => h * 0.24f, _ => h * 0.16f };
            var (cr, cg, cb, width) = SnapStyle(tick.Denominator);
            dc.DrawLine(new Vector2(x, h - len), new Vector2(x, h), Brush(cr, cg, cb), width);
        }
    }

    // ---- hit objects ----

    private void DrawObjects(ID2D1DeviceContext dc, Scene s, TimelineLayout layout, double now, double from, double to,
        float laneY, float r, float w, float h, bool fadeAtEnds, SkinImages? skin, double skinScale)
    {
        var objects = s.Index.Objects;
        // As in osu!: the map's own combo colours, else the skin's, else osu!'s defaults.
        var colours = !s.Beatmap.HasOwnComboColours && skin?.Ini.ComboColours is { Count: > 0 } skinColours
            ? skinColours
            : s.Beatmap.ComboColours;
        var (first, end) = s.Index.Query(from, to);

        int drawn = 0;
        // Later objects first, so earlier ones (the next to be hit) end up on top, as in the editor.
        for (int i = end - 1; i >= first; i--)
        {
            var o = objects[i];
            if (o.EndTime < from || o.StartTime > to) continue;
            float x0 = (float)layout.TimeToX(o.StartTime, now), x1 = (float)layout.TimeToX(o.EndTime, now);
            var c = colours[o.ComboColourIndex % colours.Count];

            // On osu!'s timeline, objects fade out towards both ends as they do in the editor: by the tail at the left
            // end (where objects leave, so a long slider or spinner stays solid while any of it is well inside) and by
            // the head at the right end (where they come in).
            float alpha = fadeAtEnds ? (float)EditorTimeline.EdgeAlpha(Math.Min(x1, w - x0) / h) : 1;
            if (alpha <= 0.001f) continue;
            objectAlpha = alpha; // multiplied into every brush and image this object uses

            if (skin == null || !DrawSkinned(dc, skin, o, x0, x1, laneY, skinScale, c))
                DrawVector(dc, o, x0, x1, laneY, r, c);

            drawn++;
        }
        LastDrawn = (drawn, first, end, from, to);
        objectAlpha = 1;
    }

    /// <summary>Objects drawn in the last frame, and the query range (for diagnostics).</summary>
    public (int Drawn, int First, int End, double From, double To) LastDrawn { get; private set; }

    /// <summary>Plain drawn objects, used when no skin is available.</summary>
    private void DrawVector(ID2D1DeviceContext dc, HitObject o, float x0, float x1, float laneY, float r, Rgb c)
    {
        var white = Brush(255, 255, 255);
        var bodyEdge = Brush(255, 255, 255, 150);
        switch (o.Kind)
        {
            case HitObjectKind.Slider:
            {
                // The body's rounded ends are centred on the head and tail, so the three read as one shape.
                var body = Capsule(x0, x1, laneY, r);
                dc.FillRoundedRectangle(body, Brush((byte)(c.R * 0.6), (byte)(c.G * 0.6), (byte)(c.B * 0.6), 235));
                dc.DrawRoundedRectangle(body, bodyEdge, 1.5f);
                for (int k = 1; k < o.Slides; k++)
                    dc.FillEllipse(new Ellipse(new Vector2(x0 + (x1 - x0) * k / o.Slides, laneY), r * 0.3f, r * 0.3f), white);
                var tail = new Ellipse(new Vector2(x1, laneY), r, r);
                dc.FillEllipse(tail, Brush(c.R, c.G, c.B));
                dc.DrawEllipse(tail, white, 2);
                DrawHead(dc, Brush(c.R, c.G, c.B), x0, laneY, r, o.ComboNumber);
                break;
            }
            case HitObjectKind.Spinner:
            {
                var body = Capsule(x0, x1, laneY, r);
                dc.FillRoundedRectangle(body, Brush(150, 150, 160, 120));
                dc.DrawRoundedRectangle(body, bodyEdge, 1.5f);
                var head = new Ellipse(new Vector2(x0, laneY), r, r);
                dc.FillEllipse(head, Brush(150, 150, 160));
                dc.DrawEllipse(head, white, 2);
                break;
            }
            default:
                DrawHead(dc, Brush(c.R, c.G, c.B), x0, laneY, r, o.ComboNumber);
                break;
        }
    }

    /// <summary>
    /// Objects drawn with the skin's images, as osu! does: the circle tinted with the combo colour, then the number and
    /// the (untinted) overlay in the order the skin asks for. Returns false if the skin lacks the basic circle images.
    /// </summary>
    private bool DrawSkinned(ID2D1DeviceContext dc, SkinImages skin, HitObject o, float x0, float x1, float y, double scale, Rgb c)
    {
        if (!skin.Has("hitcircle") || !skin.Has("hitcircleoverlay")) return false;
        switch (o.Kind)
        {
            case HitObjectKind.Slider:
            {
                // A capsule exactly as tall as the hit circle (radius 61.25 @1x px: the circle's visible edge), so the
                // body never sticks out past the head and tail, filled with the combo colour at ~58 % opacity.
                var body = Capsule(x0, x1, y, (float)(61.25 * scale));
                dc.FillRoundedRectangle(body, Brush(c.R, c.G, c.B, 148));
                // Tail: a hit circle without a number.
                skin.Draw(dc, "hitcircle", x1, y, scale, ObjectOpacity, c);
                skin.Draw(dc, "hitcircleoverlay", x1, y, scale, ObjectOpacity);
                for (int k = 1; k < o.Slides; k++)
                    skin.Draw(dc, "reversearrow", x0 + (x1 - x0) * k / o.Slides, y, scale, ObjectOpacity);
                DrawSkinnedCircle(dc, skin, x0, y, scale, c, o.ComboNumber);
                return true;
            }
            case HitObjectKind.Spinner:
            {
                // Like a slider, in a neutral grey (spinners have no combo colour): a body as tall as the hit circle,
                // with an unnumbered hit circle at the start and at the tail.
                var grey = new Rgb(150, 150, 160);
                var body = Capsule(x0, x1, y, (float)(61.25 * scale));
                dc.FillRoundedRectangle(body, Brush(grey.R, grey.G, grey.B, 148));
                foreach (var x in new[] { x1, x0 }) // tail first, so the start is drawn on top
                {
                    skin.Draw(dc, "hitcircle", x, y, scale, ObjectOpacity, grey);
                    skin.Draw(dc, "hitcircleoverlay", x, y, scale, ObjectOpacity);
                }
                return true;
            }
            default:
                DrawSkinnedCircle(dc, skin, x0, y, scale, c, o.ComboNumber);
                return true;
        }
    }

    private void DrawSkinnedCircle(ID2D1DeviceContext dc, SkinImages skin, float x, float y, double scale, Rgb c, int number)
    {
        skin.Draw(dc, "hitcircle", x, y, scale, ObjectOpacity, c);
        if (skin.Ini.HitCircleOverlayAboveNumber)
        {
            DrawSkinnedNumber(dc, skin, number, x, y, scale);
            skin.Draw(dc, "hitcircleoverlay", x, y, scale, ObjectOpacity);
        }
        else
        {
            skin.Draw(dc, "hitcircleoverlay", x, y, scale, ObjectOpacity);
            DrawSkinnedNumber(dc, skin, number, x, y, scale);
        }
    }

    /// <summary>Combo number from the skin's digit images, at 0.8× the circle's scale, digits overlapping as the skin says.</summary>
    private void DrawSkinnedNumber(ID2D1DeviceContext dc, SkinImages skin, int number, float x, float y, double scale)
    {
        string digits = number >= 0 && number < Numbers.Length ? Numbers[number] : number.ToString();
        double s = scale * 0.8, overlap = skin.Ini.HitCircleOverlap * s;
        double total = -overlap * (digits.Length - 1);
        foreach (char d in digits) total += (skin.Size($"{skin.Ini.HitCirclePrefix}-{d}")?.Width ?? 0) * s;
        double left = x - total / 2;
        foreach (char d in digits)
        {
            string name = $"{skin.Ini.HitCirclePrefix}-{d}";
            double dw = (skin.Size(name)?.Width ?? 0) * s;
            skin.Draw(dc, name, (float)(left + dw / 2), y, s, ObjectOpacity);
            left += dw - overlap;
        }
    }
    /// <summary>A bar of height 2r whose round ends are centred on x0 and x1.</summary>
    private static RoundedRectangle Capsule(float x0, float x1, float y, float r) =>
        new(new System.Drawing.RectangleF(x0 - r, y - r, MathF.Max(0, x1 - x0) + 2 * r, 2 * r), r, r);

    private void DrawHead(ID2D1DeviceContext dc, ID2D1SolidColorBrush fill, float x, float y, float r, int number)
    {
        var e = new Ellipse(new Vector2(x, y), r, r);
        dc.FillEllipse(e, fill);
        dc.DrawEllipse(e, Brush(255, 255, 255), 2);
        if (numberFormat == null || numberFormatSize != r)
        {
            numberFormat?.Dispose();
            numberFormat = dwrite.CreateTextFormat("Segoe UI", FontWeight.Bold, FontStyle.Normal, r * 1.1f);
            numberFormat.TextAlignment = TextAlignment.Center;
            numberFormat.ParagraphAlignment = ParagraphAlignment.Center;
            numberFormat.WordWrapping = WordWrapping.NoWrap;
            numberFormatSize = r;
        }
        string text = number >= 0 && number < Numbers.Length ? Numbers[number] : number.ToString();
        dc.DrawText(text, numberFormat, new Rect(x - r, y - r, 2 * r, 2 * r), Brush(255, 255, 255));
    }

    private void DrawLabel(ID2D1DeviceContext dc, string s, float x, float y)
    {
        labelFormat ??= dwrite.CreateTextFormat("Consolas", 11f);
        using var layout = dwrite.CreateTextLayout(s, labelFormat, 2000, 100);
        var m = layout.Metrics;
        dc.FillRectangle(new Rect(x - 3, y - 1, m.Width + 6, m.Height + 2), Brush(0, 0, 0, 170));
        dc.DrawTextLayout(new Vector2(x, y), layout, Brush(230, 230, 235));
    }

    // ---- brushes ----

    /// <summary>A cached brush, set to the current overall opacity × the current object's fade.</summary>
    private ID2D1SolidColorBrush Brush(byte r, byte g, byte b, byte a = 255)
    {
        uint key = (uint)(a << 24 | r << 16 | g << 8 | b);
        if (!brushes.TryGetValue(key, out var entry))
        {
            entry = new BrushEntry(brushOwner!.CreateSolidColorBrush(new Color4(r / 255f, g / 255f, b / 255f, a / 255f)));
            brushes[key] = entry;
        }
        float opacity = ObjectOpacity;
        if (entry.Opacity != opacity)
        {
            entry.Brush.Opacity = opacity;
            entry.Opacity = opacity;
        }
        return entry.Brush;
    }

    private sealed class BrushEntry(ID2D1SolidColorBrush brush)
    {
        public ID2D1SolidColorBrush Brush { get; } = brush;
        public float Opacity { get; set; } = 1;
    }

    private void DisposeBrushes()
    {
        foreach (var b in brushes.Values) b.Brush.Dispose();
        brushes.Clear();
    }

    public void Dispose()
    {
        DisposeBrushes();
        numberFormat?.Dispose();
        labelFormat?.Dispose();
        dwrite.Dispose();
    }
}
