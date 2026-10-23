using Overlay.Core.Settings;
using Overlay.Core.Timeline;

namespace Overlay.Tests;

public class SettingsTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("overlay-settings-").FullName;
    private string File(string name = "settings.json") => Path.Combine(dir, name);

    public void Dispose() => Directory.Delete(dir, true);

    [Fact]
    public void Missing_file_gives_defaults()
    {
        var s = SettingsStore.Load(File());
        Assert.Equal(TimeSourceKind.Memory, s.Source);
        Assert.Equal(OverlayPlacement.Default, s.Placement);
        Assert.Equal("Ctrl+Alt+O", s.Hotkeys[HotkeyAction.ToggleOverlay]);
    }

    [Fact]
    public void Round_trips_every_setting()
    {
        var s = new OverlaySettings
        {
            Source = TimeSourceKind.Demo, Enabled = false, PxPerMs = 0.5, NowFraction = 0.3,
            ShowGrid = false, Opacity = 0.7, BackgroundOpacity = 0.2, OffsetMs = -12, ShowDebug = true, StartInTray = true,
            Placement = new OverlayPlacement(0.1, 0.2, 0.5, 90),
            PinnedDifficultyPath = @"C:\Songs\a\b.osu", TosuPath = @"C:\tosu\tosu.exe",
        };
        s.LastDifficulty[@"C:\Songs\Set"] = @"C:\Songs\Set\x.osu";
        s.Hotkeys[HotkeyAction.ZoomIn] = "Ctrl+Shift+Z";

        SettingsStore.Save(s, File());
        var r = SettingsStore.Load(File());

        Assert.Equal(TimeSourceKind.Demo, r.Source);
        Assert.False(r.Enabled);
        Assert.Equal(0.5, r.PxPerMs);
        Assert.Equal(0.3, r.NowFraction);
        Assert.False(r.ShowGrid);
        Assert.Equal(0.7, r.Opacity);
        Assert.Equal(0.2, r.BackgroundOpacity);
        Assert.Equal(-12, r.OffsetMs);
        Assert.True(r.ShowDebug);
        Assert.True(r.StartInTray);
        Assert.Equal(new OverlayPlacement(0.1, 0.2, 0.5, 90), r.Placement);
        Assert.Equal(@"C:\Songs\a\b.osu", r.PinnedDifficultyPath);
        Assert.Equal(@"C:\tosu\tosu.exe", r.TosuPath);
        Assert.Equal(@"C:\Songs\Set\x.osu", r.LastDifficulty[@"c:\songs\set"]); // folder lookup ignores case
        Assert.Equal("Ctrl+Shift+Z", r.Hotkeys[HotkeyAction.ZoomIn]);
        Assert.Equal("Ctrl+Alt+L", r.Hotkeys[HotkeyAction.LayoutMode]);
        Assert.False(System.IO.File.Exists(File() + ".tmp"));
    }

    [Fact]
    public void Corrupt_file_falls_back_to_defaults_and_is_kept()
    {
        System.IO.File.WriteAllText(File(), "{ \"PxPerMs\": oops");
        var s = SettingsStore.Load(File());
        Assert.Equal(Zoom.Default, s.PxPerMs);
        Assert.True(System.IO.File.Exists(File() + ".bad"));
    }

    [Fact]
    public void Out_of_range_values_are_clamped_and_new_hotkeys_get_defaults()
    {
        // "BeatDivisor" is a setting from an older version: unknown keys are ignored.
        System.IO.File.WriteAllText(File(), """
            { "PxPerMs": 99, "Opacity": 0, "BeatDivisor": 5, "Hotkeys": { "ZoomIn": "" } }
            """);
        var s = SettingsStore.Load(File());
        Assert.Equal(Zoom.Max, s.PxPerMs);
        Assert.Equal(0.2, s.Opacity);
        Assert.Equal("", s.Hotkeys[HotkeyAction.ZoomIn]); // explicitly disabled stays disabled
        Assert.Equal("Ctrl+Alt+O", s.Hotkeys[HotkeyAction.ToggleOverlay]);
    }
}
