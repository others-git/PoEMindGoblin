using System.Diagnostics;
using PoeMarketWatch.Core.Voyage;

namespace PoeMarketWatch.Tests;

public class VoyageSolverTests
{
    private static Chart C(string id, ChartShape shape, double value = 1, int level = 80) =>
        new(id, id, shape, level, Array.Empty<string>()) { Value = value };

    [Fact]
    public void SingleCellTakesTheMostValuableLegalChart()
    {
        // On a 1x1 board every shape's paths run straight off into the border, so the
        // only thing that matters is value.
        var charts = new[] { C("cheap", ChartShape.End, 1), C("rich", ChartShape.End, 9) };
        var s = new VoyageSolver(1, 1, charts).Solve();
        Assert.Equal("rich", Assert.Single(s.Placements).Chart.Id);
        Assert.Equal(9, s.Value, 3);
    }

    [Fact]
    public void RefusesToLeaveAPathPointingIntoAnEmptyCell()
    {
        var charts = new[] { C("a", ChartShape.End, 5) };
        var s = new VoyageSolver(1, 2, charts).Solve();
        Assert.True(new[] { 0, 1 }.Contains(s.Placements.Count));
        // whatever it chose must be a legal board
        var board = new VoyageBoard(1, 2);
        foreach (var p in s.Placements) board.Place(p);
        Assert.True(board.IsValid());
    }

    [Fact]
    public void ConnectsTwoChartsRatherThanStrandingThem()
    {
        // Two Ends on a 1x2 board can only both be placed by facing each other.
        var charts = new[] { C("a", ChartShape.End, 5), C("b", ChartShape.End, 5) };
        var s = new VoyageSolver(1, 2, charts).Solve();
        Assert.Equal(2, s.Placements.Count);
        Assert.Equal(10, s.Value, 3);

        var board = new VoyageBoard(1, 2);
        foreach (var p in s.Placements) board.Place(p);
        Assert.True(board.IsValid());
    }

    [Fact]
    public void FillsAThreeByThreeBoardLegally()
    {
        var charts = Enumerable.Range(0, 12)
            .Select(i => C($"c{i}", i % 2 == 0 ? ChartShape.Crossing : ChartShape.Straight, i))
            .ToList();

        var s = new VoyageSolver(3, 3, charts).Solve();

        var board = new VoyageBoard(3, 3);
        foreach (var p in s.Placements) board.Place(p);
        Assert.True(board.IsValid(), board.Render());
    }

    [Fact]
    public void PrefersHigherValueWhenBothLayoutsAreLegal()
    {
        var charts = new[]
        {
            C("dull", ChartShape.Crossing, 1),
            C("prize", ChartShape.Crossing, 100),
        };
        var s = new VoyageSolver(1, 1, charts).Solve();
        Assert.Equal("prize", Assert.Single(s.Placements).Chart.Id);
    }

    [Fact]
    public void BoardModifiersPullValueTowardsTheCellsTheyTouch()
    {
        // The whole reason value is scored per (chart, cell): a modifier buffs ADJACENT
        // squares, so the same chart is worth more in the right place.
        var charts = new[] { C("a", ChartShape.Crossing, 10), C("b", ChartShape.Crossing, 10) };
        var buffed = new Cell(0, 1);
        var modifiers = new[]
        {
            new BoardModifier("Adjacent Areas contain 8 additional packs of Sea Beasts", [buffed]),
        };

        var score = VoyageSolver.ScoreWith(modifiers, (_, chart) => chart.Value * 0.5);
        var s = new VoyageSolver(1, 2, charts, score).Solve();

        // 10 + 10 + a 5 bonus for whichever chart landed on the buffed cell
        Assert.Equal(25, s.Value, 3);
        Assert.Contains(s.Placements, p => p.Cell == buffed);
    }

