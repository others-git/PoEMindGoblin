using MindGoblin.Core.Voyage;

namespace MindGoblin.Tests;

/// <summary>
/// Branch and bound dies on plateaus: with many near-equal charts the bound stops
/// pruning, and connected boards are too rare for blind descent to hit -- measured at
/// 28 million nodes without a single improvement on a real 60-chart panel, while the
/// seed held junk any same-shape swap would better. Same-shape swaps cannot break a
/// layout, so the polish harvests exactly the value the search could not.
/// </summary>
public class PolishTests
{
    [Fact]
    public void TheBestChartsAreSpentEvenWithNoTimeToSearch()
    {
        // Twelve crossings, values 1..12, and a budget that is barely more than the
        // seed (10ms died to cold-start JIT alone): the plain seed grabs whatever comes
        // first, and the polish turns that into the top nine.
        var charts = Enumerable.Range(1, 12)
            .Select(i => new Chart($"c{i}", $"c{i}", ChartShape.Crossing, 80, []) { Value = i })
            .ToList();

        var solution = new VoyageSolver(3, 3, charts, strandedPenalty: 40)
            .Solve(TimeSpan.FromMilliseconds(150));

        Assert.Equal(9, solution.Placements.Count);
        Assert.Empty(solution.StrandedCells);
        Assert.True(solution.Placements.All(p => p.Chart.Value >= 4),
                    "a chart from the bottom three survived the polish");
    }

    [Fact]
    public void PolishRespectsThePin()
    {
        // The pinned chart is worthless and pinned to a corner; the polish must not
        // swap it away for something better.
        var charts = Enumerable.Range(1, 12)
            .Select(i => new Chart($"c{i}", $"c{i}", ChartShape.Crossing, 80, []) { Value = i })
            .Append(new Chart("junk", "junk", ChartShape.Corner, 80, []) { Value = 0 })
            .ToList();

        var solution = new VoyageSolver(3, 3, charts, strandedPenalty: 40,
                                        pin: ("junk", new Cell(0, 0)))
            .Solve(TimeSpan.FromSeconds(2));

        Assert.Contains(solution.Placements, p => p.Chart.Id == "junk" && p.Cell == new Cell(0, 0));
    }
}
