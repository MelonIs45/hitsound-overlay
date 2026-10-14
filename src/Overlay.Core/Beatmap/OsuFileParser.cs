using System.Globalization;

namespace Overlay.Core.Beatmap;

/// <summary>
/// Parser for .osu files, limited to what the timeline needs. Timing rules follow osu!'s legacy decoder
/// (checked against osu!lazer's decoder by the tests in Overlay.Tests).
/// </summary>
public static class OsuFileParser
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // osu!'s default skin combo colours, used when the map defines none.
    public static readonly IReadOnlyList<Rgb> DefaultComboColours = new Rgb[]
    {
        new(255, 192, 0), new(0, 202, 0), new(18, 124, 255), new(242, 24, 57),
    };

    private const int TypeCircle = 1, TypeSlider = 2, TypeNewCombo = 4, TypeSpinner = 8, TypeComboOffset = 0b111_0000;

    public static OsuBeatmap ParseFile(string path) => Parse(File.ReadAllText(path));

    public static OsuBeatmap Parse(string text)
    {
        var general = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var difficulty = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var colours = new SortedDictionary<int, Rgb>();
        var timingLines = new List<string>();
        var objectLines = new List<string>();

        int formatVersion = 14;
        string section = "";
        bool first = true;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (first)
            {
                first = false;
                var header = line.Trim().TrimStart('﻿');
                const string prefix = "osu file format v";
                if (header.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(header[prefix.Length..], out var v))
                {
                    formatVersion = v;
                    continue;
                }
            }
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
            if (line.StartsWith('[') && line.TrimEnd().EndsWith(']'))
            {
                section = line.Trim()[1..^1];
                continue;
            }
            switch (section)
            {
                case "General": AddKeyValue(general, line); break;
                case "Metadata": AddKeyValue(metadata, line); break;
                case "Difficulty": AddKeyValue(difficulty, line); break;
                case "Colours":
                    var kv = SplitKeyValue(line);
                    if (kv is { } c && c.key.StartsWith("Combo", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(c.key[5..], out var n) && TryParseColour(c.value, out var rgb))
                        colours[n] = rgb;
                    break;
                case "TimingPoints": timingLines.Add(line); break;
                case "HitObjects": objectLines.Add(line); break;
            }
        }

        // Very old maps (format < 5) are offset by 24 ms in osu!.
        double offset = formatVersion < 5 ? 24 : 0;

        var timingPoints = timingLines.Select(l => ParseTimingPoint(l, offset)).OfType<TimingPoint>().ToList();
        var timing = TimingModel.Build(timingPoints);
        double sliderMultiplier = Math.Clamp(GetDouble(difficulty, "SliderMultiplier", 1.4), 0.4, 3.6);

        return new OsuBeatmap
        {
            FormatVersion = formatVersion,
            Mode = (int)GetDouble(general, "Mode", 0),
            AudioFilename = general.GetValueOrDefault("AudioFilename", ""),
            AudioLeadIn = GetDouble(general, "AudioLeadIn", 0),
            Title = metadata.GetValueOrDefault("Title", ""),
            Artist = metadata.GetValueOrDefault("Artist", ""),
            Creator = metadata.GetValueOrDefault("Creator", ""),
            Version = metadata.GetValueOrDefault("Version", ""),
            SliderMultiplier = sliderMultiplier,
            CircleSize = GetDouble(difficulty, "CircleSize", 5),
            SliderTickRate = GetDouble(difficulty, "SliderTickRate", 1),
            TimingPoints = timingPoints,
            Timing = timing,
            HitObjects = ParseHitObjects(objectLines, offset, formatVersion, timing, sliderMultiplier),
            ComboColours = colours.Count > 0 ? colours.Values.ToArray() : DefaultComboColours,
            HasOwnComboColours = colours.Count > 0,
        };
    }

    private static TimingPoint? ParseTimingPoint(string line, double offset)
    {
        var p = line.Split(',');
        if (p.Length < 2) return null;
        if (!TryDouble(p[0], out var time) || !TryDouble(p[1], out var beatLength)) return null;
        int meter = p.Length > 2 && TryDouble(p[2], out var m) && m >= 1 ? (int)m : 4;
        bool uninherited = p.Length < 7 || p[6].Trim().StartsWith('1');
        return new TimingPoint(time + offset, beatLength, meter, uninherited);
    }

    private readonly record struct ParsedObject(HitObjectKind Kind, double Start, double End, int Slides, bool NewCombo, int ComboOffset);

    private static List<HitObject> ParseHitObjects(List<string> lines, double offset, int formatVersion,
        TimingModel timing, double sliderMultiplier)
    {
        var parsed = new List<ParsedObject>(lines.Count);
        bool firstObject = true, forceNewCombo = false;
        int extraComboOffset = 0;

        foreach (var line in lines)
        {
            var p = line.Split(',');
            if (p.Length < 5 || !TryDouble(p[2], out var start) || !int.TryParse(p[3].Trim(), NumberStyles.Integer, Inv, out var type))
                continue;
            start += offset;
            int comboOffset = (type & TypeComboOffset) >> 4;
            bool newCombo = (type & TypeNewCombo) != 0;

            if ((type & TypeSpinner) != 0)
            {
                double end = p.Length > 5 && TryDouble(p[5], out var e) ? Math.Max(start, e + offset) : start;
                // Spinners don't start a combo themselves; they force the next object to, and pass on their combo offset.
                forceNewCombo |= formatVersion <= 8 || newCombo;
                extraComboOffset += comboOffset;
                parsed.Add(new ParsedObject(HitObjectKind.Spinner, start, end, 0, false, 0));
                continue;
            }

            newCombo |= firstObject || forceNewCombo;
            comboOffset += extraComboOffset;
            forceNewCombo = false;
            extraComboOffset = 0;
            firstObject = false;

            if ((type & TypeSlider) != 0 && p.Length > 6)
            {
                int slides = Math.Max(1, int.TryParse(p[6].Trim(), NumberStyles.Integer, Inv, out var r) ? r : 1);
                double length = p.Length > 7 && TryDouble(p[7], out var len) ? Math.Max(0, len) : 0;
                if (length == 0) length = ApproximatePathLength(p[0], p[1], p[5]);

                double beatLength = timing.BeatLengthAt(start);
                double velocity = 100 * sliderMultiplier * timing.SliderVelocityAt(start) / beatLength; // osu!px per ms
                double end = start + slides * length / velocity;
                parsed.Add(new ParsedObject(HitObjectKind.Slider, start, end, slides, newCombo, comboOffset));
            }
            else
            {
                parsed.Add(new ParsedObject(HitObjectKind.Circle, start, start, 0, newCombo, comboOffset));
            }
        }

        // Stable sort by time, then assign combo colours and numbers.
        var sorted = parsed.Select((o, i) => (o, i)).OrderBy(x => x.o.Start).ThenBy(x => x.i).Select(x => x.o);
        var result = new List<HitObject>(parsed.Count);
        int colourIndex = 0, number = 0;
        foreach (var o in sorted)
        {
            if (o.NewCombo || result.Count == 0)
            {
                colourIndex += 1 + o.ComboOffset;
                number = 1;
            }
            else number++;
            result.Add(new HitObject(o.Kind, o.Start, o.End, o.Slides, o.NewCombo, colourIndex, number));
        }
        return result;
    }

    /// <summary>
    /// Fallback for sliders without a pixel length: length of the polyline through the control points.
    /// Exact for linear sliders, an approximation for curves. Maps saved by modern osu! always store the length.
    /// </summary>
    private static double ApproximatePathLength(string x, string y, string curve)
    {
        if (!TryDouble(x, out var px) || !TryDouble(y, out var py)) return 0;
        double total = 0;
        foreach (var point in curve.Split('|').Skip(1))
        {
            var xy = point.Split(':');
            if (xy.Length != 2 || !TryDouble(xy[0], out var nx) || !TryDouble(xy[1], out var ny)) continue;
            total += Math.Sqrt((nx - px) * (nx - px) + (ny - py) * (ny - py));
            px = nx; py = ny;
        }
        return total;
    }

    private static void AddKeyValue(Dictionary<string, string> dict, string line)
    {
        if (SplitKeyValue(line) is { } kv) dict[kv.key] = kv.value;
    }

    private static (string key, string value)? SplitKeyValue(string line)
    {
        int i = line.IndexOf(':');
        return i < 0 ? null : (line[..i].Trim(), line[(i + 1)..].Trim());
    }

    private static bool TryParseColour(string s, out Rgb rgb)
    {
        rgb = default;
        var p = s.Split(',');
        if (p.Length < 3) return false;
        if (!byte.TryParse(p[0].Trim(), out var r) || !byte.TryParse(p[1].Trim(), out var g) || !byte.TryParse(p[2].Trim(), out var b))
            return false;
        rgb = new Rgb(r, g, b);
        return true;
    }

    private static double GetDouble(Dictionary<string, string> dict, string key, double fallback) =>
        dict.TryGetValue(key, out var s) && TryDouble(s, out var v) ? v : fallback;

    private static bool TryDouble(string s, out double v) =>
        double.TryParse(s.Trim(), NumberStyles.Float, Inv, out v) && !double.IsNaN(v) && !double.IsInfinity(v);
}
