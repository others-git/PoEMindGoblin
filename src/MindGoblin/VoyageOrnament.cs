using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MindGoblin;

/// <summary>
/// The carved figurine cartouches, matched position-for-position to the game's frame:
/// squids at the top corners with a sea-dragon between them, then curled tentacles
/// around a mid-side coiled serpent down each flank, lobsters at the bottom corners
/// and an anchor at bottom centre. The frame is SYMMETRIC, so each right-side creature is the mirror
/// of its left-side twin -- one geometry, flipped, never two drawings drifting apart.
///
/// Every cartouche carries a number medallion, because WHERE a figurine is carries as
/// much meaning as what it says. Drawn rather than an image so they stay crisp at any
/// DPI and recolour by state; verdigris fills one once read.
/// </summary>
internal static class VoyageOrnament
{
    /// <summary>Native size of the geometry below; everything scales from this.</summary>
    private const double DesignWidth = 48;
    private const double DesignHeight = 30;

    internal enum State { Unread, Captured, Selected, Skipped }

    private enum Kind { Kraken, Dragon, Tentacle, Coil, Lobster, Anchor }

    // ---- the squid (top corners): pointed mantle with fins, tentacles trailing ----

    private const string KrakenHead =
        "M24,0.8 C26.9,3.8 28.6,8.2 28.2,12.8 C28.7,14.8 28.3,16.4 27,17.4 "
        + "L21,17.4 C19.7,16.4 19.3,14.8 19.8,12.8 C19.4,8.2 21.1,3.8 24,0.8 Z "
        + "M21.9,4.4 L17.6,6.6 L21.1,7.9 Z M26.1,4.4 L30.4,6.6 L26.9,7.9 Z";

    private const string KrakenArms =
        "M20.6,17 C16.4,18.6 13.9,21.8 14.1,25.8 C15.5,23 17.8,21.2 20.9,20.6 "
        + "C19.8,19.4 19.7,18.2 20.6,17 Z "
        + "M27.4,17 C31.6,18.6 34.1,21.8 33.9,25.8 C32.5,23 30.2,21.2 27.1,20.6 "
        + "C28.2,19.4 28.3,18.2 27.4,17 Z";

    private const string KrakenCurls =
        "M15.2,25 C13.6,27.4 11.4,28.2 9.4,27.2 C11.4,26.7 12.9,25.6 13.8,23.8 Z "
        + "M32.8,25 C34.4,27.4 36.6,28.2 38.6,27.2 C36.6,26.7 35.1,25.6 34.2,23.8 Z "
        + "M22.3,17.8 C21.5,20.4 21.6,22.8 22.6,25 C22.9,22.6 22.9,20.2 22.3,17.8 Z "
        + "M25.7,17.8 C26.5,20.4 26.4,22.8 25.4,25 C25.1,22.6 25.1,20.2 25.7,17.8 Z";

    private const string KrakenEyes =
        "M21.4,14.4 a0.95,0.95 0 1 0 0.01,0 Z M25.7,14.4 a0.95,0.95 0 1 0 0.01,0 Z";

    // ---- the sea-dragon (top centre): horned head, spiked arch, curled tail --------

    private const string DragonBody =
        "M5.6,20 C7,12.5 11,8.5 15.5,9.8 C19.5,10.9 20.5,8.2 24,7.8 "
        + "C27.5,8.2 29,10.9 33,9.8 C37.5,8.5 41.5,12 43,17.5 "
        + "C41,14.6 38,13 34,13.8 C30,14.6 28,11.9 24,11.5 "
        + "C20,11.9 18,14.6 14.5,13.8 C10.5,13 8,15.5 6.8,20.5 Z";

    private const string DragonHead =
        "M0.8,17.8 C-0.2,13.2 2.4,10 6.4,10.9 C9.4,11.6 10.8,14.4 9.6,17.2 "
        + "C8.9,18.9 6.8,19.9 4.6,19.6 L1.2,22.2 L2.4,19.2 C1.6,18.9 1,18.4 0.8,17.8 Z";

    private const string DragonHorns =
        "M4.2,11.2 L2.2,6 L6.4,9.7 Z M7.2,11.4 L6.8,6.2 L10.2,10 Z";

    private const string DragonSpikes =
        "M12.6,10 L14.2,5.6 L16.2,9.4 Z M19.4,8.3 L21,4 L22.8,7.9 Z "
        + "M25.4,7.9 L27.2,4.2 L28.8,8.2 Z M31.8,9.4 L33.8,5.6 L35.4,10 Z";

