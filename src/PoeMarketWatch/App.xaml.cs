using System.IO;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PoeMarketWatch;

/// <summary>
/// Application entry point.
///
/// Also renders a view straight to a PNG without ever showing a window:
///
///     PoeMarketWatch.exe --render voyage out.png [--demo]
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
            RenderToFile(view, path, e.Args.Contains("--demo"));
            Shutdown();
            return;
        }
        base.OnStartup(e);
    }

    private static void RenderToFile(string view, string path, bool demo)
    {
        FrameworkElement element = view.ToLowerInvariant() switch
        {
            "voyage" => BuildVoyage(demo),
            _ => throw new ArgumentException($"unknown view '{view}'"),
        };

        // Arrange at the size the second-monitor window opens at, so what is measured is
        // what the user will actually see.
        const int width = 1420;
        const int height = 880;
        element.Width = width;
        element.Height = height;
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();

        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(element);

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
    private static FrameworkElement BuildVoyage(bool demo)
    {
        var view = new VoyageView();
        if (demo) view.LoadSample();
        return view;
    }
}
