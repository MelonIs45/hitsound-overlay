using System.Runtime.InteropServices;

namespace Overlay.App;

internal static class NativeMethods
{
    public const int GWL_EXSTYLE = -20;
    public const uint WS_POPUP = 0x80000000;
    public const uint WS_EX_TRANSPARENT = 0x00000020;
    public const uint WS_EX_TOPMOST = 0x00000008;
    public const uint WS_EX_TOOLWINDOW = 0x00000080;
    public const uint WS_EX_LAYERED = 0x00080000;
    public const uint WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
    public const uint WS_EX_NOACTIVATE = 0x08000000;
    public const uint LWA_ALPHA = 0x2;

    public const uint WM_DESTROY = 0x0002, WM_SIZE = 0x0005, WM_CLOSE = 0x0010, WM_MOUSEACTIVATE = 0x0021;
    public const uint WM_NCHITTEST = 0x0084, WM_EXITSIZEMOVE = 0x0232, WM_DPICHANGED = 0x02E0, WM_APP = 0x8000;
    public const int MA_NOACTIVATE = 3;
    public const int HTTRANSPARENT = -1, HTCAPTION = 2, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13,
        HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

    public const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
    public const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_FRAMECHANGED = 0x0020;

    public delegate nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int ptX, ptY;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateWindowEx(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern nint DefWindowProc(nint hWnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetMessage(out MSG msg, nint hWnd, uint min, uint max);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern nint DispatchMessage(ref MSG msg);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] public static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] public static extern bool DestroyWindow(nint hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(nint hWnd, int cmd);
    [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(nint hWnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(nint hWnd);
    [DllImport("user32.dll")] public static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] public static extern bool GetClientRect(nint hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(nint hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool ScreenToClient(nint hWnd, ref POINT p);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(nint hWnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern nint GetWindowLongPtr(nint hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern nint SetWindowLongPtr(nint hWnd, int index, nint value);
    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")] public static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] public static extern nint MonitorFromPoint(POINT pt, uint flags);
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);

    /// <summary>Waits for the next compositor frame (Windows 11+; throws EntryPointNotFoundException on older systems).</summary>
    [DllImport("dcomp.dll")] public static extern uint DCompositionWaitForCompositorClock(uint count, nint handles, uint timeoutInMs);
    [DllImport("dwmapi.dll")] public static extern int DwmFlush();

    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(nint hWnd, ref POINT p);
    [DllImport("user32.dll")] public static extern bool IsIconic(nint hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindow(nint hWnd);
    public static readonly nint HWND_TOPMOST = -1;

    public const uint MONITOR_DEFAULTTOPRIMARY = 1;
    public const uint MONITOR_DEFAULTTONEAREST = 2;
    [DllImport("user32.dll")] public static extern nint MonitorFromWindow(nint hWnd, uint flags);
    [DllImport("kernel32.dll")] public static extern nint GetModuleHandle(string? name);
    [DllImport("kernel32.dll")] public static extern uint WaitForSingleObjectEx(nint handle, uint ms, bool alertable);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X, Y;
    }

    /// <summary>Raises the system timer resolution so the mock source's sample timing is close to real.</summary>
    [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint period);
}
