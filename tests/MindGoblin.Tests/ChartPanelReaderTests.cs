using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using MindGoblin.Core.Voyage;

namespace MindGoblin.Tests;

/// <summary>Pixel source backed by a PNG, so reading is tested with no game running.</summary>
[SupportedOSPlatform("windows")]
internal sealed class BitmapPixels : IPixels, IDisposable
{
    private readonly Bitmap _bmp;
    private readonly int[] _argb;

    public BitmapPixels(string path)
    {
        _bmp = new Bitmap(path);
        Width = _bmp.Width;
        Height = _bmp.Height;

        // GetPixel per call is far too slow for a 2560x1440 sweep; lock once.
        _argb = new int[Width * Height];
        var data = _bmp.LockBits(new Rectangle(0, 0, Width, Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, _argb, 0, _argb.Length);
        }
        finally
        {
            _bmp.UnlockBits(data);
        }
    }

    public int Width { get; }
    public int Height { get; }

    public (int R, int G, int B) At(int x, int y)
    {
        var p = _argb[y * Width + x];
        return ((p >> 16) & 0xFF, (p >> 8) & 0xFF, p & 0xFF);
    }

    public void Dispose() => _bmp.Dispose();
}

[SupportedOSPlatform("windows")]
public class ChartPanelReaderTests
{
    private static string Fixture =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "voyage-panel.png");

    private static IReadOnlyList<ChartPanelReader.ReadCell> ReadFixture()
    {
        using var px = new BitmapPixels(Fixture);
        return new ChartPanelReader().Read(px);
    }

    [Fact]
    public void FindsEveryOccupiedCellAndNoEmptyOnes()
    {
        // The real screenshot holds 24 charts in a 6x10 panel.
        Assert.Equal(24, ReadFixture().Count);
    }

    /// <summary>
    /// The 3.29.1 capture whose glow flecks stretched min/max glyph bounds into the
    /// surrounding artwork and read four E+S Corners as Junctions (#12, #26, #39,
    /// #44). Contiguous-run bounds fixed it; this fixture holds the patch's art.
    /// </summary>
    [Fact]
    public void ThePostPatchCaptureDecodesTheCornersCorrectly()
    {
        using var px = new BitmapPixels(
            Path.Combine(AppContext.BaseDirectory, "fixtures", "voyage-panel-3291.png"));
        var cells = new ChartPanelReader().Read(px);

        Assert.Equal(60, cells.Count);
        Assert.All(cells, c => Assert.NotNull(c.Shape));
        foreach (var index in new[] { 12, 26, 39, 44 })
            Assert.Equal(ChartShape.Corner, cells.Single(c => c.Index == index).Shape);
        Assert.Equal(17, cells.Count(c => c.Shape == ChartShape.Crossing));
        Assert.Equal(15, cells.Count(c => c.Shape == ChartShape.Corner));
        Assert.Equal(14, cells.Count(c => c.Shape == ChartShape.Straight));
        Assert.Equal(8, cells.Count(c => c.Shape == ChartShape.Junction));
        Assert.Equal(6, cells.Count(c => c.Shape == ChartShape.End));
    }

    [Fact]
    public void EveryChartResolvesToAKnownShape()
    {
        // An unresolved opening pattern means the reader is misreading pixels, which is
        // worse than failing outright -- it would feed the solver a fictional board.
        var cells = ReadFixture();
        Assert.All(cells, c => Assert.NotNull(c.Shape));
    }

    [Fact]
    public void ShapeDistributionMatchesTheScreenshot()
    {
        // Counted from the same capture by an independent decoder.
        var byShape = ReadFixture().GroupBy(c => c.Shape!.Value)
                                   .ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(10, byShape[ChartShape.Straight]);
        Assert.Equal(4, byShape[ChartShape.Corner]);
        Assert.Equal(4, byShape[ChartShape.Junction]);
        Assert.Equal(4, byShape[ChartShape.Crossing]);
        Assert.Equal(2, byShape[ChartShape.End]);
    }

    [Theory]
    // cell (row,col) -> N,E,S,W, verified against the rendered glyphs one by one
    [InlineData(0, 1, false, true, false, true)]    // horizontal Straight
    [InlineData(0, 5, false, false, true, false)]   // End pointing south
    [InlineData(2, 1, true, true, true, true)]      // Crossing
    [InlineData(2, 3, true, false, true, false)]    // vertical Straight
    [InlineData(5, 0, true, false, false, true)]    // Corner N-W
    [InlineData(1, 4, true, true, true, false)]     // Junction N-E-S
    public void ReadsIndividualCellsCorrectly(int row, int col, bool n, bool e, bool s, bool w)
    {
        var cell = ReadFixture().Single(c => c.Row == row && c.Col == col);
        Assert.Equal((n, e, s, w), (cell.North, cell.East, cell.South, cell.West));
    }

    [Fact]
    public void EmptyCellsAreSkippedNotReportedAsCharts()
    {
        var cells = ReadFixture();
        // row 4 is entirely empty in this capture
        Assert.DoesNotContain(cells, c => c.Row == 4);
    }

    [Fact]
    public void RotationRoundTripsThroughChartFace()
    {
        // The rotation reported must reproduce the openings that were read, otherwise a
        // correctly-identified shape would still be placed facing the wrong way.
        foreach (var cell in ReadFixture())
        {
            var face = new ChartFace(cell.Shape!.Value, cell.Rotation);
            Assert.Equal(cell.North, face.IsOpen(Side.North));
            Assert.Equal(cell.East, face.IsOpen(Side.East));
            Assert.Equal(cell.South, face.IsOpen(Side.South));
            Assert.Equal(cell.West, face.IsOpen(Side.West));
        }
    }

    [Fact]
    public void IndexIsOneBasedRowMajorMatchingThePanel()
    {
        var cells = ReadFixture();
        Assert.Equal(2, cells.Single(c => c is { Row: 0, Col: 1 }).Index);
        Assert.Equal(14, cells.Single(c => c is { Row: 2, Col: 1 }).Index);
        Assert.Equal(59, cells.Single(c => c is { Row: 9, Col: 4 }).Index);
    }

    [Fact]
    public void CalibrationRescalesToAnotherResolution()
    {
        // 2560x1440 is where this was calibrated, not an assumption baked in.
        var o = new ChartPanelReader.Options();
        var scaled = o.ScaledTo(1920, 1080);
        Assert.Equal(1326, scaled.OriginX);          // 1768 * 0.75
        Assert.Equal(314, scaled.OriginY);           // 419 * 0.75
        // EXACT, not rounded. Pitch is multiplied by the row and column, so a rounded
        // 50 has drifted 2.25px by the tenth row and clips the far arm off the glyph.
        Assert.Equal(50.25, scaled.Pitch, 6);        // 67 * 0.75
        Assert.Equal(1920, scaled.ReferenceWidth);
    }
}

