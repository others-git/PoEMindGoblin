using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MindGoblin;

/// <summary>
/// The carved figurine cartouches, matched creature-for-creature to the game's own
/// board frame: serpent-dragons along the top, coiled serpents down the right,
/// fish-dragons down the left, lobsters at the bottom corners and an anchor at the
/// bottom centre. Each carries a number medallion, because WHERE a figurine is
/// carries as much meaning as what it says.
///
/// Drawn rather than an image so they stay crisp at any DPI and recolour by state
/// without shipping bitmaps. Verdigris fills one once read, so a glance at the board
/// says which edges are still missing their modifier.
/// </summary>
internal static class VoyageOrnament
{
    /// <summary>Native size of the geometry below; everything scales from this.</summary>
    private const double DesignWidth = 48;
    private const double DesignHeight = 28;

    internal enum State { Unread, Captured, Selected, Skipped }

    // ---- the serpent-dragon (top edge) --------------------------------------------

    private const string DragonBody =
        "M5.6,19 C7,11.5 11,7.5 15.5,8.8 C19.5,9.9 20.5,7.2 24,6.8 "
        + "C27.5,7.2 29,9.9 33,8.8 C37.5,7.5 41.5,11 43,16.5 "
        + "C41,13.6 38,12 34,12.8 C30,13.6 28,10.9 24,10.5 "
        + "C20,10.9 18,13.6 14.5,12.8 C10.5,12 8,14.5 6.8,19.5 Z";

    private const string DragonHead =
        "M0.8,16.8 C-0.2,12.2 2.4,9 6.4,9.9 C9.4,10.6 10.8,13.4 9.6,16.2 "
        + "C8.9,17.9 6.8,18.9 4.6,18.6 L1.2,21.2 L2.4,18.2 C1.6,17.9 1,17.4 0.8,16.8 Z";

    private const string DragonHorns =
        "M4.2,10.2 L2.2,5 L6.4,8.7 Z M7.2,10.4 L6.8,5.2 L10.2,9 Z";

    private const string DragonSpikes =
        "M12.6,9 L14.2,4.6 L16.2,8.4 Z M19.4,7.3 L21,3 L22.8,6.9 Z "
        + "M25.4,6.9 L27.2,3.2 L28.8,7.2 Z M31.8,8.4 L33.8,4.6 L35.4,9 Z";

    private const string DragonTail =
        "M43,16.5 C45.6,14 47.2,15.8 46.2,18.4 C45.4,20.4 42.8,20.4 42,18.6 "
        + "C42.9,19 44.2,18.6 44.4,17.4 C44.6,16.2 43.8,15.7 43,16.5 Z";

    // ---- the coiled serpent (right edge) ------------------------------------------
    // A ring wrapped around the medallion: head emerging upper-left over its own body,
    // tail crossing out at the lower-right and curling under itself.

    private const string CoilRing =
        "M12.2,11.2 C15,5.2 31,4 36.4,10.2 C40.2,14.8 37.4,21.6 31,24 "
        + "C26.2,25.8 20,25.4 16,22.8 C13.4,21.4 12.6,19.6 14.2,18.2 "
        + "C15,20 17.2,21.4 20.4,21.8 C25.4,22.4 31,20.6 32.8,17 "
        + "C34.4,13.6 32,9.6 27,8.4 C22.4,7.3 16.6,8.6 14,12.6 Z";

    private const string CoilHead =
        "M12.6,12.4 C9.4,12.8 7,11 7,8.4 C7,5.8 9.4,4.2 12,4.9 "
        + "C14.4,5.5 15.7,7.7 15,9.9 C14.9,10.3 14.6,10.8 14.2,11.2 "
        + "L10.6,13.9 L11.8,11.8 C12.1,11.9 12.4,12.2 12.6,12.4 Z";

    private const string CoilFins =
        "M36.6,10.8 L40.4,9 L39,13 Z M38.2,15.6 L42,15.4 L39.4,18.4 Z "
        + "M35,20.6 L38.2,22.4 L34.4,23.4 Z";

