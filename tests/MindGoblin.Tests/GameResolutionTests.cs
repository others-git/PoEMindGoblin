using MindGoblin.Core.Voyage;

namespace MindGoblin.Tests;

/// <summary>
/// Every calibrated coordinate is measured against one resolution and rescaled to the
/// game's. Getting that number wrong is not subtle — the reader looks in the wrong place
/// and reports an empty panel, and the slurp hovers a cell that is not the one the plan
/// numbered. So it is detected, then pinned, then guessed, in that order.
/// </summary>
public class GameResolutionTests
{
    [Fact]
    public void AutoIsTheDefault()
    {
        var fresh = new GameResolution();
        Assert.True(fresh.Auto);
        Assert.False(fresh.IsPinned);
    }

    /// <summary>A half-filled override is worse than none: it would scale the
    /// calibration by zero and hover at the origin.</summary>
    [Theory]
    [InlineData(1920, 0)]
    [InlineData(0, 1080)]
    [InlineData(0, 0)]
    public void AHalfFilledPinIsNotAPin(int width, int height) =>
        Assert.False(new GameResolution { Auto = false, Width = width, Height = height }.IsPinned);

    [Fact]
    public void APinnedResolutionIsUsable() =>
        Assert.True(new GameResolution { Auto = false, Width = 1920, Height = 1080 }.IsPinned);

    /// <summary>Auto wins even with a resolution left in the file: the pin is a
    /// fallback, and a stale one must not quietly outrank live detection.</summary>
    [Fact]
    public void AutoOutranksALeftoverResolution() =>
        Assert.False(new GameResolution { Auto = true, Width = 1280, Height = 720 }.IsPinned);

    [Fact]
    public void ItRoundTripsThroughDisk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mg-res-{Guid.NewGuid():N}.json");
        try
        {
            new GameResolution { Auto = false, Width = 3440, Height = 1440 }.Save(path);
            var loaded = GameResolution.Load(path);

            Assert.True(loaded.IsPinned);
            Assert.Equal(3440, loaded.Width);
            Assert.Equal(1440, loaded.Height);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AMissingOrCorruptFileFallsBackToAuto()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mg-res-{Guid.NewGuid():N}.json");
        Assert.True(GameResolution.Load(path).Auto);          // absent
        try
        {
            File.WriteAllText(path, "{ not json at all");
            Assert.True(GameResolution.Load(path).Auto);      // unreadable
        }
        finally { File.Delete(path); }
    }

    /// <summary>The shortlist must carry the resolution the calibration was measured
    /// at, or the one preset guaranteed to need no nudging is missing.</summary>
    [Fact]
    public void TheMeasuredResolutionIsOffered() =>
        Assert.NotNull(ScreenPreset.Match(2560, 1440));

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(3840, 2160)]
    [InlineData(3440, 1440)]
    public void TheCommonOnesAreOffered(int width, int height) =>
        Assert.NotNull(ScreenPreset.Match(width, height));

    /// <summary>An unlisted resolution is a real answer, not one to round to a
    /// neighbour: silently calibrating for 1920x1080 on a 1920x1440 screen would put
    /// every coordinate slightly wrong and explain nothing.</summary>
    [Fact]
    public void AnUnlistedResolutionDoesNotMatch() =>
        Assert.Null(ScreenPreset.Match(1234, 567));

    /// <summary>
    /// Every launcher variant the game ships under. A name that stops matching means the
    /// app silently falls back to the primary screen — right only when the game happens
    /// to be fullscreen there, and impossible to diagnose from the symptom.
    /// </summary>
    [Theory]
    [InlineData("PathOfExile")]
    [InlineData("PathOfExile_x64")]
    [InlineData("PathOfExileSteam")]
    [InlineData("PathOfExile_x64Steam")]
    [InlineData("PathOfExile_KG")]
    [InlineData("PathOfExile_x64_KG")]
    [InlineData("PathOfExileEGS")]
    [InlineData("pathofexile")]
    public void EveryLauncherVariantIsRecognised(string name) =>
        Assert.True(GameProcess.IsGame(name), name);

    [Theory]
    [InlineData("MindGoblin")]
    [InlineData("chrome")]
    [InlineData("explorer")]
    [InlineData("")]
    [InlineData(null)]
    public void NothingElseIs(string? name) => Assert.False(GameProcess.IsGame(name));

    [Fact]
    public void PresetsAreDistinctAndLabelled()
    {
        Assert.All(ScreenPreset.Common, p =>
        {
            Assert.True(p.Width > 0 && p.Height > 0);
            Assert.Contains("×", p.Label);
        });
        Assert.Equal(ScreenPreset.Common.Count,
                     ScreenPreset.Common.Select(p => (p.Width, p.Height)).Distinct().Count());
    }
}
