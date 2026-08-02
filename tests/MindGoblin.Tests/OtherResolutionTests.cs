using System.Runtime.Versioning;
using MindGoblin.Core.Voyage;

namespace MindGoblin.Tests;

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
    /// The level reader DECLINES rather than guessing when its templates do not fit the
    /// band. A missing level is recoverable; a fabricated one silently corrupts a plan,
    /// which is the whole reason this reader refuses untrained digits too.
    /// </summary>
    [Fact]
    public void AnUnfittableTemplateDeclinesInsteadOfGuessing()
    {
        // A band shorter than the 20-row templates: what every sub-reference screen
        // produces once TextHeight has been scaled down.
        var options = new ChartPanelReader.Options().ScaledTo(1920, 1080);
        var levels = new LevelReader();
        var band = new bool[12, 30];
        for (var y = 0; y < 12; y++) band[y, 5] = true;      // ink, but nothing that fits

        Assert.Null(levels.Decode(band, new LevelReader.Options
        {
            TextHeight = 8,        // band 12 tall, so pad 4 -- room to slide, none to fit
        }));
        Assert.Equal(1920, options.ReferenceWidth);
    }

    /// <summary>The reference resolution still reads its levels: the fix declines only
    /// where the templates genuinely do not fit, and must not cost the measured case.</summary>
    [Fact]
    public void TheMeasuredResolutionStillReadsLevels()
    {
        using var px = new BitmapPixels(Fixture("voyage-panel.png"));
        var cells = new ChartPanelReader().Read(px);

        Assert.Equal(24, cells.Count);
        Assert.All(cells, c => Assert.NotNull(c.Level));
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
    /// A LEVEL IS NEVER FABRICATED. The templates are resampled to the screen's caption
    /// height so a smaller screen can read levels at all, and resampling is the one thing
    /// this reader was built to distrust — so the standing rule is checked directly:
    /// whatever it does read must agree with the reference, and the rest must read null.
    /// A missing level is recoverable; a wrong one silently corrupts a plan.
    /// </summary>
    [Fact]
    public void ARescaledTemplateNeverReadsTheWrongLevel()
    {
        using var reference = new BitmapPixels(Fixture("voyage-panel.png"));
        using var smaller = new BitmapPixels(Fixture(SmallerScreen));

        var expected = new ChartPanelReader().Read(reference).ToDictionary(c => c.Index);
        var actual = new ChartPanelReader().Read(smaller).ToDictionary(c => c.Index);

        var wrong = expected.Keys
            .Where(i => actual[i].Level is not null && actual[i].Level != expected[i].Level)
            .Select(i => $"#{i}: {expected[i].Level} read as {actual[i].Level}")
            .ToList();
        Assert.True(wrong.Count == 0, string.Join("; ", wrong));

        // And it must read SOME of them, or the resampling is not earning its place.
        Assert.True(actual.Values.Any(c => c.Level is not null),
                    "no level decoded at all on the smaller screen");
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
        Assert.All(cells, c => Assert.NotNull(c.Level));
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
