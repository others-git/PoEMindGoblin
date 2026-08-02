using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MindGoblin;

/// <summary>
/// Finds the Path of Exile window and asks it how big it is.
///
/// This is what makes the calibration portable. Every coordinate the app has is measured
/// against one resolution and rescaled to the game's; before this it rescaled to the
/// PRIMARY SCREEN, which is the same number only when the game is fullscreen on the
/// primary monitor. Windowed, on a second monitor, or at a non-native resolution, the
/// reader looked in the wrong place and reported an empty panel.
///
/// The CLIENT area, not the window: the border and title bar of a windowed game are not
/// part of what it draws, and including them shifts every coordinate by their thickness.
/// Read-only -- it asks Windows for a rectangle and nothing else.
/// </summary>
[SupportedOSPlatform("windows")]
public static class GameWindow
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

    private const uint GA_ROOT = 2;

    /// <summary>
    /// Is something DRAWN OVER the game?
    ///
    /// Capturing is SCREEN scraping: CopyFromScreen copies whatever is at those
    /// coordinates, so a window in front of a windowed game is captured instead of it
    /// and the reader decodes that. A code editor's green diff highlighting decoded as
    /// eighteen Crossings -- the panel is not there and every pixel says something is.
    ///
    /// Asking whether the game is FOREGROUND was the obvious test and a useless one:
    /// the user presses Identify inside this app, so this app is in front every single
    /// time and the warning fired always. What matters is not which window has focus but
    /// what is on top of the PIXELS about to be copied -- so ask the desktop directly, at
    /// points spread across the game's own client area. One sample coming back as the
    /// game is enough; a second monitor showing this app covers nothing.
    /// </summary>
    public static bool IsCovered()
    {
        if (Handle() is not { } hwnd) return false;          // not running: not this problem
        if (!GetClientRect(hwnd, out var rect)) return false;

        var origin = new POINT { X = rect.Left, Y = rect.Top };
        if (!ClientToScreen(hwnd, ref origin)) return false;
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return false;

        foreach (var (fx, fy) in new[] { (0.5, 0.5), (0.8, 0.3), (0.8, 0.7), (0.2, 0.5) })
        {
            var point = new POINT
            {
                X = origin.X + (int)(width * fx),
                Y = origin.Y + (int)(height * fy),
            };
            if (GetAncestor(WindowFromPoint(point), GA_ROOT) == hwnd) return false;
        }
        return true;
    }

    /// <summary>The game's main window, or null when it is not running.</summary>
    private static IntPtr? Handle()
    {
        foreach (var process in Process.GetProcesses())
            using (process)
                if (MindGoblin.Core.Voyage.GameProcess.IsGame(process.ProcessName)
                    && process.MainWindowHandle != IntPtr.Zero)
                    return process.MainWindowHandle;
        return null;
    }

    /// <summary>
    /// The game's client rectangle in SCREEN coordinates, or null when the game is not
    /// running -- which is not an error worth shouting about, only a reason to fall back.
    /// </summary>
    public static Rectangle? ClientBounds()
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (!MindGoblin.Core.Voyage.GameProcess.IsGame(process.ProcessName)) continue;

                var hwnd = process.MainWindowHandle;
                if (hwnd == IntPtr.Zero) continue;                 // minimised or headless
                if (!GetClientRect(hwnd, out var rect)) continue;

                var width = rect.Right - rect.Left;
                var height = rect.Bottom - rect.Top;
                // A minimised window reports a zero client area, and dividing the
                // calibration by it would scale every coordinate to infinity.
                if (width <= 0 || height <= 0) continue;

                var origin = new POINT { X = rect.Left, Y = rect.Top };
                if (!ClientToScreen(hwnd, ref origin)) continue;

                return new Rectangle(origin.X, origin.Y, width, height);
            }
        }
        return null;
    }
}
