namespace PoeMarketWatch.Core.Voyage;

/// <summary>
/// A modifier attached to the board itself rather than to a chart.
///
/// These are the icons on the board border. They do not affect the square they sit on --
/// they affect the squares NEXT to it ("Adjacent Areas contain 8 additional packs of Sea
/// Beasts"), which is why chart value has to be scored per (chart, cell) rather than per
/// chart. The same chart is worth more beside a good modifier than in a dead corner.
/// </summary>
public sealed record BoardModifier(string Description, IReadOnlyList<Cell> AffectedCells)
{
    public bool Affects(Cell cell) => AffectedCells.Contains(cell);
}

/// <summary>
/// Picks which charts go where.
///
/// The search is small in principle -- 9 cells -- but the candidate set is not: 60 charts
/// times up to 4 rotations at every cell. Brute force is 60P9 x rotations, so this is
/// branch and bound:
///   * cells are filled in row-major order, so when a cell is decided its north and west
///     neighbours are already fixed and can be checked immediately;
///   * an optimistic bound (the best remaining charts, ignoring shape) prunes any branch
///     that cannot beat the incumbent.
///
/// Leaving a cell empty is legal, but only if no decided neighbour points a path into it.
/// </summary>
public sealed class VoyageSolver
{
    private readonly int _rows;
    private readonly int _cols;
    private readonly IReadOnlyList<Chart> _charts;
    private readonly Func<Chart, Cell, double> _score;
    private readonly bool _allowEmpty;

    public VoyageSolver(
        int rows,
        int cols,
        IReadOnlyList<Chart> charts,
        Func<Chart, Cell, double>? score = null,
        bool allowEmpty = true)
    {
        _rows = rows;
        _cols = cols;
        _charts = charts;
        _score = score ?? ((c, _) => c.Value);
        _allowEmpty = allowEmpty;
    }

    /// <summary>Nodes explored on the last solve, so pathological inputs are visible.</summary>
    public long NodesExplored { get; private set; }

    /// <summary>Per-cell chart indices, best-scoring first. Built once per solve.</summary>
    private int[][] _ordering = [];

    public sealed record Solution(IReadOnlyList<Placement> Placements, double Value)
    {
        public bool IsEmpty => Placements.Count == 0;

        /// <summary>
        /// True when the search finished rather than hitting its deadline.
        ///
        /// Worth surfacing: an anytime result is usually excellent but not provably the
        /// maximum, and a tool that quietly presents "good" as "best" is lying.
        /// </summary>
        public bool ProvedOptimal { get; init; }

        public long NodesExplored { get; init; }
        public TimeSpan Elapsed { get; init; }
    }

    /// <summary>
    /// Scoring helper that folds board modifiers into a chart's value: a chart on a cell
    /// touched by N modifiers is worth its own value plus each modifier's weight.
    /// </summary>
    public static Func<Chart, Cell, double> ScoreWith(
        IReadOnlyList<BoardModifier> modifiers, Func<BoardModifier, Chart, double> weight) =>
        (chart, cell) => chart.Value
            + modifiers.Where(m => m.Affects(cell)).Sum(m => weight(m, chart));

    /// <summary>
    /// Search for the best layout, stopping at <paramref name="budget"/>.
    ///
    /// Anytime by design. Exhaustive proof of optimality is not worth chasing here: with
    /// board modifiers in play the bound is loose enough that a full proof can run for
    /// minutes, while value-ordered search reaches a strong layout in the FIRST descent
    /// and improves from there. Since the objective is a rule set you chose rather than
    /// an objective truth, a great answer now beats a provably-maximal one later --
    /// but the result says which it got.
    /// </summary>
    public Solution Solve(TimeSpan? budget = null, CancellationToken ct = default)
    {
        NodesExplored = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var deadline = budget ?? TimeSpan.FromSeconds(5);
        var board = new VoyageBoard(_rows, _cols);
        var used = new bool[_charts.Count];

        // Optimistic remaining value: the best scores any chart could achieve anywhere,
        // largest first. Never underestimates, so pruning against it is safe.
        var bestPossible = BestPossiblePerCell();
        _ordering = BuildOrdering();

        var best = new Solution(Array.Empty<Placement>(), double.NegativeInfinity);
        var exhausted = true;
        try
        {
            Recurse(0, board, used, 0.0, bestPossible, ref best, sw, deadline, ct);
        }
        catch (DeadlineReached)
        {
            exhausted = false;
        }
        sw.Stop();

        var result = best.Value == double.NegativeInfinity
            ? new Solution(Array.Empty<Placement>(), 0)
            : best;
        return result with
        {
            ProvedOptimal = exhausted,
            NodesExplored = NodesExplored,
            Elapsed = sw.Elapsed,
        };
    }

