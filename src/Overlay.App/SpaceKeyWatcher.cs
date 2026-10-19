using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Overlay.App;

/// <summary>
/// Notices presses of Space (the editor's play/pause key) so the overlay can start or stop at once, rather than when the
/// time source next reports. It only reads Space's up/down state, polled about once a millisecond, and only while
/// <see cref="Active"/> (osu!'s editor has focus); there's no keyboard hook, nothing else is read, and osu! still
/// receives the key as usual.
/// </summary>
public sealed class SpaceKeyWatcher : IDisposable
{
    private const int VK_SPACE = 0x20, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);

    private readonly Stopwatch wall;
    private readonly Thread thread;
    private volatile bool stopping;

    /// <summary>Raised (on a background thread) with the wall time of a Space press.</summary>
    public event Action<double>? Pressed;

    /// <summary>Whether to watch at all: set while osu!'s editor is the focused window.</summary>
    public volatile bool Active;

    public SpaceKeyWatcher(Stopwatch wall)
    {
        this.wall = wall;
        thread = new Thread(Run) { IsBackground = true, Name = "Space key watcher", Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private void Run()
    {
        bool wasDown = true; // don't treat a key already held when watching starts as a press
        while (!stopping)
        {
            if (!Active)
            {
                wasDown = true;
                Thread.Sleep(50);
                continue;
            }
            bool down = Down(VK_SPACE);
            // Plain Space only: with Ctrl/Alt/Shift held it's some other shortcut.
            if (down && !wasDown && !Down(VK_CONTROL) && !Down(VK_MENU) && !Down(VK_SHIFT))
                Pressed?.Invoke(wall.Elapsed.TotalMilliseconds);
            wasDown = down;
            Thread.Sleep(1); // ~1 ms with the 1 ms timer resolution the app sets
        }
    }

    public void Dispose()
    {
        stopping = true;
        thread.Join(500);
    }
}
