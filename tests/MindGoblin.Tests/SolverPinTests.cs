using MindGoblin.Core.Voyage;

namespace MindGoblin.Tests;

/// <summary>
/// Pinning is how a person overrules the score. Soul Eater is voyage-wide and grants
/// player power rather than loot, so every profile prices it at zero and the planner
/// never places the chart -- which is right arithmetic and the wrong answer.
/// </summary>
public class SolverPinTests
{
    private static Chart Crossing(string id, double value) =>
        new(id, id, ChartShape.Crossing, 80, []) { Value = value };

    /// <summary>Crossings tile a full board, so a joined layout always exists.</summary>
    private static IReadOnlyList<Chart> Panel(int count, double value = 10) =>
        [.. Enumerable.Range(0, count).Select(i => Crossing($"c{i}", value))];

    [Fact]
    public void ThePinnedChartLandsExactlyWhereItWasPinned()
    {
        var charts = Panel(12);
        var solution = new VoyageSolver(3, 3, charts, strandedPenalty: 40,
                                        pin: ("c7", new Cell(2, 0)))
            .Solve(TimeSpan.FromSeconds(2));

        var placed = Assert.Single(solution.Placements, p => p.Chart.Id == "c7");
        Assert.Equal(new Cell(2, 0), placed.Cell);
    }

    /// <summary>
    /// A worthless chart is exactly the case the button exists for: left alone the
    /// planner drops it, because eleven better ones are competing for nine squares.
    /// </summary>
    [Fact]
    public void AChartWorthNothingIsPlacedAnywayWhenPinned()
    {
        var charts = Panel(11).Append(Crossing("soul-eater", 0)).ToList();

        var without = new VoyageSolver(3, 3, charts, strandedPenalty: 40)
            .Solve(TimeSpan.FromSeconds(2));
        Assert.DoesNotContain(without.Placements, p => p.Chart.Id == "soul-eater");

        var with = new VoyageSolver(3, 3, charts, strandedPenalty: 40,
                                    pin: ("soul-eater", new Cell(0, 0)))
            .Solve(TimeSpan.FromSeconds(2));
        Assert.Contains(with.Placements, p => p.Chart.Id == "soul-eater");
    }

    [Fact]
    public void PinningDoesNotStrandAnything()
    {
        var solution = new VoyageSolver(3, 3, Panel(12), strandedPenalty: 40,
                                        pin: ("c3", new Cell(1, 1)))
            .Solve(TimeSpan.FromSeconds(2));

        Assert.Equal(9, solution.Placements.Count);
        Assert.Empty(solution.StrandedCells);
    }

    /// <summary>
    /// The cap on placements must not quietly win. Filtering the orderings keeps the
    /// pinned chart out of every other cell but does not force the pinned cell to be
    /// used, and an empty one would drop the chart without saying so.
    ///
    /// Corners rather than Crossings: a partial board has to form a CLOSED cluster, and
    /// a Crossing opens on all four sides, so no cell beside one can be left empty. Four
    /// Corners tile a 2x2 block; four Crossings tile nothing.
    /// </summary>
    [Fact]
    public void ACappedBoardStillSpendsOneOfItsChartsOnThePin()
    {
        var corners = Enumerable.Range(0, 12)
            .Select(i => new Chart($"k{i}", $"k{i}", ChartShape.Corner, 80, []) { Value = 10 })
            .ToList();

        var solution = new VoyageSolver(3, 3, corners, strandedPenalty: 40,
                                        maxPlacements: 4, pin: ("k9", new Cell(2, 2)))
            .Solve(TimeSpan.FromSeconds(2));

        Assert.Equal(4, solution.Placements.Count);
        Assert.Contains(solution.Placements, p => p.Chart.Id == "k9" && p.Cell == new Cell(2, 2));
    }

    [Fact]
    public void AnUnknownChartIdIsNotAPin()
    {
        var solver = new VoyageSolver(3, 3, Panel(12), pin: ("nope", new Cell(0, 0)));
        Assert.False(solver.HasPin);
        Assert.Equal(9, solver.Solve(TimeSpan.FromSeconds(2)).Placements.Count);
    }
}

/// <summary>
/// Choosing WHERE to pin. A voyage-wide modifier pays the same from any square, so the
/// square it occupies is pure opportunity cost and the cheapest one is the right spend.
/// </summary>
public class CheapestSquareTests
{
    private static VoyageSession Panel()
    {
        var session = new VoyageSession();
        session.ApplyPanelRead(Enumerable.Range(1, 12).Select(i =>
            new ChartPanelReader.ReadCell(i, (i - 1) / 6, (i - 1) % 6, true, true, true, true)
            { Level = 80 }).ToList());
        return session;
    }

