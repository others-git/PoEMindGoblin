using MindGoblin.Core.Voyage;

namespace MindGoblin.Tests;

/// <summary>
/// Three bugs that all showed up as "the optimiser ignores the charts I care about", and
/// none of which the solver reported as a failure -- two of them it reported as a PROOF.
/// </summary>
public class SolverSearchTests
{
    private static Chart Chart(string id, double own, double adjacent = 0) =>
        new(id, id, ChartShape.Crossing, 80, []) { Value = own, AdjacentValue = adjacent };

    [Fact]
    public void AChartWhoseValueIsAllAdjacencyIsStillTried()
    {
        // The candidates at each cell are ordered by the chart's OWN score, but a chart's
        // gain also includes what it gives its neighbours. Stopping the loop at the first
        // candidate that could not beat the incumbent therefore threw away every chart
        // whose worth is in its adjacency -- which is exactly what a strongbox chart is.
        var charts = Enumerable.Range(0, 12).Select(i => Chart($"plain{i}", 10)).ToList();
        charts.Add(Chart("spreader", own: 1, adjacent: 500));

        var solution = new VoyageSolver(3, 3, charts).Solve(TimeSpan.FromSeconds(2));

        Assert.Contains(solution.Placements, p => p.Chart.Id == "spreader");
    }

    [Fact]
    public void TheAdjacencyChartLandsWhereItReachesMost()
    {
        // Exactly nine charts, so the search can actually finish and the claim is about
        // the optimum rather than about how far an anytime search happened to get.
        var charts = Enumerable.Range(0, 8).Select(i => Chart($"plain{i}", 10)).ToList();
        charts.Add(Chart("spreader", own: 1, adjacent: 500));

        var solution = new VoyageSolver(3, 3, charts).Solve(TimeSpan.FromSeconds(5));
        var spreader = solution.Placements.Single(p => p.Chart.Id == "spreader");

        // Four neighbours in the centre, two in a corner -- and the modifier is paid per
        // neighbour, so the centre is worth exactly twice.
        Assert.Equal(new Cell(1, 1), spreader.Cell);
    }

    [Fact]
    public void AChartWhoseValueIsAllContainerGiftIsStillTried()
    {
        // The strongbox story above, per channel: the loop must CONTINUE past a weak
        // candidate so a chart whose whole worth is its per-quantity gift is still
        // reached. The ordering boost surfaces these channels early too -- its first
        // cut summed only the flat and per-monster parts, and a real bottles board
        // paid 121 points for the weak incumbents that ordering produced.
        var charts = Enumerable.Range(0, 12)
            .Select(i => Chart($"plain{i}", 10) with { QuantityDensity = 1 }).ToList();
        charts.Add(Chart("bottler", own: 1) with { AdjacentPerQuantityValue = 500 });

        var solution = new VoyageSolver(3, 3, charts).Solve(TimeSpan.FromSeconds(2));

        Assert.Contains(solution.Placements, p => p.Chart.Id == "bottler");
    }

    [Fact]
    public void AChartWhoseValueIsAllAmplifierIsStillTried()
    {
        // Fourth channel: the amplifier pays a share of its neighbours' explicit value
        // and nothing of its own, so every mechanism that surfaces candidates -- the
        // continue rule and the ordering boost -- must see that channel as well.
        var charts = Enumerable.Range(0, 12)
            .Select(i => Chart($"plain{i}", 10) with { ExplicitValue = 100 }).ToList();
        charts.Add(Chart("amplifier", own: 1) with { AdjacentMagnitudeValue = 5 });

        var solution = new VoyageSolver(3, 3, charts).Solve(TimeSpan.FromSeconds(2));

        Assert.Contains(solution.Placements, p => p.Chart.Id == "amplifier");
    }

    [Fact]
    public void TheContainerGiftLandsBesideTheRichestReceivers()
    {
        // Nine charts so the claim is about the optimum. The gift multiplies by each
        // RECEIVER's quantity, so with uniform receivers the centre's four neighbours
        // are worth exactly twice a corner's two.
        var charts = Enumerable.Range(0, 8)
            .Select(i => Chart($"plain{i}", 10) with { QuantityDensity = 1 }).ToList();
        charts.Add(Chart("bottler", own: 1) with { AdjacentPerQuantityValue = 500 });

        var solution = new VoyageSolver(3, 3, charts).Solve(TimeSpan.FromSeconds(5));
        var bottler = solution.Placements.Single(p => p.Chart.Id == "bottler");

        Assert.Equal(new Cell(1, 1), bottler.Cell);
    }

