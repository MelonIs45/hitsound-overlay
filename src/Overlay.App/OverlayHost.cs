using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using static Overlay.App.NativeMethods;

namespace Overlay.App;

/// <summary>
/// The overlay window: borderless, always on top, no taskbar entry, click-through except in layout mode.
/// Content is drawn by an <see cref="IOverlayRenderer"/> with Direct2D into a DirectComposition surface, so
/// transparency is handled on the GPU (WPF's transparent windows copy every frame through the CPU and could not keep
/// up with high refresh rates).
/// </summary>
/// <remarks>
/// Two threads: one owns the window and runs its message loop (so dragging or resizing never stalls drawing),
/// the other renders, pacing itself to the desktop compositor's clock so each frame reads the song clock just
/// before it's composed.
/// </remarks>
public sealed class OverlayHost : IDisposable
{
    private const uint WM_APP_LAYOUT = WM_APP + 1, WM_APP_VISIBLE = WM_APP + 2;
    // Each window gets its own class: a class's window procedure belongs to the instance that registered it, so a
    // shared class would send the second window's messages (sizes, hit tests) to the first instance.
    private static int instances;
    private readonly string className = "OsuTimelineOverlay" + Interlocked.Increment(ref instances);

    private readonly IOverlayRenderer renderer;
    private readonly Thread windowThread;
    private Thread? renderThread;
    private readonly ManualResetEventSlim created = new();
    private WndProc? wndProc; // kept alive for the native side
    private volatile bool stopping, layoutMode, visible = true;
    private volatile int pixelWidth, pixelHeight;
    private volatile uint dpi = 96;
    private nint hwnd;

    /// <param name="bounds">Initial position and size in physical pixels.</param>
    public OverlayHost(IOverlayRenderer renderer, (int X, int Y, int Width, int Height) bounds)
    {
        this.renderer = renderer;
        windowThread = new Thread(() => WindowThread(bounds)) { IsBackground = true, Name = "Overlay window" };
        windowThread.SetApartmentState(ApartmentState.STA);
        windowThread.Start();
        created.Wait();
        renderThread = new Thread(RenderThread) { IsBackground = true, Name = "Overlay render", Priority = ThreadPriority.AboveNormal };
        renderThread.Start();
    }

    /// <summary>Raised (on the window thread) after the user moves or resizes the overlay in layout mode.</summary>
    public event Action<(int X, int Y, int Width, int Height)>? BoundsChanged;

    public bool LayoutMode
    {
        get => layoutMode;
        set
        {
            layoutMode = value;
            renderer.ShowLayoutFrame = value;
            PostMessage(hwnd, WM_APP_LAYOUT, value ? 1 : 0, 0);
        }
    }

    public bool Visible
    {
        get => visible;
        set
        {
            visible = value;
            PostMessage(hwnd, WM_APP_VISIBLE, value ? 1 : 0, 0);
        }
    }

    // ---- window thread ----

    private void WindowThread((int X, int Y, int Width, int Height) b)
    {
        wndProc = WindowProc;
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = wndProc,
            hInstance = GetModuleHandle(null),
            lpszClassName = className,
        };
        RegisterClassEx(ref wc);
        hwnd = CreateWindowEx(
            WS_EX_NOREDIRECTIONBITMAP | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED | WS_EX_TRANSPARENT,
            className, "osu! timeline overlay", WS_POPUP, b.X, b.Y, b.Width, b.Height, 0, 0, wc.hInstance, 0);
        // A layered window that is fully opaque at the window level: per-pixel alpha comes from the swap chain,
        // and WS_EX_LAYERED | WS_EX_TRANSPARENT lets clicks fall through to osu!.
        SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
        UpdateSize();
        ShowWindow(hwnd, SW_SHOWNOACTIVATE);
        created.Set();