    /// <summary>Unwinds the recursion when the time budget is spent.</summary>
    private sealed class DeadlineReached : Exception { }

    /// <summary>
    /// Upper bound on the value still obtainable from cell <c>i</c> onwards.
    ///
    /// Bounded PER CELL, not per chart. The obvious version -- take each chart's best
    /// score anywhere, sort, sum the top nine -- is admissible but useless once board
    /// modifiers exist: it assumes all nine charts land on a buffed square when only one
    /// or two squares are buffed, so the bound sits far above anything reachable and
    /// prunes nothing. Measured with two modifiers over 60 charts, that version explored
    /// 4 million nodes without finishing; this one settles it in milliseconds.
    ///
    /// Ceiling each cell by the best any chart could score THERE. Still admissible (no
    /// cell can beat its own maximum) and far tighter, because an unbuffed cell now
    /// carries an unbuffed ceiling.
    /// </summary>
    private double[] BestPossiblePerCell()
    {
        var cells = AllCells().ToList();
        var n = cells.Count;

        // Bound A -- by chart. Each chart's best score anywhere, biggest first. Captures
        // "a chart is used at most once", so it is tight when every cell scores alike.
        // Useless once board modifiers exist: it assumes all nine charts land on a buffed
        // square when only one or two squares are buffed.
        var chartBest = _charts
            .Select(c => Math.Max(0, cells.Max(cell => _score(c, cell))))
            .OrderByDescending(v => v)
            .ToList();

        // Bound B -- by cell. The best any chart could score on THAT cell. Captures the
        // scarcity of buffed squares, but repeats the single best chart across every
        // cell, so it loses the no-repeat information that A has.
        var cellBest = cells
            .Select(cell => _charts.Count == 0 ? 0 : Math.Max(0, _charts.Max(c => _score(c, cell))))
            .ToList();

        // Both are admissible, so the smaller is admissible and never worse than either.
        // Neither alone is enough: A collapses with modifiers, B collapses without them,
        // and each left the search grinding through millions of nodes on its bad case.
        var suffix = new double[n + 1];
        var byChart = new double[n + 1];
        var byCell = new double[n + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            byChart[i] = byChart[i + 1] + (i < chartBest.Count ? chartBest[i] : 0);
            byCell[i] = byCell[i + 1] + cellBest[i];
            suffix[i] = Math.Min(byChart[i], byCell[i]);
        }
        return suffix;
    }

    /// <summary>
    /// For each cell, the chart indices sorted by what they would score there.
    ///
    /// Per-cell rather than global because board modifiers buff specific squares, so the
    /// best chart for the cell beside "8 additional packs of Sea Beasts" is not the best
    /// chart for a corner.
    /// </summary>
    private int[][] BuildOrdering()
    {
        var cells = AllCells().ToList();
        var result = new int[cells.Count][];
        for (var i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            result[i] = Enumerable.Range(0, _charts.Count)
                .OrderByDescending(idx => _score(_charts[idx], cell))
                .ToArray();
        }
        return result;
    }

    private IEnumerable<Cell> AllCells()
    {
        for (var r = 0; r < _rows; r++)
            for (var c = 0; c < _cols; c++)
                yield return new Cell(r, c);
    }

