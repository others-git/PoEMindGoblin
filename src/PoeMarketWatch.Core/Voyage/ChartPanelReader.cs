namespace PoeMarketWatch.Core.Voyage;

/// <summary>Minimal pixel source, so reading is testable from a PNG with no game running.</summary>
public interface IPixels
{
    int Width { get; }
    int Height { get; }
    (int R, int G, int B) At(int x, int y);
}

/// <summary>
/// Reads the 6x10 chart panel off a screenshot.
///
/// How the glyphs actually work, established from real pixels rather than guessed: each
/// chart icon is a GREEN tile with the path drawn on it as BLACK lines. A path is open on
/// a side when a black line runs to that side of the tile. So this does not template-match
/// shapes at all -- it reads the four openings directly and derives shape and rotation
/// from them, which is exactly what the solver needs and is immune to artwork changes
/// that leave the topology alone.
///
/// Two details that cost several wrong attempts:
///   * The green GLOW extends past the tile, so a bounding box of green pixels is larger
///     than the tile. Openings are therefore measured with a margin, not at the box edge.
///   * A Corner's path bends and never crosses the tile centre, so scanning only the
///     centre row/column reports it as an End. Each edge is scanned across its middle
///     60% instead, which also skips the tile corners and the parchment artwork.
/// </summary>
public sealed class ChartPanelReader
{
    public sealed record Options
    {
        /// <summary>Centre of the first cell (row 0, col 0), in screen pixels.</summary>
        public int OriginX { get; init; } = 1768;
        public int OriginY { get; init; } = 419;

        /// <summary>Cell-to-cell spacing. Square in practice.</summary>
        public int Pitch { get; init; } = 67;

        public int Rows { get; init; } = 10;
        public int Cols { get; init; } = 6;

        /// <summary>The glyph sits slightly above cell centre; the level text is below it.</summary>
        public int GlyphOffsetY { get; init; } = -3;

        /// <summary>Half-size of the window searched for a glyph.</summary>
        public int GlyphHalf { get; init; } = 18;

        /// <summary>Below this many green pixels the cell is treated as empty.</summary>
        public int OccupiedThreshold { get; init; } = 150;

        /// <summary>How close to the tile edge a path must reach to count as open.</summary>
        public int EdgeMargin { get; init; } = 3;

        /// <summary>Fraction of each edge scanned, centred -- avoids corners and artwork.</summary>
        public double EdgeSpan { get; init; } = 0.60;

        /// <summary>Columns/rows of black that must touch an edge before it counts.</summary>
        public int OpenThreshold { get; init; } = 2;

        /// <summary>Reference resolution these coordinates were calibrated at.</summary>
        public int ReferenceWidth { get; init; } = 2560;
        public int ReferenceHeight { get; init; } = 1440;

        /// <summary>Rescale the calibration to another resolution.</summary>
        public Options ScaledTo(int width, int height)
        {
            var sx = width / (double)ReferenceWidth;
            var sy = height / (double)ReferenceHeight;
            var s = Math.Min(sx, sy);
            return this with
            {
                OriginX = (int)Math.Round(OriginX * sx),
                OriginY = (int)Math.Round(OriginY * sy),
                Pitch = (int)Math.Round(Pitch * s),
                GlyphHalf = Math.Max(6, (int)Math.Round(GlyphHalf * s)),
                GlyphOffsetY = (int)Math.Round(GlyphOffsetY * s),
                OccupiedThreshold = Math.Max(20, (int)Math.Round(OccupiedThreshold * s * s)),
                ReferenceWidth = width,
                ReferenceHeight = height,
            };
        }
    }