    [Fact]
    public void ModifierAffectsOnlyItsListedCells()
    {
        var m = new BoardModifier("x", [new Cell(0, 0), new Cell(1, 1)]);
        Assert.True(m.Affects(new Cell(0, 0)));
        Assert.True(m.Affects(new Cell(1, 1)));
        Assert.False(m.Affects(new Cell(0, 1)));
    }

    [Fact]
    public void EmptyInventoryYieldsAnEmptyPlan()
    {
        var s = new VoyageSolver(3, 3, Array.Empty<Chart>()).Solve();
        Assert.True(s.IsEmpty);
        Assert.Equal(0, s.Value, 3);
    }

    [Fact]
    public void DisallowingEmptyCellsCanMakeAnUnsatisfiableBoard()
    {
        // One chart cannot fill a 1x2 board when every cell must be occupied.
        var charts = new[] { C("a", ChartShape.End, 5) };
        var s = new VoyageSolver(1, 2, charts, allowEmpty: false).Solve();
        Assert.True(s.IsEmpty);
    }

    [Fact]
    public void HandlesTheRealProblemSizeQuickly()
    {
        // The actual case: 60 charts, 9 squares. Brute force is 60P9 x rotations, so this
        // asserts the pruning is doing its job rather than that the model is correct.
        var rng = new Random(42);
        var shapes = Enum.GetValues<ChartShape>();
        var charts = Enumerable.Range(0, 60)
            .Select(i => C($"c{i}", shapes[rng.Next(shapes.Length)], rng.Next(1, 100)))
            .ToList();

        var sw = Stopwatch.StartNew();
        var solver = new VoyageSolver(3, 3, charts);
        var s = solver.Solve(TimeSpan.FromSeconds(2));
        sw.Stop();

        var board = new VoyageBoard(3, 3);
        foreach (var p in s.Placements) board.Place(p);
        Assert.True(board.IsValid(), board.Render());
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20),
            $"took {sw.Elapsed.TotalSeconds:0.0}s over {solver.NodesExplored:N0} nodes");
    }

    [Fact]
    public void CancellationIsHonoured()
    {
        var charts = Enumerable.Range(0, 60)
            .Select(i => C($"c{i}", ChartShape.Crossing, i)).ToList();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => new VoyageSolver(3, 3, charts).Solve(ct: cts.Token));
    }
}

public class VoyagePlanTests
{
    private static Chart C(string id, ChartShape shape, double value = 1) =>
        new(id, id, shape, 80, Array.Empty<string>()) { Value = value };

    [Theory]
    [InlineData(0, 0, 1)]
    [InlineData(0, 2, 3)]
    [InlineData(1, 0, 4)]
    [InlineData(2, 2, 9)]
    public void SquaresAreNumberedRowMajorFromOne(int row, int col, int expected)
    {
        Assert.Equal(expected, VoyagePlan.SquareNumber(new Cell(row, col), 3));
    }

    [Fact]
    public void PlanNamesBothTheSquareAndTheChartNumber()
    {
        // The output the player needs: "square 5 <- chart 23", so it can be followed
        // without re-deriving anything from shapes or names.
        var inventory = Enumerable.Range(0, 60).Select(i => C($"c{i}", ChartShape.Crossing, i)).ToList();
        var solution = new VoyageSolver.Solution(
            [new Placement(inventory[22], new Cell(1, 1), 0)], 22);

        var step = Assert.Single(VoyagePlan.Describe(solution, 3, inventory));
        Assert.Equal(5, step.Square);
        Assert.Equal(23, step.ChartNumber);   // 1-based, matching the 6x10 panel
        Assert.Contains("square 5", step.ToString());
        Assert.Contains("chart 23", step.ToString());
    }

    [Fact]
    public void StepsComeBackInSquareOrder()
    {
        var inventory = Enumerable.Range(0, 9).Select(i => C($"c{i}", ChartShape.Crossing)).ToList();
        var solution = new VoyageSolver.Solution(
        [
            new Placement(inventory[0], new Cell(2, 2), 0),
            new Placement(inventory[1], new Cell(0, 0), 0),
            new Placement(inventory[2], new Cell(1, 1), 0),
        ], 3);

        var steps = VoyagePlan.Describe(solution, 3, inventory);
        Assert.Equal([1, 5, 9], steps.Select(s => s.Square));
    }