    private void Recurse(
        int index, VoyageBoard board, bool[] used, double value,
        double[] bound, ref Solution best,
        System.Diagnostics.Stopwatch sw, TimeSpan deadline, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        NodesExplored++;

        // Checking the clock every node would cost more than the search; every 4096 is
        // frequent enough to stop promptly and cheap enough not to matter.
        if ((NodesExplored & 0xFFF) == 0 && sw.Elapsed > deadline) throw new DeadlineReached();

        if (index == _rows * _cols)
        {
            // Cells were only checked against decided neighbours during the search, so
            // the finished layout still has to satisfy the full rule.
            if (board.IsValid() && value > best.Value)
                best = new Solution(board.Placements.ToList(), value);
            return;
        }

        // Nothing below can beat the incumbent -- stop.
        if (value + bound[index] <= best.Value) return;

        var cell = new Cell(index / _cols, index % _cols);

        // Try the most valuable charts first.
        //
        // This is not a micro-optimisation -- it is the difference between solving and
        // hanging. Branch and bound only prunes against the best solution found SO FAR,
        // so exploring in input order starts with a weak incumbent and the bound rejects
        // almost nothing. Descending value finds a strong incumbent in the first branch
        // and everything worse dies immediately. Measured on 60 charts x a 3x3 board:
        // over two minutes unordered, milliseconds ordered.
        var order = _ordering[index];

        foreach (var i in order)
        {
            if (used[i]) continue;
            var chart = _charts[i];
            var gain = _score(chart, cell);

            // Ordered by value, so once a candidate cannot beat the incumbent even in
            // the best case, neither can any candidate after it.
            if (value + gain + bound[index + 1] <= best.Value) break;

            foreach (var rotation in ChartFace.DistinctRotations(chart.Shape))
            {
                var face = new ChartFace(chart.Shape, rotation);
                if (!board.CanPlace(cell, face)) continue;
                if (!SatisfiesDecidedNeighbours(board, cell, face)) continue;

                used[i] = true;
                board.Place(new Placement(chart, cell, rotation));
                Recurse(index + 1, board, used, value + gain, bound, ref best, sw, deadline, ct);
                board.Clear(cell);
                used[i] = false;
            }
        }

        if (_allowEmpty && LeavingEmptyIsLegal(board, cell))
            Recurse(index + 1, board, used, value, bound, ref best, sw, deadline, ct);
    }

    /// <summary>
    /// Row-major order means north and west are already decided when we reach a cell, so
    /// a mismatch there can be rejected now rather than at the end of the branch.
    /// </summary>
    private static bool SatisfiesDecidedNeighbours(VoyageBoard board, Cell cell, ChartFace face)
    {
        foreach (var side in new[] { Side.North, Side.West })
        {
            var neighbourCell = VoyageBoard.Neighbour(cell, side);
            if (!board.InBounds(neighbourCell)) continue;   // border satisfies either state

            // A decided-but-empty neighbour cannot accept a path.
            if (board.At(neighbourCell) is not { } neighbour)
            {
                if (face.IsOpen(side)) return false;
                continue;
            }
            if (face.IsOpen(side) != neighbour.Face.IsOpen(side.Opposite())) return false;
        }
        return true;
    }

    /// <summary>An empty cell is fine only if no decided neighbour leads a path into it.</summary>
    private static bool LeavingEmptyIsLegal(VoyageBoard board, Cell cell)
    {
        foreach (var side in new[] { Side.North, Side.West })
        {
            var neighbourCell = VoyageBoard.Neighbour(cell, side);
            if (board.At(neighbourCell) is { } neighbour
                && neighbour.Face.IsOpen(side.Opposite()))
                return false;
        }
        return true;
    }
}

/// <summary>
/// Turns a solution into the instruction the player actually needs: which numbered chart
/// goes in which numbered board square.
///
/// Both grids are numbered row-major from 1 -- the board 1-9, the chart panel 1-60 -- so
/// the plan reads "square 5 <- chart 23" and can be followed without re-deriving anything
/// from shapes or names.
/// </summary>
public static class VoyagePlan
{
    public sealed record Step(int Square, int ChartNumber, Chart Chart, int Rotation)
    {
        public string RotationText => Rotation == 0 ? "as-is" : $"rotate {Rotation * 90}°";
        public override string ToString() =>
            $"square {Square} <- chart {ChartNumber}  {Chart.Name} ({Chart.Shape}, {RotationText})";
    }

    public static int SquareNumber(Cell cell, int cols) => cell.Row * cols + cell.Col + 1;

    public static IReadOnlyList<Step> Describe(
        VoyageSolver.Solution solution, int boardCols, IReadOnlyList<Chart> inventory)
    {
        var order = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < inventory.Count; i++) order[inventory[i].Id] = i + 1;

        return solution.Placements
            .Select(p => new Step(
                SquareNumber(p.Cell, boardCols),
                order.GetValueOrDefault(p.Chart.Id, 0),
                p.Chart,
                p.Rotation))
            .OrderBy(s => s.Square)
            .ToList();
    }
}
