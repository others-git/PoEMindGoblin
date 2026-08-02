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
