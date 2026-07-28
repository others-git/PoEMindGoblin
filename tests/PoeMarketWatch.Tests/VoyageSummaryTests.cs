using PoeMarketWatch.Core.Voyage;

namespace PoeMarketWatch.Tests;

/// <summary>
/// The summary replaced a list that repeated the plan -- nine rows of "square 1 &lt;- chart
/// 23" beside a board already showing exactly that. What a board is WORTH cannot be read
/// off the squares, and that is what this has to get right.
/// </summary>
public class VoyageSummaryTests
{
    private static Chart Chart(string id, double quantity = 0, double sulphur = 0,
                               string? global = null, string? adjacent = null,
                               int level = 80) =>
        new(id, id, ChartShape.Crossing, level, [])
        {
            ItemQuantity = quantity,
            Sulphur = sulphur,
            VoyageModifier = global,
            AdjacentModifier = adjacent,
        };

    private static (VoyageBoard Board, Dictionary<string, int> Numbers) Board(
        params (Chart Chart, int Row, int Col)[] cells)
    {
        var board = new VoyageBoard(3, 3);
        var numbers = new Dictionary<string, int>(StringComparer.Ordinal);
        var n = 1;
        foreach (var (chart, row, col) in cells)
        {
            board.Place(new Placement(chart, new Cell(row, col), 0));
            numbers[chart.Id] = n++;
        }
        return (board, numbers);
    }

    [Fact]
    public void StatsAreSummedAcrossTheBoard()
    {
        // Every square is its own AREA, so nine charts at +80% quantity is nine areas each
        // rolling at +80%. Averaging would make a four-chart board look identical to a
        // nine-chart one.
        var (board, numbers) = Board(
            (Chart("a", quantity: 80, sulphur: 30), 0, 0),
            (Chart("b", quantity: 40, sulphur: 60), 0, 1));

        var summary = VoyageSummary.Build(board, numbers, 3);

        Assert.Equal(120, summary.Stats.Single(s => s.Stat == "Item Quantity").Total);
        Assert.Equal(90, summary.Stats.Single(s => s.Stat == "Dead Man's Sulphur").Total);
    }

    [Fact]
    public void AStatNothingCarriesIsLeftOutEntirely()
    {
        var (board, numbers) = Board((Chart("a", quantity: 80), 0, 0));
        var summary = VoyageSummary.Build(board, numbers, 3);

        Assert.Contains(summary.Stats, s => s.Stat == "Item Quantity");
        Assert.DoesNotContain(summary.Stats, s => s.Stat == "Gold Found");
    }

    [Fact]
    public void OnlyGlobalModifiersOnTheBoardAreInEffect()
    {
        // A chart left in the panel contributes nothing, which is the whole reason the
        // summary is built from the BOARD rather than the session.
        var (board, numbers) = Board(
            (Chart("a", global: "8% increased Pack Size in all Voyage Areas"), 0, 0),
            (Chart("b"), 0, 1));

        var summary = VoyageSummary.Build(board, numbers, 3);
        Assert.Equal(["8% increased Pack Size in all Voyage Areas"], summary.VoyageWide);
    }

    [Fact]
    public void AdjacencyReachIsCountedBecauseItDecidesTheValue()
    {
        // The same modifier is worth twice as much in the middle as in a corner, and that
        // is invisible without the neighbour count.
        var spreader = Chart("mid", adjacent: "Adjacent Areas contain 4 additional Strongboxes");
        var (board, numbers) = Board(
            (spreader, 1, 1),                     // centre: four neighbours
            (Chart("n"), 0, 1), (Chart("s"), 2, 1),
            (Chart("w"), 1, 0), (Chart("e"), 1, 2));

        var adjacency = Assert.Single(VoyageSummary.Build(board, numbers, 3).Adjacencies);
        Assert.Equal(4, adjacency.Reach);
        Assert.Equal(5, adjacency.Square);          // centre of a 3x3
        Assert.Equal(1, adjacency.ChartNumber);
    }

    [Fact]
    public void AnAdjacencyChartInACornerReachesFewer()
    {
        var spreader = Chart("corner", adjacent: "Adjacent Areas contain 4 additional Strongboxes");
        var (board, numbers) = Board(
            (spreader, 0, 0),
            (Chart("e"), 0, 1), (Chart("s"), 1, 0));

        var adjacency = Assert.Single(VoyageSummary.Build(board, numbers, 3).Adjacencies);
        Assert.Equal(2, adjacency.Reach);
    }

    [Fact]
    public void AnAdjacencyChartWithNoNeighboursIsNotListed()
    {
        // It pays nothing, so reporting it would overstate the board.
        var (board, numbers) = Board(
            (Chart("lonely", adjacent: "Adjacent Areas contain 4 additional Strongboxes"), 0, 0));

        Assert.Empty(VoyageSummary.Build(board, numbers, 3).Adjacencies);
    }

    [Fact]
    public void TheBiggestReachIsListedFirst()
    {
        var (board, numbers) = Board(
            (Chart("corner", adjacent: "A"), 0, 0),
            (Chart("mid", adjacent: "B"), 1, 1),
            (Chart("x"), 0, 1), (Chart("y"), 1, 0),
            (Chart("z"), 2, 1), (Chart("w"), 1, 2));

        var adjacencies = VoyageSummary.Build(board, numbers, 3).Adjacencies;
        Assert.Equal(4, adjacencies[0].Reach);
        Assert.True(adjacencies[0].Reach >= adjacencies[1].Reach);
    }

    [Fact]
    public void TheHeadlineDescribesTheTierSpread()
    {
        var (board, numbers) = Board(
            (Chart("a", level: 76), 0, 0),
            (Chart("b", level: 83), 0, 1));

        var summary = VoyageSummary.Build(board, numbers, 3);
        Assert.Equal(76, summary.LowestLevel);
        Assert.Equal(83, summary.HighestLevel);
        Assert.Contains("76", summary.Headline);
        Assert.Contains("83", summary.Headline);
    }

    [Fact]
    public void AUniformBoardSaysSoRatherThanShowingARange()
    {
        var (board, numbers) = Board(
            (Chart("a", level: 83), 0, 0), (Chart("b", level: 83), 0, 1));
        Assert.Contains("all level 83", VoyageSummary.Build(board, numbers, 3).Headline);
    }

    [Fact]
    public void AnEmptyBoardSummarisesToNothing()
    {
        var summary = VoyageSummary.Build(new VoyageBoard(3, 3), new Dictionary<string, int>(), 3);
        Assert.Equal(0, summary.Charts);
        Assert.Empty(summary.Stats);
        Assert.Equal("Nothing placed", summary.Headline);
    }
}
