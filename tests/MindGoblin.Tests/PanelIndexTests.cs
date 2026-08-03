using MindGoblin.Core.Voyage;

namespace MindGoblin.Tests;

/// <summary>
/// A PANEL INDEX IS A PHYSICAL POSITION, not the n-th occupied cell.
///
/// Everything the session stores per chart -- its copied modifier text, the required
/// star, the exclude -- is keyed by that index, and the panel is re-read from scratch
/// every Identify. So the index of a cell may not depend on which OTHER cells happen to
/// hold a chart, and a voyage consumes nine charts at a time: the leading row empties
/// routinely. Anchoring the located grid on the first inked band broke exactly that,
/// silently, by re-keying every chart onto its neighbour's modifiers.
///
/// The panel here is painted rather than captured because the property is about
/// arithmetic, not about artwork: a fixture can only be emptied where it is already
/// empty, and this needs a leading row and a leading column that can be taken away.
/// The real capture that reproduced the bug is pinned in
/// <see cref="OtherResolutionTests"/>.
/// </summary>
public class PanelIndexTests
{
    private const int CanvasWidth = 2560;
    private const int CanvasHeight = 1440;
    private const int Tile = 26;
    private const double Pitch = 67;

    private static readonly ChartPanelReader.Options Calibration = new();

    // Twenty pixels off the calibration, which is the drift the native 1080p capture
    // showed. It is what makes the LOCATED grid win: the calibration's own cells catch a
    // sliver of tile, fall under OccupiedThreshold and read nothing, so what this test
    // observes is the located grid's indexing and not the calibration's.
    private static readonly int OriginX = Calibration.OriginX + 20;
    private static readonly int OriginY = Calibration.OriginY + 20;

    private static readonly (int Row, int Col)[] Charts =
        [.. Enumerable.Range(0, 4).SelectMany(
            row => Enumerable.Range(0, 4).Select(col => (Row: row, Col: col)))];

    /// <summary>
    /// A panel painted where the test wants it: green tiles with a dark path running out
    /// to all four edges, which is the whole of what the reader looks at.
    /// </summary>
    private sealed class PaintedPanel : IPixels
    {
        private static readonly (int R, int G, int B) Parchment = (90, 80, 70);
        private static readonly (int R, int G, int B) Green = (60, 180, 80);
        private static readonly (int R, int G, int B) Line = (10, 10, 10);

        private readonly byte[] _ink = new byte[CanvasWidth * CanvasHeight];
        private readonly Func<int, int, bool>? _blanked;

        public PaintedPanel(Func<int, int, bool>? blanked = null)
        {
            _blanked = blanked;
            foreach (var (row, col) in Charts) Paint(row, col);
        }

        public int Width => CanvasWidth;
        public int Height => CanvasHeight;

        public (int R, int G, int B) At(int x, int y) =>
            _blanked?.Invoke(x, y) == true
                ? Parchment
                : _ink[y * CanvasWidth + x] switch { 1 => Green, 2 => Line, _ => Parchment };

        private void Paint(int row, int col)
        {
            var cx = CentreX(col);
            var cy = CentreY(row);
            const int half = Tile / 2;
            for (var y = cy - half; y < cy + half; y++)
                for (var x = cx - half; x < cx + half; x++)
                    _ink[y * CanvasWidth + x] = 1;

            // Each arm stops short of the middle. A path drawn straight through would
            // leave its columns with no green at all, and the contiguous run that bounds
            // the glyph would split on it -- the 1080p failure, reproduced in a fixture
            // that is meant to be testing something else.
            for (var d = -1; d <= 1; d++)
                for (var t = 4; t <= half; t++)
                {
                    _ink[(cy - t) * CanvasWidth + cx + d] = 2;
                    _ink[(cy + t - 1) * CanvasWidth + cx + d] = 2;
                    _ink[(cy + d) * CanvasWidth + cx - t] = 2;
                    _ink[(cy + d) * CanvasWidth + cx + t - 1] = 2;
                }
        }
    }

    private static int CentreX(int col) => (int)Math.Round(OriginX + col * Pitch);
    private static int CentreY(int row) =>
        (int)Math.Round(OriginY + row * Pitch) + Calibration.GlyphOffsetY;

    private static int[] Decode(Func<int, int, bool>? blanked = null) =>
        [.. new ChartPanelReader().Read(new PaintedPanel(blanked)).Select(c => c.Index).Order()];

    [Fact]
    public void TheWholePanelReadsFirst()
    {
        // Four rows of four, so a leading row and a leading column can both be taken
        // away and still leave the grid something to be found by.
        Assert.Equal([1, 2, 3, 4, 7, 8, 9, 10, 13, 14, 15, 16, 19, 20, 21, 22], Decode());
    }

    [Fact]
    public void EveryChartKeepsItsIndexWhenTheLeadingColumnEmpties()
    {
        var full = Decode();
        var without = Decode((x, _) => Math.Abs(x - CentreX(0)) <= 16);

        // Column 0 holds indices 1, 7, 13, 19. Everything else must be untouched --
        // anchoring on the first band renumbered the survivors 1, 2, 3, 7, 8, 9, ...
        Assert.Equal(full.Where(i => (i - 1) % 6 != 0), without);
    }

    [Fact]
    public void EveryChartKeepsItsIndexWhenTheLeadingRowEmpties()
    {
        var full = Decode();
        var without = Decode((_, y) => Math.Abs(y - CentreY(0)) <= 16);

        // This is the one that happens in play: nine charts go on a voyage and the top
        // of the panel empties.
        Assert.Equal(full.Where(i => i > Calibration.Cols), without);
    }

    /// <summary>
    /// The grid found in the image reports itself AS the screen it was found on, so a
    /// caller that hovers can hand it back to ForScreen and get the same numbers. The
    /// slurp does exactly that, and a rescale applied twice aims at a different cell.
    /// </summary>
    [Fact]
    public void TheResolvedGridIsAlreadyScaledToTheCapture()
    {
        var px = new PaintedPanel();
        var resolved = new ChartPanelReader().Resolve(px);

        Assert.Equal(px.Width, resolved.ReferenceWidth);
        Assert.Equal(px.Height, resolved.ReferenceHeight);
        Assert.Same(resolved, resolved.ForScreen(px.Width, px.Height));
    }

    /// <summary>
    /// Read is Resolve then ReadWith, which is the split the app needs: it keeps the
    /// grid to hover with, and hovering a grid the decode did not use copies the
    /// neighbouring chart -- whose text parses perfectly.
    /// </summary>
    [Fact]
    public void ReadingWithAResolvedGridIsTheSameAsReading()
    {
        var px = new PaintedPanel();
        var reader = new ChartPanelReader();

        Assert.Equal(reader.Read(px), reader.ReadWith(px, reader.Resolve(px)));
    }
}
