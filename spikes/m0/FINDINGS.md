# M0 findings: tosu as the editor time source

Recorded 2026-10-09 with tosu v4.26.2, osu! stable, mania map *KAF - BLUE SIGNAL (Melons) [hs]*.
Recording: `run1.csv` (147 s). Probe and analyzer: `TosuProbe/` (`dotnet run --project TosuProbe -- analyze run1.csv`).

## Verdict: GO (with one known limitation)

tosu is good enough for M1–M4. Steady playback at every speed can be predicted accurately. The limitation is
seek latency: a seek can show up to ~100 ms late (about 50 ms on average), because tosu updates at 10 Hz.
If that feels sluggish in M4 testing, M7 (native reader) is the fix.

**User decision (2026-10-09):** 10 Hz updates and the seek delay that comes with them are acceptable. M7 is not planned.

## What tosu provides (WebSocket `ws://127.0.0.1:24050/websocket/v2`)

| Need | Field | Notes |
|---|---|---|
| Editor song time | `beatmap.time.live` (ms, integer) | Valid in the editor; follows play, pause, seek and speed. Also moves in song select (preview audio). |
| In editor? | `state.number == 1` (`"edit"`) | Song select for edit is `4` (`selectEdit`), main menu is `0`. Use for auto-hide. |
| Paused? | `game.paused` | Tracks the editor's play/pause. Useful as a direct pause signal. |
| Focus | `game.focused` | Whether osu! is the foreground window. |
| Beatmap | `folders.songs` + `directPath.beatmapFile` / `directPath.beatmapFolder` | Enough to list the set's `.osu` files. |
| Global offset | `settings.audio.offset.universal` | Might be a useful default for calibration. |
| Playback speed | **not exposed** | `play.mods.rate` is the mods rate (stays 1 in the editor). The rate must be estimated. |

The precise socket (`/websocket/v2/precise`, 10 ms) has no time value in the editor (only `keys`, `hitErrors`, `tourney`).

## Measurements

- **Sample rate:** 9.8 msg/s. tosu clamps `POLL_RATE` to a minimum of **100 ms** (a value of 10 is rejected and logged).
  Message interval in the editor: median 101.9 ms, p95 104.9 ms, max 126.6 ms. No repeated (stale) values while playing.
- **Speed is clearly measurable** from the samples (linear fit over steady playback):

  | Editor speed | Fitted rate | Residual RMS | Residual max |
  |---|---|---|---|
  | 100 % | 1.000 | 9.5 ms | 26 ms |
  | 75 % | 0.750 | 3.9 ms | 17 ms |
  | 50 % | 0.500 | 3.5 ms | 20 ms |
  | 25 % | 0.250 | 1.1 ms | 4 ms |

  The residual grows with the speed, so it comes from ~10 ms jitter in *when* tosu reads and sends each value,
  not from the song clock itself. Each sample is individually noisy (±10–25 ms at 100 %), so the clock must not
  snap to every sample. With the rate snapped to a known editor speed, only the phase is unknown, and averaging
  over ~3 s (~30 samples) should bring the error under ~2 ms.
- **Seeks:** every seek type showed up (timeline click, wheel, arrow keys, while paused and while playing).
  Wheel and arrow seeks step by the beat-snap divisor (~83 ms here), so they can be smaller than the per-sample noise
  at 100 %. Seek detection should therefore also use `game.paused`: while paused, any change in `live` is a seek.
  Scrubbing while playing produces long runs of jumps at every sample.
- **Pause/resume:** on pause, osu! sometimes moves the time back a few ms (e.g. 22696 → 22690). This is harmless:
  while paused the overlay just shows the exact sample.
- **Latency (constant part):** this test can't measure it. It needs a visual check against the playhead in M4.
  The user calibration offset covers it.

## Implications for M2 (PlaybackClock)

1. Rate: fit over a sliding window, snap to {0.25, 0.5, 0.75, 1.0} (the user's default set). Don't trust a single pair of samples.
2. Phase: with the rate snapped, estimate the offset as the mean residual over the last ~2–3 s, and correct it gradually per frame.
3. Seek threshold while playing: about 40 ms + 3× the expected jitter at that rate (~70 ms at 100 %). While paused, snap on any change.
4. Use `game.paused` as an authoritative pause flag, and the first non-paused sample as the start of a new segment.
5. Simulation in M2 should add ~10 ms of uniform wall-clock jitter per sample at 10 Hz to match reality.

## Setup notes

- Run exactly one tosu. A second instance fails with `EADDRINUSE` on port 24050 and silently serves nothing.
- `tosu.env` is watched and reloaded live. Don't edit it with PowerShell `Set-Content` while tosu is running: it
  emptied the file once (since restored). Write a temp file and move it into place instead.
