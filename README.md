# osu! timeline overlay

Shows an osu!standard difficulty's hit objects as a timeline strip on top of the osu! editor, locked to the
editor's song position, so you can map an osu!mania difficulty against the std map.

- Reads the editor's time straight from osu!'s memory, about every millisecond, so it follows play, pauses, seeks and
  scrolling within a frame or two, at every editor speed (25 / 50 / 75 / 100 %).
- Pick any std difficulty of the open set, or pin a std map from another folder.
- Re-reads the std map when you save it.
- Follows the osu! window; hides when you leave the editor or switch to another program.

## Requirements

- Windows 10/11, **osu! stable** (lazer isn't supported).
- [tosu](https://github.com/tosuapp/tosu) running, for the open beatmap and your skin (from its local WebSocket,
  `ws://127.0.0.1:24050`). Run only one copy of tosu. The editor's time and osu!'s state are read from osu!'s memory
  directly (read-only); under **Time source** you can switch back to taking everything from tosu.
- .NET 8 SDK to build.

## Use

1. Start osu! and `OsuTimelineOverlay.exe`. If tosu isn't running, the overlay starts it (without a console window)
   once it knows where tosu is: it remembers the path whenever it sees tosu running, or asks via **Start tosu**.
   A tosu started this way keeps running after the overlay exits; stop it in Task Manager if needed. To stop tosu
   opening its web dashboard in the browser, set `OPEN_DASHBOARD_ON_STARTUP=false` in its `tosu.env`. It lives in the notification area; left-click the icon for settings.
2. Open a beatmap in the editor. The **Std difficulty** list shows the set's osu!standard difficulties.
   - Std map in a different folder? Open it in the editor, press **Use beatmap open in editor** (it gets a 📌),
     then open your mania map. The pin stays until you choose another difficulty.
3. By default the std objects are drawn onto osu!'s own editor timeline, where the std editor would put them.
   Their spacing uses the **timeline zoom** of the map you're editing, and the strip style's grid uses its **beat snap**.
   tosu doesn't report either live, so both come from the map file: **after zooming or changing the snap, press
   Ctrl+S** (until then the objects are spaced for the old zoom and drift away from osu!'s ticks).
   Objects are drawn with your current skin's images (from tosu: `hitcircle`, `hitcircleoverlay` and the number font,
   an unnumbered hit circle at slider ends, `reversearrow` on repeats, and the skin's combo colours if the map has
   none), with slider bodies as tall as the hit circle; they fade out towards the timeline's
   ends like osu!'s own, and are hidden while an osu!standard map is open (osu! draws those itself).
   osu!'s beat ticks are drawn over the circles, slider and spinner bodies, as in the std editor (they follow the open
   map's timing and saved snap). Slider repeats get an unnumbered hit circle under the reverse arrow.
   The ½ and ¼ tick colours are measured; ⅓, ⅙, ⅛, 1/12 and 1/16 are approximate.
4. **Mania column tint:** the mania playfield's columns get a faint overlay in the std map's combo colours, stepping to
   the next colour at the columns listed under *Mania playfield* (default 3, 5, 7, 11, 15, 17: columns 1–2, 3–4, 5–6,
   7–10, 11–14, 15–16 and 17–18 each get their own colour). The playfield position was measured on an 18-key map at
   2560×1441; other key counts assume the same column width, centred (unverified).
5. Turn on **Layout mode** to drag and resize the strip; its position is kept relative to the osu! window.
6. **Calibrate the offset once**: play at 100 % and nudge the offset until the objects stay put when you pause (or
   line up with osu!'s ticks while playing). The offset is in ms of real time, so it stays right at the slower speeds too.
7. **Space and the mouse wheel** (only with the tosu time source; both on by default, each with a checkbox): while osu!'s editor has focus the overlay
   notices Space (play/pause) and plain wheel scrolls (seeks), so it starts, stops and follows seeks at once instead of
   waiting for tosu's next report. Space is read by polling just that key; the wheel through Windows raw input; neither
   is a hook, nothing is blocked, and nothing is read while osu! isn't focused.

The status banner at the top of the settings window says what the overlay is waiting for (tosu, osu!, the editor, a map).
Expand **Details** for the live clock readout.

### Hotkeys

| Keys | Action |
|---|---|
| Ctrl+Alt+O | overlay on / off |
| Ctrl+Alt+L | layout mode |
| Ctrl+Alt+= / Ctrl+Alt+- | zoom in / out |
| Ctrl+Alt+] / Ctrl+Alt+[ | offset +1 / −1 ms (add Shift for ±5 ms) |
| Ctrl+Alt+. / Ctrl+Alt+, | next / previous std difficulty |

Change them in `settings.json` (uses WPF key names, e.g. `"Ctrl+Shift+F5"`; an empty string disables one) and restart.

### Files

`%AppData%\OsuTimelineOverlay\settings.json` holds all settings. If it can't be read, it's kept as `settings.json.bad`
and defaults are used. Unexpected errors are written to `crash.log` in the same folder.

## How it stays in sync

By default the overlay reads osu!'s song time and game state from its memory, read-only, finding them with the same
byte signatures tosu uses. osu! updates the time every frame (about every 2 ms in the editor), and the overlay reads it
about every millisecond. `DirectClock` shows that value exactly while paused or after a seek, and while playing
follows a line fitted over the last half second (speed snapped to the editor speeds), so motion is smooth between
osu!'s updates; a pause shows within ~25 ms. It never writes to osu!'s memory.

With the tosu time source instead: tosu reports the editor's time about 10 times a second (its minimum poll interval is 100 ms), with 0–40 ms of delivery
jitter that comes in bursts. `PlaybackClock` predicts the position for every frame: it fits the playback speed and snaps
it to the editor speeds, takes the phase from the least-delayed samples, and treats a sample far from the prediction as
a seek. Measured on recorded tosu data, the predicted position wanders by under 1 ms at 100 % speed. Seeks show up
within about 0.1 s. Details: [spikes/m0/FINDINGS.md](spikes/m0/FINDINGS.md).

Some editor actions look alike in tosu's data, so the clock uses what tosu does report: a forward jump from a
standstill is held until the next sample shows playback, and forward moves within 0.45 s of a "paused" report are
seeks (tosu reports paused on the standstills between scroll steps, never during playback). With Space, playback
starts at once and stops 36 ms after the key, when osu! actually stops.

The strip is drawn with Direct2D through DirectComposition (GPU-composited transparency), on the GPU that drives
osu!'s monitor, at the display's refresh rate.

## Build and test

```
dotnet build -c Release
dotnet test
```

Run `src/Overlay.App/bin/Release/net8.0-windows/OsuTimelineOverlay.exe`. Options: a `.osu` path to show, `--demo` for
a simulated editor (no osu! needed), `--log <file>` to append frame and clock stats every second, `--trace <file>` to
record every frame and tosu sample around each Space press and wheel-scroll burst.

| Project | Contents |
|---|---|
| `src/Overlay.Core` | .osu parser and timing model, `PlaybackClock`, timeline layout and beat grid, settings |
| `src/Overlay.Sources` | osu! memory reader; tosu WebSocket source; simulated editor and tosu for tests and the demo |
| `src/Overlay.App` | Win32/Direct2D overlay window, WPF settings window, tray icon, hotkeys |
| `tests/Overlay.Tests` | parser checked against osu!lazer's decoder, clock simulations, and a replay of a real tosu recording (not included in the repo; those tests skip without `TestData/tosu-run1.csv`, which `spikes/m0/TosuProbe` can record) |
| `tools/LazerOracle` | regenerates the parser's reference files with osu!lazer's decoder |
| `spikes/m0` | the tosu feasibility probe and its findings |
| `spikes/memory` | probe that finds osu!'s time and state in its memory and compares them with tosu |

## Note

The overlay only reads data: the editor's time and state come from tosu (which reads osu!'s memory, read-only), and
beatmaps are read from disk. It never writes to osu! or its files, and it's meant for the editor only.

## License

MIT, see [LICENSE](LICENSE).
