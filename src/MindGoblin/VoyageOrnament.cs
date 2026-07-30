using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MindGoblin;

/// <summary>
/// The figurine markers around the board: plain cast studs, a numbered disc with a rim.
///
/// Several rounds of vector sea-creatures tried to mimic the game's carved frame and
/// none survived contact with real render sizes -- at 20 pixels a hand-authored kraken
/// is a green blob, and a blob is worse than nothing (the user said so in fewer words).
/// So the markers are the simplest thing that does the job: round, numbered, coloured
/// by state. Verdigris once read, brass when selected, so a glance at the ring still
/// says which edges are missing their modifier.
/// </summary>
internal static class VoyageOrnament
{
    internal enum State { Unread, Captured, Selected, Skipped }

    private const double Diameter = 22;

    /// <summary>Build one marker. Edge is accepted for call-site stability; a disc
    /// needs no rotation and no mirroring -- that is the point of a disc.</summary>
    internal static FrameworkElement Build(int label, string edge, State state, double scale = 1.0)
    {
        var (fill, stroke, ink) = Palette(state);
        var d = Diameter * scale;

        var host = new Grid { Width = d + 4, Height = d + 4 };

        // The cast shadow is one ellipse a pixel down -- relief, not a sticker.
        host.Children.Add(new Ellipse
        {
            Width = d, Height = d,
            Fill = new SolidColorBrush(Color.FromArgb(0xB0, 0x04, 0x03, 0x02)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 2.5, 0, 0),
        });
        host.Children.Add(new Ellipse
        {
            Width = d, Height = d,
            Fill = fill,
            Stroke = stroke,
            StrokeThickness = 1.2,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        // A faint inner ring, which is what makes it read as a struck coin.
        host.Children.Add(new Ellipse
        {
            Width = d - 5, Height = d - 5,
            Stroke = new SolidColorBrush(Color.FromArgb(0x38, 0x00, 0x00, 0x00)),
            StrokeThickness = 1,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        host.Children.Add(new TextBlock
        {
            Text = label.ToString(),
            FontFamily = PoeFonts.Display,
            FontSize = 10.5 * scale,
            Foreground = ink,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, -1, 0, 0),
        });
        return host;
    }

    /// <summary>Fill, rim and number colours for each state.</summary>
    private static (Brush Fill, Brush Stroke, Brush Ink) Palette(State state) => state switch
    {
        // Selected: brass, lit. The one the capture box below is pointed at.
        State.Selected => (Gradient(Color.FromRgb(0xD9, 0xB1, 0x35), Color.FromRgb(0xA3, 0x8D, 0x6D)),
                           Frozen(Color.FromRgb(0xF0, 0xD2, 0x6A)),
                           Frozen(Color.FromRgb(0x1A, 0x15, 0x09))),

        // Captured: verdigris, the colour bronze goes once it has been out in the weather.
        State.Captured => (Gradient(Color.FromRgb(0x86, 0xA8, 0x6A), Color.FromRgb(0x44, 0x5C, 0x38)),
                           Frozen(Color.FromRgb(0x9E, 0xC4, 0x76)),
                           Frozen(Color.FromRgb(0x12, 0x18, 0x0E))),

        // Skipped: passed over on purpose, so it is dimmed rather than flagged.
        State.Skipped => (Gradient(Color.FromRgb(0x2C, 0x25, 0x1D), Color.FromRgb(0x1A, 0x16, 0x12)),
                          Frozen(Color.FromRgb(0x3A, 0x30, 0x24)),
                          Frozen(Color.FromRgb(0x5A, 0x50, 0x42))),

        // Unread: unlit metal.
        _ => (Gradient(Color.FromRgb(0x3A, 0x30, 0x24), Color.FromRgb(0x20, 0x1A, 0x14)),
              Frozen(Color.FromRgb(0x4A, 0x3D, 0x2C)),
              Frozen(Color.FromRgb(0x94, 0x86, 0x6F))),
    };

    private static Brush Gradient(Color top, Color bottom)
    {
        var brush = new LinearGradientBrush(top, bottom, 90);
        brush.Freeze();
        return brush;
    }

    private static Brush Frozen(Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }
}
