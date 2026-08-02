using MindGoblin.Core.Voyage;

namespace MindGoblin.Tests;

/// <summary>
/// Calibration is measured in pixels at one resolution, and the app both READS at those
/// coordinates and HOVERS at them. The two have to move together.
///
/// They did not. The chart reader scaled its own copy of the calibration to whatever
/// screen it was decoding, while the slurp hovered the raw numbers -- so on any
/// resolution but 2560x1440 the panel decoded correctly and the cursor landed on a
/// different cell than the one the plan had numbered. The board hover points had the same
/// split: the Area Modifiers rectangle is fractional and rescaled itself for free, the
/// figurine positions beside it were absolute pixels and never moved.
/// </summary>
public class CalibrationScalingTests
{
    [Fact]
    public void ThePanelCalibrationIsUnchangedAtItsOwnResolution()
    {
        var o = new ChartPanelReader.Options();
        Assert.Same(o, o.ForScreen(o.ReferenceWidth, o.ReferenceHeight));
    }

    [Fact]
    public void ThePanelCalibrationFollowsTheScreen()
    {
        var o = new ChartPanelReader.Options();
        var scaled = o.ForScreen(1920, 1080);

        Assert.Equal((int)Math.Round(o.OriginX * 0.75), scaled.OriginX);
        Assert.Equal((int)Math.Round(o.OriginY * 0.75), scaled.OriginY);
        Assert.Equal((int)Math.Round(o.Pitch * 0.75), scaled.Pitch);
        Assert.Equal(1920, scaled.ReferenceWidth);
    }

    /// <summary>
    /// ForScreen is exactly the rescale Read used to inline, so extracting it for the
    /// slurp to share changed nothing about how the panel decodes. That equivalence is
    /// the whole point: a hover and a decode that disagree copy the wrong chart, and the
    /// wrong chart's text parses perfectly.
    /// </summary>
    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(3440, 1440)]
    [InlineData(2560, 1600)]
    public void ForScreenIsTheRescaleTheReaderAlreadyDid(int width, int height)
    {
        var o = new ChartPanelReader.Options();
        var expected = o.ScaledTo(width, height);
        var actual = o.ForScreen(width, height);

        Assert.Equal(expected.OriginX, actual.OriginX);
        Assert.Equal(expected.OriginY, actual.OriginY);
        Assert.Equal(expected.Pitch, actual.Pitch);
        Assert.Equal(expected.GlyphHalf, actual.GlyphHalf);
        Assert.Equal(expected.OccupiedThreshold, actual.OccupiedThreshold);
    }

    /// <summary>The fixture still decodes: ForScreen is identity at the resolution the
    /// reader's own fixtures were captured at, so the decode path is untouched.</summary>
    [Fact]
    public void TheReaderStillSeesItsOwnCalibrationUnscaled()
    {
        var o = ChartPanelReader.Options.Load(
            Path.Combine(Path.GetTempPath(), $"mg-absent-{Guid.NewGuid():N}.json"));
        Assert.Same(o, o.ForScreen(2560, 1440));
    }

    [Fact]
    public void TheBoardHoverPointsAreUnchangedAtTheirOwnResolution()
    {
        var o = new AreaModifierPanel.Options();
        Assert.Same(o, o.ForScreen(o.ReferenceWidth, o.ReferenceHeight));
    }

    [Fact]
    public void TheBoardHoverPointsFollowTheScreen()
    {
        var o = new AreaModifierPanel.Options();
        var scaled = o.ForScreen(1280, 720);

        Assert.Equal((int)Math.Round(o.BoardOriginX * 0.5), scaled.BoardOriginX);
        Assert.Equal((int)Math.Round(o.BoardOriginY * 0.5), scaled.BoardOriginY);
        Assert.Equal((int)Math.Round(o.BoardPitch * 0.5), scaled.BoardPitch);
        Assert.Equal((int)Math.Round(o.FigurineInset * 0.5), scaled.FigurineInset);
    }

    /// <summary>A degenerate scale must not produce a zero pitch: every square would
    /// then hover the same pixel and the whole ring would read as one figurine.</summary>
    [Fact]
    public void ScalingNeverCollapsesThePitchToZero()
    {
        var scaled = new AreaModifierPanel.Options().ForScreen(64, 36);
        Assert.True(scaled.BoardPitch > 0);
        Assert.True(scaled.FigurineInset > 0);
    }

    /// <summary>
    /// The reference fields are new. A calibration file written before they existed
    /// deserialises without them, and the defaults have to be the resolution those
    /// numbers were actually measured at -- otherwise every existing calibration
    /// silently rescales itself on load.
    /// </summary>
    [Fact]
    public void AnOlderCalibrationFileDefaultsToTheMeasuredResolution()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mg-panel-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path,
                """{ "boardOriginX": 1057, "boardOriginY": 534, "boardPitch": 193 }""");
            var loaded = AreaModifierPanel.Options.Load(path);

            Assert.Equal(2560, loaded.ReferenceWidth);
            Assert.Equal(1440, loaded.ReferenceHeight);
            Assert.Same(loaded, loaded.ForScreen(2560, 1440));
        }
        finally { File.Delete(path); }
    }
}
