namespace Overlay.Core.Beatmap;

/// <param name="BeatDivisor">The editor's beat snap when the file was last saved ([Editor] BeatDivisor), if present.</param>
/// <param name="TimelineZoom">The editor's timeline zoom when the file was last saved ([Editor] TimelineZoom), if present.</param>
/// <param name="CircleSize">[Difficulty] CircleSize, if present; for mania maps this is the key count.</param>
public sealed record DifficultyInfo(string Path, string Version, int Mode, int? BeatDivisor = null, double? TimelineZoom = null,
    double? CircleSize = null);

public static class DifficultyDiscovery
{
    /// <summary>All osu!standard (Mode 0) difficulties in a beatmap folder, sorted by difficulty name.</summary>
    public static IReadOnlyList<DifficultyInfo> FindStandardDifficulties(string folder) =>
        ReadAll(folder).Where(d => d.Mode == 0).ToList();

    /// <summary>
    /// The difficulty to show when a beatmap set is opened: the last one used for this set if it still exists,
    /// otherwise the largest file (most objects, usually the top difficulty). Null if the list is empty.
    /// </summary>
    public static DifficultyInfo? PickDefault(IReadOnlyList<DifficultyInfo> difficulties, string? lastUsedPath)
    {
        if (difficulties.Count == 0) return null;
        var last = difficulties.FirstOrDefault(d => string.Equals(d.Path, lastUsedPath, StringComparison.OrdinalIgnoreCase));
        if (last != null) return last;
        return difficulties.MaxBy(d => new FileInfo(d.Path) is { Exists: true } f ? f.Length : 0);
    }

    /// <summary>Header info for every .osu file in <paramref name="folder"/>, sorted by difficulty name.</summary>
    public static IReadOnlyList<DifficultyInfo> ReadAll(string folder)
    {
        if (!Directory.Exists(folder)) return Array.Empty<DifficultyInfo>();
        var result = new List<DifficultyInfo>();
        foreach (var path in Directory.EnumerateFiles(folder, "*.osu"))
        {
            try { result.Add(ReadHeader(path)); }
            catch (IOException) { } // being written by the editor; the folder watcher will retry
            catch (UnauthorizedAccessException) { }
        }
        return result.OrderBy(d => d.Version, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Reads Mode, Version, CircleSize and the editor's beat snap and timeline zoom without parsing the whole file.</summary>
    public static DifficultyInfo ReadHeader(string path)
    {
        int mode = 0;
        int? divisor = null;
        double? zoom = null, circleSize = null;
        string version = System.IO.Path.GetFileNameWithoutExtension(path);
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.StartsWith("[TimingPoints]", StringComparison.Ordinal) || line.StartsWith("[HitObjects]", StringComparison.Ordinal)) break;
            if (line.StartsWith("Mode:", StringComparison.Ordinal) && int.TryParse(line[5..].Trim(), out var m)) mode = m;
            else if (line.StartsWith("Version:", StringComparison.Ordinal)) version = line[8..].Trim();
            else if (line.StartsWith("BeatDivisor:", StringComparison.Ordinal) && int.TryParse(line[12..].Trim(), out var d) && d > 0) divisor = d;
            else if (line.StartsWith("TimelineZoom:", StringComparison.Ordinal)
                && double.TryParse(line[13..].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var z)
                && z > 0 && double.IsFinite(z)) zoom = z;
            else if (line.StartsWith("CircleSize:", StringComparison.Ordinal)
                && double.TryParse(line[11..].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var cs))
                circleSize = cs;
        }
        return new DifficultyInfo(path, version, mode, divisor, zoom, circleSize);
    }
}
