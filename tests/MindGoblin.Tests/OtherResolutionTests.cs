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
}
