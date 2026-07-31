using MindGoblin.Core.Voyage;

namespace MindGoblin.Tests;

/// <summary>
/// "Next": the voyage was run, so its charts are spent and the board is stale.
/// </summary>
public class CompleteVoyageTests
{
    private static VoyageSession Session()
    {
        var session = new VoyageSession();
        session.ApplyPanelRead(Enumerable.Range(1, 12).Select(i =>
            new ChartPanelReader.ReadCell(i, (i - 1) / 6, (i - 1) % 6, true, true, true, true)
            { Level = 80 }).ToList());
        return session;
    }

    [Fact]
    public void PlacedChartsLeaveTheInventoryAndTheRestKeepTheirNumbers()
    {
        var session = Session();
        var spent = session.CompleteVoyage([1, 3, 5]);

        Assert.Equal(3, spent);
        Assert.Equal(9, session.Charts.Count);
        // Panel indices point at physical panel positions; the survivors keep theirs.
        Assert.False(session.ByPanelIndex.ContainsKey(3));
        Assert.True(session.ByPanelIndex.ContainsKey(4));
    }

    /// <summary>The border rerolls each voyage, so the old reads describe a dead board.</summary>
    [Fact]
    public void BoardModifiersAndFigurinesAreCleared()
    {
        var session = Session();
        session.ApplySquareModifiers(2, ["Areas have 20% increased Monster Pack Size"]);
        session.ApplyFigurineText(1, "Adjacent Areas contain 4 additional packs of Crabs");

        session.CompleteVoyage([1]);

        Assert.Empty(session.SquareModifiers);
        Assert.Empty(session.Figurines);
        Assert.Contains(2, session.SquaresAwaitingModifiers);
    }

    [Fact]
    public void ACompletedChartThatWasNeverInThePanelCountsNothing()
    {
        Assert.Equal(1, Session().CompleteVoyage([1, 99]));
    }
}

/// <summary>Icons on the plan board come from the same lines the scoring uses, so an
/// icon never promises something the plan did not price.</summary>
public class SquareBadgeTests
{
    [Fact]
    public void LanternsAndBossesAreBadgedFromBoardAndNeighbours()
    {
        var session = new VoyageSession();
        session.ApplyPanelRead(Enumerable.Range(1, 12).Select(i =>
            new ChartPanelReader.ReadCell(i, (i - 1) / 6, (i - 1) % 6, true, true, true, true)
            { Level = 80 }).ToList());
        session.ApplySquareModifiers(1, ["Area contains 4 additional Golden Lanterns"]);
        session.ApplySquareModifiers(5, ["Area contains Filthscrabble"]);

        var sulphur = VoyageRules.Defaults().Single(p => p.Name == "sulphur");
        var solution = session.Solve(sulphur, TimeSpan.FromSeconds(3));
        var badges = session.Badges(solution);

        Assert.Equal(4, badges[1].GoldenLanterns);
        Assert.Contains("Filthscrabble", badges[5].Bosses);
        Assert.False(badges.ContainsKey(9));   // nothing there, no icon
    }

    [Fact]
    public void PayoutsBoxesAndDangersAreBadgedToo()
    {
        var session = new VoyageSession();
        session.ApplyPanelRead(Enumerable.Range(1, 12).Select(i =>
            new ChartPanelReader.ReadCell(i, (i - 1) / 6, (i - 1) % 6, true, true, true, true)
            { Level = 80 }).ToList());
        session.ApplySquareModifiers(1, ["Rare Monsters in Area drop Dead Man's Sulphur"]);
        session.ApplySquareModifiers(3, ["Area contains 2 additional Diviner's Strongboxes"]);
        // The sulphur stat guarantees chart 2 outranks the blank charts and is placed,
        // which the danger-badge assertion below depends on.
        session.ApplyChartText(2,
            "Reach\nAnchorfield\nDead Man's Sulphur: +40\n"
            + "Monsters apply Grasping Vines on Hit");

        var sulphur = VoyageRules.Defaults().Single(p => p.Name == "sulphur");
        var solution = session.Solve(sulphur, TimeSpan.FromSeconds(3));
        var badges = session.Badges(solution);
        var plan = session.Plan(solution);

        Assert.Contains("Dead Man's Sulphur", badges[1].Payouts);
        Assert.Contains("Diviner's Strongboxes", badges[3].Strongboxes);
        var dangerSquare = Assert.Single(plan, s => s.ChartNumber == 2).Square;
        Assert.NotEmpty(badges[dangerSquare].Dangers);
    }
}