    private const string DragonTail =
        "M43,17.5 C45.6,15 47.2,16.8 46.2,19.4 C45.4,21.4 42.8,21.4 42,19.6 "
        + "C42.9,20 44.2,19.6 44.4,18.4 C44.6,17.2 43.8,16.7 43,17.5 Z";

    private const string DragonEye = "M3.6,13.6 a1.1,1.1 0 1 0 0.01,0 Z";

    // ---- the coiled serpent (mid sides): wrapped round its medallion ---------------

    private const string CoilRing =
        "M12.2,12.2 C15,6.2 31,5 36.4,11.2 C40.2,15.8 37.4,22.6 31,25 "
        + "C26.2,26.8 20,26.4 16,23.8 C13.4,22.4 12.6,20.6 14.2,19.2 "
        + "C15,21 17.2,22.4 20.4,22.8 C25.4,23.4 31,21.6 32.8,18 "
        + "C34.4,14.6 32,10.6 27,9.4 C22.4,8.3 16.6,9.6 14,13.6 Z";

    private const string CoilHead =
        "M12.6,13.4 C9.4,13.8 7,12 7,9.4 C7,6.8 9.4,5.2 12,5.9 "
        + "C14.4,6.5 15.7,8.7 15,10.9 C14.9,11.3 14.6,11.8 14.2,12.2 "
        + "L10.6,14.9 L11.8,12.8 C12.1,12.9 12.4,13.2 12.6,13.4 Z";

    private const string CoilFins =
        "M36.6,11.8 L40.4,10 L39,14 Z M38.2,16.6 L42,16.4 L39.4,19.4 Z "
        + "M35,21.6 L38.2,23.4 L34.4,24.4 Z";

    private const string CoilTail =
        "M16.2,23.7 C12.8,23.4 11.4,21 12.8,19 C13,20.8 14.3,21.9 16.6,22 Z "
        + "M13.4,19.9 C11.2,19.4 10.4,17.8 11.4,16.4 C11.6,17.8 12.4,18.7 14,18.9 Z";

    private const string CoilEye = "M9.7,8.3 a1.1,1.1 0 1 0 0.01,0 Z";

    // ---- the tentacle (upper and lower sides): an S-curl, suckers inward ---------

    private const string TentacleBody =
        "M28.4,27.6 C23.6,27.2 20.4,24.4 20,20.8 C19.6,17.4 21.6,14.4 24.8,12.2 "
        + "C27.2,10.6 28.2,8.6 27.6,6.4 C27,4.4 25,3.2 22.8,3.6 "
        + "C24.8,1.8 27.8,2.4 29.4,4.6 C31,6.9 30.4,10 28.1,12.2 "
        + "C25.4,14.8 23.9,17.4 24.3,19.9 C24.7,22.6 26.2,24.6 28.9,25.4 Z";

    private const string TentacleTip =
        "M23.2,3.8 C21.2,4.4 20.2,5.9 20.4,7.8 C21.3,6.5 22.6,5.8 24.3,5.8 Z";

    private const string TentacleSuckers =
        "M22.9,19.7 a0.85,0.85 0 1 0 0.01,0 Z M23.2,16.4 a0.85,0.85 0 1 0 0.01,0 Z "
        + "M24.7,13.5 a0.85,0.85 0 1 0 0.01,0 Z M26.4,10.6 a0.85,0.85 0 1 0 0.01,0 Z";

    // ---- the lobster (bottom corners): claws raised, medallion as carapace ---------

    private const string LobsterClaws =
        "M14.6,11.4 C10.8,9.6 8.6,6.4 9.6,3.4 C10.4,5.6 12,7.2 14.2,7.6 "
        + "C13.4,6.2 13.6,4.6 14.8,3.6 C15.2,5.4 16.4,6.8 18.2,7.4 "
        + "C17.8,9.2 16.6,10.8 14.6,11.4 Z "
        + "M33.4,11.4 C37.2,9.6 39.4,6.4 38.4,3.4 C37.6,5.6 36,7.2 33.8,7.6 "
        + "C34.6,6.2 34.4,4.6 33.2,3.6 C32.8,5.4 31.6,6.8 29.8,7.4 "
        + "C30.2,9.2 31.4,10.8 33.4,11.4 Z";