    [Fact]
    public void RotationIsSpelledOutInDegrees()
    {
        var inventory = new[] { C("a", ChartShape.Corner) };
        var solution = new VoyageSolver.Solution(
            [new Placement(inventory[0], new Cell(0, 0), 2)], 1);
        var step = Assert.Single(VoyagePlan.Describe(solution, 3, inventory));
        Assert.Equal("rotate 180°", step.RotationText);
    }

    [Fact]
    public void UnrotatedChartsSaySo()
    {
        var inventory = new[] { C("a", ChartShape.Crossing) };
        var solution = new VoyageSolver.Solution(
            [new Placement(inventory[0], new Cell(0, 0), 0)], 1);
        Assert.Equal("as-is", VoyagePlan.Describe(solution, 3, inventory)[0].RotationText);
    }
}

public class VoyageAnytimeTests
{
    private static List<Chart> Make(int n, int seed)
    {
        var rng = new Random(seed);
        var shapes = Enum.GetValues<ChartShape>();
        return Enumerable.Range(0, n).Select(i => new Chart(
                $"c{i}", $"chart{i}", shapes[rng.Next(shapes.Length)],
                rng.Next(68, 84), Array.Empty<string>())
            { Value = rng.Next(5, 120) }).ToList();
    }

    [Fact]
    public void ReturnsAStrongLayoutWithinASmallBudget()
    {
        // The real case: 60 charts, 9 squares, board modifiers. A full optimality proof
        // can run for minutes here; a great answer immediately is what the tool needs.
        var mods = new[] { new BoardModifier("Adjacent: 8 additional packs", [new Cell(1, 0)]) };
        var score = VoyageSolver.ScoreWith(mods, (_, c) => c.Value * 0.5);

        var s = new VoyageSolver(3, 3, Make(60, 7), score).Solve(TimeSpan.FromMilliseconds(300));

        Assert.Equal(9, s.Placements.Count);
        var board = new VoyageBoard(3, 3);
        foreach (var p in s.Placements) board.Place(p);
        Assert.True(board.IsValid(), board.Render());
    }

    [Fact]
    public void SaysWhetherOptimalityWasProved()
    {
        // Presenting "good" as "best" would be a lie, so the flag is part of the result.
        var quick = new VoyageSolver(1, 1, Make(3, 1)).Solve(TimeSpan.FromSeconds(5));
        Assert.True(quick.ProvedOptimal);

        var mods = new[] { new BoardModifier("Adjacent: packs", [new Cell(1, 0)]) };
        var hard = new VoyageSolver(3, 3, Make(60, 7),
            VoyageSolver.ScoreWith(mods, (_, c) => c.Value * 0.5))
            .Solve(TimeSpan.FromMilliseconds(80));
        Assert.False(hard.ProvedOptimal);
    }

    [Fact]
    public void MoreTimeNeverProducesAWorseAnswer()
    {
        var mods = new[] { new BoardModifier("Adjacent: packs", [new Cell(1, 0)]) };
        var score = VoyageSolver.ScoreWith(mods, (_, c) => c.Value * 0.5);

        var quick = new VoyageSolver(3, 3, Make(60, 7), score).Solve(TimeSpan.FromMilliseconds(100));
        var longer = new VoyageSolver(3, 3, Make(60, 7), score).Solve(TimeSpan.FromMilliseconds(800));
        Assert.True(longer.Value >= quick.Value);
    }

    [Fact]
    public void ReportsItsOwnEffort()
    {
        var s = new VoyageSolver(3, 3, Make(20, 3)).Solve(TimeSpan.FromMilliseconds(200));
        Assert.True(s.NodesExplored > 0);
        Assert.True(s.Elapsed > TimeSpan.Zero);
    }
}
