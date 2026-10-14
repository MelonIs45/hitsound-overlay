using System.Diagnostics;
using Overlay.Core.Timeline;
using static Overlay.App.NativeMethods;

namespace Overlay.App;

/// <summary>Finds the osu! window and reports its client area, DPI and whether it (or this app) has focus.</summary>
public sealed class OsuWindowTracker
{
    private readonly uint ownProcessId = (uint)Environment.ProcessId;
    private nint hwnd;
    private uint osuProcessId;
    private long lastSearch = -10_000; // so the first Update searches straight away

    /// <summary>Client area in physical screen pixels, or null if osu! isn't running or is minimised.</summary>
    public PixelRect? ClientRect { get; private set; }
    public double DpiScale { get; private set; } = 1;
    /// <summary>osu! is the foreground window, or one of this app's windows is (so adjusting settings doesn't hide the overlay).</summary>
    public bool OsuOrUsInForeground { get; private set; }
    public bool OsuRunning => hwnd != 0;

    public void Update()
    {
        if (hwnd == 0 || !IsWindow(hwnd))
        {
            hwnd = 0;
            // Process lookup is relatively expensive; retry once a second.
            if (Environment.TickCount64 - lastSearch >= 1000)
            {
                lastSearch = Environment.TickCount64;
                foreach (var p in Process.GetProcessesByName("osu!"))
                {
                    using (p)
                        if (p.MainWindowHandle != 0) { hwnd = p.MainWindowHandle; osuProcessId = (uint)p.Id; break; }
                }
            }
        }

        if (hwnd == 0 || IsIconic(hwnd))
        {
            ClientRect = null;
            OsuOrUsInForeground = false;
            return;
        }

        GetClientRect(hwnd, out var rc);
        var origin = new POINT();
        ClientToScreen(hwnd, ref origin);
        ClientRect = new PixelRect(origin.X, origin.Y, rc.Right - rc.Left, rc.Bottom - rc.Top);
        DpiScale = Math.Max(96, GetDpiForWindow(hwnd)) / 96.0;

        GetWindowThreadProcessId(GetForegroundWindow(), out uint fgProcess);
        OsuOrUsInForeground = fgProcess == osuProcessId || fgProcess == ownProcessId;
    }
}
