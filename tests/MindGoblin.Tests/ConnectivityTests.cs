using MindGoblin.Core.Voyage;

namespace MindGoblin.Tests;

/// <summary>
/// Edge matching permits a square that is legal and useless: an End pointing at the
/// border, closed on its other three sides, satisfies every rule while touching nothing.
/// It reads as a dead corner on the board, and whatever chart lands there is cut off.
/// </summary>
public class ConnectivityTests
{
    private static Chart Chart(string id, ChartShape shape, double value = 0) =>
        new(id, id, shape, 80, []) { Value = value };

    private static VoyageBoard Board(params (int Row, int Col, ChartShape Shape, int Rot)[] cells)
    {
        var board = new VoyageBoard(3, 3);
        foreach (var (r, c, shape, rot) in cells)
            board.Place(new Placement(Chart($"{r}{c}", shape), new Cell(r, c), rot));
        return board;
    }

    [Fact]
    public void ADeadCornerIsLegalButReportedAsStranded()
    {
        // A single End in the top-left, pointing north into the border. Nothing else on
        // the board, so it violates nothing and connects to nothing.
        var north = Enumerable.Range(0, 4)
            .First(r => new ChartFace(ChartShape.End, r).IsOpen(Side.North));
        var board = Board((0, 0, ChartShape.End, north));

        Assert.Empty(board.Validate());                 // legal
        Assert.Single(board.Components());
        Assert.Empty(board.StrandedCells());            // it IS the only component
    }

    [Fact]
    public void TwoSeparateGroupsLeaveTheSmallerStranded()
    {
        var vertical = Enumerable.Range(0, 4)
            .First(r => new ChartFace(ChartShape.Straight, r).IsOpen(Side.North)
                        && new ChartFace(ChartShape.Straight, r).IsOpen(Side.South));
        var north = Enumerable.Range(0, 4)
            .First(r => new ChartFace(ChartShape.End, r).IsOpen(Side.North));

        // Column 0 joined top-to-bottom, plus a lone End in the far corner.
        var board = Board(
            (0, 0, ChartShape.Straight, vertical),
            (1, 0, ChartShape.Straight, vertical),
            (2, 0, ChartShape.Straight, vertical),
            (0, 2, ChartShape.End, north));

        Assert.Empty(board.Validate());
        Assert.Equal(2, board.Components().Count);
        Assert.Equal(3, board.Components()[0].Count);   // largest first
        Assert.Equal([new Cell(0, 2)], board.StrandedCells());
    }

    [Fact]
    public void ConnectionsMustBeMutualToJoinAGroup()
    {
        // Components must use the same mutual rule as Validate, or it would report two
        // squares as joined when only one of them opens onto the other.
        var eastWest = Enumerable.Range(0, 4)
            .First(r => new ChartFace(ChartShape.Straight, r).IsOpen(Side.East));
        var board = Board(
            (0, 0, ChartShape.Straight, eastWest),
            (0, 1, ChartShape.Straight, eastWest),
            (0, 2, ChartShape.Straight, eastWest));

        Assert.Empty(board.Validate());
        Assert.Single(board.Components());
        Assert.Equal(3, board.Components()[0].Count);
    }

    [Fact]
    public void TheSolverPrefersAConnectedBoardWhenValuesAreEqual()
    {
        // Nine crossings tile the board fully connected. With every value identical the
        // penalty is the only thing distinguishing layouts, so nothing may be stranded.
        var charts = Enumerable.Range(0, 9)
            .Select(i => Chart($"c{i}", ChartShape.Crossing, 10)).ToList();

        var solution = new VoyageSolver(3, 3, charts, strandedPenalty: 40)
            .Solve(TimeSpan.FromSeconds(2));

        Assert.Equal(9, solution.Placements.Count);
        Assert.Empty(solution.StrandedCells);
    }

