using System.Text.Json;

namespace MindGoblin.Core.Voyage;

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

        // ---- persistence ---------------------------------------------------------

        /// <summary>
        /// Calibration lives in a file because it CANNOT be static. These coordinates
        /// were measured at 2560x1440 windowed fullscreen; UI scale, window mode, an
        /// ultrawide monitor or a patch that moves the panel all shift them, and
        /// <see cref="ScaledTo"/> only covers the pure-resolution case. A tool that works
        /// at exactly one resolution works for exactly one person.
        /// </summary>
        public static string DefaultPath => SettingsFolder.FileIn("panel-calibration.json");

        private static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        /// <summary>Load the user's calibration, or the measured defaults if there is none.</summary>
        public static Options Load(string? path = null)
        {
            path ??= DefaultPath;
            if (!File.Exists(path)) return new Options();
            try
            {
                // A hand-edited file with a zero pitch would divide the panel into
                // nothing; fall back rather than read gibberish off the screen.
                var loaded = JsonSerializer.Deserialize<Options>(File.ReadAllText(path), Json);
                return loaded is { Pitch: > 0, Rows: > 0, Cols: > 0, GlyphHalf: > 0 }
                    ? loaded
                    : new Options();
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                return new Options();
            }
        }

        public void Save(string? path = null)
        {
            path ??= DefaultPath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, path, overwrite: true);
        }

        /// <summary>Write the defaults out so there is something to edit.</summary>
        public static void WriteDefaultsIfMissing(string? path = null)
        {
            path ??= DefaultPath;
            if (!File.Exists(path)) new Options().Save(path);
        }
    }

    public sealed record ReadCell(
        int Index, int Row, int Col,
        bool North, bool East, bool South, bool West)
    {
        /// <summary>
        /// Area level from the "L:83" caption, or null when it could not be read.
        /// Null is deliberate: see <see cref="LevelReader"/>, which refuses to guess at a
        /// digit it has no template for rather than report a plausible wrong level.
        /// </summary>
        public int? Level { get; init; }


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
            $"#{Index} r{Row}c{Col} N{(North ? 1 : 0)}E{(East ? 1 : 0)}S{(South ? 1 : 0)}W{(West ? 1 : 0)} "
            + $"{Shape?.ToString() ?? "?"}{(Level is { } l ? $" L{l}" : "")}";
    }

    private readonly Options _o;
    private readonly LevelReader? _levels;

    public ChartPanelReader(Options? options = null, LevelReader? levels = null)
    {
        _o = options ?? new Options();
        _levels = levels ?? new LevelReader();
    }

    /// <summary>The longest contiguous stretch of lines dense enough to be glyph
    /// rather than stray glow. Ties go to the earlier run; a fleck loses either way.</summary>
    private static (int Min, int Max) LongestRun(int[] counts)
    {
        const int floor = 3;
        int bestMin = 0, bestMax = -1, runStart = -1;
        for (var i = 0; i <= counts.Length; i++)
        {
            var dense = i < counts.Length && counts[i] >= floor;
            if (dense && runStart < 0) runStart = i;
            if (!dense && runStart >= 0)
            {
                if (i - runStart > bestMax - bestMin + 1) { bestMin = runStart; bestMax = i - 1; }
                runStart = -1;
            }
        }
        return (bestMin, bestMax);
    }

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
                var cellY = o.OriginY + row * o.Pitch;
                var cy = cellY + o.GlyphOffsetY;
                if (ReadCellAt(pixels, o, cx, cy, row, col) is not { } cell) continue;
                // Only bother with the caption once a glyph is confirmed -- an empty cell
                // has no level to read.
                found.Add(cell with { Level = _levels?.Read(pixels, cx, cellY) });
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
        var rowGreen = new int[size];
        var colGreen = new int[size];

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var (r, g, b) = px.At(cx - h + x, cy - h + y);
                if (IsGreen(r, g, b))
                {
                    green[y, x] = true;
                    count++;
                    rowGreen[y]++;
                    colGreen[x]++;
                }
                if (IsDark(r, g, b)) dark[y, x] = true;
            }
        }

        if (count < o.OccupiedThreshold) return null;

        // ROBUST bounds, not min/max: 3.29.1 brightened the glyph glow, and an
        // isolated fleck of green ABOVE a glyph stretched a min/max box into the dark
        // artwork -- where the north edge band then found "paths" and read E+S
        // Corners as Junctions. A density floor alone cannot fix it, because a truly
        // open edge's exit glow is real green at similar density. What separates a
        // fleck from the glyph is CONTIGUITY: bounds come from the longest contiguous
        // run of dense rows (and columns), so nothing detached can join the box.
        var (minY, maxY) = LongestRun(rowGreen);
        var (minX, maxX) = LongestRun(colGreen);
        if (maxX < 0 || maxY < 0) return null;

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
