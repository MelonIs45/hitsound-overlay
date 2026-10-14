using System.Globalization;

namespace Overlay.Core.Beatmap;

/// <summary>The parts of an osu! skin's skin.ini that affect how hit objects are drawn.</summary>
public sealed record SkinIni(
    string HitCirclePrefix,
    double HitCircleOverlap,
    bool HitCircleOverlayAboveNumber,
    IReadOnlyList<Rgb> ComboColours)
{
    public static readonly SkinIni Default = new("default", -2, true, Array.Empty<Rgb>());

    public static SkinIni Load(string skinFolder)
    {
        // osu! accepts any casing for the file name.
        var file = Directory.Exists(skinFolder)
            ? Directory.EnumerateFiles(skinFolder, "*.ini").FirstOrDefault(f => Path.GetFileName(f).Equals("skin.ini", StringComparison.OrdinalIgnoreCase))
            : null;
        return file == null ? Default : Parse(File.ReadAllText(file));
    }

    public static SkinIni Parse(string text)
    {
        string prefix = Default.HitCirclePrefix;
        double overlap = Default.HitCircleOverlap;
        bool overlayAbove = Default.HitCircleOverlayAboveNumber;
        var colours = new SortedDictionary<int, Rgb>();
        string section = "";

        foreach (var raw in text.Split('\n'))
        {
            var line = raw;
            int comment = line.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0) line = line[..comment];
            line = line.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1].Trim(); continue; }

            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            string key = line[..colon].Trim(), value = line[(colon + 1)..].Trim();

            if (section.Equals("General", StringComparison.OrdinalIgnoreCase) && key.Equals("HitCircleOverlayAboveNumber", StringComparison.OrdinalIgnoreCase)
                || section.Equals("General", StringComparison.OrdinalIgnoreCase) && key.Equals("HitCircleOverlayAboveNumer", StringComparison.OrdinalIgnoreCase)) // osu! also reads this old misspelling
                overlayAbove = value != "0";
            else if (section.Equals("Fonts", StringComparison.OrdinalIgnoreCase) && key.Equals("HitCirclePrefix", StringComparison.OrdinalIgnoreCase))
                prefix = value.Replace('\\', '/');
            else if (section.Equals("Fonts", StringComparison.OrdinalIgnoreCase) && key.Equals("HitCircleOverlap", StringComparison.OrdinalIgnoreCase)
                && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var o))
                overlap = o;
            else if (section.Equals("Colours", StringComparison.OrdinalIgnoreCase) && key.StartsWith("Combo", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(key[5..], out int n) && n is >= 1 and <= 8 && TryColour(value, out var c))
                colours[n] = c;
        }
        return new SkinIni(prefix, overlap, overlayAbove, colours.Values.ToArray());
    }

    private static bool TryColour(string s, out Rgb rgb)
    {
        rgb = default;
        var p = s.Split(',');
        if (p.Length < 3) return false;
        if (!byte.TryParse(p[0].Trim(), out var r) || !byte.TryParse(p[1].Trim(), out var g) || !byte.TryParse(p[2].Trim(), out var b)) return false;
        rgb = new Rgb(r, g, b);
        return true;
    }
}