    private const string CoilTail =
        "M16.2,22.7 C12.8,22.4 11.4,20 12.8,18 C13,19.8 14.3,20.9 16.6,21 Z "
        + "M13.4,18.9 C11.2,18.4 10.4,16.8 11.4,15.4 C11.6,16.8 12.4,17.7 14,17.9 Z";

    // ---- the fish-dragon (left edge) ----------------------------------------------
    // A koi-dragon arcing over the medallion: whiskered head, finned back, fan tail.

    private const string FishBody =
        "M5.5,17 C7,10.5 11.5,7.6 16,9 C20,10.2 21.5,7.6 25,7.2 "
        + "C28.5,7.6 30.5,10.2 34,9.4 C37,8.7 39.5,10.5 41,14 "
        + "C39,11.8 36.5,11.2 33.5,12.2 C29.5,13.4 27.5,10.4 25,10 "
        + "C21.5,10.4 20,13.2 15.5,12.2 C11.5,11.3 9,13.5 7,18 Z";

    private const string FishHead =
        "M1.6,17.8 C0.6,13.8 3,10.8 6.6,11.6 C9.2,12.2 10.4,14.8 9.4,17.2 "
        + "C8.6,19 6.4,19.8 4.4,19.4 C3.2,19.2 2,18.6 1.6,17.8 Z";

    private const string FishWhiskers =
        "M2,14.4 C0.6,13.2 0.4,11.6 1.4,10.4 C1.2,11.8 1.8,12.8 3.2,13.4 Z "
        + "M4.6,12.2 C3.8,10.6 4.2,9 5.6,8.2 C4.9,9.6 5.1,10.8 6.2,11.8 Z";

    private const string FishFins =
        "M14,10 L16,5.6 L18.4,9.2 Z M22,8.2 L24.4,4.2 L26.4,8 Z "
        + "M30.4,9.2 L32.8,5.8 L34.4,9.6 Z "
        + "M8,18.6 C9.6,20.8 9.4,23 7.8,24.4 C8.2,22.4 7.6,20.8 6.2,19.6 Z";

    private const string FishTail =
        "M41,14 C43.4,11.4 46,10.8 47.4,12 C45.8,12.4 44.6,13.4 44,14.8 "
        + "C45.4,14.6 46.8,15 47.6,16.2 C45.9,16.2 44.3,16.9 43.4,18.2 "
        + "C43,16.6 41.8,15 41,14 Z";

    // ---- the lobster (bottom corners) ---------------------------------------------
    // Seen from above, claws raised toward the board: the medallion IS the carapace.

    private const string LobsterClaws =
        "M14.6,10.4 C10.8,8.6 8.6,5.4 9.6,2.4 C10.4,4.6 12,6.2 14.2,6.6 "
        + "C13.4,5.2 13.6,3.6 14.8,2.6 C15.2,4.4 16.4,5.8 18.2,6.4 "
        + "C17.8,8.2 16.6,9.8 14.6,10.4 Z "
        + "M33.4,10.4 C37.2,8.6 39.4,5.4 38.4,2.4 C37.6,4.6 36,6.2 33.8,6.6 "
        + "C34.6,5.2 34.4,3.6 33.2,2.6 C32.8,4.4 31.6,5.8 29.8,6.4 "
        + "C30.2,8.2 31.4,9.8 33.4,10.4 Z";

    private const string LobsterArms =
        "M15.4,9.6 C17,10.8 18.6,11.4 20.4,11.6 L20,13.6 C17.8,13.2 16.2,12 15,10.4 Z "
        + "M32.6,9.6 C31,10.8 29.4,11.4 27.6,11.6 L28,13.6 C30.2,13.2 31.8,12 33,10.4 Z";

