using MindGoblin.Core.Voyage;

namespace MindGoblin.Tests;

/// <summary>
/// LOCATE CORRECTS DRIFT; IT DOES NOT SEARCH THE DESKTOP.
///
/// The grid is chosen by counting how many shapes each candidate decodes, and more
/// always wins. That is fine on a full panel and fatal on a nearly empty one: with a
/// single chart the true grid scores ONE, so any patch of green elsewhere on the screen
/// that decodes two things beats it.
///
/// Field-reported on a real capture — tab 2 held one chart and the reader announced
/// five. The winning grid sat at x 279 while the panel was at x 1768, a thousand pixels
/// away, with five charts invented out of the game's own chrome.
/// </summary>
public class SparsePanelTests
{
    private const int CanvasWidth = 2560;
    private const int CanvasHeight = 1440;
    private const int Tile = 26;

    private static readonly ChartPanelReader.Options Calibration = new();

    /// <summary>
    /// One real chart on the panel, and a block of grid-shaped green far to the LEFT --
    /// the shape of the bug: decoys elsewhere on screen outnumbering the truth.
    /// </summary>
    private sealed class SparseCanvas : IPixels
    {
        private static readonly (int R, int G, int B) Parchment = (90, 80, 70);
        private static readonly (int R, int G, int B) Green = (60, 180, 80);
        private static readonly (int R, int G, int B) Line = (10, 10, 10);

        private readonly byte[] _ink = new byte[CanvasWidth * CanvasHeight];

        public SparseCanvas(bool withDecoys)
        {
            // The one chart the player actually has, on the calibration's own grid.
            Paint((int)Math.Round(Calibration.OriginX + 3 * Calibration.Pitch),
                  (int)Math.Round(Calibration.OriginY + 3 * Calibration.Pitch));

            if (!withDecoys) return;
            // A run of tiles at the far left, spaced like the panel so it forms a
            // plausible grid. Five of them, which is what the real capture invented.
            for (var i = 0; i < 5; i++)
                Paint(320 + (int)Math.Round(i * Calibration.Pitch), 300);
        }

        public int Width => CanvasWidth;
        public int Height => CanvasHeight;

        public (int R, int G, int B) At(int x, int y) =>
            _ink[y * CanvasWidth + x] switch { 1 => Green, 2 => Line, _ => Parchment };

        private void Paint(int cx, int cy)
        {
            const int half = Tile / 2;
            for (var y = cy - half; y < cy + half; y++)
                for (var x = cx - half; x < cx + half; x++)
                    _ink[y * CanvasWidth + x] = 1;

            // Arms to all four edges, stopping short of the middle: a Crossing.
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

    [Fact]
    public void ASinglePanelChartIsNotOutvotedByGreenElsewhereOnScreen()
    {
        var reader = new ChartPanelReader(Calibration);
        var cells = reader.Read(new SparseCanvas(withDecoys: true));

        Assert.Single(cells, c => c.Shape is not null);
    }

    /// <summary>The grid chosen must still be the PANEL's, not merely one that decodes
    /// the right number of things somewhere else.</summary>
    [Fact]
    public void TheResolvedGridStaysOnThePanel()
    {
        var reader = new ChartPanelReader(Calibration);
        var grid = reader.Resolve(new SparseCanvas(withDecoys: true));

        // Well inside the three-pitch bound, and nowhere near the decoys at x 320.
        Assert.InRange(grid.OriginX,
            Calibration.OriginX - Calibration.Pitch, Calibration.OriginX + Calibration.Pitch);
        Assert.InRange(grid.OriginY,
            Calibration.OriginY - Calibration.Pitch, Calibration.OriginY + Calibration.Pitch);
    }

    /// <summary>And the chart keeps its PANEL INDEX: a sparse panel must not renumber
    /// the one chart on it, or its stored modifier text follows the wrong tile.</summary>
    [Fact]
    public void TheChartKeepsThePositionItActuallyOccupies()
    {
        var reader = new ChartPanelReader(Calibration);
        var cell = reader.Read(new SparseCanvas(withDecoys: true))
                         .Single(c => c.Shape is not null);

        Assert.Equal(3, cell.Row);
        Assert.Equal(3, cell.Col);
        Assert.Equal(3 * Calibration.Cols + 3 + 1, cell.Index);
    }

    /// <summary>The guard must not cost the ordinary case: with no decoys the same
    /// single chart reads exactly the same way.</summary>
    [Fact]
    public void ACleanSparsePanelReadsTheSame()
    {
        var reader = new ChartPanelReader(Calibration);
        var cell = reader.Read(new SparseCanvas(withDecoys: false))
                         .Single(c => c.Shape is not null);

        Assert.Equal(3 * Calibration.Cols + 3 + 1, cell.Index);
    }
}
