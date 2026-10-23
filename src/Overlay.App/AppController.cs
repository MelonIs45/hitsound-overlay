using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Threading;
using Overlay.Core.Beatmap;
using Overlay.Core.Clock;
using Overlay.Core.Settings;
using Overlay.Core.Timeline;
using Overlay.Sources.Memory;
using Overlay.Sources.Mock;
using Overlay.Sources.Tosu;

namespace Overlay.App;

/// <summary>
/// Wires everything together: time source → clock → renderer, the beatmap folder and difficulty picker
/// (with live reload), and the overlay's position and visibility relative to the osu! window.
/// </summary>
public sealed class AppController : IDisposable
{
    private const double DemoLengthMs = 60 * 60 * 1000;
    private const double AutoHideDebounceMs = 150;

    private readonly Stopwatch wall = Stopwatch.StartNew();
    private readonly Dispatcher ui = Dispatcher.CurrentDispatcher;
    private readonly OsuWindowTracker osu = new();
    private readonly DispatcherTimer followTimer;
    private readonly DispatcherTimer reloadTimer;

    private ITimeSource? source;
    /// <summary>tosu: the time source itself, or (with the memory source) just for the open map and skin.</summary>
    private TosuTimeSource? tosu;
    private MemoryTimeSource? memory;
    private volatile SourceStatus status = SourceStatus.Disconnected;
    private volatile IEditorClock clock = new PlaybackClock();
    private int sampleCount;

    private BeatmapInfo? openBeatmap;
    private string? folder; // the open beatmap's folder
    private readonly Dictionary<string, FileSystemWatcher> watchers = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<DifficultyInfo> difficulties = Array.Empty<DifficultyInfo>();
    private DifficultyInfo? selected;
    private DifficultyInfo? pinned; // a std map chosen independently of the open beatmap
    private DateTime selectedWriteTime;

    private bool wantVisible;
    private double wantSince;
    private PixelRect? lastBounds;

    public OverlaySettings Settings { get; } = SettingsStore.Load(SettingsStore.DefaultPath);
    public TrayIcon Tray { get; }
    public HotkeyManager Hotkeys { get; }
    /// <summary>Space (play/pause) presses in osu!'s editor, so playback starts and stops on the overlay at once.</summary>
    public SpaceKeyWatcher SpaceKey { get; }
    /// <summary>Mouse-wheel scrolls in osu!'s editor (they seek), so even small seeks are followed exactly.</summary>
    public WheelWatcher Wheel { get; }
    public TimelineRenderer Renderer { get; }
    public OverlayHost Overlay { get; }
    /// <summary>Second click-through window over the mania playfield, for the bar tint.</summary>
    public ManiaTintRenderer ManiaTint { get; }
    public OverlayHost ManiaOverlay { get; }
    public ControlWindow Control { get; }

    public AppController()
    {
        Renderer = new TimelineRenderer(wall, Settings) { Clock = clock };
        Overlay = new OverlayHost(Renderer, InitialBounds(Settings.Placement));
        Overlay.BoundsChanged += b => ui.BeginInvoke(() => OnUserMovedOverlay(b));
        ManiaTint = new ManiaTintRenderer(Settings);
        ManiaOverlay = new OverlayHost(ManiaTint, (0, 0, 100, 100)) { Visible = false }; // placed once a mania map is open
        Control = new ControlWindow(this);

        reloadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        reloadTimer.Tick += (_, _) => { reloadTimer.Stop(); RefreshFolder(); };
        followTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Normal, (_, _) => FollowOsu(), ui);
        followTimer.Start();
        saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        saveTimer.Tick += (_, _) => SaveNow();

        Tray = new TrayIcon();
        Tray.OpenSettings += () => Control.ShowFromTray();
        Tray.ToggleEnabled += () => OnHotkey(HotkeyAction.ToggleOverlay);
        Tray.ToggleLayout += () => OnHotkey(HotkeyAction.LayoutMode);
        Tray.Exit += () => System.Windows.Application.Current.Shutdown();

        SpaceKey = new SpaceKeyWatcher(wall);
        SpaceKey.Pressed += wallMs => clock.PlayPauseKeyPressed(wallMs);
        Wheel = new WheelWatcher(Control, wall);
        Wheel.Scrolled += wallMs => clock.ScrollWheelMoved(wallMs);

