namespace Overlay.Core.Beatmap;

public enum HitObjectKind { Circle, Slider, Spinner }

/// <param name="Slides">Number of spans for sliders (repeats + 1); 0 for circles and spinners.</param>
/// <param name="ComboColourIndex">Running colour index including combo skips; colour = colours[index % count].</param>
/// <param name="ComboNumber">1-based number shown on the object.</param>
public sealed record HitObject(
    HitObjectKind Kind,
    double StartTime,
    double EndTime,
    int Slides,
    bool NewCombo,
    int ComboColourIndex,
    int ComboNumber);

/// <param name="Uninherited">True for red (timing) lines, false for green (inherited) lines.</param>
/// <param name="BeatLength">Raw value from the file: ms per beat for red lines, negative SV value for green lines.</param>
public sealed record TimingPoint(double Time, double BeatLength, int Meter, bool Uninherited);

public readonly record struct Rgb(byte R, byte G, byte B);

public sealed class OsuBeatmap
{
    public int FormatVersion { get; init; }
    public int Mode { get; init; }
    public string AudioFilename { get; init; } = "";
    public double AudioLeadIn { get; init; }
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Creator { get; init; } = "";
    public string Version { get; init; } = "";
    public double SliderMultiplier { get; init; } = 1.4;
    /// <summary>CircleSize from [Difficulty]; for mania maps this is the key count.</summary>
    public double CircleSize { get; init; } = 5;
    public double SliderTickRate { get; init; } = 1;
    public IReadOnlyList<TimingPoint> TimingPoints { get; init; } = Array.Empty<TimingPoint>();
    public TimingModel Timing { get; init; } = TimingModel.Empty;
    /// <summary>Sorted by start time.</summary>
    public IReadOnlyList<HitObject> HitObjects { get; init; } = Array.Empty<HitObject>();
    /// <summary>Combo colours from the [Colours] section, or osu!'s defaults when the map has none.</summary>
    public IReadOnlyList<Rgb> ComboColours { get; init; } = Array.Empty<Rgb>();
    /// <summary>Whether the map defines its own combo colours (if not, osu! uses the skin's).</summary>
    public bool HasOwnComboColours { get; init; }
}