    private const string LobsterLegs =
        "M16.2,15.4 C13.8,15 11.8,15.6 10.4,17 L11.6,18 C12.8,16.8 14.4,16.3 16.4,16.6 Z "
        + "M16.4,18 C14.2,18.2 12.4,19.2 11.4,20.8 L12.8,21.6 C13.6,20.2 15,19.4 16.8,19.2 Z "
        + "M31.8,15.4 C34.2,15 36.2,15.6 37.6,17 L36.4,18 C35.2,16.8 33.6,16.3 31.6,16.6 Z "
        + "M31.6,18 C33.8,18.2 35.6,19.2 36.6,20.8 L35.2,21.6 C34.4,20.2 33,19.4 31.2,19.2 Z";

    private const string LobsterTail =
        "M21.6,20.6 L26.4,20.6 L26,23 L22,23 Z "
        + "M22.2,23.4 L25.8,23.4 L25.4,25.2 L22.6,25.2 Z "
        + "M20.4,25.6 C21.6,25.4 22.6,25.6 24,26.6 C25.4,25.6 26.4,25.4 27.6,25.6 "
        + "C26.6,27.4 25.4,28 24,28 C22.6,28 21.4,27.4 20.4,25.6 Z";

    private const string LobsterAntennae =
        "M20.6,9.4 C19.2,6.6 19.4,3.8 21.2,1.4 C20.6,4 21,6.4 22.4,8.6 Z "
        + "M27.4,9.4 C28.8,6.6 28.6,3.8 26.8,1.4 C27.4,4 27,6.4 25.6,8.6 Z";

    // ---- the anchor (bottom centre) ------------------------------------------------

    private const string AnchorShape =
        "M24,1.4 C25.8,1.4 27.2,2.8 27.2,4.6 C27.2,6 26.4,7.1 25.2,7.6 "
        + "L25.2,9.4 L30,9.4 L30,11.6 L25.2,11.6 L25.2,21.8 "
        + "C28.4,21 30.8,18.8 31.8,15.8 L33.8,18 C32.2,22.6 28.6,25.6 24,26.4 "
        + "C19.4,25.6 15.8,22.6 14.2,18 L16.2,15.8 C17.2,18.8 19.6,21 22.8,21.8 "
        + "L22.8,11.6 L18,11.6 L18,9.4 L22.8,9.4 L22.8,7.6 "
        + "C21.6,7.1 20.8,6 20.8,4.6 C20.8,2.8 22.2,1.4 24,1.4 Z "
        + "M24,3.2 C23.2,3.2 22.6,3.8 22.6,4.6 C22.6,5.4 23.2,6 24,6 "
        + "C24.8,6 25.4,5.4 25.4,4.6 C25.4,3.8 24.8,3.2 24,3.2 Z";

    private const string AnchorFlukes =
        "M14.2,18 L11.8,16.2 C12.4,19 13.8,21.2 15.8,22.8 Z "
        + "M33.8,18 L36.2,16.2 C35.6,19 34.2,21.2 32.2,22.8 Z";

    /// <summary>Medallion centres differ per creature so the number never fights art.</summary>
    private static (double X, double Y, double R) Medallion(string edge, int label) =>
        edge switch
        {
            "bottom" when IsAnchor(label) => (24, 14.6, 5.2),
            "bottom" => (24, 14.6, 5.6),          // the lobster's carapace
            "right" or "left" when IsCoil(edge) => (24, 15, 5.6),
            _ => (24, 15.4, 6.2),
        };

    private static bool IsAnchor(int label) => label == 8;
    private static bool IsCoil(string edge) => edge == "right";

