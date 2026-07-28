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
/// This is a small escutcheon in vector: a central mask flanked by two scrolled volutes,
/// with a raised bevel above and a cast shadow below. It is the one bold element on the
/// panel; everything around it is deliberately quiet. Verdigris fills it once read, so a
/// glance at the board says which edges are still missing their modifier.
///
/// Drawn rather than an image so it stays crisp at any DPI and recolours by state without
/// shipping four bitmaps -- and so it scales with the board rather than the other way up.
/// </summary>
internal static class VoyageOrnament
{
    /// <summary>Native size of the geometry below; everything scales from this.</summary>
    private const double DesignWidth = 44;
    private const double DesignHeight = 22;

    /// <summary>Central mask: a shield with a pointed crown and a rounded chin.</summary>
    private const string MaskPath =
        "M22,1.5 C25.5,1.5 28,4.5 28.4,8.4 C30.4,9 30.4,10.4 28.4,11 "
        + "C28,16 25.5,20.5 22,20.5 C18.5,20.5 16,16 15.6,11 "
        + "C13.6,10.4 13.6,9 15.6,8.4 C16,4.5 18.5,1.5 22,1.5 Z";

    /// <summary>Scrolled wings sweeping out to either side, as on the carved header.</summary>
    private const string WingsPath =
        "M15.8,7.6 C11,4.4 6,4.6 2.2,7.4 C0.6,8.6 0.6,10.6 2.4,11.4 "
        + "C4.4,12.3 6.2,11.4 6.2,9.8 C6.2,8.8 5.2,8.2 4.4,8.8 "
        + "C5.4,7.6 7.4,7.2 9.2,8 C11.6,9 13.4,10.6 15.4,12.6 Z "
        + "M28.2,7.6 C33,4.4 38,4.6 41.8,7.4 C43.4,8.6 43.4,10.6 41.6,11.4 "
        + "C39.6,12.3 37.8,11.4 37.8,9.8 C37.8,8.8 38.8,8.2 39.6,8.8 "
        + "C38.6,7.6 36.6,7.2 34.8,8 C32.4,9 30.6,10.6 28.6,12.6 Z";

    /// <summary>A small pendant drop below the mask, which grounds the shape.</summary>
    private const string DropPath =
        "M22,20 C23.2,20 24,21 24,22 C24,23.2 23.2,24 22,24 "
        + "C20.8,24 20,23.2 20,22 C20,21 20.8,20 22,20 Z";

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
        art.Children.Add(Figure(WingsPath, ShadowBrush, null, 0, 1.6));
        art.Children.Add(Figure(MaskPath, ShadowBrush, null, 0, 1.6));
        art.Children.Add(Figure(DropPath, ShadowBrush, null, 0, 1.6));

        art.Children.Add(Figure(WingsPath, fill, stroke, 0, 0));
        art.Children.Add(Figure(DropPath, fill, stroke, 0, 0));
        art.Children.Add(Figure(MaskPath, fill, stroke, 0, 0));

        // Bevel: a hairline of light along the top edge of the mask only. One highlight,
        // not an outline on everything -- the latter reads as neon, not bronze.
        art.Children.Add(new Path
        {
            Data = Geometry.Parse("M17.4,7.2 C18.2,3.8 20,2.6 22,2.6 C24,2.6 25.8,3.8 26.6,7.2"),
            Stroke = BevelBrush,
            StrokeThickness = 1,
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
            FontFamily = new FontFamily("Georgia"),
            FontSize = 10 * scale,
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
