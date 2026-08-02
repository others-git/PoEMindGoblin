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
    /// THE AMPLIFIER CHANNEL MUST NOT BREAK THE BOUND.
    ///
    /// Its pair term is a PRODUCT of two per-chart quantities, and either can be signed,
    /// so the adjacency ceiling has to be taken over absolute values. Get that wrong and
    /// the bound runs too low, which does not merely lose value — it prunes the true
    /// optimum and then reports the survivor as PROVED, the one failure this solver must
    /// never have.
    ///
    /// Checked against brute force rather than against a number I worked out: the
    /// instance is small enough to enumerate every layout exactly.
    /// </summary>
    [Fact]
    public void TheAmplifierPairTermCannotPruneTheTrueOptimum()
    {
        // Crossings on a 1x3: every arrangement tiles and connects, so shape decides
        // nothing and only value can.
        Chart Amp(string id, double own, double explicitValue, double magnitude) =>
            new(id, id, ChartShape.Crossing, 80, [])
            { Value = own, ExplicitValue = explicitValue, AdjacentMagnitudeValue = magnitude };

        // The optimum needs b BESIDE c: b is worth nothing alone and c is worth nothing
        // alone, and greedy-by-own-value takes a and d instead.
        var charts = new[]
        {
            Amp("a", own: 100, explicitValue: 0, magnitude: 0),
            Amp("b", own: 0, explicitValue: 0, magnitude: 2.0),
            Amp("c", own: 0, explicitValue: 100, magnitude: 0),
            Amp("d", own: 40, explicitValue: 0, magnitude: 0),
        };

        double Score(Chart chart, Cell _) => chart.Value;

        // Every ordered choice of three charts across the three cells, scored the way
        // the solver accumulates: own value, plus each adjacent pair counted both ways.
        var best = double.NegativeInfinity;
        foreach (var first in charts)
            foreach (var second in charts.Where(x => x != first))
                foreach (var third in charts.Where(x => x != first && x != second))
                {
                    var row = new[] { first, second, third };
                    var total = row.Sum(c => c.Value);
                    for (var i = 0; i + 1 < row.Length; i++)
                        total += row[i].AdjacentMagnitudeValue * row[i + 1].ExplicitValue
                                 + row[i + 1].AdjacentMagnitudeValue * row[i].ExplicitValue;
                    best = Math.Max(best, total);
                }

        var solution = new VoyageSolver(1, 3, charts, Score, strandedPenalty: 40)
            .Solve(TimeSpan.FromSeconds(5));

        Assert.Equal(best, solution.Value, 6);
        Assert.True(solution.ProvedOptimal, "an instance this small must be proved");
    }

    /// <summary>
    /// The same claim, over instances nobody chose.
    ///
    /// A single hand-built case proves less than it looks: the polish is strong enough to
    /// stumble onto the optimum by swapping, so a hand-picked instance can pass with a
    /// BROKEN bound. Signed values are the point — the amplifier pair term is a product,
    /// so a negative magnitude against a negative explicit value is a positive payout,
    /// and a ceiling taken over raw values instead of absolute ones would sit under it.
    /// Seeded, so a failure is reproducible rather than a rumour.
    /// </summary>
    [Fact]
    public void TheSolverMatchesBruteForceOnRandomAmplifierInstances()
    {
        var random = new Random(20260801);

        for (var trial = 0; trial < 60; trial++)
        {
            var charts = Enumerable.Range(0, 5).Select(i =>
                new Chart($"c{i}", $"c{i}", ChartShape.Crossing, 80, [])
                {
                    Value = Math.Round(random.NextDouble() * 150 - 50, 3),
                    ExplicitValue = Math.Round(random.NextDouble() * 200 - 50, 3),
                    AdjacentMagnitudeValue = Math.Round(random.NextDouble() * 3 - 1, 3),
                }).ToArray();

            double Score(Chart chart, Cell _) => chart.Value;

            // Every ordered arrangement of four of the five charts along a 1x4 row.
            var best = double.NegativeInfinity;
            foreach (var row in Permutations(charts, 4))
            {
                var total = row.Sum(c => c.Value);
                for (var i = 0; i + 1 < row.Count; i++)
                    total += row[i].AdjacentMagnitudeValue * row[i + 1].ExplicitValue
                             + row[i + 1].AdjacentMagnitudeValue * row[i].ExplicitValue;
                best = Math.Max(best, total);
            }

            var solution = new VoyageSolver(1, 4, charts, Score,
                                            allowEmpty: false, strandedPenalty: 40)
                .Solve(TimeSpan.FromSeconds(5));

            Assert.Equal(best, solution.Value, 6);
        }
    }

    private static IEnumerable<IReadOnlyList<Chart>> Permutations(
        IReadOnlyList<Chart> pool, int take)
    {
        var chosen = new List<Chart>();
        var used = new bool[pool.Count];

        IEnumerable<IReadOnlyList<Chart>> Walk()
        {
            if (chosen.Count == take) { yield return chosen.ToList(); yield break; }
            for (var i = 0; i < pool.Count; i++)
            {
                if (used[i]) continue;
                used[i] = true;
                chosen.Add(pool[i]);
                foreach (var result in Walk()) yield return result;
                chosen.RemoveAt(chosen.Count - 1);
                used[i] = false;
            }
        }
        return Walk();
    }

    /// <summary>
    /// A BOARD WITH NOTHING POSITIONAL TO DECIDE IS SOLVED WITHOUT SEARCHING, AND THAT
    /// IS CORRECT.
    ///
    /// When a profile scores no adjacency modifier and no border modifier — a real and
    /// common case, e.g. sulphur on a panel whose sulphur is all headline stats — every
    /// chart is worth the same on every cell. The objective collapses to "take the nine
    /// best charts", the seed finds a connected board, the polish swaps the best charts
    /// into it, and the root bound then EQUALS the incumbent, so the search correctly
    /// expands nothing.
    ///
    /// This is pinned because it wears the exact costume of two bugs that were real:
    /// a pass bottoming out in two nodes while claiming a proof, and a seed that spent
    /// the whole clock. The difference is the answer, so the answer is what is checked —
    /// against the top-N sum computed independently of the solver.
    /// </summary>
    [Fact]
    public void APositionIndependentBoardIsProvedWithoutExpandingANode()
    {
        // Nine cells, twelve candidates, value depending on the CHART alone. Crossings,
        // so every subset tiles into a fully connected board and shape decides nothing
        // either -- the instance has to be degenerate in POSITION to test what it says.
        var charts = Enumerable.Range(1, 12)
            .Select(i => new Chart($"c{i}", $"c{i}", ChartShape.Crossing, 80, [])).ToList();
        var worth = charts.ToDictionary(c => c.Id, c => (double)(100 - int.Parse(c.Id[1..]) * 5));
        double Score(Chart chart, Cell _) => worth[chart.Id];

        var solution = new VoyageSolver(3, 3, charts, Score, strandedPenalty: 40)
            .Solve(TimeSpan.FromSeconds(5));

        // The optimum is arithmetic, not a search result: the nine best charts.
        Assert.Equal(worth.Values.OrderByDescending(v => v).Take(9).Sum(), solution.Value, 6);
        Assert.True(solution.ProvedOptimal, "an instance this easy must be proved");

        // And the REPORT must not read as a collapsed search. The seed and the polish
        // did the work; counting only search nodes said "3 layouts checked" on a solve
        // that had examined thousands, which is what sent someone hunting a bug that
        // was not there.
        Assert.True(solution.NodesExplored > 100,
                    $"only {solution.NodesExplored} layouts reported for a full solve");
    }

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
