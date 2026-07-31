using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace MindGoblin;

/// <summary>
/// Reads text off the screen using the OCR engine built into Windows.
///
/// The figurines around the Voyage board cannot be copied -- the game only puts items on
/// the clipboard -- so their modifiers have to be read visually. Windows.Media.Ocr ships
/// with the OS, needs no model download, no network and no third-party binary, which
/// keeps the app a single portable exe.
///
/// It handles PoE's prose well. Checked against a real 2560x1440 capture it returned
/// "Travel to the Tidal Island" and "Hover a square of the Voyage Board to see the
/// relevant Area Modifiers" verbatim. It is NOT good at the small stylised numerals --
/// the same pass read "L:76" as "L.'j6" -- which is exactly why chart levels still go
/// through the template reader and only modifier text comes through here.
///
/// Read-only, like everything else that touches the game: it takes a picture and reads
/// it. Nothing is sent to the client.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public static class ScreenOcr
{
    private static OcrEngine? _engine;
    private static bool _tried;

    /// <summary>
    /// The engine, or null when no OCR language pack is installed.
    ///
    /// Cached because creating it per capture is slow, and the answer never changes
    /// within a session.
    /// </summary>
    public static OcrEngine? Engine
    {
        get
        {
            if (_tried) return _engine;
            _tried = true;
            try
            {
                _engine = OcrEngine.TryCreateFromLanguage(new Language("en-US"))
                          ?? OcrEngine.TryCreateFromUserProfileLanguages();
            }
            catch (Exception ex) when (ex is COMException or TypeLoadException
                                             or DllNotFoundException)
            {
                _engine = null;
            }
            return _engine;
        }
    }

    public static bool IsAvailable => Engine is not null;

    /// <summary>Read the lines of text in a screen rectangle, top to bottom.</summary>
    public static async Task<IReadOnlyList<string>> ReadRegionAsync(
        Rectangle region, int upscale = 3, bool blueText = false)
    {
        if (Engine is not { } engine) return [];
        if (region.Width <= 0 || region.Height <= 0) return [];

        using var shot = ScreenCapture.CaptureRegion(region);
        using var prepared = Prepare(shot, upscale, blueText);
        var bitmap = await ToSoftwareBitmapAsync(prepared);

        var result = await engine.RecognizeAsync(bitmap);

        // Sorted by vertical position: OcrResult.Lines is not guaranteed to be in reading
        // order, and a modifier wrapped across two lines only rejoins correctly in order.
        return result.Lines
            .Where(l => l.Words.Count > 0)
            .OrderBy(l => l.Words.Min(w => w.BoundingRect.Top))
            .Select(l => l.Text)
            .ToList();
    }

    /// <summary>
    /// Enlarge before recognising.
    ///
    /// The panel text is small and light-on-dark, which the engine reads poorly at native
    /// size; scaling up costs a millisecond and measurably improves the result. The
    /// engine also refuses images past MaxImageDimension, so the factor is capped.
    /// </summary>
    private static Bitmap Prepare(Bitmap source, int upscale, bool blueText = false)
    {
        var factor = Math.Max(1, upscale);
        var max = (int)OcrEngine.MaxImageDimension;
        while (factor > 1
               && (source.Width * factor > max || source.Height * factor > max))
            factor--;

        Bitmap scaled;
        if (factor == 1)
        {
            scaled = (Bitmap)source.Clone();
        }
        else
        {
            scaled = new Bitmap(source.Width * factor, source.Height * factor,
                                PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(scaled);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(source, 0, 0, scaled.Width, scaled.Height);
        }
        if (blueText) ThresholdBlue(scaled);
        return scaled;
    }

    /// <summary>
    /// Reduce a magic-blue-on-anything capture to black text on white.
    ///
    /// Figurine tooltips render over whatever sits behind them -- parchment on the
    /// left of the board, the dark chart stash on the right -- and OCR against the
    /// noisy dark side misread numbers and mangled words the parchment side got
    /// right. The text colour itself is the one constant, so keep exactly it.
    /// </summary>
    private static void ThresholdBlue(Bitmap bitmap)
    {
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                for (var y = 0; y < bitmap.Height; y++)
                {
                    var row = (byte*)data.Scan0 + y * data.Stride;
                    for (var x = 0; x < bitmap.Width; x++)
                    {
                        var b = row[x * 4];
                        var g = row[x * 4 + 1];
                        var r = row[x * 4 + 2];
                        var isText = b > 110 && b > r + 30 && b > g + 20;
                        var v = (byte)(isText ? 0 : 255);
                        row[x * 4] = v; row[x * 4 + 1] = v; row[x * 4 + 2] = v;
                        row[x * 4 + 3] = 255;
                    }
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    /// <summary>
    /// Bridge System.Drawing to WinRT.
    ///
    /// There is no direct conversion between the two imaging stacks, so the bitmap goes
    /// through an in-memory BMP. Wasteful in principle, irrelevant in practice: the
    /// region is a few hundred pixels square and this happens once per keypress.
    /// </summary>
    private static async Task<SoftwareBitmap> ToSoftwareBitmapAsync(Bitmap bitmap)
    {
        using var memory = new MemoryStream();
        bitmap.Save(memory, ImageFormat.Bmp);
        memory.Position = 0;

        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(memory.ToArray().AsBuffer());
        stream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(stream);
        return await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
    }
}