        Hotkeys = new HotkeyManager(Control);
        Hotkeys.Pressed += OnHotkey;
        Hotkeys.Register(Settings.Hotkeys);

        StartTosuIfNeeded();
        SetSource(Settings.Source);
        ApplyStyle();
        if (Settings.PinnedDifficultyPath is { } pinnedPath && File.Exists(pinnedPath)) OpenFile(pinnedPath);
    }

    // ---- settings ----

    private readonly DispatcherTimer saveTimer;

    /// <summary>Saves shortly after the last change (sliders send many changes while dragged).</summary>
    public void SaveSoon()
    {
        saveTimer.Stop();
        saveTimer.Start();
    }

    private void SaveNow()
    {
        saveTimer.Stop();
        try { SettingsStore.Save(Settings, SettingsStore.DefaultPath); }
        catch (IOException e) { Trace.WriteLine($"Could not save settings: {e.Message}"); }
        catch (UnauthorizedAccessException e) { Trace.WriteLine($"Could not save settings: {e.Message}"); }
    }

    public void OnHotkey(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.ToggleOverlay: Settings.Enabled = !Settings.Enabled; break;
            case HotkeyAction.LayoutMode:
                if (Settings.Style == TimelineStyle.Strip) Overlay.LayoutMode = !Overlay.LayoutMode;
                break;
            case HotkeyAction.ZoomIn: Settings.PxPerMs = Zoom.Step(Settings.PxPerMs, 1); break;
            case HotkeyAction.ZoomOut: Settings.PxPerMs = Zoom.Step(Settings.PxPerMs, -1); break;
            case HotkeyAction.OffsetPlus1: Settings.OffsetMs += 1; break;
            case HotkeyAction.OffsetMinus1: Settings.OffsetMs -= 1; break;
            case HotkeyAction.OffsetPlus5: Settings.OffsetMs += 5; break;
            case HotkeyAction.OffsetMinus5: Settings.OffsetMs -= 5; break;
            case HotkeyAction.NextDifficulty: StepDifficulty(1); break;
            case HotkeyAction.PreviousDifficulty: StepDifficulty(-1); break;
        }
        Control.RefreshFromSettings();
        SaveSoon();
    }

    private void StepDifficulty(int step)
    {
        var items = PickerItems();
        if (items.Count == 0) return;
        int i = selected == null ? -1 : items.ToList().FindIndex(d => SamePath(d.Path, selected.Path));
        ChooseDifficulty(items[((i + step) % items.Count + items.Count) % items.Count]);
    }

    public IEditorClock Clock => clock;
    public IReadOnlyList<DifficultyInfo> Difficulties => difficulties;
    public DifficultyInfo? Selected => selected;

    // ---- time source ----

    public void SetSource(TimeSourceKind kind)
    {
        Settings.Source = kind;
        if (source is IDisposable d) d.Dispose();
        if (tosu != source) tosu?.Dispose();
        source = null;
        tosu = null;
        memory = null;
        status = SourceStatus.Disconnected;
        Renderer.TruthAt = null;
        ResetClock();

        if (kind is TimeSourceKind.Tosu or TimeSourceKind.Memory)
        {
            // tosu provides the open map and skin either way; with Memory, the time and game state come from osu! directly.
            tosu = new TosuTimeSource(wall);
            tosu.SkinChanged += folder => ui.BeginInvoke(() => LoadSkin(folder));
            tosu.Error += LogSourceError;
            if (kind == TimeSourceKind.Memory)
            {
                memory = new MemoryTimeSource(wall, tosu);
                memory.Error += LogSourceError;
                Attach(memory);
                tosu.Start();
                memory.Start();
                source = memory;
            }
            else
            {
                Attach(tosu);
                tosu.Start();
                source = tosu;
            }
        }
        else if (Renderer.Beatmap is { } map)
        {
            StartDemo(map);
        }
        Control.ShowSource(kind);
    }

    private void Attach(ITimeSource s)
    {
        s.Sample += sample =>
        {
            // Only editor time is meaningful (song select, gameplay and menus also report a time).
            if (!status.InEditor) return;
            clock.AddSample(sample);
            Interlocked.Increment(ref sampleCount);
        };
        s.StatusChanged += st =>
        {
            bool entering = st.InEditor && !status.InEditor;
            status = st;
            if (entering) ResetClock(); // fresh clock state each time the editor opens
        };
        s.BeatmapChanged += info => ui.BeginInvoke(() => OnBeatmapChanged(info));
    }

    private string? lastSourceError;

    /// <summary>
    /// Appends an error the time source recovered from to crash.log (each distinct error once in a row, as a failing
    /// handler would otherwise repeat it ten times a second).
    /// </summary>
    private void LogSourceError(Exception e)
    {
        string text = e.ToString();
        if (Interlocked.Exchange(ref lastSourceError, text) == text) return;
        try
        {
            string log = Path.Combine(Path.GetDirectoryName(SettingsStore.DefaultPath)!, "crash.log");
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            File.AppendAllText(log, $"{DateTime.Now:u} (recovered, tosu message){Environment.NewLine}{text}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException) { }
    }

    private void ResetClock()
    {
        // osu!'s memory is fresh every couple of ms and needs only a light clock; tosu's 10 Hz samples need the full estimator.
        clock = Settings.Source == TimeSourceKind.Memory ? new DirectClock() : new PlaybackClock();
        Renderer.Clock = clock;
        AttachTraceToClock();
    }

    /// <summary>Runs a simulated editor session over the current map, for trying the overlay without osu!.</summary>
    private void StartDemo(OsuBeatmap map)
    {
        var editor = DemoSession.Create(map, DemoLengthMs);
        var mock = new MockTimeSource(editor, new SimulatedTosu(), wall, DemoLengthMs);
        Attach(mock);
        double origin = wall.Elapsed.TotalMilliseconds;
        Renderer.TruthAt = wallMs => editor.SongAt(wallMs - origin);
        mock.Start();
        source = mock;
    }

    // ---- beatmap folder and difficulty ----

    /// <summary>The beatmap osu! currently has open, as reported by the time source.</summary>
    private void OnBeatmapChanged(BeatmapInfo info)
    {
        openBeatmap = info;
        ReadEditorSnap();
        OpenFolder(info.BeatmapFolder);
    }

    private int? editorSnap;
    private double? editorZoom;
    /// <summary>The map open in the editor is osu!standard, so osu! already shows std objects on its timeline.</summary>
    private bool openIsStandard;

    /// <summary>
    /// The beat snap and timeline zoom of the map open in the editor, as saved in its file. tosu doesn't report the
    /// live values, so these update when the map is opened or saved.
    /// </summary>
    private void ReadEditorSnap()
    {
        DifficultyInfo? header = null;
        TimingModel? timing = null;
        if (openBeatmap != null)
        {
            var path = Path.Combine(openBeatmap.BeatmapFolder, openBeatmap.BeatmapFile);
            try
            {
                if (File.Exists(path))
                {
                    header = DifficultyDiscovery.ReadHeader(path);
                    timing = OsuFileParser.ParseFile(path).Timing; // osu! draws its beat ticks from the open map's timing
                }
            }
            catch (IOException) { RestartReloadTimer(); return; } // mid-save; read again shortly
            catch (FormatException) { } // the ticks then follow the std map's timing
        }
        Renderer.EditorTiming = timing is { Sections.Count: > 0 } ? timing : null;
        editorSnap = header?.BeatDivisor;
        editorZoom = header?.TimelineZoom;
        openIsStandard = header?.Mode == 0;
        Renderer.BeatSnap = editorSnap ?? 4; // a map that was never saved has neither; these are osu!'s defaults
        ReadOpenManiaMap(header);
        ApplyStyle();
    }

    private int maniaKeys;

    /// <summary>For the column tint: the open mania map's key count (its CircleSize).</summary>
    private void ReadOpenManiaMap(DifficultyInfo? header)
    {
        maniaKeys = header?.Mode == 3 && header.CircleSize is { } cs ? (int)Math.Round(cs) : 0;
        ManiaTint.Keys = maniaKeys;
        lastManiaBounds = null;
    }

    /// <summary>The colours osu! would give the std map's combos: the map's own, else the skin's, else the defaults.</summary>
    private void UpdateTintColours()
    {
        var map = Renderer.Beatmap;
        ManiaTint.Colours = map == null ? null
            : !map.HasOwnComboColours && Renderer.Skin?.Ini.ComboColours is { Count: > 0 } skinColours ? skinColours
            : map.ComboColours;
    }

    private string? skinFolder;

    /// <summary>Uses the skin osu! is using (null: osu!'s default skin, which we don't have, so plain drawn circles).</summary>
    private void LoadSkin(string? folder)
    {
        skinFolder = folder;
        Renderer.Skin = folder == null ? null : SkinImages.Load(folder); // the renderer disposes the old one
        UpdateTintColours();
    }

    /// <summary>Points the renderer at osu!'s timeline (at the editor's zoom) or at the overlay's own strip.</summary>
    public void ApplyStyle()
    {
        Renderer.EditorView = Settings.Style == TimelineStyle.OsuTimeline ? EditorTimeline.View(editorZoom ?? 1) : null;
        if (Settings.Style == TimelineStyle.OsuTimeline) Overlay.LayoutMode = false; // the position is osu!'s
        lastBounds = null; // re-place the window on the next tick
    }

    private void OpenFolder(string path)
    {
        if (string.Equals(path, folder, StringComparison.OrdinalIgnoreCase)) return;
        folder = path;
        difficulties = DifficultyDiscovery.FindStandardDifficulties(path);
        // A pinned map stays shown when a different beatmap is opened (e.g. the mania map in another folder).
        if (pinned != null) ShowPicker();
        else SelectDifficulty(DifficultyDiscovery.PickDefault(difficulties, Settings.LastDifficulty.GetValueOrDefault(path)));
        UpdateWatchers();
    }

    /// <summary>
    /// Pins the beatmap that is open in the editor right now as the std map, e.g. open the std map, press the button,
    /// then open the mania map. Returns an error message, or null on success.
    /// </summary>
    public string? UseOpenBeatmap()
    {
        if (openBeatmap == null)
            return "tosu hasn't reported an open beatmap yet. Check that tosu is running and a map is open in osu!.";
        var path = Path.Combine(openBeatmap.BeatmapFolder, openBeatmap.BeatmapFile);
        if (!File.Exists(path)) return $"Can't find the open beatmap's file:\n{path}";
        var d = DifficultyDiscovery.ReadHeader(path);
        if (d.Mode != 0) return $"The open difficulty [{d.Version}] is not an osu!standard difficulty.";
        Pin(d);
        return null;
    }

    /// <summary>Pins any .osu file chosen by hand.</summary>
    public void OpenFile(string path) => Pin(DifficultyDiscovery.ReadHeader(path));

    private void Pin(DifficultyInfo d)
    {
        SetPinned(d);
        SelectDifficulty(d);
    }

    private void SetPinned(DifficultyInfo? d)
    {
        pinned = d;
        Settings.PinnedDifficultyPath = d?.Path;
        SaveSoon();
    }

    /// <summary>A choice from the picker. Choosing anything but the pinned map goes back to following the open beatmap's set.</summary>
    public void ChooseDifficulty(DifficultyInfo d)
    {
        if (pinned != null && !SamePath(d.Path, pinned.Path)) SetPinned(null);
        SelectDifficulty(d);
    }

    private void RestartReloadTimer()
    {
        // Debounce: the editor writes a file in several steps.
        reloadTimer.Stop();
        reloadTimer.Start();
    }

    /// <summary>After .osu files change: refresh the list, and re-parse the shown difficulty if it was saved.</summary>
    private void RefreshFolder()
    {
        ReadEditorSnap(); // the open map may have been saved with a new snap
        if (folder != null) difficulties = DifficultyDiscovery.FindStandardDifficulties(folder);
        if (selected == null || !File.Exists(selected.Path))
        {
            if (selected != null && pinned != null && SamePath(selected.Path, pinned.Path)) SetPinned(null); // pinned file was deleted
            if (pinned == null) SelectDifficulty(DifficultyDiscovery.PickDefault(difficulties, null));
            else ShowPicker();
            return;
        }
        if (File.GetLastWriteTimeUtc(selected.Path) != selectedWriteTime)
        {
            var refreshed = DifficultyDiscovery.ReadHeader(selected.Path); // the difficulty name may have changed
            if (pinned != null && SamePath(pinned.Path, refreshed.Path)) SetPinned(refreshed);
            SelectDifficulty(refreshed);
        }
        else ShowPicker();
    }

    private void SelectDifficulty(DifficultyInfo? d)
    {
        selected = d;
        UpdateWatchers();
        if (d == null)
        {
            Renderer.Beatmap = null;
            Renderer.Message = folder == null ? "Open a beatmap in the osu! editor" : "No osu!standard difficulty in this beatmap set";
            ShowPicker();
            return;
        }

        try
        {
            selectedWriteTime = File.GetLastWriteTimeUtc(d.Path);
            var map = OsuFileParser.ParseFile(d.Path);
            Renderer.Beatmap = map;
            UpdateTintColours();
            Renderer.Message = null;
            if (pinned == null && folder != null && SamePath(Path.GetDirectoryName(d.Path), folder))
            {
                Settings.LastDifficulty[folder] = d.Path;
                SaveSoon();
            }
            if (Settings.Source == TimeSourceKind.Demo) SetSource(TimeSourceKind.Demo); // the demo is scripted per map
        }
        catch (IOException)
        {
            RestartReloadTimer(); // still being written; try again shortly
        }
        ShowPicker();
    }

    /// <summary>Picker entries: the open set's std difficulties, plus the pinned map if it lives elsewhere.</summary>
    private void ShowPicker() => Control.ShowDifficulties(PickerItems(), selected, pinned);

    private IReadOnlyList<DifficultyInfo> PickerItems() =>
        pinned == null || difficulties.Any(x => SamePath(x.Path, pinned.Path)) ? difficulties : difficulties.Prepend(pinned).ToList();

    /// <summary>Watches the open set's folder (for the list) and the shown map's folder (for live reload).</summary>
    private void UpdateWatchers()
    {
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (folder != null) dirs.Add(folder);
        if (selected != null && Path.GetDirectoryName(selected.Path) is { } dir) dirs.Add(dir);

        foreach (var stale in watchers.Keys.Where(k => !dirs.Contains(k)).ToList())
        {
            watchers[stale].Dispose();
            watchers.Remove(stale);
        }
        foreach (var d in dirs.Where(d => !watchers.ContainsKey(d) && Directory.Exists(d)))
        {
            var w = new FileSystemWatcher(d, "*.osu") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
            FileSystemEventHandler changed = (_, _) => ui.BeginInvoke(RestartReloadTimer);
            w.Changed += changed;
            w.Created += changed;
            w.Deleted += changed;
            w.Renamed += (_, _) => ui.BeginInvoke(RestartReloadTimer);
            w.EnableRaisingEvents = true;
            watchers[d] = w;
        }
    }

    private static bool SamePath(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // ---- window following and auto-hide ----

    private static (int, int, int, int) InitialBounds(OverlayPlacement placement)
    {
        var tracker = new OsuWindowTracker();
        tracker.Update();
        if (tracker.ClientRect is { } c)
        {
            var b = placement.ToScreen(c, tracker.DpiScale);
            return (b.X, b.Y, b.Width, b.Height);
        }
        return (200, 100, 1200, 110);
    }

    private int followTicks;

    private void FollowOsu()
    {
        osu.Update();
        if (followTicks++ % 10 == 0) UpdateStatusSurfaces();
        SpaceKey.Active = Settings.UseSpaceKey && Settings.Source == TimeSourceKind.Tosu && status.InEditor && osu.OsuInForeground;
        Wheel.Active = Settings.UseScrollWheel && Settings.Source == TimeSourceKind.Tosu && status.InEditor && osu.OsuInForeground;
        double now = wall.Elapsed.TotalMilliseconds;

        bool want = Overlay.LayoutMode
            || (Settings.Enabled && (Settings.Source == TimeSourceKind.Demo
                || (status.InEditor && osu.OsuOrUsInForeground && !openIsStandard)));
        if (want != wantVisible) { wantVisible = want; wantSince = now; }
        // Debounce, so alt-tabbing through osu! doesn't flicker the overlay.
        if (Overlay.Visible != want && (now - wantSince >= AutoHideDebounceMs || Overlay.LayoutMode)) Overlay.Visible = want;

        if (!Overlay.LayoutMode && osu.ClientRect is { } client)
        {
            var b = Settings.Style == TimelineStyle.OsuTimeline
                ? EditorTimeline.StripBounds(client) // exactly over osu!'s own timeline
                : Settings.Placement.ToScreen(client, osu.DpiScale);
            if (b != lastBounds)
            {
                lastBounds = b;
                Overlay.SetBounds(b.X, b.Y, b.Width, b.Height);
            }
        }

        // Mania column tint: with the overlay, while a mania map is open and a std map is chosen.
        bool tint = Overlay.Visible && !Overlay.LayoutMode && Settings.ManiaColumnTint && maniaKeys > 0
            && ManiaTint.Colours != null && osu.ClientRect != null;
        if (ManiaOverlay.Visible != tint) ManiaOverlay.Visible = tint;
        if (tint && osu.ClientRect is { } c2)
        {
            var mb = ManiaPlayfield.Bounds(c2, maniaKeys);
            if (mb != lastManiaBounds)
            {
                lastManiaBounds = mb;
                ManiaOverlay.SetBounds(mb.X, mb.Y, mb.Width, mb.Height);
            }
        }
    }

    private PixelRect? lastManiaBounds;

    /// <summary>The user dragged or resized the overlay in layout mode: remember where, relative to osu!.</summary>
    private void OnUserMovedOverlay((int X, int Y, int Width, int Height) b)
    {
        var rect = new PixelRect(b.X, b.Y, b.Width, b.Height);
        lastBounds = rect;
        if (osu.ClientRect is { } client)
        {
            Settings.Placement = OverlayPlacement.FromScreen(rect, client, osu.DpiScale);
            SaveSoon();
        }
    }

    // ---- status ----

    public enum Health { Ok, Waiting, Problem }

    private long lastTosuCheck = -10_000;
    private bool tosuProcessRunning;
    private string lastSummary = "";

    /// <summary>One plain sentence on what the overlay is doing or what's in its way, plus whether "Start tosu" applies.</summary>
    public (string Text, Health Health, bool OfferStartTosu) Summary()
    {
        if (Environment.TickCount64 - lastTosuCheck >= 1000)
        {
            lastTosuCheck = Environment.TickCount64;
            var procs = Process.GetProcessesByName("tosu");
            tosuProcessRunning = procs.Length > 0;
            // Remember where tosu lives, so "Start tosu" works next time without asking.
            if (procs.Length > 0 && Settings.TosuPath == null)
            {
                try { Settings.TosuPath = procs[0].MainModule?.FileName; SaveSoon(); }
                catch (System.ComponentModel.Win32Exception) { } // e.g. tosu running elevated
            }
            foreach (var p in procs) p.Dispose();
        }

        if (!Settings.Enabled) return ("Overlay switched off (tray menu or hotkey to turn it on).", Health.Waiting, false);
        if (Settings.Source == TimeSourceKind.Demo) return ("Demo mode: simulated editor playback.", Health.Ok, false);
        var st = status;
        if (Settings.Source == TimeSourceKind.Memory)
        {
            if (!osu.OsuRunning) return ("osu! isn't running.", Health.Waiting, false);
            if (memory?.Problem is { } problem) return ($"Can't read the editor's time: {problem}.", Health.Problem, false);
            if (!st.Connected) return ("Reading osu!'s memory…", Health.Waiting, false);
            if (tosu?.Status.Connected != true)
                return tosuProcessRunning
                    ? ("Connecting to tosu (for the open map and skin)…", Health.Waiting, false)
                    : ("tosu isn't running. Start it for the open map and skin.", Health.Problem, true);
        }
        else if (!st.Connected)
            return tosuProcessRunning
                ? ("Connecting to tosu…", Health.Waiting, false)
                : ("tosu isn't running. Start it to read the editor's time.", Health.Problem, true);
        if (!osu.OsuRunning) return ("osu! isn't running.", Health.Waiting, false);
        if (!st.InEditor) return ($"Waiting for the osu! editor (osu! is in: {st.StateName}).", Health.Waiting, false);
        if (openIsStandard) return ("Hidden while an osu!standard map is open: osu! shows its own objects.", Health.Waiting, false);
        if (Renderer.Beatmap == null)
            return ("No osu!standard map chosen. Open one in the editor and press \"Use beatmap open in editor\".", Health.Problem, false);
        if (!osu.OsuOrUsInForeground) return ("Ready (hidden while osu! isn't the focused window).", Health.Ok, false);
        return ("Ready.", Health.Ok, false);
    }

    private void UpdateStatusSurfaces()
    {
        var (text, health, offerTosu) = Summary();
        string key = $"{text}|{Settings.Enabled}|{Overlay.LayoutMode}";
        if (key == lastSummary) return;
        lastSummary = key;
        Tray.Update(text, Settings.Enabled, Overlay.LayoutMode);
        Control.ShowSummary(text, health, offerTosu);
    }

    /// <summary>Starts tosu from the remembered path. Returns false if the path isn't known (the UI then asks for it).</summary>
    public bool StartTosu()
    {
        if (Settings.TosuPath is not { } path || !File.Exists(path)) return false;
        // Without a console window: tosu writes its own log files, and it keeps running after the overlay exits.
        Process.Start(new ProcessStartInfo(path)
        {
            WorkingDirectory = Path.GetDirectoryName(path),
            UseShellExecute = false,
            CreateNoWindow = true,
        })?.Dispose();
        lastTosuCheck = -10_000;
        return true;
    }

    /// <summary>On launch: if the overlay reads from tosu and tosu isn't running, start it (when we know where it is).</summary>
    private void StartTosuIfNeeded()
    {
        if (Settings.Source == TimeSourceKind.Demo) return;
        var running = Process.GetProcessesByName("tosu");
        bool isRunning = running.Length > 0;
        foreach (var p in running) p.Dispose();
        if (!isRunning) StartTosu();
    }

    public string StatusText()
    {
        var sb = new StringBuilder();
        var st = status;
        sb.AppendLine(Settings.Source switch
        {
            TimeSourceKind.Memory => $"source     osu! memory: {(st.Connected ? st.StateName : memory?.Problem ?? "osu! not found")}; " +
                                     $"tosu (map, skin): {(tosu?.Status.Connected == true ? "connected" : "not connected")}",
            TimeSourceKind.Tosu => $"source     tosu: {(st.Connected ? st.StateName : "not connected (is tosu running?)")}",
            _ => "source     demo",
        });
        sb.AppendLine($"osu!       {(osu.OsuRunning ? (osu.ClientRect is { } c ? $"window {c.Width}x{c.Height} at {c.X},{c.Y}" : "minimised") : "not found")}" +
                      $"{(osu.OsuOrUsInForeground ? ", focused" : "")}");
        sb.AppendLine($"overlay    {(Overlay.Visible ? "shown" : "hidden")} on {Overlay.AdapterName}");
        sb.AppendLine($"beat snap  1/{Renderer.BeatSnap}{(editorSnap == null ? " (default: the open map has none saved yet)" : " (saved in the open map)")}");
        sb.AppendLine($"skin       {(Renderer.Skin is { } sk ? System.IO.Path.GetFileName(sk.Folder) + (sk.Has("hitcircle") && sk.Has("hitcircleoverlay") ? "" : " (no hitcircle images: drawn circles)") : skinFolder == null ? "default (drawn circles)" : "not found")}");
        sb.AppendLine($"col. tint  {(!Settings.ManiaColumnTint ? "off" : maniaKeys > 0 ? $"{(ManiaOverlay.Visible ? "shown" : "hidden")}, {maniaKeys}K playfield" : "no mania map open")}");
        if (Settings.Style == TimelineStyle.OsuTimeline)
            sb.AppendLine($"zoom     {editorZoom ?? 1:0.##}{(editorZoom == null ? " (default: the open map has none saved yet)" : " (saved in the open map)")}");

        var c2 = clock;
        var d = c2.Debug;
        double now = wall.Elapsed.TotalMilliseconds;
        if (!c2.HasTime) sb.AppendLine("clock      waiting for editor time");
        else
        {
            sb.AppendLine($"shown      {Renderer.LastShownTime,10:F1} ms");
            sb.AppendLine($"last raw   {d.LastSampleSongMs,10:F0} ms  ({now - d.LastSampleWallMs:F0} ms ago)");
            sb.AppendLine($"state      {(d.Paused ? "paused" : $"playing at {d.Rate:0.###}x")}");
            sb.AppendLine($"seeks      {c2.SeekCount}  (last {(double.IsNaN(d.LastSeekWallMs) ? "-" : $"{(now - d.LastSeekWallMs) / 1000:F1} s ago")})");
            if (Renderer.TruthAt != null && !double.IsNaN(Renderer.LastError)) sb.AppendLine($"demo error {Renderer.LastError,10:+0.00;-0.00} ms");
        }
        sb.AppendLine($"samples    {sampleCount}");
        var f = Renderer.Stats;
        sb.Append($"frames     {f.Fps} fps, max gap {f.MaxFrameGapMs:F1} ms, render {f.AvgRenderMs:F2} / {f.MaxRenderMs:F2} ms");
        return sb.ToString();
    }

    private readonly object traceLock = new();
    private readonly Queue<(double Wall, string Line)> traceRing = new();
    private string? tracePath;

    /// <summary>
    /// Diagnostics: keeps the last few seconds of frames and samples, and after each Space press writes the stretch from
    /// 0.5 s before to 1.5 s after it to <paramref name="path"/>.
    /// </summary>
    public void StartTrace(string path)
    {
        tracePath = path;
        Renderer.FrameTrace = (wallMs, shown) => AddTrace(wallMs, $"frame wall {wallMs:F1} shown {shown:F1}");
        SpaceKey.Pressed += wallMs => Task.Delay(1500).ContinueWith(_ => DumpTrace(wallMs, wallMs, "Space"));
        // Wheel scrolling comes in bursts: write one stretch from just before the first scroll to 1.5 s after the last.
        double burstStart = double.NaN, lastWheel = double.NaN;
        Wheel.Scrolled += wallMs =>
        {
            if (double.IsNaN(lastWheel) || wallMs - lastWheel > 1000) burstStart = wallMs;
            lastWheel = wallMs;
            double start = burstStart, last = wallMs;
            Task.Delay(1500).ContinueWith(_ => { if (last == lastWheel) DumpTrace(start, last, "wheel scrolling"); });
        };
        AttachTraceToClock();
    }

    private void AttachTraceToClock()
    {
        if (tracePath != null) clock.Trace = line => AddTrace(wall.Elapsed.TotalMilliseconds, line);
    }

    private void AddTrace(double wallMs, string line)
    {
        lock (traceLock)
        {
            traceRing.Enqueue((wallMs, line));
            while (traceRing.Count > 0 && wallMs - traceRing.Peek().Wall > 20000) traceRing.Dequeue(); // long enough for a scroll burst
        }
    }

    private void DumpTrace(double fromWall, double toWall, string what)
    {
        List<string> lines;
        lock (traceLock)
            lines = traceRing.Where(e => e.Wall >= fromWall - 500 && e.Wall <= toWall + 1500).Select(e => e.Line).ToList();
        File.AppendAllText(tracePath!, $"=== {what} at {fromWall:F1} (offset {Settings.OffsetMs} ms){Environment.NewLine}" +
            string.Join(Environment.NewLine, lines) + Environment.NewLine);
    }

    private System.Threading.Timer? statsLog;

    /// <summary>Appends one line of frame and clock stats per second to <paramref name="path"/> (for measuring).</summary>
    public void StartStatsLog(string path)
    {
        statsLog = new System.Threading.Timer(_ =>
        {
            var f = Renderer.Stats;
            var d = clock.Debug;
            File.AppendAllText(path,
                $"{wall.Elapsed.TotalSeconds:F1}s gpu '{Overlay.AdapterName}' state {status.StateName} visible {Overlay.Visible} " +
                $"fps {f.Fps} maxgap {f.MaxFrameGapMs:F1} render {f.AvgRenderMs:F2}/{f.MaxRenderMs:F2} " +
                $"shown {Renderer.LastShownTime:F1} raw {d.LastSampleSongMs:F0} rate {d.Rate} paused {d.Paused} seeks {clock.SeekCount} " +
                $"map '{Renderer.Beatmap?.Version}' objects {Renderer.Beatmap?.HitObjects.Count} drawn {Renderer.LastDrawn} " +
                $"view {Renderer.EditorView} skin '{Renderer.Skin?.Folder}' bounds {Overlay.Bounds} " +
                $"mania tint visible {ManiaOverlay.Visible} keys {maniaKeys} bounds {ManiaOverlay.Bounds}{Environment.NewLine}");
        }, null, 1000, 1000);
    }

    public void Dispose()
    {
        SaveNow();
        Hotkeys.Dispose();
        SpaceKey.Dispose();
        Wheel.Dispose();
        Tray.Dispose();
        statsLog?.Dispose();
        followTimer.Stop();
        foreach (var w in watchers.Values) w.Dispose();
        if (source is IDisposable d) d.Dispose();
        if (tosu != source) tosu?.Dispose();
        Overlay.Dispose();
        ManiaOverlay.Dispose();
        ManiaTint.Dispose();
        Renderer.Dispose();
    }
}
