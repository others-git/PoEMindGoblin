using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MindGoblin.Core.Voyage;

namespace MindGoblin;

/// <summary>
/// Grabs the screen so the board can be read.
///
/// Read-only by construction: this class takes pictures and nothing else. The one thing
/// in the app that sends input toward the game is GameInput -- a single hover-and-copy
/// fired only inside the user's own F9 press, which is the one-action-per-keypress line
/// GGG's macro policy draws. Nothing anywhere runs input on a timer or in a loop.
///
/// Pixels are read from a locked buffer rather than GetPixel: a 2560x1440 slurp through
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
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    /// <summary>Size of the primary screen, for turning fractional layouts into pixels.</summary>
    public static Rectangle PrimaryScreenBounds() =>
        new(0, 0, GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN));

    /// <summary>
    /// Every monitor, as one rectangle -- and it DOES NOT START AT 0,0.
    ///
    /// A monitor to the left of the primary one has negative x, and a game windowed
    /// there is at, say, (-1986, 253). The whole app reasoned in primary-screen
    /// coordinates, so it asked for that rectangle and got black: CopyFromScreen does
    /// not clip to a monitor, it just copies whatever is at coordinates that are not
    /// there. The calibrator then drew its grid over a blank capture and no amount of
    /// locating the panel could rescue pixels that were never in the image.
    /// </summary>
    public static Rectangle VirtualScreenBounds() =>
        new(GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
            GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN));

    /// <summary>Where a game rectangle came from, so the UI can say so rather than
    /// leaving the user to wonder which resolution the plan was built against.</summary>
    public enum BoundsSource { Detected, Pinned, PrimaryScreen }

    public readonly record struct GameBounds(Rectangle Rect, BoundsSource Source)
    {
        public string Describe => Source switch
        {
            BoundsSource.Detected => $"{Rect.Width}×{Rect.Height} (detected)",
            BoundsSource.Pinned => $"{Rect.Width}×{Rect.Height} (set by you)",
            _ => $"{Rect.Width}×{Rect.Height} (primary screen)",
        };
    }

    /// <summary>
    /// The rectangle the game is drawn in -- the one every calibrated coordinate is
    /// relative to.
    ///
    /// Detection first, the user's pin second, the primary screen last. Everything
    /// downstream works in CLIENT coordinates: the capture is this rectangle, so the
    /// reader's pixels start at the game's top-left whether it is fullscreen or a window
    /// in the corner of a second monitor, and only the code that moves the real cursor
    /// has to add the origin back.
    /// </summary>
    public static GameBounds ResolveGameBounds()
    {
        var pin = MindGoblin.Core.Voyage.GameResolution.Load();
        if (pin.Auto && GameWindow.ClientBounds() is { } client)
            return new GameBounds(client, BoundsSource.Detected);
        if (pin.IsPinned)
            // A pinned resolution says nothing about WHERE the window is, so it means
            // the usual thing: fullscreen at the origin.
            return new GameBounds(new Rectangle(0, 0, pin.Width, pin.Height), BoundsSource.Pinned);
        return new GameBounds(PrimaryScreenBounds(), BoundsSource.PrimaryScreen);
    }

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

/// <summary>
/// Fast pixel access over a captured bitmap.
///
/// BORROWS the bitmap: every pixel is copied out in the constructor and the bitmap is
/// never touched again, so this owns nothing and disposes nothing. It used to dispose
/// what it was handed, which made every `using var bmp` / `using var pixels` pair a
/// double-dispose and had the calibration window cloning a bitmap to defend itself.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BitmapPixels : IPixels
{
    private readonly int[] _argb;

    public BitmapPixels(Bitmap bmp)
    {
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
}
