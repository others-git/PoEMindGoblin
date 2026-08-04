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
    /// <summary>
    /// Write the stack somewhere the user can find it, then say where.
    ///
    /// This is a portable exe on somebody else's machine: there is no debugger to
    /// attach and no console to read, so an unhandled exception reaches the person as
    /// four words in a dialog. "Index outside the bounds of the array on launch" cost a
    /// full reproduction hunt that a stack trace would have answered in a line.
    /// </summary>
    private static void Report(Exception ex, string when)
    {
        var path = MindGoblin.Core.SettingsFolder.FileIn("crash.log");
        try
        {
            MindGoblin.Core.SettingsFolder.EnsureExists();
            File.AppendAllText(path,
                $"--- {DateTimeOffset.Now:u} · {when} · {Environment.OSVersion}\n{ex}\n\n");
        }
        catch (Exception) { /* a crash while reporting a crash helps nobody */ }

        try
        {
            MessageBox.Show(
                $"{ex.GetType().Name}: {ex.Message}\n\nWritten to:\n{path}\n\n"
                + "Please send that file — it names the line.",
                "MindGoblin hit a problem", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception) { }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Both halves: the dispatcher catches the UI thread, AppDomain catches the rest.
        DispatcherUnhandledException += (_, args) =>
        {
            Report(args.Exception, "ui");
            args.Handled = true;      // a reported crash beats a silent disappearance
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) Report(ex, "background");
        };

        if (e.Args is [ "--render", var view, var path, ..])
        {
            // --demo takes the screenshot to populate from. It is not shipped with the
            // app: it is a capture of somebody's game, and a test fixture has no business
            // in a distributable.
            var demoIndex = Array.IndexOf(e.Args, "--demo");
            var demo = demoIndex >= 0 && demoIndex + 1 < e.Args.Length
                ? e.Args[demoIndex + 1]
                : null;
            var searchIndex = Array.IndexOf(e.Args, "--search");
            RenderToFile(view, path, demoIndex >= 0, demo,
                         weights: Array.IndexOf(e.Args, "--weights") >= 0,
                         search: searchIndex >= 0 && searchIndex + 1 < e.Args.Length
                             ? e.Args[searchIndex + 1] : null);
            Shutdown();
            return;
        }
        base.OnStartup(e);
    }

    private static void RenderToFile(string view, string path, bool demo, string? screenshot,
                                     bool weights = false, string? search = null)
    {
        // The calibrator is a WINDOW, and all of its work happens in Loaded -- so it has
        // to be shown to be rendered. Shown far off the desktop: this app must never put
        // a window over the game, and "briefly" is still over it.
        if (view.Equals("calibration", StringComparison.OrdinalIgnoreCase))
        {
            var window = new CalibrationWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000,
                Top = -20000,
                ShowInTaskbar = false,
            };
            window.Show();
            window.UpdateLayout();
            Save(Render(window, (int)window.ActualWidth, (int)window.ActualHeight), path);
            window.Close();
            return;
        }

        FrameworkElement element = view.ToLowerInvariant() switch
        {
            "voyage" => BuildVoyage(demo, screenshot, weights, search),
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

        var target = Render(element, width, height);

        // The view holds a file watcher; a render is a one-shot and should not leave one.
        (element as IDisposable)?.Dispose();

        Save(target, path);
    }

    private static RenderTargetBitmap Render(Visual visual, int width, int height)
    {
        var target = new RenderTargetBitmap(
            Math.Max(1, width), Math.Max(1, height), 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        return target;
    }

    private static void Save(RenderTargetBitmap target, string path)
    {
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
    private static FrameworkElement BuildVoyage(bool demo, string? screenshot,
                                                bool weights = false, string? search = null)
    {
        var view = new VoyageView();
        if (demo) view.LoadSample(screenshot);
        // --weights: render with the panel open, so slider chrome is checkable offscreen
        if (weights) view.OpenWeightsForRender();
        // --search: render mid-filter, so the dimmed misses are checkable too
        if (!string.IsNullOrWhiteSpace(search)) view.SearchForRender(search!);
        return view;
    }
}