    [Fact]
    public void APinnedChartSitsAtItsPinAndNowhereElse()
    {
        // A pin is a constraint, not a price: priced, the bound cannot see that an
        // early rotation closed the pinned cell off, and the budget burns inside
        // prefixes that can never satisfy it (the bottle chart came back cornered at
        // a 1e7 bonus). Constrained, such branches die at the pinned cell.
        var charts = Enumerable.Range(0, 12).Select(i => Chart($"plain{i}", 50)).ToList();
        charts.Add(Chart("pinned", 1));

        var solution = new VoyageSolver(3, 3, charts, pin: ("pinned", new Cell(0, 2)))
            .Solve(TimeSpan.FromSeconds(2));

        var placed = solution.Placements.Single(p => p.Chart.Id == "pinned");
        Assert.Equal(new Cell(0, 2), placed.Cell);
        Assert.Equal(9, solution.Placements.Count);
    }

    [Fact]
    public void ASolutionNeverHoldsMoreThanTheBoard()
    {
        // The deadline unwinds the recursion by exception, and each frame undoes its
        // placement only AFTER the recursive call returns -- so an abort used to leave
        // the board half filled. Reusing it across passes produced solutions holding
        // charts from an abandoned branch.
        var charts = Enumerable.Range(0, 40).Select(i => Chart($"c{i}", i)).ToList();

        foreach (var ms in new[] { 1, 5, 20, 100 })
        {
            var solution = new VoyageSolver(3, 3, charts, strandedPenalty: 40)
                .Solve(TimeSpan.FromMilliseconds(ms));

            Assert.True(solution.Placements.Count <= 9,
                        $"{solution.Placements.Count} placements on a 9-cell board");
            Assert.Equal(solution.Placements.Count,
                         solution.Placements.Select(p => p.Cell).Distinct().Count());
            Assert.Equal(solution.Placements.Count,
                         solution.Placements.Select(p => p.Chart.Id).Distinct().Count());
        }
    }

    [Fact]
    public void ATimedOutSearchDoesNotClaimToHaveProvedAnything()
    {
        // A pass starting from a corrupted board bottomed out after two nodes and
        // reported itself exhausted, so a search that had explored almost nothing
        // announced the answer as proved.
        var charts = Enumerable.Range(0, 40).Select(i => Chart($"c{i}", i, adjacent: i)).ToList();

        var solution = new VoyageSolver(3, 3, charts, strandedPenalty: 40)
            .Solve(TimeSpan.FromMilliseconds(2));

        Assert.False(solution.ProvedOptimal,
                     "a two-millisecond search over 40 charts cannot have proved anything");
    }

    [Fact]
    public void MoreTimeNeverProducesAWorseAnswer()
    {
        // Anytime search: the incumbent only ever improves. A drop means a pass corrupted
        // shared state, which is precisely what was happening.
        var charts = Enumerable.Range(0, 30).Select(i => Chart($"c{i}", i % 7, adjacent: i % 5)).ToList();

        var quick = new VoyageSolver(3, 3, charts, strandedPenalty: 40)
            .Solve(TimeSpan.FromMilliseconds(200));
        var slow = new VoyageSolver(3, 3, charts, strandedPenalty: 40)
            .Solve(TimeSpan.FromSeconds(3));

        Assert.True(slow.Value >= quick.Value,
                    $"3s found {slow.Value} where 200ms found {quick.Value}");
    }

    [Fact]
    public void EachPassStartsFromAnEmptyBoard()
    {
        // Two solves on separate instances with the same input must agree; if a pass left
        // state behind, the second would differ.
        var charts = Enumerable.Range(0, 20).Select(i => Chart($"c{i}", i, adjacent: i % 3)).ToList();

        var first = new VoyageSolver(3, 3, charts, strandedPenalty: 40).Solve(TimeSpan.FromSeconds(1));
        var second = new VoyageSolver(3, 3, charts, strandedPenalty: 40).Solve(TimeSpan.FromSeconds(1));

        Assert.Equal(first.Value, second.Value);
        Assert.Equal(first.Placements.Select(p => p.Chart.Id).Order(),
                     second.Placements.Select(p => p.Chart.Id).Order());
    }
}

/// <summary>
/// Two claims the solver makes about itself, both of which it got wrong.
/// </summary>
public class SolverHonestyTests
{
    private static Chart Chart(string id, double own, double adjacent = 0) =>
        new(id, id, ChartShape.Crossing, 80, []) { Value = own, AdjacentValue = adjacent };

    [Fact]
    public void FinishingARestrictedPassIsNotAProof()
    {
        // The search runs restricted passes first, and those deliberately exclude legal
        // layouts. Finishing one says nothing about what it was forbidden to consider, so
        // only the unrestricted pass can claim a proof -- and if the budget ran out
        // before it started, nothing was proved at all.
        var charts = Enumerable.Range(0, 40).Select(i => Chart($"c{i}", i, adjacent: i % 5)).ToList();

        foreach (var ms in new[] { 1, 3, 10, 40 })
            Assert.False(new VoyageSolver(3, 3, charts, strandedPenalty: 40)
                             .Solve(TimeSpan.FromMilliseconds(ms)).ProvedOptimal,
                         $"claimed a proof after {ms}ms over 40 charts");
    }