        while (GetMessage(out var msg, 0, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private void UpdateSize()
    {
        GetClientRect(hwnd, out var rc);
        dpi = Math.Max(96, GetDpiForWindow(hwnd));
        pixelWidth = Math.Max(1, rc.Right - rc.Left);
        pixelHeight = Math.Max(1, rc.Bottom - rc.Top);
    }

    private nint WindowProc(nint h, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
                return layoutMode ? HitTestForLayout(h, lParam) : HTTRANSPARENT;
            case WM_MOUSEACTIVATE:
                return MA_NOACTIVATE;
            case WM_SIZE:
                UpdateSize();
                return 0;
            case WM_EXITSIZEMOVE:
                GetWindowRect(h, out var wr);
                BoundsChanged?.Invoke((wr.Left, wr.Top, wr.Right - wr.Left, wr.Bottom - wr.Top));
                return 0;
            case WM_DPICHANGED:
                var suggested = Marshal.PtrToStructure<RECT>(lParam);
                SetWindowPos(h, 0, suggested.Left, suggested.Top, suggested.Right - suggested.Left,
                    suggested.Bottom - suggested.Top, SWP_NOZORDER | SWP_NOACTIVATE);
                UpdateSize();
                return 0;
            case WM_APP_LAYOUT:
                uint ex = (uint)GetWindowLongPtr(h, GWL_EXSTYLE);
                ex = wParam != 0 ? ex & ~WS_EX_TRANSPARENT : ex | WS_EX_TRANSPARENT;
                SetWindowLongPtr(h, GWL_EXSTYLE, (nint)ex);
                SetWindowPos(h, 0, 0, 0, 0, 0, 0x0001 | 0x0002 | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
                return 0;
            case WM_APP_VISIBLE:
                ShowWindow(h, wParam != 0 ? SW_SHOWNOACTIVATE : SW_HIDE);
                return 0;
            case WM_CLOSE:
                DestroyWindow(h);
                return 0;
            case WM_DESTROY:
                PostQuitMessage(0);
                return 0;
        }
        return DefWindowProc(h, msg, wParam, lParam);
    }

    /// <summary>In layout mode: edges and corners resize, the rest drags.</summary>
    private static nint HitTestForLayout(nint h, nint lParam)
    {
        var p = new POINT { X = (short)(lParam & 0xFFFF), Y = (short)((lParam >> 16) & 0xFFFF) };
        ScreenToClient(h, ref p);
        GetClientRect(h, out var rc);
        int edge = (int)(8 * GetDpiForWindow(h) / 96);
        bool left = p.X < edge, right = p.X >= rc.Right - edge, top = p.Y < edge, bottom = p.Y >= rc.Bottom - edge;
        if (top && left) return HTTOPLEFT;
        if (top && right) return HTTOPRIGHT;
        if (bottom && left) return HTBOTTOMLEFT;
        if (bottom && right) return HTBOTTOMRIGHT;
        if (left) return HTLEFT;
        if (right) return HTRIGHT;
        if (top) return HTTOP;
        if (bottom) return HTBOTTOM;
        return HTCAPTION;
    }

    // ---- render thread ----

    private void RenderThread()
    {
        while (!stopping)
        {
            try
            {
                RenderOnCurrentMonitor();
            }
            catch (SharpGen.Runtime.SharpGenException e)
            {
                // Device lost (driver update, GPU reset, ...): rebuild everything after a moment.
                LogRenderError(e);
                Thread.Sleep(500);
            }
        }
    }

    /// <summary>
    /// Renders with a device on the GPU that drives the monitor the overlay is on, until the overlay moves to a
    /// monitor on another GPU (or the overlay closes). Rendering on that GPU avoids a cross-GPU copy every frame
    /// on laptops with integrated and discrete graphics.
    /// </summary>
    private void RenderOnCurrentMonitor()
    {
        nint monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        using var dxgiFactory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
        using var adapter = AdapterForMonitor(dxgiFactory, monitor);
        long adapterLuid = adapter == null ? 0 : (long)adapter.Description1.Luid.HighPart << 32 | adapter.Description1.Luid.LowPart;
        AdapterName = adapter?.Description1.Description ?? "default";
        var levels = new[] { Vortice.Direct3D.FeatureLevel.Level_11_1, Vortice.Direct3D.FeatureLevel.Level_11_0,
            Vortice.Direct3D.FeatureLevel.Level_10_1, Vortice.Direct3D.FeatureLevel.Level_10_0 };
        D3D11.D3D11CreateDevice(adapter, adapter == null ? DriverType.Hardware : DriverType.Unknown,
            DeviceCreationFlags.BgraSupport, levels, out ID3D11Device? d3dDevice).CheckError();
        using var d3d = d3dDevice!;
        using var dxgiDevice = d3d.QueryInterface<IDXGIDevice>();

        using var d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(Vortice.Direct2D1.FactoryType.SingleThreaded, DebugLevel.None);
        using var d2dDevice = d2dFactory.CreateDevice(dxgiDevice);
        using var dc = d2dDevice.CreateDeviceContext(DeviceContextOptions.None);
        dc.AntialiasMode = AntialiasMode.PerPrimitive;
        dc.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale; // ClearType needs an opaque background

        // Content is a DirectComposition surface that the desktop compositor composites directly; each frame is drawn
        // into it and committed, paced to the compositor's clock (steady at the display rate on the dev laptop).
        using var dcomp = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
        dcomp.CreateTargetForHwnd(hwnd, true, out var target).CheckError();
        using var _target = target;
        using var visual = dcomp.CreateVisual();
        target!.SetRoot(visual);

        int w = 0, h = 0;
        uint surfaceDpi = 0;
        IDCompositionSurface? surface = null;
        try
        {
            for (long frame = 0; !stopping; frame++)
            {
                // Moved to a monitor on a different GPU? Return so the device is rebuilt there.
                if (frame % 30 == 0)
                {
                    nint now = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                    if (now != monitor)
                    {
                        using var newAdapter = AdapterForMonitor(dxgiFactory, now);
                        long luid = newAdapter == null ? 0 : (long)newAdapter.Description1.Luid.HighPart << 32 | newAdapter.Description1.Luid.LowPart;
                        if (luid != adapterLuid) return;
                        monitor = now;
                    }
                }

                if (surface == null || pixelWidth != w || pixelHeight != h || dpi != surfaceDpi)
                {
                    w = pixelWidth; h = pixelHeight; surfaceDpi = dpi;
                    surface?.Dispose();
                    dcomp.CreateSurface((uint)w, (uint)h, Format.B8G8R8A8_UNorm, Vortice.DXGI.AlphaMode.Premultiplied, out surface).CheckError();
                    visual.SetContent(surface);
                }

                if (!visible) Thread.Sleep(50); // nothing on screen; just keep the content current

                // Draw into the surface. It may be part of a larger shared texture, so draw at the given offset and
                // clip to our area (Clear would otherwise wipe neighbouring content).
                float scale = surfaceDpi / 96f;
                using (var dxgiSurface = surface!.BeginDraw<IDXGISurface>(null, out var offset))
                using (var bitmap = dc.CreateBitmapFromDxgiSurface(dxgiSurface, new BitmapProperties1(
                    new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                    surfaceDpi, surfaceDpi, BitmapOptions.Target | BitmapOptions.CannotDraw)))
                {
                    dc.Target = bitmap;
                    dc.Dpi = new Vortice.Mathematics.Size(surfaceDpi, surfaceDpi);
                    dc.BeginDraw();
                    dc.Transform = System.Numerics.Matrix3x2.CreateTranslation(offset.X / scale, offset.Y / scale);
                    dc.PushAxisAlignedClip(new Vortice.Mathematics.Rect(0, 0, w / scale, h / scale), AntialiasMode.Aliased);
                    renderer.Render(dc, w / scale, h / scale);
                    dc.PopAxisAlignedClip();
                    dc.Transform = System.Numerics.Matrix3x2.Identity;
                    var result = dc.EndDraw();
                    dc.Target = null;
                    surface.EndDraw().CheckError();
                    result.CheckError();
                }
                dcomp.Commit().CheckError();

                // Pacing: wait for the compositor's next frame, so each frame reads the clock just before it's composed.
                WaitForCompositorFrame();
            }
        }
        finally
        {
            dc.Target = null;
            surface?.Dispose();
        }
    }

    private static bool compositorClockAvailable = true;

    /// <summary>Blocks until the desktop compositor's next frame (DCompositionWaitForCompositorClock on Windows 11, else DwmFlush).</summary>
    private static void WaitForCompositorFrame()
    {
        if (compositorClockAvailable)
        {
            try { DCompositionWaitForCompositorClock(0, 0, 1000); return; }
            catch (EntryPointNotFoundException) { compositorClockAvailable = false; }
        }
        DwmFlush();
    }
    /// <summary>Moves the overlay (physical pixels) and keeps it above osu!, without activating it.</summary>
    public void SetBounds(int x, int y, int width, int height) =>
        SetWindowPos(hwnd, HWND_TOPMOST, x, y, width, height, SWP_NOACTIVATE);

    public (int X, int Y, int Width, int Height) Bounds
    {
        get
        {
            GetWindowRect(hwnd, out var r);
            return (r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        }
    }

    /// <summary>Render failures go next to the settings (render-errors.log), so problems can be diagnosed.</summary>
    private static void LogRenderError(Exception e)
    {
        Trace.WriteLine($"Overlay render error, recreating device: {e.Message}");
        try
        {
            string dir = Path.GetDirectoryName(Overlay.Core.Settings.SettingsStore.DefaultPath)!;
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "render-errors.log"), $"{DateTime.Now:u} {e}{Environment.NewLine}");
        }
        catch (IOException) { }
    }

    /// <summary>The GPU the overlay renders on (for the status readout).</summary>
    public string AdapterName { get; private set; } = "";

    private static IDXGIAdapter1? AdapterForMonitor(IDXGIFactory2 factory, nint monitor)
    {
        for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
        {
            for (uint j = 0; adapter.EnumOutputs(j, out var output).Success; j++)
            {
                using (output)
                    if (output.Description.Monitor == monitor) return adapter;
            }
            adapter.Dispose();
        }
        return null;
    }

    public void Dispose()
    {
        stopping = true;
        renderThread?.Join(2000);
        if (hwnd != 0) PostMessage(hwnd, WM_CLOSE, 0, 0);
        windowThread.Join(2000);
    }
}
