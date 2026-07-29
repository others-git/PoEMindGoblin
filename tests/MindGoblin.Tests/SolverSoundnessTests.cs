using MindGoblin.Core.Voyage;

namespace MindGoblin.Tests;

/// <summary>
/// A branch-and-bound answer is only as honest as its bound. These tests pin the two
/// properties that make the solver's word worth anything: the bound never prunes the
/// true optimum, and the result never misdescribes the board it returns.
/// </summary>
public class SolverSoundnessTests
{
    private static Chart Straight(string id) => new(id, id, ChartShape.Straight, 80, []);

    /// <summary>
    /// The per-chart bound must survive PER-CELL scoring -- which is the normal case in
    /// the app, because board modifiers buff specific squares.
    ///
    /// This instance is a trap for a suffix-of-the-sorted-list bound. Chart a is the
    /// best chart overall AND the greedy first choice at cell 0, but its true home is
    /// cell 2. The greedy descent (a,c,b = 13) becomes the incumbent; the optimal branch
    /// starts with the modest b at cell 0, and a bound that assumes later cells only
    /// ever hold LOWER-ranked charts caps that branch at 12 and prunes it -- then
    /// reports the 13 as proved. The real optimum is b(4) + c(4) + a(10) = 18.
    /// </summary>
    [Fact]
    public void PerCellScoringCannotPruneTheTrueOptimum()
    {
        var charts = new[] { Straight("a"), Straight("b"), Straight("c") };
        double Score(Chart chart, Cell cell) => (chart.Id, cell.Col) switch
        {
            ("a", 0) => 9,
            ("a", 2) => 10,
            ("b", 0) => 4,
            ("c", 1) => 4,
            _ => 0,
        };

        var solution = new VoyageSolver(1, 3, charts, Score, strandedPenalty: 40)
            .Solve(TimeSpan.FromSeconds(5));

        Assert.Equal(18, solution.Value);
        Assert.True(solution.ProvedOptimal);
    }

    /// <summary>
    /// Stranding must be REPORTED regardless of whether it is priced.
    ///
    /// Three Ends on a 1x3 cannot all join -- the middle chart has one open edge and
    /// cannot face both neighbours -- so any full board strands a square. Here the
    /// adjacency-heavy c makes that worth doing anyway: c in the middle pays 50 to each
    /// physical neighbour whatever the edges say, so the best board is a joined pair
    /// plus a stranded flanker. A solver with no penalty configured used to return that
    /// board while claiming every square was joined.
    /// </summary>
    [Fact]
    public void AStrandedSquareIsReportedEvenWhenThePenaltyIsZero()
    {
        var a = new Chart("a", "a", ChartShape.End, 80, []) { Value = 5 };
        var b = new Chart("b", "b", ChartShape.End, 80, []) { Value = 5 };
        var c = new Chart("c", "c", ChartShape.End, 80, []) { Value = 5, AdjacentValue = 50 };

        var solution = new VoyageSolver(1, 3, [a, b, c], strandedPenalty: 0)
            .Solve(TimeSpan.FromSeconds(5));

        // 15 raw + two 50-point adjacencies - the stranded flanker's forfeited 5.
        Assert.Equal(3, solution.Placements.Count);
        Assert.Equal(110, solution.Value);
        Assert.NotEmpty(solution.StrandedCells);
    }

    /// <summary>
    /// The forfeit is not a penalty and does not switch off with one. A chart standing
    /// where the route never goes pays NOTHING, so with three plain Ends the solver
    /// leaves the third square empty rather than spend a chart on it: the full board's
    /// raw 15 forfeits down to the joined pair's 10, and the pair wastes nothing.
    /// Before the forfeit applied at zero penalty, this returned the full board and
    /// called the dead chart free value.
    /// </summary>
    [Fact]
    public void AChartThatWouldPayNothingIsNotSpent()
    {
        var ends = Enumerable.Range(0, 3)
            .Select(i => new Chart($"e{i}", $"e{i}", ChartShape.End, 80, []) { Value = 5 })
            .ToList();

        var solution = new VoyageSolver(1, 3, ends, strandedPenalty: 0)
            .Solve(TimeSpan.FromSeconds(5));

        Assert.Equal(2, solution.Placements.Count);
        Assert.Equal(10, solution.Value);
        Assert.Empty(solution.StrandedCells);
    }
}