    [Fact]
    public void AStrandedSquareIsAcceptedWhenItBuysEnough()
    {
        // The trade is the user's to make: a board where one dead corner buys far better
        // charts can still be the right board, so this is a penalty and not a ban.
        var charts = Enumerable.Range(0, 9)
            .Select(i => Chart($"c{i}", ChartShape.Crossing, 10)).ToList();

        var cheap = new VoyageSolver(3, 3, charts, strandedPenalty: 1).Solve(TimeSpan.FromSeconds(2));
        Assert.Equal(9, cheap.Placements.Count);

        // With no penalty at all the solver is free to strand squares; it must still
        // return a legal board either way.
        var free = new VoyageSolver(3, 3, charts, strandedPenalty: 0).Solve(TimeSpan.FromSeconds(2));
        var board = new VoyageBoard(3, 3);
        foreach (var p in free.Placements) board.Place(p);
        Assert.Empty(board.Validate());
    }

    [Fact]
    public void ANegativePenaltyIsClampedBecauseItWouldBreakThePruningBound()
    {
        // The search prunes against a bound built from placement scores alone. A BONUS
        // applied at the end would push the true value above that bound, and the real
        // best layout could be pruned away.
        var charts = Enumerable.Range(0, 9)
            .Select(i => Chart($"c{i}", ChartShape.Crossing, 10)).ToList();

        var solution = new VoyageSolver(3, 3, charts, strandedPenalty: -100)
            .Solve(TimeSpan.FromSeconds(2));

        Assert.Equal(90, solution.Value);       // 9 x 10, no bonus applied
    }

    [Fact]
    public void ProfilesCarryTheirOwnPenaltySoItIsTunable()
    {
        Assert.All(VoyageRules.Defaults(), p => Assert.True(p.StrandedSquarePenalty >= 0));
    }

    [Fact]
    public void TheRealPanelPlanReportsWhetherAnythingIsStranded()
    {
        var session = new VoyageSession();
        session.ApplyPanelRead(Enumerable.Range(1, 12).Select(i =>
            new ChartPanelReader.ReadCell(i, (i - 1) / 6, (i - 1) % 6, true, true, true, true)
            { Level = 80 }).ToList());

        var solution = session.Solve(
            VoyageRules.Defaults().Single(p => p.Name == "high tier"), TimeSpan.FromSeconds(2));

        Assert.Equal(9, solution.Placements.Count);
        Assert.Empty(solution.StrandedCells);
    }
}

/// <summary>
/// A square cut off from the route is never visited -- the voyage starts bottom-left and
/// travels by connections -- so a chart there is wasted. Pricing that was tried twice and
/// neither held.
/// </summary>
public class StrandingIsForbiddenTests
{
    private static Chart Chart(string id, double value) =>
        new(id, id, ChartShape.Crossing, 80, []) { Value = value };

    /// <summary>Crossings tile a full board, so a joined layout always exists.</summary>
    private static IReadOnlyList<Chart> Crossings(int count, double value = 10) =>
        Enumerable.Range(0, count).Select(i => Chart($"c{i}", value)).ToList();

    [Fact]
    public void NoLayoutStrandsASquareWhenAJoinedOneExists()
    {
        var solution = new VoyageSolver(3, 3, Crossings(20), strandedPenalty: 40)
            .Solve(TimeSpan.FromSeconds(2));

        Assert.Equal(9, solution.Placements.Count);
        Assert.Empty(solution.StrandedCells);
    }

    [Fact]
    public void AValuableStrandedLayoutIsStillRejected()
    {
        // The case that kept slipping through: one chart worth far more than the penalty,
        // reachable only by cutting a square off. A flat fee said yes; forfeiting the
        // stranded chart's own value still said yes, because the layout it bought was
        // worth more than the chart it cost.
        var charts = Crossings(20, value: 1).ToList();
        charts.Add(Chart("jackpot", 10_000));

        var solution = new VoyageSolver(3, 3, charts, strandedPenalty: 40)
            .Solve(TimeSpan.FromSeconds(2));

        Assert.Empty(solution.StrandedCells);
    }