    [Fact]
    public void ASmallBoardIsStillSolvedToTheOptimum()
    {
        // The restricted passes are a head start, not the search. Giving them a fixed
        // SLICE of the clock meant that on a board the unrestricted search could actually
        // finish, half the budget went to passes that could not, and the real search ran
        // out before reaching the answer. They are capped by work now.
        var charts = Enumerable.Range(0, 8).Select(i => Chart($"plain{i}", 10)).ToList();
        charts.Add(Chart("spreader", own: 1, adjacent: 500));

        // Asserting the OPTIMUM is found, not that the search proved it. Proving even
        // nine charts means exhausting on the order of a million nodes, and whether that
        // fits in a budget is an implementation detail; landing on the right answer, and
        // not improving on it with far more time, is the property that matters.
        var quick = new VoyageSolver(3, 3, charts).Solve(TimeSpan.FromSeconds(2));
        var slow = new VoyageSolver(3, 3, charts).Solve(TimeSpan.FromSeconds(8));

        Assert.Equal(new Cell(1, 1), quick.Placements.Single(p => p.Chart.Id == "spreader").Cell);
        Assert.Equal(quick.Value, slow.Value);
    }
}

/// <summary>
/// The summary describes what the voyage collects, and a chart on a square cut off from
/// the route collects nothing.
/// </summary>
public class StrandedSummaryTests
{
    [Fact]
    public void AStrandedChartIsNotCountedInTheTotals()
    {
        var board = new VoyageBoard(3, 3);
        var joined = new Chart("joined", "joined", ChartShape.Straight, 80, []) { ItemQuantity = 50 };
        var cut = new Chart("cut", "cut", ChartShape.End, 80, []) { ItemQuantity = 999 };

        var eastWest = Enumerable.Range(0, 4)
            .First(r => new ChartFace(ChartShape.Straight, r).IsOpen(Side.East));
        var north = Enumerable.Range(0, 4)
            .First(r => new ChartFace(ChartShape.End, r).IsOpen(Side.North));

        board.Place(new Placement(joined, new Cell(0, 0), eastWest));
        board.Place(new Placement(joined with { Id = "j2" }, new Cell(0, 1), eastWest));
        board.Place(new Placement(cut, new Cell(2, 2), north));

        var summary = VoyageSummary.Build(board, new Dictionary<string, int>(), 3);

        // Two joined charts at +50 each; the cut-off one's +999 is not collected.
        Assert.Equal(100, summary.Stats.Single(s => s.Stat == "Item Quantity").Total);
        Assert.Equal(2, summary.Charts);
    }
}

/// <summary>
/// Solve takes a budget. Honouring it is not a nicety -- the UI passes three seconds and
/// blocks the dispatcher until it returns.
/// </summary>
public class SolverBudgetTests
{
    private static Chart Chart(string id, double own, double adjacent = 0) =>
        new(id, id, ChartShape.Crossing, 80, []) { Value = own, AdjacentValue = adjacent };

    [Theory]
    [InlineData(200)] [InlineData(1000)] [InlineData(3000)]
    public void TheBudgetIsRespected(int ms)
    {
        // Seeding used to run on a node cap with no clock at all. Six dives -- three
        // border rules by two orderings -- meant a three-second budget could spend nine
        // seconds before the search even began.
        var charts = Enumerable.Range(0, 42)
            .Select(i => Chart($"c{i}", i % 11, adjacent: i % 7)).ToList();

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var solution = new VoyageSolver(3, 3, charts, strandedPenalty: 40)
            .Solve(TimeSpan.FromMilliseconds(ms));
        clock.Stop();

        // Generous: the clock is checked sparsely on purpose. Overrunning by a small
        // multiple is tolerable, by 3x is the bug.
        Assert.True(clock.ElapsedMilliseconds < ms * 2 + 750,
                    $"a {ms}ms budget took {clock.ElapsedMilliseconds}ms");
        Assert.NotEmpty(solution.Placements);
    }

    [Fact]
    public void ShapesThatCannotBeJoinedStillReturnPromptly()
    {
        // The worst case for seeding: every border rule fails, under both orderings, so
        // all six dives run before the search starts.
        var ends = Enumerable.Range(0, 30)
            .Select(i => new Chart($"e{i}", $"e{i}", ChartShape.End, 80, []) { Value = i })
            .ToList();

        var clock = System.Diagnostics.Stopwatch.StartNew();
        _ = new VoyageSolver(3, 3, ends, strandedPenalty: 40).Solve(TimeSpan.FromMilliseconds(500));
        clock.Stop();

        // Generous against a 500ms budget, but not SO generous that a seed which ignores
        // the clock passes: that regression measured 2.9 seconds and a 2500ms bar had
        // been raised to accommodate it rather than to describe the contract.
        Assert.True(clock.ElapsedMilliseconds < 1200,
                    $"took {clock.ElapsedMilliseconds}ms on an unjoinable board");
    }
}
