using PoeMarketWatch.Core.Voyage;

namespace PoeMarketWatch.Tests;

public class VoyageSessionTests
{
    private static ChartPanelReader.ReadCell Cell(
        int index, bool n, bool e, bool s, bool w, int? level = 80) =>
        new(index, (index - 1) / 6, (index - 1) % 6, n, e, s, w) { Level = level };

    /// <summary>Twelve crossings: any of them fits any square, so layout is never the blocker.</summary>
    private static IReadOnlyList<ChartPanelReader.ReadCell> Crossings(int count = 12) =>
        Enumerable.Range(1, count).Select(i => Cell(i, true, true, true, true)).ToList();

    [Fact]
    public void PanelReadCreatesChartsKeyedByPanelIndex()
    {
        var s = new VoyageSession();
        s.ApplyPanelRead([Cell(3, true, false, true, false, 81)]);

        var chart = Assert.Single(s.Charts);
        Assert.Equal(ChartShape.Straight, chart.Shape);
        Assert.Equal(81, chart.AreaLevel);
        Assert.Equal(3, s.ByPanelIndex.Keys.Single());
    }

    [Fact]
    public void UnreadableGlyphsAreSkippedNotInvented()
    {
        // A cell whose openings match no shape must not become a chart -- a fictional
        // chart would be placed on the board and the plan would be unfollowable.
        var s = new VoyageSession();
        s.ApplyPanelRead([new ChartPanelReader.ReadCell(1, 0, 0, false, false, false, false)]);
        Assert.Empty(s.Charts);
    }

    [Fact]
    public void ARereadKeepsHoverDetailAlreadyCaptured()
    {
        // Re-reading the panel mid-hover is normal; wiping the completed half would make
        // the checklist pointless.
        var s = new VoyageSession();
        s.ApplyPanelRead([Cell(1, true, true, true, true)]);
        s.ApplyChartText(1, "Storm Hollow\nMonster Pack Size: +30%");

        s.ApplyPanelRead([Cell(1, true, true, true, true, level: 83)]);

        var chart = Assert.Single(s.Charts);
        Assert.Equal(30, chart.MonsterPackSize);
        Assert.Equal(83, chart.AreaLevel);          // the fresh read still updates the level
        Assert.Equal("Storm Hollow", chart.Name);
    }

    [Fact]
    public void ChartsMissingFromANewReadAreDropped()
    {
        var s = new VoyageSession();
        s.ApplyPanelRead([Cell(1, true, true, true, true), Cell(2, true, true, true, true)]);
        s.ApplyPanelRead([Cell(1, true, true, true, true)]);
        Assert.Single(s.Charts);
    }

    [Fact]
    public void TheMeasuredShapeBeatsAnythingTheTextClaims()
    {
        var s = new VoyageSession();
        s.ApplyPanelRead([Cell(1, true, false, true, false)]);       // Straight
        s.ApplyChartText(1, "Storm Hollow\nShape: Corner");
        Assert.Equal(ChartShape.Straight, s.Charts[0].Shape);
    }

    [Fact]
    public void HoverTextForAnUnknownChartIsRejected()
    {
        var s = new VoyageSession();
        Assert.False(s.ApplyChartText(42, "Storm Hollow\nArea Level: 80"));
    }

    [Fact]
    public void ChecklistTracksWhatStillNeedsHovering()
    {
        var s = new VoyageSession();
        s.ApplyPanelRead([Cell(1, true, true, true, true), Cell(2, true, true, true, true)]);

        Assert.Equal([1, 2], s.ChartsAwaitingDetail);
        Assert.Equal(12, s.FigurinesAwaitingDetail.Count);

        s.ApplyChartText(1, "A\nItem Quantity: +10%");
        s.ApplyFigurineText(3, "Adjacent Areas contain 8 additional packs of Sea Beasts");

        Assert.Equal([2], s.ChartsAwaitingDetail);
        Assert.Equal(11, s.FigurinesAwaitingDetail.Count);
    }