    public sealed record ReadCell(
        int Index, int Row, int Col,
        bool North, bool East, bool South, bool West)
    {
        public int OpenCount => (North ? 1 : 0) + (East ? 1 : 0) + (South ? 1 : 0) + (West ? 1 : 0);

        /// <summary>The shape implied by the openings, or null if they match none.</summary>
        public ChartShape? Shape => (North, East, South, West) switch
        {
            (true, false, false, false) or (false, true, false, false)
                or (false, false, true, false) or (false, false, false, true) => ChartShape.End,
            (true, true, false, false) or (false, true, true, false)
                or (false, false, true, true) or (true, false, false, true) => ChartShape.Corner,
            (true, false, true, false) or (false, true, false, true) => ChartShape.Straight,
            (true, true, true, false) or (false, true, true, true)
                or (true, false, true, true) or (true, true, false, true) => ChartShape.Junction,
            (true, true, true, true) => ChartShape.Crossing,
            _ => null,
        };

        /// <summary>Rotation matching <see cref="ChartFace"/>'s base orientations.</summary>
        public int Rotation
        {
            get
            {
                if (Shape is not { } shape) return 0;
                foreach (var rot in ChartFace.DistinctRotations(shape))
                {
                    var face = new ChartFace(shape, rot);
                    if (face.IsOpen(Side.North) == North && face.IsOpen(Side.East) == East
                        && face.IsOpen(Side.South) == South && face.IsOpen(Side.West) == West)
                        return rot;
                }
                return 0;
            }
        }

        public override string ToString() =>
            $"#{Index} r{Row}c{Col} N{(North ? 1 : 0)}E{(East ? 1 : 0)}S{(South ? 1 : 0)}W{(West ? 1 : 0)} {Shape?.ToString() ?? "?"}";
    }

    private readonly Options _o;

    public ChartPanelReader(Options? options = null) => _o = options ?? new Options();

    /// <summary>The path glyph is a saturated green distinct from the parchment behind it.</summary>
    private static bool IsGreen(int r, int g, int b) => g > 90 && g - r > 35 && g - b > 25;

    /// <summary>The path itself is drawn in near-black.</summary>
    private static bool IsDark(int r, int g, int b) => r + g + b < 150;

    public IReadOnlyList<ReadCell> Read(IPixels pixels)
    {
        var o = pixels.Width == _o.ReferenceWidth && pixels.Height == _o.ReferenceHeight
            ? _o
            : _o.ScaledTo(pixels.Width, pixels.Height);

        var found = new List<ReadCell>();
        for (var row = 0; row < o.Rows; row++)
        {
            for (var col = 0; col < o.Cols; col++)
            {
                var cx = o.OriginX + col * o.Pitch;
                var cy = o.OriginY + row * o.Pitch + o.GlyphOffsetY;
                if (ReadCellAt(pixels, o, cx, cy, row, col) is { } cell) found.Add(cell);
            }
        }
        return found;
    }

    private static ReadCell? ReadCellAt(IPixels px, Options o, int cx, int cy, int row, int col)
    {
        var h = o.GlyphHalf;
        var size = h * 2;
        if (cx - h < 0 || cy - h < 0 || cx + h >= px.Width || cy + h >= px.Height) return null;

        var green = new bool[size, size];
        var dark = new bool[size, size];
        var count = 0;
        int minX = size, minY = size, maxX = -1, maxY = -1;

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var (r, g, b) = px.At(cx - h + x, cy - h + y);
                if (IsGreen(r, g, b))
                {
                    green[y, x] = true;
                    count++;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
                if (IsDark(r, g, b)) dark[y, x] = true;
            }
        }

        if (count < o.OccupiedThreshold || maxX < 0) return null;

        var bw = maxX - minX + 1;
        var bh = maxY - minY + 1;
        var padX = (int)(bw * (1 - o.EdgeSpan) / 2);
        var padY = (int)(bh * (1 - o.EdgeSpan) / 2);
        var m = o.EdgeMargin;

        var north = 0; var south = 0; var west = 0; var east = 0;
        for (var x = minX + padX; x <= maxX - padX; x++)
        {
            for (var d = 0; d <= m; d++)
            {
                if (dark[minY + d, x]) { north++; break; }
            }
            for (var d = 0; d <= m; d++)
            {
                if (dark[maxY - d, x]) { south++; break; }
            }
        }
        for (var y = minY + padY; y <= maxY - padY; y++)
        {
            for (var d = 0; d <= m; d++)
            {
                if (dark[y, minX + d]) { west++; break; }
            }
            for (var d = 0; d <= m; d++)
            {
                if (dark[y, maxX - d]) { east++; break; }
            }
        }

        var t = o.OpenThreshold;
        return new ReadCell(row * o.Cols + col + 1, row, col,
            north >= t, east >= t, south >= t, west >= t);
    }
}