    /// <summary>
    /// Build one ornament, rotated to lie along the board edge it guards.
    /// </summary>
    internal static FrameworkElement Build(int label, string edge, State state, double scale = 1.0)
    {
        var (fill, stroke, ink) = Palette(state);

        var art = new Canvas
        {
            Width = DesignWidth,
            Height = DesignHeight + 2,
            Background = Brushes.Transparent,
        };

        var parts = Parts(edge, label);
        var (mx, my, mr) = Medallion(edge, label);
        var medallion = $"M{mx},{my - mr} A{mr},{mr} 0 1 1 {mx},{my + mr} A{mr},{mr} 0 1 1 {mx},{my - mr} Z";

        // Cast shadow first: the same silhouettes a pixel down, which is what reads
        // as relief rather than a flat sticker.
        foreach (var part in parts.Prepend(medallion))
            art.Children.Add(Figure(part, ShadowBrush, null, 0, 1.5));

        // The medallion sits UNDER the creature so claws and coils overlap it.
        art.Children.Add(Figure(medallion, fill, stroke, 0, 0));
        foreach (var part in parts)
            art.Children.Add(Figure(part, fill, stroke, 0, 0));

        // The eye, where the creature has a head to put one on.
        if (Eye(edge, label) is { } eye)
            art.Children.Add(new Ellipse
            {
                Width = 1.6, Height = 1.6,
                Fill = ShadowBrush,
                Margin = new Thickness(eye.X, eye.Y, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
            });

        var carving = new Viewbox
        {
            Child = art,
            Width = DesignWidth * scale,
            Height = (DesignHeight + 2) * scale,
            Stretch = Stretch.Uniform,
        };

        // Left and right figurines are turned to run along their edge, the way the
        // carving follows the frame in game.
        var quarter = edge switch { "left" => 90.0, "right" => -90.0, _ => 0.0 };
        FrameworkElement rotated = quarter == 0
            ? carving
            : new Grid { Children = { carving }, LayoutTransform = new RotateTransform(quarter) };

        // The number sits OUTSIDE the rotated art, never inside it, so every label
        // stays upright and whole whichever edge the figurine is on.
        var host = new Grid();
        host.Children.Add(rotated);
        host.Children.Add(new TextBlock
        {
            Text = label.ToString(),
            FontFamily = PoeFonts.Display,
            FontSize = 9 * scale,
            Foreground = ink,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = LabelNudge(edge, label, scale),
        });
        return host;
    }

    /// <summary>The creature for a position, matching the game's frame.</summary>
    private static string[] Parts(string edge, int label) => edge switch
    {
        "top" => [DragonBody, DragonSpikes, DragonHead, DragonHorns, DragonTail],
        "right" => [CoilRing, CoilFins, CoilHead, CoilTail],
        "left" => [FishBody, FishFins, FishHead, FishWhiskers, FishTail],
        _ => IsAnchor(label)
            ? [AnchorShape, AnchorFlukes]
            : [LobsterClaws, LobsterArms, LobsterLegs, LobsterTail, LobsterAntennae],
    };

    private static (double X, double Y)? Eye(string edge, int label) => edge switch
    {
        "top" => (3.9, 12.9),
        "right" => (9.7, 7.3),
        "left" => (4.0, 14.2),
        _ => null,
    };

    /// <summary>Keep the number centred on each creature's medallion after rotation.</summary>
    private static Thickness LabelNudge(string edge, int label, double scale) => edge switch
    {
        // rotated art: the medallion's design-space x-offset becomes a y-offset
        "left" => new Thickness(0, 1.4 * scale, 0, 0),
        "right" => new Thickness(0, 0, 0, 1.4 * scale),
        "bottom" => new Thickness(0, 0.4 * scale, 0, 0),
        _ => new Thickness(0, 1.6 * scale, 0, 0),
    };

    private static Path Figure(string data, Brush fill, Brush? stroke, double dx, double dy)
    {
        var path = new Path
        {
            Data = Geometry.Parse(data),
            Fill = fill,
            Stroke = stroke,
            StrokeThickness = stroke is null ? 0 : 0.7,
        };
        if (dx != 0 || dy != 0) path.RenderTransform = new TranslateTransform(dx, dy);
        return path;
    }

    private static readonly Brush ShadowBrush = Frozen(Color.FromArgb(0xB0, 0x04, 0x03, 0x02));

    /// <summary>Fill, rim and engraved-number colours for each state.</summary>
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