    [Fact]
    public void ProgressRunsFromNothingReadToEverythingRead()
    {
        var s = new VoyageSession();
        Assert.Equal(0, s.ReadProgress);

        s.ApplyPanelRead(Crossings(2));
        Assert.Equal(0, s.ReadProgress);                     // panel read, nothing hovered

        foreach (var i in new[] { 1, 2 }) s.ApplyChartText(i, $"c{i}\nItem Quantity: +5%");
        foreach (var f in s.Layout.Figurines) s.ApplyFigurineText(f.Index, "Adjacent Areas are fun");
        Assert.Equal(1.0, s.ReadProgress, 6);
    }

    [Fact]
    public void ClearingFigurineTextPutsItBackOnTheChecklist()
    {
        var s = new VoyageSession();
        s.ApplyFigurineText(1, "something");
        Assert.Equal(11, s.FigurinesAwaitingDetail.Count);
        s.ApplyFigurineText(1, "   ");
        Assert.Equal(12, s.FigurinesAwaitingDetail.Count);
    }

    [Fact]
    public void SolvesWithNoHoverTextAtAll()
    {
        // Hovering 60 charts is the expensive part. A plan built on shapes and levels
        // alone must still come out, or the tool is unusable until the chore is done.
        var s = new VoyageSession();
        s.ApplyPanelRead(Crossings());

        var profile = new VoyageProfile { Name = "levels", AreaLevelWeight = 1 };
        var solution = s.Solve(profile, TimeSpan.FromMilliseconds(500));

        Assert.Equal(9, solution.Placements.Count);
        var board = new VoyageBoard(3, 3);
        foreach (var p in solution.Placements) board.Place(p);
        Assert.True(board.IsValid());
    }

    [Fact]
    public void ThePlanNumbersChartsByPanelIndex()
    {
        // "square 5 <- chart 23" is only followable if 23 is where it sits in the panel.
        var s = new VoyageSession();
        s.ApplyPanelRead(Enumerable.Range(1, 9)
            .Select(i => Cell(i * 2, true, true, true, true)).ToList());

        var solution = s.Solve(new VoyageProfile { Name = "flat" }, TimeSpan.FromMilliseconds(500));
        var plan = s.Plan(solution);

        Assert.Equal(9, plan.Count);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9], plan.Select(p => p.Square));
        Assert.All(plan, step => Assert.Contains(step.ChartNumber, s.ByPanelIndex.Keys));
        Assert.All(plan, step => Assert.Equal(0, step.ChartNumber % 2));
    }

    [Fact]
    public void ProfileChoiceChangesWhichChartsGetPlaced()
    {
        // The same board scored two ways must not give the same answer, or the rule
        // profiles are decorative.
        var s = new VoyageSession();
        s.ApplyPanelRead(Crossings(10));
        s.ApplyChartText(1, "sulphur one\nDead Man's Sulphur: +20");
        for (var i = 2; i <= 10; i++) s.ApplyChartText(i, $"packs {i}\nMonster Pack Size: +40%");

        var sulphur = new VoyageProfile
        {
            Name = "sulphur",
            Rules = [new VoyageRule { Pattern = @"Sulphur", Weight = 100 }],
        };
        var packs = new VoyageProfile
        {
            Name = "packs",
            Rules = [new VoyageRule { Pattern = @"Pack Size", Weight = 100 }],
        };

        var withSulphur = s.Plan(s.Solve(sulphur, TimeSpan.FromMilliseconds(500)));
        var withPacks = s.Plan(s.Solve(packs, TimeSpan.FromMilliseconds(500)));

        Assert.Contains(withSulphur, p => p.ChartNumber == 1);
        Assert.DoesNotContain(withPacks, p => p.ChartNumber == 1);
    }

    [Fact]
    public void FigurineModifiersReachTheSolverBoundToTheirCells()
    {
        var s = new VoyageSession();
        s.ApplyFigurineText(1, "Adjacent Areas contain 8 additional packs of Sea Beasts");

        var mod = Assert.Single(s.BoardModifiers());
        Assert.Contains("Sea Beasts", mod.Description);
        Assert.NotEmpty(mod.AffectedCells);
    }

    [Fact]
    public void AChartWithAnAdjacentModifierIsPulledTowardsBusySquares()
    {
        // The centre square touches 4 neighbours, a corner only 2, so an adjacency chart
        // belongs in the middle. This is the whole reason the objective is not separable.
        var s = new VoyageSession();
        s.ApplyPanelRead(Crossings(9));
        s.ApplyChartText(5, "spreader\nAdjacent Modifier: Adjacent Areas contain 4 additional Strongboxes");

        var profile = new VoyageProfile
        {
            Name = "boxes",
            Rules = [new VoyageRule { Pattern = @"(\d+) additional Strongboxes", Weight = 10 }],
        };
        var plan = s.Plan(s.Solve(profile, TimeSpan.FromSeconds(1)));

        Assert.Equal(5, plan.Single(p => p.ChartNumber == 5).Square);   // centre of a 3x3
    }
}

