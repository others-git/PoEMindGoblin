using System.IO;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MindGoblin;

/// <summary>
/// Application entry point.
///
/// Also renders a view straight to a PNG without ever showing a window:
///
///     MindGoblin.exe --render voyage out.png [--demo]
///
/// That exists because the only other way to look at the interface is to screenshot the
/// screen, and the screen usually has Path of Exile on it -- the one thing this tool must
/// never disturb. Offscreen rendering inspects the layout without stealing focus, and
/// works over a remote session where there is no visible desktop at all.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args is [ "--render", var view, var path, ..])
        {
            // --demo takes the screenshot to populate from. It is not shipped with the
            // app: it is a capture of somebody's game, and a test fixture has no business
            // in a distributable.
            var demoIndex = Array.IndexOf(e.Args, "--demo");
            var demo = demoIndex >= 0 && demoIndex + 1 < e.Args.Length
                ? e.Args[demoIndex + 1]
                : null;
            RenderToFile(view, path, demoIndex >= 0, demo);
            Shutdown();
            return;
        }
        base.OnStartup(e);
    }

    private static void RenderToFile(string view, string path, bool demo, string? screenshot)
    {
        FrameworkElement element = view.ToLowerInvariant() switch
        {
            "voyage" => BuildVoyage(demo, screenshot),
            _ => throw new ArgumentException($"unknown view '{view}'"),
        };

        // The client area the view actually gets inside a default 1500x1040 window,
        // after the title bar, the app header and the tab strip -- so what is measured
        // here is what the user will see.
        const int width = 1484;
        const int height = 908;
        element.Width = width;
        element.Height = height;
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();

        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(element);

        // The view holds a file watcher; a render is a one-shot and should not leave one.
        (element as IDisposable)?.Dispose();

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>
    /// A Voyage view, optionally filled with a worked example.
    ///
    /// The sample is not a mock-up: it drives real charts through the real session and
    /// the real solver, so what gets rendered is what the code produces rather than a
    /// picture of what it is meant to produce.
    /// </summary>
    private static FrameworkElement BuildVoyage(bool demo, string? screenshot)
    {
        var view = new VoyageView();
        if (demo) view.LoadSample(screenshot);
        return view;
    }
}