/// <summary>
/// The calibration must be data, not constants: these coordinates were measured at one
/// resolution in one window mode, and UI scale, ultrawide monitors and patches all move
/// them. ScaledTo covers only the pure-resolution case.
/// </summary>
public class PanelCalibrationTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"panel-calibration-{Guid.NewGuid():N}.json");

    [Fact]
    public void MissingFileGivesTheMeasuredDefaults()
    {
        var loaded = ChartPanelReader.Options.Load(TempPath());
        Assert.Equal(new ChartPanelReader.Options().OriginX, loaded.OriginX);
        Assert.Equal(new ChartPanelReader.Options().Pitch, loaded.Pitch);
    }

    [Fact]
    public void EditsRoundTrip()
    {
        var path = TempPath();
        try
        {
            new ChartPanelReader.Options { OriginX = 1234, Pitch = 50, Rows = 8 }.Save(path);
            var loaded = ChartPanelReader.Options.Load(path);
            Assert.Equal(1234, loaded.OriginX);
            Assert.Equal(50, loaded.Pitch);
            Assert.Equal(8, loaded.Rows);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(@"{ ""Pitch"": 0 }")]        // would divide the panel into nothing
    [InlineData(@"{ ""Rows"": 0 }")]
    [InlineData(@"{ ""GlyphHalf"": 0 }")]
    [InlineData("{ not json")]
    public void NonsenseFallsBackRatherThanReadingGarbage(string content)
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, content);
            Assert.Equal(new ChartPanelReader.Options().Pitch,
                         ChartPanelReader.Options.Load(path).Pitch);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void WriteDefaultsDoesNotClobberAnExistingFile()
    {
        var path = TempPath();
        try
        {
            new ChartPanelReader.Options { OriginX = 999 }.Save(path);
            ChartPanelReader.Options.WriteDefaultsIfMissing(path);
            Assert.Equal(999, ChartPanelReader.Options.Load(path).OriginX);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void ASavedDefaultCalibrationStillReadsTheRealCapture()
    {
        // Round-tripping through JSON must not quietly drop a field the reader needs.
        var path = TempPath();
        try
        {
            new ChartPanelReader.Options().Save(path);
            using var px = new BitmapPixels(
                Path.Combine(AppContext.BaseDirectory, "fixtures", "voyage-panel.png"));
            Assert.Equal(24, new ChartPanelReader(ChartPanelReader.Options.Load(path)).Read(px).Count);
        }
        finally { File.Delete(path); }
    }
}