    private const string LobsterArms =
        "M15.4,10.6 C17,11.8 18.6,12.4 20.4,12.6 L20,14.6 C17.8,14.2 16.2,13 15,11.4 Z "
        + "M32.6,10.6 C31,11.8 29.4,12.4 27.6,12.6 L28,14.6 C30.2,14.2 31.8,13 33,11.4 Z";

    private const string LobsterLegs =
        "M16.2,16.4 C13.8,16 11.8,16.6 10.4,18 L11.6,19 C12.8,17.8 14.4,17.3 16.4,17.6 Z "
        + "M16.4,19 C14.2,19.2 12.4,20.2 11.4,21.8 L12.8,22.6 C13.6,21.2 15,20.4 16.8,20.2 Z "
        + "M31.8,16.4 C34.2,16 36.2,16.6 37.6,18 L36.4,19 C35.2,17.8 33.6,17.3 31.6,17.6 Z "
        + "M31.6,19 C33.8,19.2 35.6,20.2 36.6,21.8 L35.2,22.6 C34.4,21.2 33,20.4 31.2,20.2 Z";

    private const string LobsterTail =
        "M21.6,21.6 L26.4,21.6 L26,24 L22,24 Z "
        + "M22.2,24.4 L25.8,24.4 L25.4,26.2 L22.6,26.2 Z "
        + "M20.4,26.6 C21.6,26.4 22.6,26.6 24,27.6 C25.4,26.6 26.4,26.4 27.6,26.6 "
        + "C26.6,28.4 25.4,29 24,29 C22.6,29 21.4,28.4 20.4,26.6 Z";

    private const string LobsterAntennae =
        "M20.6,10.4 C19.2,7.6 19.4,4.8 21.2,2.4 C20.6,5 21,7.4 22.4,9.6 Z "
        + "M27.4,10.4 C28.8,7.6 28.6,4.8 26.8,2.4 C27.4,5 27,7.4 25.6,9.6 Z";

    // ---- the anchor (bottom centre) ------------------------------------------------

    private const string AnchorShape =
        "M24,2.4 C25.8,2.4 27.2,3.8 27.2,5.6 C27.2,7 26.4,8.1 25.2,8.6 "
        + "L25.2,10.4 L30,10.4 L30,12.6 L25.2,12.6 L25.2,22.8 "
        + "C28.4,22 30.8,19.8 31.8,16.8 L33.8,19 C32.2,23.6 28.6,26.6 24,27.4 "
        + "C19.4,26.6 15.8,23.6 14.2,19 L16.2,16.8 C17.2,19.8 19.6,22 22.8,22.8 "
        + "L22.8,12.6 L18,12.6 L18,10.4 L22.8,10.4 L22.8,8.6 "
        + "C21.6,8.1 20.8,7 20.8,5.6 C20.8,3.8 22.2,2.4 24,2.4 Z "
        + "M24,4.2 C23.2,4.2 22.6,4.8 22.6,5.6 C22.6,6.4 23.2,7 24,7 "
        + "C24.8,7 25.4,6.4 25.4,5.6 C25.4,4.8 24.8,4.2 24,4.2 Z";

    private const string AnchorFlukes =
        "M14.2,19 L11.8,17.2 C12.4,20 13.8,22.2 15.8,23.8 Z "
        + "M33.8,19 L36.2,17.2 C35.6,20 34.2,22.2 32.2,23.8 Z";

    // ---- assembly -------------------------------------------------------------------

    /// <summary>The game frame's creature for each position.</summary>
    private static Kind KindOf(string edge, int label) => edge switch
    {
        "top" => label == 2 ? Kind.Dragon : Kind.Kraken,
        "right" => label == 5 ? Kind.Coil : Kind.Tentacle,
        "left" => label == 11 ? Kind.Coil : Kind.Tentacle,
        _ => label == 8 ? Kind.Anchor : Kind.Lobster,
    };

    /// <summary>
    /// The frame is symmetric: each right-hand creature mirrors its left-hand twin,
    /// and the top-right kraken mirrors the top-left one. Symmetric creatures
    /// (kraken, lobster, anchor) need no flip; the rest flip on the right/lower half.
    /// </summary>
    private static bool MirrorOf(string edge, int label) => edge == "right";