    private static VoyageProfile Quantity =>
        VoyageRules.Defaults().Single(p => p.Name == "quantity");

    [Fact]
    public void TheSoulEaterChartGoesToTheSquareTheBoardValuesLeast()
    {
        var session = new VoyageSession();
        session.ApplyPanelRead(Enumerable.Range(1, 12).Select(i =>
            // Chart 4 is a Corner. A corner square only accepts a chart whose open edges
            // both face inward, so pinning to one is only possible for a shape that fits:
            // a Crossing there would open twice onto the border.
            new ChartPanelReader.ReadCell(i, (i - 1) / 6, (i - 1) % 6,
                                          true, i == 4, i != 4, i != 4) { Level = 80 })
            .ToList());
        session.ApplyChartText(4,
            "Tempest Reach\nAnchorfield\nVoyage Modifier: Players in all Voyage Areas "
            + "have Soul Eater");

        // Squares 1-8 are paid for; square 9 is not. Nine is the one to spend.
        for (var square = 1; square <= 8; square++)
            session.ApplySquareModifiers(square,
                ["Areas have 30% increased Quantity of Items found"]);
        session.ApplySquareModifiers(9, []);

        var solution = session.Solve(Quantity, TimeSpan.FromSeconds(3), pinChart: 4);
        var plan = session.Plan(solution);

        Assert.Equal(4, Assert.Single(plan, s => s.Square == 9).ChartNumber);
    }

    /// <summary>
    /// Shape is a hard constraint on where a pin can go. A Crossing opens on all four
    /// sides, so in a corner two of them face the border -- a path leading nowhere. Pin
    /// it there and the board has no answer at all, which is a far worse outcome than
    /// spending a slightly better square.
    /// </summary>
    [Fact]
    public void APinNeverGoesSomewhereTheChartCannotFit()
    {
        var session = new VoyageSession();
        session.ApplyPanelRead(Enumerable.Range(1, 12).Select(i =>
            new ChartPanelReader.ReadCell(i, (i - 1) / 6, (i - 1) % 6, true, true, true, true)
            { Level = 80 }).ToList());

        // Every square alike, so nothing but shape decides. Chart 1 is a Crossing.
        var solution = session.Solve(Quantity, TimeSpan.FromSeconds(3), pinChart: 1);

        var placed = Assert.Single(solution.Placements, p => p.Chart.Id == session.ByPanelIndex[1].Id);
        Assert.Equal(new Cell(1, 1), placed.Cell);      // the centre: the only fit
        Assert.Empty(solution.StrandedCells);
    }

    /// <summary>
    /// A pin on a realistic panel, where shapes are mixed and connectivity is the thing
    /// the solver fights. Nailing a chart down removes a degree of freedom from exactly
    /// that problem, so the guarantee worth holding is that it does not buy the pin at
    /// the cost of a dead corner.
    /// </summary>
    [Fact]
    public void PinningARealisticPanelStillJoinsEverySquare()
    {
        var shapes = new[]
        {
            ChartShape.End, ChartShape.Corner, ChartShape.Straight,
            ChartShape.Junction, ChartShape.Crossing,
        };

        var session = new VoyageSession();
        session.ApplyPanelRead(Enumerable.Range(1, 42).Select(i =>
        {
            var face = new ChartFace(shapes[i % shapes.Length], i % 4);
            return new ChartPanelReader.ReadCell(
                i, (i - 1) / 6, (i - 1) % 6,
                face.IsOpen(Side.North), face.IsOpen(Side.East),
                face.IsOpen(Side.South), face.IsOpen(Side.West)) { Level = 70 + i % 14 };
        }).ToList());

        // A Junction: three open edges, so it fits an edge square but not a corner.
        var pinned = session.ByPanelIndex
            .First(kv => kv.Value.Shape == ChartShape.Junction).Key;

        var solution = session.Solve(Quantity, TimeSpan.FromSeconds(3), pinChart: pinned);

        Assert.Equal(9, solution.Placements.Count);
        Assert.Empty(solution.StrandedCells);
        Assert.Contains(solution.Placements,
                        p => p.Chart.Id == session.ByPanelIndex[pinned].Id);
    }

    [Fact]
    public void NoPinAskedForChangesNothing()
    {
        var session = Panel();
        var plain = session.Solve(Quantity, TimeSpan.FromSeconds(3));
        Assert.Equal(9, plain.Placements.Count);
    }
}
