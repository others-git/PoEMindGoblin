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
