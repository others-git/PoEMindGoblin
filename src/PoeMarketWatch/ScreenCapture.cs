using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PoeMarketWatch.Core.Voyage;

namespace PoeMarketWatch;

/// <summary>
/// Grabs the screen so the board can be read.
///
/// Read-only by construction. This takes pictures and nothing else -- there is no mouse
/// movement, no key injection, no window manipulation of the game. Synthetic input to the
/// client is the line that gets accounts banned, and a screenshot is on the safe side of
/// it in a way that automated hovering is not.
///
/// Pixels are read from a locked buffer rather than GetPixel: a 2560x1440 sweep through
/// GetPixel takes seconds, which would make reading the board feel broken.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ScreenCapture
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    // Win32 rather than WinForms: referencing WinForms alongside WPF makes UserControl
    // and Application ambiguous across the whole project, which is a heavy price for
    // two integers.
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;

    /// <summary>Size of the primary screen, for turning fractional layouts into pixels.</summary>
    public static Rectangle PrimaryScreenBounds() =>
        new(0, 0, GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN));

    /// <summary>Capture the whole primary screen.</summary>
    public static Bitmap CapturePrimaryScreen() => CaptureRegion(PrimaryScreenBounds());

    public static Bitmap CaptureRegion(Rectangle bounds)
    {
        var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        return bmp;
    }

    /// <summary>Bounds of the foreground window, for a windowed game.</summary>
    public static Rectangle? ForegroundWindowBounds()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return null;
        return new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }
}

/// <summary>Fast pixel access over a captured bitmap.</summary>
[SupportedOSPlatform("windows")]
public sealed class BitmapPixels : IPixels, IDisposable
{
    private readonly Bitmap _bmp;
    private readonly int[] _argb;

    public BitmapPixels(Bitmap bmp)
    {
        _bmp = bmp;
        Width = bmp.Width;
        Height = bmp.Height;
        _argb = new int[Width * Height];

        var data = bmp.LockBits(new Rectangle(0, 0, Width, Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            Marshal.Copy(data.Scan0, _argb, 0, _argb.Length);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    public int Width { get; }
    public int Height { get; }

    public (int R, int G, int B) At(int x, int y)
    {
        var p = _argb[y * Width + x];
        return ((p >> 16) & 0xFF, (p >> 8) & 0xFF, p & 0xFF);
    }

    public void Dispose() => _bmp.Dispose();
}
