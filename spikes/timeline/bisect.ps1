# Runs the overlay with the given debug switches, focuses osu!, and counts how much of the strip has drawn objects.
param([string]$Name, [string]$NoSkin = "0", [string]$NoFade = "0")

Get-Process OsuTimelineOverlay -ErrorAction SilentlyContinue | Stop-Process -Force -Confirm:$false
Start-Sleep -Milliseconds 800
$env:OVERLAY_DBG_NOSKIN = $NoSkin
$env:OVERLAY_DBG_NOFADE = $NoFade
$exe = (Resolve-Path "$PSScriptRoot\..\..\src\Overlay.App\bin\Release\net8.0-windows\OsuTimelineOverlay.exe").Path
Start-Process -FilePath $exe | Out-Null
$env:OVERLAY_DBG_NOSKIN = $null; $env:OVERLAY_DBG_NOFADE = $null
Start-Sleep 3

Add-Type @'
using System; using System.Runtime.InteropServices;
public static class Bisect { [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h); [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra); }
'@
$osu = (Get-Process osu!).MainWindowHandle
[Bisect]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero); [Bisect]::SetForegroundWindow($osu) | Out-Null; [Bisect]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 800

Add-Type -AssemblyName System.Drawing
$counts = foreach ($i in 1..5) {
    $bmp = New-Object System.Drawing.Bitmap 1911, 104
    [System.Drawing.Graphics]::FromImage($bmp).CopyFromScreen(59, 97, 0, 0, $bmp.Size)
    # Columns (every 10 px) where the lane row differs strongly from the row just above the strip's content.
    $cols = 0; for ($x = 0; $x -lt 1911; $x += 10) { $c = $bmp.GetPixel($x, 59); $b = $bmp.GetPixel($x, 2); if ([math]::Abs(($c.R + $c.G + $c.B) - ($b.R + $b.G + $b.B)) -gt 90) { $cols++ } }
    if ($i -eq 3) { $bmp.Save("$PSScriptRoot\bisect-$Name.png") }
    $bmp.Dispose(); $cols; Start-Sleep -Milliseconds 150
}
"{0,-12} noskin={1} nofade={2}: columns with objects per shot: {3}" -f $Name, $NoSkin, $NoFade, ($counts -join ' ')
