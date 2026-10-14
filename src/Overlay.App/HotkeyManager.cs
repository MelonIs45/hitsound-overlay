using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Overlay.Core.Settings;

namespace Overlay.App;

/// <summary>System-wide hotkeys (RegisterHotKey), delivered through a window's message hook on the UI thread.</summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint hWnd, int id);

    private readonly HwndSource source;
    private readonly Dictionary<int, HotkeyAction> registered = new();

    public event Action<HotkeyAction>? Pressed;

    /// <summary>Hotkeys that could not be registered (bad text, or taken by another program), for the status display.</summary>
    public List<string> Problems { get; } = new();

    public HotkeyManager(Window owner)
    {
        source = HwndSource.FromHwnd(new WindowInteropHelper(owner).EnsureHandle());
        source.AddHook(Hook);
    }

    public void Register(IReadOnlyDictionary<HotkeyAction, string> bindings)
    {
        UnregisterAll();
        Problems.Clear();
        int id = 1;
        foreach (var (action, text) in bindings)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (!TryParse(text, out uint mods, out uint vk)) { Problems.Add($"{action}: can't read \"{text}\""); continue; }
            if (RegisterHotKey(source.Handle, id, mods | MOD_NOREPEAT, vk)) registered[id++] = action;
            else Problems.Add($"{text} ({action}) is already used by another program");
        }
    }

    /// <summary>Parses text like "Ctrl+Alt+Shift+O" using WPF key names (e.g. OemPlus, F5, PageUp).</summary>
    public static bool TryParse(string text, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        Key key = Key.None;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= MOD_CONTROL; break;
                case "alt": modifiers |= MOD_ALT; break;
                case "shift": modifiers |= MOD_SHIFT; break;
                case "win": modifiers |= MOD_WIN; break;
                default:
                    if (key != Key.None || !Enum.TryParse(raw, ignoreCase: true, out key)) return false;
                    break;
            }
        }
        if (key == Key.None) return false;
        virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        return virtualKey != 0;
    }

    private nint Hook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && registered.TryGetValue((int)wParam, out var action))
        {
            handled = true;
            Pressed?.Invoke(action);
        }
        return 0;
    }

    private void UnregisterAll()
    {
        foreach (var id in registered.Keys) UnregisterHotKey(source.Handle, id);
        registered.Clear();
    }

    public void Dispose()
    {
        UnregisterAll();
        source.RemoveHook(Hook);
    }
}