    private static string[] Parts(Kind kind) => kind switch
    {
        Kind.Kraken => [KrakenArms, KrakenCurls, KrakenHead, KrakenEyes],
        Kind.Dragon => [DragonBody, DragonSpikes, DragonHead, DragonHorns, DragonTail, DragonEye],
        Kind.Tentacle => [TentacleBody, TentacleTip, TentacleSuckers],
        Kind.Coil => [CoilRing, CoilFins, CoilHead, CoilTail, CoilEye],
        Kind.Lobster => [LobsterClaws, LobsterArms, LobsterLegs, LobsterTail, LobsterAntennae],
        _ => [AnchorShape, AnchorFlukes],
    };

    /// <summary>Eyes render in shadow ink over the fill, so they are listed apart.</summary>
    private static readonly string[] InkParts =
        [KrakenEyes, DragonEye, CoilEye, TentacleSuckers];

    /// <summary>Medallion centres differ per creature so the number never fights art.
    /// All on the centreline, so mirroring cannot displace the label.</summary>
    private static (double X, double Y, double R) Medallion(Kind kind) => kind switch
    {
        Kind.Kraken => (24, 23.2, 4.9),
        Kind.Coil => (24, 16, 5.6),
        Kind.Tentacle => (24, 21.2, 4.9),
        Kind.Lobster => (24, 15.6, 5.6),
        Kind.Anchor => (24, 15.6, 5.2),
        _ => (24, 16.4, 6.2),
    };

    /// <summary>
    /// Build one ornament, mirrored for the symmetric half and rotated to lie along
    /// the board edge it guards.
    /// </summary>
    internal static FrameworkElement Build(int label, string edge, State state, double scale = 1.0)
    {
        var (fill, stroke, ink) = Palette(state);
        var kind = KindOf(edge, label);

        var art = new Canvas
        {
            Width = DesignWidth,
            Height = DesignHeight,
            Background = Brushes.Transparent,
        };

        var parts = Parts(kind);
        var (mx, my, mr) = Medallion(kind);
        var medallion = $"M{mx},{my - mr} A{mr},{mr} 0 1 1 {mx},{my + mr} A{mr},{mr} 0 1 1 {mx},{my - mr} Z";

        // Cast shadow first: the same silhouettes a pixel down, which is what reads
        // as relief rather than a flat sticker.
        foreach (var part in parts.Prepend(medallion))
            art.Children.Add(Figure(part, ShadowBrush, null, 0, 1.5));

        // The medallion sits UNDER the creature so claws and coils overlap it; eyes
        // go over the fill in shadow ink.
        art.Children.Add(Figure(medallion, fill, stroke, 0, 0));
        foreach (var part in parts)
            art.Children.Add(InkParts.Contains(part)
                ? Figure(part, ShadowBrush, null, 0, 0)
                : Figure(part, fill, stroke, 0, 0));

        if (MirrorOf(edge, label))
        {
            art.RenderTransform = new ScaleTransform(-1, 1);
            art.RenderTransformOrigin = new Point(0.5, 0.5);
        }

        var carving = new Viewbox
        {
            Child = art,
            Width = DesignWidth * scale,
            Height = DesignHeight * scale,
            Stretch = Stretch.Uniform,
        };

        // Left and right figurines are turned to run along their edge, the way the
        // carving follows the frame in game -- except the tentacle, which stands
        // upright against the frame in the game art too.
        var quarter = kind == Kind.Tentacle
            ? 0.0
            : edge switch { "left" => 90.0, "right" => -90.0, _ => 0.0 };
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
            FontSize = 8.6 * scale,
            Foreground = ink,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = LabelNudge(edge, kind, scale),
        });
        return host;
    }

    /// <summary>Keep the number on each creature's medallion after rotation/mirroring:
    /// the medallion's design-space offset from centre maps into host space.</summary>
    private static Thickness LabelNudge(string edge, Kind kind, double scale)
    {
        var (mx, my, _) = Medallion(kind);
        var dy = (my - DesignHeight / 2.0) * 2 * scale;
        // The tentacle never rotates, so its medallion offset stays vertical -- and
        // mirroring flips its x offset.
        if (kind == Kind.Tentacle)
        {
            var dx = (mx - DesignWidth / 2.0) * 2 * scale * (edge == "right" ? -1 : 1);
            return new Thickness(dx, dy, 0, 0);
        }
        return edge switch
        {
            "left" => new Thickness(dy, 0, 0, 0),     // +90: design +y becomes screen +x
            "right" => new Thickness(0, 0, dy, 0),    // -90 with mirror: +y becomes -x
            _ => new Thickness(0, dy, 0, 0),
        };
    }

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
