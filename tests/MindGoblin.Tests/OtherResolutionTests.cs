using System.Runtime.Versioning;
using MindGoblin.Core.Voyage;

namespace MindGoblin.Tests;

/// <summary>
/// A capture with part of it painted out, so a test can empty a row or a column of the
/// panel without needing a second screenshot of the same panel mid-voyage.
/// </summary>
internal sealed class Blanked(IPixels inner, Func<int, int, bool> hidden) : IPixels
{
    public int Width => inner.Width;
    public int Height => inner.Height;

    public (int R, int G, int B) At(int x, int y) => hidden(x, y) ? (0, 0, 0) : inner.At(x, y);
}

/// <summary>
/// The reader at resolutions other than the one it was measured at.
///
/// Two field reports, one cause: "index outside the bounds of the array on launch" and
/// "opening the calibration window crashed the app". The calibration is measured at
/// 2560x1440 and rescaled to whatever the game runs at — but the level reader's digit
/// TEMPLATES cannot be rescaled, because they are carved from a real capture and PoE's
/// font is not installed on Windows to re-render them from. Below the reference every
/// template was taller than the band it was laid over, and matching walked off the end
/// of the array. Every 1080p player, on the first thing the app does.
///
/// The cruelty of it was the second report: the crash fired hardest when the calibration
/// was wrong, so it killed the calibrate window — the one screen that could have fixed it.
/// </summary>
[SupportedOSPlatform("windows")]
public class OtherResolutionTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    /// <summary>The 2560x1440 fixture downscaled — a 1080p player's capture.</summary>
    private const string SmallerScreen = "voyage-panel-1080p.png";

    [Fact]
    public void ASmallerScreenReadsWithoutThrowing()
    {
        using var px = new BitmapPixels(Fixture(SmallerScreen));
        var cells = new ChartPanelReader().Read(px);

        // Not throwing is the claim. This fixture is the 1440p capture RESAMPLED, not a
        // native 1080p one, so its glyph edges are softer than the real thing and a
        // couple of cells decode to no shape at all -- which says something about the
        // resampling, not about the reader, and is not worth asserting either way.
        // Finding the occupied cells is: a reader that located nothing would "pass" a
        // does-not-throw test trivially.
        Assert.Equal(24, cells.Count);
        Assert.True(cells.Count(c => c.Shape is not null) >= 20,
            $"only {cells.Count(c => c.Shape is not null)} of {cells.Count} shapes decoded");
    }

    /// <summary>
    /// A MISALIGNED calibration on a smaller screen — the exact state of a player whose
    /// panel has moved, opening the calibrator to nudge it. Every offset must decode or
    /// decline; none may throw.
    /// </summary>
    [Fact]
    public void EveryNudgeOffsetSurvives()
    {
        using var px = new BitmapPixels(Fixture(SmallerScreen));
        var thrown = new List<string>();

        for (var dx = -24; dx <= 24; dx += 3)
            for (var dy = -24; dy <= 24; dy += 3)
            {
                var options = new ChartPanelReader.Options
                {
                    OriginX = 1768 + dx,
                    OriginY = 419 + dy,
                };
                try { new ChartPanelReader(options).Read(px); }
                catch (Exception ex) { thrown.Add($"({dx},{dy}) {ex.GetType().Name}"); }
            }

        Assert.True(thrown.Count == 0,
            $"{thrown.Count} offsets threw, e.g. {string.Join(", ", thrown.Take(3))}");
    }

    /// <summary>
    /// THE REPORTED BUG. Every chart must decode to the SAME SHAPE at 1080p as at the
    /// resolution the reader was calibrated against — a Crossing read as a Corner sends
    /// a chart to a square whose connections it does not have, and the plan is then
    /// wrong in a way the board drawing makes look deliberate.
    ///
    /// Three absolutes did it, all of them pixel counts tuned on the reference glyph and
    /// left fixed while the glyph shrank:
    ///   * LongestRun's density floor. The dark arm of a Crossing runs the tile's full
    ///     height, so its column holds few green pixels; under the floor the run SPLIT at
    ///     the arm, the bounding box collapsed to one half of the tile, and "the east
    ///     edge" was measured at the centre — where there is no opening.
    ///   * OpenThreshold, which asked a picture with half the scan lines for the same
    ///     count of them.
    ///   * Pitch, rounded then multiplied, drifting a cell's worth down the panel.
    /// </summary>
    [Fact]
    public void ShapesDecodeIdenticallyAtEveryResolution()
    {
        using var reference = new BitmapPixels(Fixture("voyage-panel.png"));
        using var smaller = new BitmapPixels(Fixture(SmallerScreen));

        var expected = new ChartPanelReader().Read(reference).ToDictionary(c => c.Index);
        var actual = new ChartPanelReader().Read(smaller).ToDictionary(c => c.Index);

        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());

        var wrong = expected.Keys
            .Where(i => expected[i].Shape != actual[i].Shape)
            .Select(i => $"#{i}: {expected[i].Shape} became {actual[i].Shape}")
            .ToList();
        Assert.True(wrong.Count == 0, string.Join("; ", wrong));
    }

    /// <summary>The openings themselves, not just the shape they add up to: a Crossing
    /// that keeps its name while losing a rotation would still be placed wrongly.</summary>
    [Fact]
    public void EveryOpeningSurvivesTheRescale()
    {
        using var reference = new BitmapPixels(Fixture("voyage-panel.png"));
        using var smaller = new BitmapPixels(Fixture(SmallerScreen));

        var expected = new ChartPanelReader().Read(reference).ToDictionary(c => c.Index);
        var actual = new ChartPanelReader().Read(smaller).ToDictionary(c => c.Index);

        foreach (var i in expected.Keys)
        {
            var a = expected[i];
            var b = actual[i];
            Assert.True(a.North == b.North && a.East == b.East
                        && a.South == b.South && a.West == b.West,
                $"#{i} openings changed: {a} became {b}");
        }
    }

    /// <summary>
    /// A NATIVE 1080p capture, from the player who reported the bug — not a downscale of
    /// the 1440p one. It matters that this is real: the resampled fixture agreed with the
    /// reference once the thresholds were fixed, while this one still failed, because the
    /// game's UI does not scale linearly with the resolution. The grid landed 10-20px
    /// from where the arithmetic put it, which dropped three tiles outright and read
    /// every remaining shape wrongly — while still hitting the right CELLS, so the panel
    /// looked plausible and was quietly wrong.
    ///
    /// It is also CROPPED, which is the point of locating the grid rather than computing
    /// it: a crop changes the resolution and not the pitch, and the reader now measures
    /// the pitch.
    /// </summary>
    [Fact]
    public void ANativeCaptureReadsEveryChartWithoutCalibration()
    {
        using var px = new BitmapPixels(Fixture("voyage-panel-native-1080p.png"));
        var cells = new ChartPanelReader().Read(px).ToDictionary(c => c.Index);

        // Thirteen charts, in the cells the game shows them in.
        Assert.Equal(13, cells.Count);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9, 41, 42, 54, 57], cells.Keys.Order());

        // And the shapes are the ones on screen -- read off the capture by eye, because
        // a decode that agrees only with itself proves nothing.
        Assert.Equal(ChartShape.Straight, cells[1].Shape);
        Assert.Equal(ChartShape.Corner, cells[2].Shape);
        Assert.Equal(ChartShape.Corner, cells[3].Shape);
        Assert.Equal(ChartShape.Junction, cells[4].Shape);
        Assert.Equal(ChartShape.Corner, cells[5].Shape);
        Assert.Equal(ChartShape.End, cells[6].Shape);
        Assert.Equal(ChartShape.Junction, cells[7].Shape);
        Assert.Equal(ChartShape.Junction, cells[8].Shape);
        Assert.Equal(ChartShape.Straight, cells[9].Shape);
        Assert.All(cells.Values, c => Assert.NotNull(c.Shape));
    }

    /// <summary>
    /// Locating the grid must never make the calibrated case worse. It is a heuristic
    /// over green pixels and this reader is the first thing the app does, so the decode
    /// runs both ways and keeps the better one — the property being pinned here is that
    /// the reference capture is unharmed by the machinery that rescued the other.
    /// </summary>
    [Fact]
    public void LocatingTheGridDoesNotDisturbTheMeasuredResolution()
    {
        using var px = new BitmapPixels(Fixture("voyage-panel.png"));
        var cells = new ChartPanelReader().Read(px);

        Assert.Equal(24, cells.Count);
        Assert.All(cells, c => Assert.NotNull(c.Shape));
    }

    /// <summary>
    /// GREEN IS NOT ENOUGH TO IDENTIFY THE PANEL. A row of skill and buff icons glows
    /// just as hard and sits on just as regular a pitch, and the first version of the
    /// grid search latched onto exactly that: the calibrate window opened with its grid
    /// drawn over the top-left corner of the game, sixty cells from any chart.
    ///
    /// So every candidate is SCORED by decoding, and the calibration is always one of
    /// them. Whatever Resolve returns must read the panel at least as well as the
    /// calibration would have — that is the property, and it holds whatever else on
    /// screen happens to be green.
    /// </summary>
    [Theory]
    [InlineData("voyage-panel.png")]
    [InlineData("voyage-panel-1080p.png")]
    [InlineData("voyage-panel-native-1080p.png")]
    [InlineData("voyage-panel-3291.png")]
    public void LocatingNeverReadsWorseThanTheCalibration(string shot)
    {
        using var px = new BitmapPixels(Fixture(shot));
        var reader = new ChartPanelReader();

        var chosen = reader.Read(px).Count(c => c.Shape is not null);
        var calibrated = new ChartPanelReader(
                new ChartPanelReader.Options().ForScreen(px.Width, px.Height))
            .Read(px).Count(c => c.Shape is not null);

        Assert.True(chosen >= calibrated,
            $"{shot}: located grid read {chosen} shapes where the calibration read {calibrated}");
    }

    /// <summary>
    /// The same claim as above, in CELLS rather than in a count of them. A grid slid a
    /// whole cell sideways reads exactly as many shapes as the right one and calls them
    /// all by the wrong name, so counting alone cannot tell the two apart -- and the
    /// wrong name is what the session then files the chart's modifiers under.
    /// </summary>
    [Fact]
    public void EveryPanelDecodesToTheSameChartsNotJustTheSameNumberOfThem()
    {
        var expected = new (string Shot, int[] Charts)[]
        {
            ("voyage-panel.png",
                [2, 5, 6, 7, 9, 10, 11, 14, 16, 17, 18, 22,
                 31, 32, 33, 34, 35, 37, 39, 42, 43, 52, 53, 59]),
            // The same 24 charts, one resolution down: the rescale may not move a cell.
            ("voyage-panel-1080p.png",
                [2, 5, 6, 7, 9, 10, 11, 14, 16, 17, 18, 22,
                 31, 32, 33, 34, 35, 37, 39, 42, 43, 52, 53, 59]),
            ("voyage-panel-native-1080p.png", [1, 2, 3, 4, 5, 6, 7, 8, 9, 41, 42, 54, 57]),
            ("voyage-panel-3291.png", [.. Enumerable.Range(1, 60)]),
        };

        foreach (var (shot, charts) in expected)
        {
            using var px = new BitmapPixels(Fixture(shot));
            Assert.Equal(charts, new ChartPanelReader().Read(px).Select(c => c.Index).Order());
        }
    }

    /// <summary>
    /// A PANEL INDEX IS A PHYSICAL POSITION, on the capture that first proved the grid
    /// has to be located rather than computed. Blank the leftmost column of tiles -- what
    /// a voyage does to the panel every time it consumes the charts in it -- and the
    /// eleven that remain must answer to the same numbers they did before.
    ///
    /// They did not: the located grid was anchored on the first inked band, so emptying
    /// that band moved the origin 1114 -> 1162 and handed chart #2 the number #1, #3 the
    /// number #2, and so on down the panel. Nothing looks wrong afterwards -- the shapes
    /// are all still there, one seat to the left -- but every chart's stored modifier
    /// text, star and exclude now belongs to its neighbour.
    /// </summary>
    [Fact]
    public void EmptyingTheLeadingColumnDoesNotRenumberTheRestOfThePanel()
    {
        using var px = new BitmapPixels(Fixture("voyage-panel-native-1080p.png"));
        var full = new ChartPanelReader().Read(px).Select(c => c.Index).Order().ToArray();

        // The leftmost tiles are centred on x=1114 with a measured pitch of 49, so half
        // a pitch either way takes that column and nothing of the next.
        var emptied = new ChartPanelReader()
            .Read(new Blanked(px, (x, _) => Math.Abs(x - 1114) <= 24))
            .Select(c => c.Index).Order();

        Assert.Equal(full.Where(i => (i - 1) % 6 != 0), emptied);
    }

    /// <summary>
    /// WHATEVER RESOLVE RETURNS IS ALREADY SCALED TO THE CAPTURE, and says so.
    ///
    /// The app resolves the grid once, reads with it and HOVERS with it, and the hover
    /// runs the geometry through ForScreen on the way out. That is only harmless while
    /// the resolved grid reports the capture's own dimensions as its reference -- get it
    /// wrong and the rescale is applied twice, which lands the cursor on a different
    /// cell than the one the plan numbered, and the wrong chart's text parses perfectly.
    /// </summary>
    [Theory]
    [InlineData("voyage-panel.png")]
    [InlineData("voyage-panel-1080p.png")]
    [InlineData("voyage-panel-native-1080p.png")]
    [InlineData("voyage-panel-3291.png")]
    public void TheResolvedGridReportsTheCaptureAsItsOwnReference(string shot)
    {
        using var px = new BitmapPixels(Fixture(shot));
        var resolved = new ChartPanelReader().Resolve(px);

        Assert.Equal(px.Width, resolved.ReferenceWidth);
        Assert.Equal(px.Height, resolved.ReferenceHeight);
        Assert.Same(resolved, resolved.ForScreen(px.Width, px.Height));
    }

    /// <summary>
    /// The pixel-count thresholds must reduce to their tuned values at the reference and
    /// shrink from there. Stated as a property rather than left to the fixtures, because
    /// the next absolute somebody adds will look just as harmless as these three did.
    /// </summary>
    [Theory]
    [InlineData(2560, 1440)]
    [InlineData(1920, 1080)]
    [InlineData(3840, 2160)]
    public void PixelThresholdsScaleWithThePicture(int width, int height)
    {
        var reference = new ChartPanelReader.Options();
        var scaled = reference.ScaledTo(width, height);
        var s = Math.Min(width / 2560.0, height / 1440.0);

        Assert.Equal(reference.Pitch * s, scaled.Pitch, 6);
        Assert.True(scaled.OpenThreshold >= 1);
        Assert.True(scaled.EdgeMargin >= 1);
        if (s < 1)
        {
            Assert.True(scaled.OpenThreshold <= reference.OpenThreshold);
            Assert.True(scaled.EdgeMargin <= reference.EdgeMargin);
        }
        if (s == 1)
        {
            Assert.Equal(reference.OpenThreshold, scaled.OpenThreshold);
            Assert.Equal(reference.EdgeMargin, scaled.EdgeMargin);
        }
    }
}
