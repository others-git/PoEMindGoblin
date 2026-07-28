using PoeMarketWatch.Core.Voyage;

namespace PoeMarketWatch.Tests;

/// <summary>
/// Reading a board costs a screenshot, nine hovers for the Area Modifiers and a hover per
/// chart worth scoring. Losing that on exit would make the tool worse than doing it by
/// hand, so the session round-trips through disk.
/// </summary>
public class VoyagePersistenceTests : IDisposable
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), $"voyage-session-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static VoyageSession Populated()
    {
        var session = new VoyageSession();
        session.ApplyPanelRead([
            new ChartPanelReader.ReadCell(2, 0, 1, false, true, false, true) { Level = 71 },
            new ChartPanelReader.ReadCell(9, 1, 2, true, true, true, true) { Level = 83 },
        ]);
        session.ApplyChartText(2,
            "Tempest Reach\nSeafloor Ridges\nArea Level: 71\nItem Quantity: +42%\n"
            + "Dead Man's Sulphur: +14\nRequires Level 68\n"
            + "Voyage Modifier: 8% increased Quantity of Items found in all Voyage Areas\n"
            + "Adjacent Modifier: Adjacent Areas contain 2 additional Strongboxes\n"
            + "Monsters deal 25% extra Physical Damage as Fire");
        session.ApplySquareModifiers(5, ["Areas contain 8 additional packs of Sea Beasts"]);
        session.ApplyFigurineText(3, "Adjacent Areas have increased Quantity");
        return session;
    }

    [Fact]
    public void EverythingReadSurvivesARoundTrip()
    {
        Populated().Save(_path, profile: "sulphur");
        var (restored, state) = VoyageSession.Restore(_path);

        Assert.Equal("sulphur", state!.Profile);
        Assert.Equal(2, restored.Charts.Count);

        var chart = restored.ByPanelIndex[2];
        Assert.Equal("Tempest Reach", chart.Name);
        Assert.Equal("Seafloor Ridges", chart.AreaName);
        Assert.Equal(ChartShape.Straight, chart.Shape);
        Assert.Equal(71, chart.AreaLevel);
        Assert.Equal(68, chart.RequiresLevel);
        Assert.Equal(42, chart.ItemQuantity);
        Assert.Equal(14, chart.Sulphur);
        Assert.StartsWith("8% increased Quantity", chart.VoyageModifier);
        Assert.StartsWith("Adjacent Areas contain 2", chart.AdjacentModifier);
        Assert.Contains("Monsters deal 25% extra Physical Damage as Fire", chart.Modifiers);

        Assert.Equal(["Areas contain 8 additional packs of Sea Beasts"],
                     restored.SquareModifiers[5]);
        Assert.Equal("Adjacent Areas have increased Quantity", restored.Figurines[3]);
    }

    [Fact]
    public void TheChecklistPicksUpWhereItLeftOff()
    {
        Populated().Save(_path);
        var (restored, _) = VoyageSession.Restore(_path);

        Assert.DoesNotContain(2, restored.ChartsAwaitingDetail);   // hovered before saving
        Assert.Contains(9, restored.ChartsAwaitingDetail);         // never hovered
        Assert.DoesNotContain(5, restored.SquaresAwaitingModifiers);
        Assert.Contains(1, restored.SquaresAwaitingModifiers);
    }

    [Fact]
    public void ARestoredSessionStillSolves()
    {
        var session = new VoyageSession();
        session.ApplyPanelRead(Enumerable.Range(1, 9).Select(i =>
            new ChartPanelReader.ReadCell(i, (i - 1) / 6, (i - 1) % 6, true, true, true, true)
            { Level = 80 }).ToList());
        session.ApplySquareModifiers(5, ["Areas contain 8 additional packs"]);
        session.Save(_path);

        var (restored, _) = VoyageSession.Restore(_path);
        var profile = new VoyageProfile
        {
            Name = "packs",
            Rules = [new VoyageRule { Pattern = @"(\d+) additional packs", Weight = 10 }],
        };
        var solution = restored.Solve(profile, TimeSpan.FromSeconds(1));

        Assert.Equal(9, solution.Placements.Count);
        Assert.Equal(80, solution.Value);        // the square-5 modifier still applies
    }

    [Fact]
    public void ScoresAreNotPersisted()
    {
        // Value and AdjacentValue are what a PROFILE makes of a chart, not a property of
        // it. Saving them would resurrect yesterday's scoring under today's rules.
        var session = new VoyageSession();
        session.ApplyPanelRead([
            new ChartPanelReader.ReadCell(1, 0, 0, true, true, true, true) { Level = 80 }]);
        session.ApplyChartText(1, "A\nDead Man's Sulphur: +14");
        session.Save(_path);

        var (restored, _) = VoyageSession.Restore(_path);
        Assert.Equal(0, restored.ByPanelIndex[1].Value);
        Assert.Equal(0, restored.ByPanelIndex[1].AdjacentValue);
        // ...but the stat it would be scored from is still there.
        Assert.Equal(14, restored.ByPanelIndex[1].Sulphur);
    }

    [Fact]
    public void TheBoardSizeComesFromTheSavedStateNotTheDefault()
    {
        // Square numbers only mean anything against the board they were read on.
        // Reinterpreting them on a different size would move every modifier.
        var session = new VoyageSession(BoardLayout.Default(4, 4));
        session.ApplySquareModifiers(16, ["a modifier"]);
        session.Save(_path);

        var (restored, _) = VoyageSession.Restore(_path);
        Assert.Equal(4, restored.Layout.Rows);
        Assert.Equal(4, restored.Layout.Cols);
        Assert.Equal(new Cell(3, 3), restored.CellOf(16));
    }

    [Fact]
    public void NoSavedFileStartsAFreshSession()
    {
        var (session, state) = VoyageSession.Restore(_path);
        Assert.Null(state);
        Assert.Empty(session.Charts);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData(@"{ ""Version"": 99, ""Rows"": 3, ""Cols"": 3 }")]   // a future format
    [InlineData(@"{ ""Version"": 1, ""Rows"": 0, ""Cols"": 0 }")]    // nonsense board
    public void AnUnusableFileStartsFreshRatherThanHalfLoaded(string content)
    {
        File.WriteAllText(_path, content);
        var (session, state) = VoyageSession.Restore(_path);
        Assert.Null(state);
        Assert.Empty(session.Charts);
    }

    [Fact]
    public void SavingIsAtomicSoAKilledWriteCannotTruncateTheFile()
    {
        Populated().Save(_path);
        var before = File.ReadAllText(_path);

        new VoyageSession().Save(_path);      // overwrite with an empty session
        Assert.NotEqual(before, File.ReadAllText(_path));
        Assert.False(File.Exists(_path + ".tmp"), "the scratch file should not be left behind");
    }

    [Fact]
    public void DeleteRemovesTheSavedSession()
    {
        Populated().Save(_path);
        VoyageSessionState.Delete(_path);
        Assert.False(File.Exists(_path));

        // Deleting again is not an error -- Clear should work twice.
        VoyageSessionState.Delete(_path);
    }

    [Fact]
    public void TheFileRecordsWhenItWasWritten()
    {
        var before = DateTimeOffset.Now.AddSeconds(-1);
        Populated().Save(_path);
        var state = VoyageSessionState.Load(_path)!;
        Assert.True(state.SavedAt >= before, $"SavedAt {state.SavedAt} predates the save");
    }

    [Fact]
    public void ShapesAreWrittenAsNamesSoTheFileStaysReadable()
    {
        // A hand-editable file is part of the point; "Straight" survives a reordering of
        // the enum, "2" does not.
        Populated().Save(_path);
        Assert.Contains("\"Straight\"", File.ReadAllText(_path));
    }
}
