using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MindGoblin;

/// <summary>
/// The carved figurine cartouche.
///
/// The Voyage board is ringed with cast bronze figureheads, and each one buffs only the
/// square it sits beside -- so WHERE a figurine is carries as much meaning as what it
/// says. Drawing them as plain dots put the twelve most position-dependent modifiers in
/// the interface on a par with a bullet point.
///
/// This is a small sea serpent in vector, arched over a number medallion -- the game's
/// own frame is carved with sea serpents, and the earlier mask-and-volutes attempt read
/// as anything but. It is the one bold element on the panel; everything around it is
/// deliberately quiet. Verdigris fills it once read, so a glance at the board says which
/// edges are still missing their modifier.
///
/// Drawn rather than an image so it stays crisp at any DPI and recolours by state without
/// shipping four bitmaps -- and so it scales with the board rather than the other way up.
/// </summary>
internal static class VoyageOrnament
{
    /// <summary>Native size of the geometry below; everything scales from this.</summary>
    private const double DesignWidth = 44;
    private const double DesignHeight = 24;

    /// <summary>
    /// The serpent: a ribbon of body rising from the left, arching over the medallion,
    /// and diving back down to the right, with a wedge head at the left end and a
    /// curled tail at the right.
    /// </summary>
    private const string SerpentPath =
        "M3.5,15 C4,10.5 8,7.5 12,8.5 C16,9.4 18,6.6 22,6.2 "
        + "C26,6.6 28,9.4 32,8.5 C36,7.5 40,10.5 40.5,15 "
        + "C39,12.8 36.5,10.6 33,11.4 C29,12.3 26.5,9.4 22,9.1 "
        + "C17.5,9.4 15,12.3 11,11.4 C7.5,10.6 5,12.8 3.5,15 Z";

    /// <summary>Wedge head with an open jaw at the serpent's left end.</summary>
    private const string HeadPath =
        "M0.8,15.6 C0.6,13 2.4,11.2 4.8,11.9 C6.6,12.5 7.2,14.4 6.4,16 "
        + "C6,16.8 4.8,17.3 3.8,17.1 L1.2,18.4 L2.2,16.6 C1.5,16.4 0.9,16.1 0.8,15.6 Z";

    /// <summary>The tail, curling under itself at the right end.</summary>
    private const string TailPath =
        "M40.5,15 C42.6,13.2 44,14.6 43.2,16.6 C42.6,18.1 40.6,18.2 39.8,17 "
        + "C40.5,17.2 41.6,17 41.9,16.1 C42.2,15.1 41.4,14.4 40.5,15 Z";

    /// <summary>Three dorsal fins along the arch.</summary>
    private const string FinsPath =
        "M11.5,8.6 L13.2,5.2 L15.2,8.2 Z "
        + "M20,6.6 L22,3.2 L24,6.6 Z "
        + "M28.8,8.2 L30.8,5.2 L32.5,8.6 Z";

    /// <summary>The medallion the serpent guards; the number is engraved on it.</summary>
    private const string MedallionPath =
        "M22,7.6 C25.9,7.6 28.6,10.2 28.6,13.8 C28.6,17.4 25.9,20.4 22,20.4 "
        + "C18.1,20.4 15.4,17.4 15.4,13.8 C15.4,10.2 18.1,7.6 22,7.6 Z";

    internal enum State { Unread, Captured, Selected, Skipped }

    /// <summary>
    /// Build one ornament, rotated to lie along the board edge it guards.
    /// </summary>
    /// <param name="label">Figurine number, engraved into the mask.</param>
    /// <param name="edge">Which board edge it sits against.</param>
    /// <param name="scale">Multiplier on the design size.</param>
    internal static FrameworkElement Build(int label, string edge, State state, double scale = 1.0)
    {
        var (fill, stroke, ink) = Palette(state);

        var art = new Canvas
        {
            Width = DesignWidth,
            Height = DesignHeight + 3,
            Background = Brushes.Transparent,
        };

        // Cast shadow first: the same silhouette a pixel down, which is what reads as
        // relief rather than a flat sticker.
        foreach (var part in new[] { MedallionPath, SerpentPath, FinsPath, HeadPath, TailPath })
            art.Children.Add(Figure(part, ShadowBrush, null, 0, 1.6));

        // The medallion under the serpent, then the beast over it.
        art.Children.Add(Figure(MedallionPath, fill, stroke, 0, 0));
        art.Children.Add(Figure(FinsPath, fill, stroke, 0, 0));
        art.Children.Add(Figure(SerpentPath, fill, stroke, 0, 0));
        art.Children.Add(Figure(HeadPath, fill, stroke, 0, 0));
        art.Children.Add(Figure(TailPath, fill, stroke, 0, 0));

        // The eye: one dark dot is what makes the wedge read as a head.
        art.Children.Add(new Ellipse
        {
            Width = 1.6, Height = 1.6,
            Fill = ShadowBrush,
            Margin = new Thickness(2.6, 12.9, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        });

        // Bevel: a hairline of light along the serpent's arch. One highlight, not an
        // outline on everything -- the latter reads as neon, not bronze.
        art.Children.Add(new Path
        {
            Data = Geometry.Parse("M13,8.9 C17,9.8 18.6,7.1 22,6.7 C25.4,7.1 27,9.8 31,8.9"),
            Stroke = BevelBrush,
            StrokeThickness = 0.9,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        });

        var carving = new Viewbox
        {
            Child = art,
            Width = DesignWidth * scale,
            Height = (DesignHeight + 3) * scale,
            Stretch = Stretch.Uniform,
        };

        // Left and right figurines are turned to run along their edge, the way the
        // carving follows the frame in game.
        var quarter = edge switch { "left" => 90.0, "right" => -90.0, _ => 0.0 };
        FrameworkElement rotated = quarter == 0
            ? carving
            : new Grid { Children = { carving }, LayoutTransform = new RotateTransform(quarter) };

        // The number sits OUTSIDE the rotated art, never inside it. Rotating a TextBlock
        // within the Canvas pushed it past the Canvas's declared bounds and the Viewbox
        // clipped it -- "12" came out as ".2". Overlaying it keeps every label upright
        // and whole whichever edge the figurine is on.
        var host = new Grid();
        host.Children.Add(rotated);
        host.Children.Add(new TextBlock
        {
            Text = label.ToString(),
            FontFamily = PoeFonts.Display,
            FontSize = 9.5 * scale,
            Foreground = ink,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return host;
    }

    private static Path Figure(string data, Brush fill, Brush? stroke, double dx, double dy)
    {
        var path = new Path
        {
            Data = Geometry.Parse(data),
            Fill = fill,
            Stroke = stroke,
            StrokeThickness = stroke is null ? 0 : 0.8,
        };
        if (dx != 0 || dy != 0) path.RenderTransform = new TranslateTransform(dx, dy);
        return path;
    }

    private static readonly Brush ShadowBrush = Frozen(Color.FromArgb(0xB0, 0x04, 0x03, 0x02));
    private static readonly Brush BevelBrush = Frozen(Color.FromArgb(0x66, 0xFF, 0xE9, 0xA8));

    /// <summary>Fill, rim and engraved-number colours for each state.</summary>
    private static (Brush Fill, Brush Stroke, Brush Ink) Palette(State state) => state switch
    {
        // Selected: brass, lit. The one the capture box below is pointed at.
        State.Selected => (Gradient(Color.FromRgb(0xD9, 0xB1, 0x35), Color.FromRgb(0x8A, 0x6F, 0x22)),
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
