# Captures the osu! editor, then fits the editor timeline's scale (px/ms) and "now" x from the white whole-beat ticks.
# Usage: powershell -File measure.ps1 <name>
param([string]$Name = "capture", [int]$TickRow = 196, [int]$Threshold = 0xE8)

Add-Type -AssemblyName System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class OsuWin {
  [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Ri, B; }
  [StructLayout(LayoutKind.Sequential)] public struct P { public int X, Y; }
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out R r);
  [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref P p);
}
'@

$dir = $PSScriptRoot
$osu = Get-Process osu! | Select-Object -First 1
$rc = New-Object OsuWin+R; [OsuWin]::GetClientRect($osu.MainWindowHandle, [ref]$rc) | Out-Null
$pt = New-Object OsuWin+P; [OsuWin]::ClientToScreen($osu.MainWindowHandle, [ref]$pt) | Out-Null

$state = curl.exe -s -m 5 http://127.0.0.1:24050/json/v2 | ConvertFrom-Json
$now = [double]$state.beatmap.time.live
$file = Join-Path (Join-Path $state.folders.songs $state.directPath.beatmapFolder) (Split-Path $state.directPath.beatmapFile -Leaf)

$bmp = New-Object System.Drawing.Bitmap $rc.Ri, $rc.B
[System.Drawing.Graphics]::FromImage($bmp).CopyFromScreen($pt.X, $pt.Y, 0, 0, $bmp.Size)
$bmp.Save((Join-Path $dir "$Name.png"))

$lines = Get-Content -LiteralPath $file
$zoom = ($lines | Where-Object { $_ -like 'TimelineZoom:*' } | Select-Object -First 1) -replace 'TimelineZoom:\s*', ''
$mode = ($lines | Where-Object { $_ -like 'Mode:*' } | Select-Object -First 1) -replace 'Mode:\s*', ''
$i = [array]::IndexOf($lines, '[TimingPoints]')
$red = $lines[($i + 1)..($lines.Count - 1)] | Where-Object { $_ -match '^-?\d' } | ForEach-Object {
    $p = $_.Split(','); if ($p.Count -lt 7 -or $p[6] -eq '1') { [pscustomobject]@{ t = [double]$p[0]; bl = [double]$p[1] } } } | Select-Object -First 1

"window {0}x{1}  map '{2}' mode {3}  state {4}  time {5}  TimelineZoom {6}" -f $rc.Ri, $rc.B, (Split-Path $file -Leaf), $mode, $state.state.name, $now, $zoom

# White whole-beat ticks on the tick row (circle bodies are grey ~D9, ticks are near-white).
$white = @(); for ($x = 40; $x -lt $bmp.Width - 40; $x++) {
    $c = $bmp.GetPixel($x, $TickRow); if ($c.R -ge $Threshold -and $c.G -ge $Threshold -and $c.B -ge ($Threshold - 0x10)) { $white += $x } }
# Merge adjacent pixels into one tick each.
$ticks = @(); foreach ($x in $white) { if ($ticks.Count -and $x - $ticks[-1][-1] -le 1) { $ticks[-1] += $x } else { $ticks += , @($x) } }
$xs = $ticks | ForEach-Object { ($_ | Measure-Object -Average).Average }

# Rough scale from the median spacing, then assign each tick its beat and least-squares fit.
$gaps = @(); for ($j = 1; $j -lt $xs.Count; $j++) { $gaps += $xs[$j] - $xs[$j - 1] }
$beatPx = ($gaps | Sort-Object)[[int]($gaps.Count / 2)]
$k0 = $beatPx / $red.bl
$pts = foreach ($x in $xs) {
    $n = [math]::Round(($now + ($x - 1014) / $k0 - $red.t) / $red.bl)
    $t = $red.t + $n * $red.bl
    # Skip the "now" marker itself unless it happens to sit on a beat.
    if ([math]::Abs($x - 1014) -lt 3 -and [math]::Abs($x - (1014 + $k0 * ($t - $now))) -gt 2) { continue }
    [pscustomobject]@{ x = $x; t = $t } }
$mx = ($pts | Measure-Object x -Average).Average; $mt = ($pts | Measure-Object t -Average).Average
$sxy = 0; $stt = 0; foreach ($p in $pts) { $sxy += ($p.t - $mt) * ($p.x - $mx); $stt += ($p.t - $mt) * ($p.t - $mt) }
$k = $sxy / $stt; $c = $mx - $k * ($mt - $now)
$res = $pts | ForEach-Object { $_.x - ($c + $k * ($_.t - $now)) }
"ticks {0}, beat {1:F1} px  =>  k = {2:F5} px/ms, now-x = {3:F2}, k/zoom = {4:F5}, max residual {5:F2} px" -f `
    $pts.Count, ($k * $red.bl), $k, $c, ($k / [double]$zoom), (($res | ForEach-Object { [math]::Abs($_) }) | Measure-Object -Maximum).Maximum
"tick x range: {0:F0} .. {1:F0}" -f ($xs | Measure-Object -Minimum).Minimum, ($xs | Measure-Object -Maximum).Maximum
$bmp.Dispose()
