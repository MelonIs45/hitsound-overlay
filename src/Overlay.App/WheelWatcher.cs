using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Overlay.App;

/// <summary>
/// Notices mouse-wheel scrolls (which seek osu!'s editor timeline) so the overlay can follow even seeks too small to
/// tell from tosu's timing jitter. Uses Windows raw input, registered only while <see cref="Active"/> (osu!'s editor has
/// focus) and removed otherwise; it isn't a hook, nothing is blocked, and only the wheel flag is looked at. Scrolls
/// with Ctrl, Alt or Shift held are ignored (in the editor those change snap, zoom or volume rather than seek).
/// </summary>
public sealed class WheelWatcher : IDisposable
{
    private const int WM_INPUT = 0x00FF;
    private const uint RIDEV_INPUTSINK = 0x00000100, RIDEV_REMOVE = 0x00000001;
    private const uint RID_INPUT = 0x10000003, RIM_TYPEMOUSE = 0;
    private const ushort RI_MOUSE_WHEEL = 0x0400;
    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public nint Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint Type;
        public uint Size;
        public nint Device;
        public nint WParam;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(nint rawInput, uint command, nint data, ref uint size, uint headerSize);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);

    private readonly Stopwatch wall;
    private readonly HwndSource source;
    private bool registered;
    private nint buffer;
    private uint bufferSize;

    /// <summary>Raised (on the UI thread) with the wall time of a plain wheel scroll.</summary>
    public event Action<double>? Scrolled;

    public WheelWatcher(Window owner, Stopwatch wall)
    {
        this.wall = wall;
        source = HwndSource.FromHwnd(new WindowInteropHelper(owner).EnsureHandle());
        source.AddHook(Hook);
    }

    /// <summary>Whether to listen: set (from the UI thread) while osu!'s editor has focus.</summary>
    public bool Active
    {
        get => registered;
        set
        {
            if (value == registered) return;
            var device = new RAWINPUTDEVICE
            {
                UsagePage = 0x01, // generic desktop
                Usage = 0x02,     // mouse
                Flags = value ? RIDEV_INPUTSINK : RIDEV_REMOVE,
                Target = value ? source.Handle : 0,
            };
            if (RegisterRawInputDevices(new[] { device }, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>())) registered = value;
        }
    }

    private nint Hook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg != WM_INPUT || !registered) return 0;
        uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        uint size = 0;
        GetRawInputData(lParam, RID_INPUT, 0, ref size, headerSize);
        if (size == 0) return 0;
        if (size > bufferSize)
        {
            if (buffer != 0) Marshal.FreeHGlobal(buffer);
            buffer = Marshal.AllocHGlobal((int)size);
            bufferSize = size;
        }
        if (GetRawInputData(lParam, RID_INPUT, buffer, ref size, headerSize) != size) return 0;
        var header = Marshal.PtrToStructure<RAWINPUTHEADER>(buffer);
        if (header.Type != RIM_TYPEMOUSE) return 0;
        // RAWMOUSE: usFlags (2 bytes), padding (2), then usButtonFlags.
        ushort buttonFlags = (ushort)Marshal.ReadInt16(buffer, (int)headerSize + 4);
        if ((buttonFlags & RI_MOUSE_WHEEL) == 0) return 0;
        if (Down(VK_CONTROL) || Down(VK_MENU) || Down(VK_SHIFT)) return 0;
        Scrolled?.Invoke(wall.Elapsed.TotalMilliseconds);
        return 0;
    }

    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public void Dispose()
    {
        Active = false;
        source.RemoveHook(Hook);
        if (buffer != 0) Marshal.FreeHGlobal(buffer);
        buffer = 0;
    }
}
