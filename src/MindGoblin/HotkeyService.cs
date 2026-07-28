using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace MindGoblin;

/// <summary>
/// System-wide hotkey via Win32 RegisterHotKey.
///
/// It has to be system-wide, not a WPF InputBinding: the whole point is to fire while
/// Path of Exile has focus, and a WPF binding only works when our window is active.
///
/// One press fires one call. That is deliberate and is the line GGG's third-party policy
/// draws -- an overlay that turns a keypress into a single server-side action is fine,
/// anything that acts without a press is not. Nothing in this class is ever invoked on a
/// timer or from a search result.
///
/// Caveat worth knowing: if Path of Exile runs elevated and this app does not, Windows
/// blocks the hotkey while the game has focus (UIPI). Both must run at the same level.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;

    [Flags]
    public enum Mod : uint
    {
        None = 0, Alt = 0x1, Control = 0x2, Shift = 0x4, Win = 0x8,
        NoRepeat = 0x4000,
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _handlers = new();
    private int _nextId = 1;
    private bool _disposed;

    public HotkeyService(Window window)
    {
        var helper = new WindowInteropHelper(window);
        if (helper.Handle == IntPtr.Zero) helper.EnsureHandle();
        _source = HwndSource.FromHwnd(helper.Handle)
                  ?? throw new InvalidOperationException("could not obtain a window handle for hotkeys");
        _source.AddHook(WndProc);
    }

    /// <summary>Registers a hotkey. Returns an id for <see cref="Unregister"/>, or null if taken.</summary>
    public int? Register(Key key, Mod modifiers, Action onPressed)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var id = _nextId++;
        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        // NoRepeat stops key-repeat from firing a burst of requests when held down --
        // without it, leaning on the key would look exactly like automation to GGG.
        if (!RegisterHotKey(_source.Handle, id, (uint)(modifiers | Mod.NoRepeat), vk))
            return null; // almost always: another app owns this combination
        _handlers[id] = onPressed;
        return id;
    }

    public void Unregister(int id)
    {
        if (_handlers.Remove(id)) UnregisterHotKey(_source.Handle, id);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _handlers.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            // Never let a handler exception kill the message pump.
            try { action(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"hotkey handler threw: {ex}"); }
        }
        return IntPtr.Zero;
    }

    public static string Describe(Key key, Mod modifiers)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(Mod.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(Mod.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(Mod.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(Mod.Win)) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var id in _handlers.Keys.ToList()) UnregisterHotKey(_source.Handle, id);
        _handlers.Clear();
        _source.RemoveHook(WndProc);
    }
}