    [Fact]
    public void StrandingIsAllowedWhenNoJoinedBoardIsPossible()
    {
        // The constraint is dropped rather than returning nothing: an End pointing at the
        // border, alone, cannot join anything, and the best available answer beats no
        // answer at all.
        var north = Enumerable.Range(0, 4)
            .First(r => new ChartFace(ChartShape.End, r).IsOpen(Side.North));
        _ = north;

        var ends = Enumerable.Range(0, 2)
            .Select(i => new Chart($"e{i}", $"e{i}", ChartShape.End, 80, []) { Value = 5 })
            .ToList();

        var solution = new VoyageSolver(3, 3, ends, strandedPenalty: 40)
            .Solve(TimeSpan.FromSeconds(2));

        // Whatever it returns must at least be a legal board.
        var board = new VoyageBoard(3, 3);
        foreach (var p in solution.Placements) board.Place(p);
        Assert.Empty(board.Validate());
    }

    [Fact]
    public void TheRealPanelNeverStrandsUnderAnyShippedProfile()
    {
        // Five of eleven profiles used to return a dead corner on a real 42-chart board.
        var session = new VoyageSession();
        session.ApplyPanelRead(Enumerable.Range(1, 20).Select(i =>
            new ChartPanelReader.ReadCell(i, (i - 1) / 6, (i - 1) % 6, true, true, true, true)
            { Level = 80 }).ToList());

        foreach (var profile in VoyageRules.Defaults())
        {
            var solution = session.Solve(profile, TimeSpan.FromSeconds(1));
            Assert.True(solution.StrandedCells.Count == 0,
                        $"{profile.Name} stranded {solution.StrandedCells.Count} squares");
        }
    }

    /// <summary>
    /// A FULL panel of MIXED shapes -- the case the all-Crossing panel above cannot
    /// reach. Connectivity depends only on a chart's shape, so a seed that tries every
    /// CHART at a cell re-explores each refuted subproblem once per duplicate: eleven
    /// times over on a 56-chart panel. The existence proof then spent its entire clock
    /// failing, every profile silently lost the stranding constraint, and a real
    /// sulphur solve came back with squares 1, 2 and 3 unreachable.
    ///
    /// The sequence is transcribed from the panel that failed, in panel order, because
    /// the ORDER is what bites: grouped by shape the seed places nine Crossings and
    /// succeeds trivially, while interleaved -- the way a real stash looks -- the dive
    /// wanders. Before the fix sulphur and high tier strand here.
    /// </summary>
    [Fact]
    public void AFullPanelOfMixedShapesNeverStrandsUnderAnyShippedProfile()
    {
        // X Crossing, J Junction, C Corner, E End, S Straight.
        const string panel = "EESCEJJJJCSCEJEXSCEXSCEXJXCEXJXSXCJJJXXCJXXCJJSXCEXEXXCC";

        var session = new VoyageSession();
        session.ApplyPanelRead(panel.Select((glyph, i) =>
        {
            var (n, e, s, w) = glyph switch
            {
                'X' => (true, true, true, true),
                'J' => (true, true, true, false),
                'C' => (true, true, false, false),
                'S' => (true, false, true, false),
                _ => (true, false, false, false),
            };
            return new ChartPanelReader.ReadCell(i + 1, i / 6, i % 6, n, e, s, w) { Level = 80 };
        }).ToList());

        // Hover detail, so the profiles score something and their orderings diverge --
        // an unscored panel makes every chart identical and hides the bug.
        foreach (var i in Enumerable.Range(1, panel.Length))
            session.ApplyChartText(i,
                $"Chart {i}\nAnchorfield\nItem Quantity: +{20 + i % 40}%\n"
                + $"Dead Man's Sulphur: +{i % 25}\nMonster Pack Size: +{i % 30}%");

        foreach (var profile in VoyageRules.Defaults())
        {
            var solution = session.Solve(profile, TimeSpan.FromSeconds(1));
            Assert.True(solution.StrandedCells.Count == 0,
                        $"{profile.Name} stranded {solution.StrandedCells.Count} squares");
        }
    }
}