/// <summary>
/// Regression cover for two defects found once the parser and the rules were wired
/// together, both of which produced plausible-looking but wrong plans.
/// </summary>
public class VoyageScoringTests
{
    private static Chart Parse(string text) =>
        ChartText.Parse(text, "id", ChartShape.Crossing)!;

    [Fact]
    public void HeadlineStatsAreScorable()
    {
        // The parser lifts "Dead Man's Sulphur: +9" into a typed field, which put it out
        // of reach of the rules -- a sulphur profile scored every chart zero and picked
        // an arbitrary layout. Stats are offered back as text so one mechanism covers all.
        var chart = Parse("A\nDead Man's Sulphur: +9\nMonster Pack Size: +12%");
        var profile = new VoyageProfile
        {
            Name = "s",
            Rules = [new VoyageRule { Pattern = @"Dead Man's Sulphur:\s*\+?(\d+)", Weight = 2 }],
        };
        Assert.Equal(18, profile.ScoreChart(chart));
    }

    [Fact]
    public void TheGlobalVoyageModifierCountsTowardsTheChartsOwnValue()
    {
        var chart = Parse("A\nVoyage Modifier: 8% increased Quantity of Items found in all Voyage Areas");
        var profile = new VoyageProfile
        {
            Name = "q",
            Rules = [new VoyageRule { Pattern = @"(\d+)%\s+increased Quantity", Weight = 1 }],
        };
        Assert.Equal(8, profile.ScoreChart(chart));
    }

    [Fact]
    public void TheAdjacentModifierIsNotCountedInTheChartsOwnValue()
    {
        // It is paid through the neighbours it buffs. Counting it here as well would pay
        // for it twice and pull adjacency charts to squares that do not deserve them.
        var chart = Parse("A\nAdjacent Modifier: Adjacent Areas contain 4 additional Strongboxes");
        var profile = new VoyageProfile
        {
            Name = "b",
            Rules = [new VoyageRule { Pattern = @"(\d+) additional Strongboxes", Weight = 3 }],
        };
        Assert.Equal(0, profile.ScoreChart(chart));
        Assert.Equal(12, profile.ScoreAdjacent(chart));
    }

    [Fact]
    public void AreaLevelIsWeightedExactlyOnce()
    {
        // The session used to add area level on top of ScoreChart, which already
        // included it -- doubling the weight the profile actually asked for.
        var session = new VoyageSession();
        session.ApplyPanelRead(Enumerable.Range(1, 9).Select(i =>
            new ChartPanelReader.ReadCell(i, (i - 1) / 6, (i - 1) % 6, true, true, true, true)
            { Level = 80 }).ToList());

        var solution = session.Solve(
            new VoyageProfile { Name = "lv", AreaLevelWeight = 1 }, TimeSpan.FromMilliseconds(300));

        Assert.Equal(9 * 80, solution.Value);
    }

    [Theory]
    [InlineData("sulphur", "A\nDead Man's Sulphur: +9")]
    [InlineData("pack size", "A\nMonster Pack Size: +12%")]
    [InlineData("quantity", "A\nItem Quantity: +42%")]
    public void ShippedProfilesScoreTheStatTheyAreNamedFor(string profileName, string text)
    {
        // A profile called "sulphur" that cannot see the sulphur stat is decorative.
        var profile = VoyageRules.Defaults().Single(p => p.Name == profileName);
        Assert.True(profile.ScoreChart(Parse(text)) > 0);
    }

    [Fact]
    public void TheSulphurFallbackRuleDoesNotDoubleFireOnTheStatLine()
    {
        var profile = VoyageRules.Defaults().Single(p => p.Name == "sulphur");
        // Only the stat rule may match: 9 * 5.0.
        Assert.Equal(45, profile.ScoreChart(Parse("A\nDead Man's Sulphur: +9")));
    }
}
