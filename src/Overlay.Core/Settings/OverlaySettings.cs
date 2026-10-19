using System.Text.Json;
using System.Text.Json.Serialization;
using Overlay.Core.Timeline;

namespace Overlay.Core.Settings;

public enum TimeSourceKind { Tosu, Demo }

/// <summary>How the std objects are shown.</summary>
public enum TimelineStyle
{
    /// <summary>Only the objects, drawn onto osu!'s own editor timeline where the std editor would put them.</summary>
    OsuTimeline,
    /// <summary>The overlay's own strip, with background, beat grid and "now" line, placed and zoomed freely.</summary>
    Strip,
}

/// <summary>Actions that can be bound to global hotkeys.</summary>
public enum HotkeyAction
{
    ToggleOverlay, LayoutMode, ZoomIn, ZoomOut,
    OffsetPlus1, OffsetMinus1, OffsetPlus5, OffsetMinus5,
    NextDifficulty, PreviousDifficulty,
}

/// <summary>Everything the user can adjust. Saved as JSON (see <see cref="SettingsStore"/>).</summary>
public sealed class OverlaySettings
{
    public TimeSourceKind Source { get; set; } = TimeSourceKind.Tosu;
    public TimelineStyle Style { get; set; } = TimelineStyle.OsuTimeline;
    /// <summary>Master switch (tray menu / hotkey). When off, the overlay stays hidden even in the editor.</summary>
    public bool Enabled { get; set; } = true;
    public double PxPerMs { get; set; } = Zoom.Default;
    /// <summary>Position of the "now" line as a fraction of the strip width.</summary>
    public double NowFraction { get; set; } = 0.5;
    public bool ShowGrid { get; set; } = true;
    /// <summary>Opacity of the whole overlay (objects included).</summary>
    public double Opacity { get; set; } = 1.0;
    /// <summary>Opacity of the strip's dark background.</summary>
    public double BackgroundOpacity { get; set; } = 0.6;
    /// <summary>Latency compensation in ms of wall time (see PlaybackClock.OffsetMs).</summary>
    public double OffsetMs { get; set; }
    public bool ShowDebug { get; set; }
    /// <summary>Start/stop the moment Space (the editor's play/pause key) is pressed, instead of when tosu next reports.</summary>
    public bool UseSpaceKey { get; set; } = true;
    /// <summary>Follow mouse-wheel seeks in the editor exactly (raw input while osu!'s editor has focus).</summary>
    public bool UseScrollWheel { get; set; } = true;
    /// <summary>Tint the mania playfield's columns with the std map's combo colours.</summary>
    public bool ManiaColumnTint { get; set; } = true;
    /// <summary>The tint moves on to the next combo colour at each of these column numbers (1 = leftmost).</summary>
    public int[] ManiaColumnColourSwitches { get; set; } = { 3, 5, 7, 11, 15, 17 };
    /// <summary>Opacity of the column tint (0..1); kept low so notes stay readable.</summary>
    public double ManiaColumnTintOpacity { get; set; } = 0.15;
    /// <summary>Start with the settings window hidden in the tray.</summary>
    public bool StartInTray { get; set; }
    /// <summary>Where the overlay sits relative to osu!'s window.</summary>
    public OverlayPlacement Placement { get; set; } = OverlayPlacement.Default;
    /// <summary>Last std difficulty chosen for each beatmap folder (folder path → .osu path).</summary>
    public Dictionary<string, string> LastDifficulty { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>A std map pinned with "Use beatmap open in editor" (or "Other…"), shown whatever beatmap is open.</summary>
    public string? PinnedDifficultyPath { get; set; }
    /// <summary>Path of tosu.exe, for the "Start tosu" button.</summary>
    public string? TosuPath { get; set; }
    /// <summary>Global hotkeys, e.g. "Ctrl+Alt+O". An empty string disables one.</summary>
    public Dictionary<HotkeyAction, string> Hotkeys { get; set; } = DefaultHotkeys();

    public static Dictionary<HotkeyAction, string> DefaultHotkeys() => new()
    {
        [HotkeyAction.ToggleOverlay] = "Ctrl+Alt+O",
        [HotkeyAction.LayoutMode] = "Ctrl+Alt+L",
        [HotkeyAction.ZoomIn] = "Ctrl+Alt+OemPlus",
        [HotkeyAction.ZoomOut] = "Ctrl+Alt+OemMinus",
        [HotkeyAction.OffsetPlus1] = "Ctrl+Alt+OemCloseBrackets",
        [HotkeyAction.OffsetMinus1] = "Ctrl+Alt+OemOpenBrackets",
        [HotkeyAction.OffsetPlus5] = "Ctrl+Alt+Shift+OemCloseBrackets",
        [HotkeyAction.OffsetMinus5] = "Ctrl+Alt+Shift+OemOpenBrackets",
        [HotkeyAction.NextDifficulty] = "Ctrl+Alt+OemPeriod",
        [HotkeyAction.PreviousDifficulty] = "Ctrl+Alt+OemComma",
    };
}

/// <summary>Loads and saves <see cref="OverlaySettings"/> as JSON.</summary>
public static class SettingsStore
{
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OsuTimelineOverlay", "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Reads settings, falling back to defaults when the file is missing. A file that can't be read is kept as
    /// "settings.json.bad" so a typo in a hand edit doesn't silently lose everything.
    /// </summary>
    public static OverlaySettings Load(string path)
    {
        if (!File.Exists(path)) return new OverlaySettings();
        try
        {
            var s = JsonSerializer.Deserialize<OverlaySettings>(File.ReadAllText(path), Options) ?? new OverlaySettings();
            return Normalize(s);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or IOException)
        {
            try { File.Copy(path, path + ".bad", overwrite: true); } catch (IOException) { }
            return new OverlaySettings();
        }
    }

    /// <summary>Writes atomically (temp file, then replace), so a crash mid-write can't corrupt the settings.</summary>
    public static void Save(OverlaySettings settings, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
        File.Move(temp, path, overwrite: true);
    }

    private static OverlaySettings Normalize(OverlaySettings s)
    {
        s.LastDifficulty = new Dictionary<string, string>(s.LastDifficulty ?? new(), StringComparer.OrdinalIgnoreCase);
        var hotkeys = OverlaySettings.DefaultHotkeys();
        foreach (var (action, key) in s.Hotkeys ?? new()) hotkeys[action] = key ?? "";
        s.Hotkeys = hotkeys; // actions added in later versions get their defaults
        s.Placement ??= OverlayPlacement.Default;
        s.PxPerMs = Math.Clamp(s.PxPerMs, Zoom.Min, Zoom.Max);
        s.NowFraction = Math.Clamp(s.NowFraction, 0.05, 0.95);
        s.Opacity = Math.Clamp(s.Opacity, 0.2, 1);
        s.BackgroundOpacity = Math.Clamp(s.BackgroundOpacity, 0, 1);
        s.ManiaColumnTintOpacity = Math.Clamp(s.ManiaColumnTintOpacity, 0, 1);
        s.ManiaColumnColourSwitches = (s.ManiaColumnColourSwitches ?? Array.Empty<int>()).Where(b => b > 0).Distinct().OrderBy(b => b).ToArray();
        return s;
    }
}
