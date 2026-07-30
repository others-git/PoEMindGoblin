using MindGoblin.Core.Voyage;

namespace MindGoblin.Tests;

/// <summary>
/// Two post-solve conveniences: knowing when spending all nine charts would discard a
/// refund the board just paid for, and finding the nine in the in-game stash at all.
/// </summary>
public class PreserveAndSearchTests
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
    public void AChartRefundChanceOnAChartIsNoticed()
    {
        var session = Session();
        session.ApplyChartText(3,
            "Reach\nAnchorfield\nVoyage Modifier: 30% chance for Charts to not be consumed");

        Assert.True(session.PreserveChanceInPlay([3, 5, 7]));
        Assert.False(session.PreserveChanceInPlay([5, 7]));   // the carrier is not placed
    }

    [Fact]
    public void AChartRefundChanceOnTheBorderIsNoticed()
    {
        var session = Session();
        session.ApplySquareModifiers(4, ["20% chance for Charts to not be consumed"]);
        Assert.True(session.PreserveChanceInPlay([1, 2]));
    }

    [Fact]
    public void NoRefundChanceMeansNoQuestion() =>
        Assert.False(Session().PreserveChanceInPlay([1, 2, 3]));

    [Fact]
    public void TheSearchStringNamesEveryNamedChartOnce()
    {
        var session = Session();
        session.ApplyChartText(1, "Armoured Coral Forest\nAnchorfield\nItem Quantity: +10%");
        session.ApplyChartText(2, "Sunken Pelagic Reach\nPelagic Abyss\nItem Quantity: +10%");
        // same most-distinctive word as chart 1 -- deduplicated, not repeated
        session.ApplyChartText(3, "Armoured Kelp Bed\nDiving Shoals\nItem Quantity: +10%");

        Assert.Equal("\"armoured|pelagic\"", session.StashSearch([1, 2, 3]));
    }

    [Fact]
    public void UnnamedChartsProduceNoSearchString() =>
        Assert.Equal("", Session().StashSearch([1, 2]));
}
