namespace MindGoblin.Core.Voyage;

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
    private readonly double _strandedPenalty;
    private readonly int _maxPlacements;

    /// <param name="strandedPenalty">
    /// Charged per square left outside the largest connected group. Must be >= 0: the
    /// search prunes against an optimistic bound computed from placement scores alone,
    /// and only a penalty applied at the end keeps that bound above the true value. A
    /// BONUS here would make the bound too low and could discard the real best layout.
    /// </param>
    public VoyageSolver(
        int rows,
        int cols,
        IReadOnlyList<Chart> charts,
        Func<Chart, Cell, double>? score = null,
        bool allowEmpty = true,
        double strandedPenalty = 0,
        int? maxPlacements = null)
    {
        _rows = rows;
        _cols = cols;
        _charts = charts;
        _score = score ?? ((c, _) => c.Value);
        _allowEmpty = allowEmpty;
        _strandedPenalty = Math.Max(0, strandedPenalty);

        // "Place UP TO nine Charts onto the board... Your very first Voyage will require
        // only four Charts." A cap is not the same as leaving cells empty by choice: it
        // is a limit on how many charts you have to spend.
        _maxPlacements = Math.Clamp(maxPlacements ?? rows * cols, 0, rows * cols);
    }

    /// <summary>Cached: Enum.GetValues allocates a fresh array every call, and these
    /// loops run per placement in the innermost part of the search.</summary>
    private static readonly Side[] Sides = Enum.GetValues<Side>();

    /// <summary>Where a Voyage begins: the bottom-left square, per the in-game help.</summary>
    public static Cell StartCell(int rows) => new(rows - 1, 0);

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

        /// <summary>
        /// Squares cut off from the main route. Legal, but whatever sits there is
        /// stranded, so the UI names them rather than letting them pass unremarked.
        /// </summary>
        public IReadOnlyList<Cell> StrandedCells { get; init; } = [];
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
        // Optimistic remaining value: the best scores any chart could achieve anywhere,
        // largest first. Never underestimates, so pruning against it is safe.
        var bestPossible = BestPossiblePerCell();
        _ordering = BuildOrdering();

        var best = new Solution(Array.Empty<Placement>(), double.NegativeInfinity);
        bestRank = double.NegativeInfinity;

        // Start from a connected board if one can be found cheaply. Without this the
        // search can run for minutes and still return a layout with a dead corner --
        // measured on a real 24-chart panel, connected boards were roughly one in ten
        // million legal ones, and 2.9M nodes of value-ordered search never reached one.
        // The seed is also an EXISTENCE PROOF. If a fully joined board can be built from
        // these charts, then a layout that strands a square is not a trade-off worth
        // offering -- an unvisited square is a wasted chart, and no amount of value
        // elsewhere makes a dead corner the answer the user wanted. So once the seed
        // succeeds, stranding is forbidden outright rather than priced.
        //
        // Pricing it was tried twice and neither held. A flat fee is meaningless across
        // profiles that score in tens and in thousands, and forfeiting the stranded
        // chart's own value still left four of eleven profiles choosing a dead corner,
        // because the layout it bought was worth more than the chart it cost.
        //
        // When no connected board exists the constraint is dropped, or the tool would
        // return nothing at all rather than the best available.
        _requireConnected = false;
        if (SeedConnected() is { } seed)
        {
            best = seed;
            bestRank = seed.Value + seed.Placements.Count * FullnessEpsilon;
            _requireConnected = true;
        }


        // Search the RESTRICTED space first, then widen.
        //
        // Forbidding edges that open onto the border makes connected boards common rather
        // than one in ten million, so a pass there finds a good JOINED layout quickly.
        // Without it the search spends its whole budget on layouts that are rejected for
        // being cut off, never improves on the seed, and hands back a board chosen for
        // connectivity alone -- which is how the strongbox profile came to skip every
        // strongbox chart it had.
        //
        // The restriction rules out perfectly good layouts, so it is only ever a head
        // start: each pass keeps the incumbent, and the final unrestricted pass is free
        // to beat it. Only that last pass decides whether the answer was proved.
        // Only the UNRESTRICTED pass can prove anything. The restricted ones search a
        // deliberately smaller space, so finishing one says nothing about the layouts it
        // was forbidden from considering -- and if the budget ran out before that pass
        // started, the honest answer is that nothing was proved.
        var exhausted = false;
        foreach (var rule in new[] { BorderRule.All, BorderRule.NorthAndWest, BorderRule.None })
        {
            _searchRule = rule;
            var completed = true;
            _passStartNodes = NodesExplored;

            // A FRESH board and used-set per pass.
            //
            // DeadlineReached unwinds through every recursive frame, and those frames
            // undo their placement AFTER the recursive call returns -- so an abort leaves
            // the board half filled and charts marked used. Reusing it meant the next
            // pass started from that wreckage, bottomed out after two nodes, and reported
            // itself EXHAUSTED, which is how a search that had explored almost nothing
            // came to claim it had proved the answer.
            var board = new VoyageBoard(_rows, _cols);
            var used = new bool[_charts.Count];
            if (Trace)
                Console.Error.WriteLine(
                    $"    pass {rule,-13} start best={best.Value,10:0.##} bound0={bestPossible[0]:0.##} "
                    + $"nodes={NodesExplored} t={sw.Elapsed.TotalMilliseconds:0}ms");
            try
            {
                Recurse(0, board, used, 0.0, bestPossible, ref best, sw, deadline, ct);
            }
            catch (DeadlineReached)
            {
                completed = false;
            }
            if (rule == BorderRule.None) exhausted = completed;
            if (Trace)
                Console.Error.WriteLine(
                    $"    pass {rule,-13} end   best={best.Value,10:0.##} completed={completed} "
                    + $"nodes={NodesExplored} t={sw.Elapsed.TotalMilliseconds:0}ms");
            if (sw.Elapsed >= deadline) break;
        }
        _searchRule = BorderRule.None;
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

    /// <summary>
    /// Tie-breakers, ordered by how much they matter and all far below any real score.
    ///
    /// Connectivity outranks fullness: a joined board beats a fuller one that strands a
    /// square, and a fuller board beats an emptier one worth the same. Both only ever
    /// decide exact ties, which are common when a profile has little to score.
    /// </summary>
    private const double StrandedEpsilon = 1e-6;
    private const double FullnessEpsilon = 1e-9;

    /// <summary>Unwinds the recursion when the time budget is spent.</summary>
    private sealed class DeadlineReached : Exception { }

    /// <summary>
    /// Find one connected board fast, to start the search from something decent.
    ///
    /// The trick is a restriction the main search cannot make: every edge facing the
    /// BORDER must be closed. An edge open to the border is precisely a path that leads
    /// nowhere, so forbidding them removes almost every dead corner by construction --
    /// and it collapses the space enough to hit a connected board in a handful of nodes
    /// where the unrestricted search needed eleven million.
    ///
    /// It is only a seed, never the answer: the restriction rules out perfectly good
    /// layouts, so whatever it finds is scored honestly and handed to the real search as
    /// an incumbent to beat. Returns null when no such board exists, which is legitimate
    /// -- some chart sets cannot close every border.
    /// </summary>
    private Solution? SeedConnected()
    {
        // Progressively weaker restrictions. The first that yields a connected board wins;
        // the stricter ones collapse the space enough to find one almost immediately, and
        // the weaker ones exist because a strict one can be infeasible for a given set of
        // charts -- which is exactly what happened, and left several profiles returning
        // layouts with squares cut off.
        foreach (var closed in new[] { BorderRule.All, BorderRule.NorthAndWest, BorderRule.None })
            if (SeedConnected(closed) is { } seed)
                return seed;
        return null;
    }

    /// <summary>
    /// Does this face send a path off the board, where the rule forbids it?
    ///
    /// An edge open to the border is a path that leads nowhere, and forbidding them is
    /// what makes connected boards COMMON instead of one in ten million. Used to restrict
    /// both the seed and the early search passes.
    /// </summary>
    private static bool OpensOntoBorder(
        VoyageBoard board, Cell cell, ChartFace face, BorderRule which)
    {
        if (which == BorderRule.None) return false;
        foreach (var side in Sides)
        {
            if (!face.IsOpen(side)) continue;
            if (board.InBounds(VoyageBoard.Neighbour(cell, side))) continue;
            if (which == BorderRule.All) return true;
            if (side is Side.North or Side.West) return true;
        }
        return false;
    }

    /// <summary>Which board edges a layout may not open onto.</summary>
    private enum BorderRule
    {
        /// <summary>No open edge may face the border at all. Tightest, often infeasible.</summary>
        All,

        /// <summary>
        /// Only the top and left borders. Weaker, and far more often satisfiable: those
        /// are the sides already decided when a cell is reached in row-major order, so
        /// constraining them prunes early without ruling out as much.
        /// </summary>
        NorthAndWest,

        /// <summary>Nothing forbidden; connectivity is checked at the leaf.</summary>
        None,
    }

    private Solution? SeedConnected(BorderRule rule)
    {
        var board = new VoyageBoard(_rows, _cols);
        var used = new bool[_charts.Count];
        var budget = 200_000;      // a seed that costs more than the search is no use
        var target = Math.Min(_maxPlacements, Math.Min(_rows * _cols, _charts.Count));

        bool Dive(int index)
        {
            if (budget-- <= 0) return false;

            if (index == _rows * _cols)
                // As many charts as may be spent, not merely a legal board: the EMPTY
                // board is trivially valid and trivially connected, and once the seed was
                // allowed to leave cells empty it returned that instantly.
                return board.FilledCount == target
                       && board.IsValid()
                       && board.StrandedCells().Count == 0;

            var cell = new Cell(index / _cols, index % _cols);

            // The seed has to obey the chart cap too. Without this it happily returned a
            // full board, and since fewer charts is always less value, the capped search
            // could never beat its own seed -- the cap silently did nothing.
            if (board.FilledCount >= _maxPlacements)
                return _allowEmpty && LeavingEmptyIsLegal(board, cell) && Dive(index + 1);

            foreach (var i in _ordering[index])
            {
                if (used[i]) continue;
                var chart = _charts[i];

                foreach (var rotation in ChartFace.DistinctRotations(chart.Shape))
                {
                    var face = new ChartFace(chart.Shape, rotation);
                    if (OpensOntoBorder(board, cell, face, rule)) continue;
                    if (!board.CanPlace(cell, face)) continue;
                    if (!SatisfiesDecidedNeighbours(board, cell, face)) continue;

                    board.Place(new Placement(chart, cell, rotation));
                    used[i] = true;
                    if (Dive(index + 1)) return true;
                    used[i] = false;
                    board.Clear(cell);
                }
            }

            // Leaving it empty is a legitimate branch, not a failure -- the board does
            // not have to be full.
            return _allowEmpty && LeavingEmptyIsLegal(board, cell) && Dive(index + 1);
        }

        if (!Dive(0)) return null;

        // Score it the same way the main search would, so the incumbent is comparable.
        var placements = board.Placements.ToList();
        var total = placements.Sum(p => _score(p.Chart, p.Cell));
        foreach (var p in placements)
            foreach (var side in Sides)
                if (board.At(VoyageBoard.Neighbour(p.Cell, side)) is { } n)
                    total += n.Chart.AdjacentValue;      // each ordered pair counted once

        return new Solution(placements, total);
    }

    /// <summary>
    /// Value created by putting <paramref name="chart"/> next to what is already placed.
    ///
    /// An Adjacent Modifier ("Adjacent Areas contain 2 additional Strongboxes") buffs the
    /// squares AROUND its chart, so the objective is not separable per cell -- a chart's
    /// worth depends on its neighbours. Every pair is scored once, when the second of the
    /// two is placed, and counted in both directions: the neighbour buffs the newcomer
    /// and the newcomer buffs the neighbour.
    ///
    /// This is why a Strongbox chart belongs in the centre (4 neighbours) rather than a
    /// corner (2) -- placement doubles its effect.
    /// </summary>
    private static double AdjacencyGain(VoyageBoard board, Cell cell, Chart chart)
    {
        var gain = 0.0;
        foreach (var side in Sides)
        {
            if (board.At(VoyageBoard.Neighbour(cell, side)) is not { } neighbour) continue;
            gain += neighbour.Chart.AdjacentValue;   // they buff us
            gain += chart.AdjacentValue;             // we buff them
        }
        return gain;
    }

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

        // Adjacency can only ADD value, so the bound has to allow for it or it stops
        // being admissible and the search could discard the true best layout.
        //
        // Bounded per adjacent PAIR rather than per cell. Adjacency is scored once per
        // pair -- when the second of the two is placed -- and each pair is worth at most
        // both charts' modifiers, so 2 x the best. Charging every remaining CELL for four
        // neighbours counted both ways triple-counts: on a 3x3 that is 72 x best where
        // the truth is 24 x best, and a bound that loose prunes almost nothing. It is why
        // even nine charts on nine cells could not be proved.
        var bestAdjacent = _charts.Count == 0 ? 0 : Math.Max(0, _charts.Max(c => c.AdjacentValue));
        var pairsRemaining = AdjacentPairsFrom();

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
            suffix[i] = Math.Min(byChart[i], byCell[i]) + 2 * bestAdjacent * pairsRemaining[i];
        }
        return suffix;
    }

    /// <summary>
    /// How many adjacent pairs are still unscored once filling reaches cell i.
    ///
    /// A pair is scored when the SECOND of its two cells is placed, so a pair still
    /// counts from index i if the later of its two indices is at least i. Row-major
    /// filling makes that the larger index of the two.
    /// </summary>
    private int[] AdjacentPairsFrom()
    {
        var n = _rows * _cols;
        var laterIndex = new List<int>();
        for (var r = 0; r < _rows; r++)
        {
            for (var c = 0; c < _cols; c++)
            {
                var index = r * _cols + c;
                if (c + 1 < _cols) laterIndex.Add(index + 1);        // east neighbour
                if (r + 1 < _rows) laterIndex.Add(index + _cols);    // south neighbour
            }
        }

        var remaining = new int[n + 1];
        for (var i = n - 1; i >= 0; i--)
            remaining[i] = remaining[i + 1] + laterIndex.Count(l => l == i);
        return remaining;
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
            // Ordered by an UPPER BOUND on what the chart could be worth here: its own
            // score plus the most its adjacency could ever pay, which is to all four
            // neighbours counted both ways. Ordering on the own-score alone buried the
            // charts whose whole value is what they give the squares around them.
            result[i] = Enumerable.Range(0, _charts.Count)
                .OrderByDescending(idx => _score(_charts[idx], cell)
                                          + Math.Max(0, _charts[idx].AdjacentValue) * 8)
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

    private double bestRank = double.NegativeInfinity;

    /// <summary>Set once the seed proves a fully joined board is possible for these charts.</summary>
    private bool _requireConnected;

    /// <summary>Print per-pass diagnostics. Set by a probe, never by the app.</summary>
    public static bool Trace { get; set; }

    /// <summary>Border restriction in force for the current search pass.</summary>
    private BorderRule _searchRule = BorderRule.None;

    /// <summary>
    /// Work allowed to a RESTRICTED pass, in nodes.
    ///
    /// Bounded by work rather than by a slice of the clock. Taking a fraction of the
    /// budget meant that on a small board -- where the unrestricted search can finish and
    /// find the true optimum -- half the time went on passes that could not, and the real
    /// search ran out before it got there. A node cap is generous on a big instance,
    /// where these passes are the only thing that finds a joined board at all, and nearly
    /// free on a small one.
    /// </summary>
    private const long RestrictedPassNodes = 250_000;

    private long _passStartNodes;

    private void Recurse(
        int index, VoyageBoard board, bool[] used, double value,
        double[] bound, ref Solution best,
        System.Diagnostics.Stopwatch sw, TimeSpan deadline, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        NodesExplored++;

        // Checking the clock every node would cost more than the search; every 4096 is
        // frequent enough to stop promptly and cheap enough not to matter.
        if ((NodesExplored & 0xFFF) == 0)
        {
            if (sw.Elapsed > deadline) throw new DeadlineReached();

            // A restricted pass is a head start, not the search. Cap its work so the
            // unrestricted pass keeps the bulk of the budget.
            if (_searchRule != BorderRule.None
                && NodesExplored - _passStartNodes > RestrictedPassNodes)
                throw new DeadlineReached();
        }

        if (index == _rows * _cols)
        {
            // Cells were only checked against decided neighbours during the search, so
            // the finished layout still has to satisfy the full rule.
            // Cheapest test first: the penalty only ever subtracts, so a board already
            // level with or behind the incumbent cannot win, and neither validity nor
            // connectivity -- both of which allocate -- need computing for it. Relaxing
            // this to "<" to let ties through cost far more than it bought: the search
            // fully evaluated every equal board and stopped proving optimality inside the
            // budget. Ties are handled by pricing instead, below.
            if (value <= best.Value) return;
            if (!board.IsValid()) return;

            // Connectivity is a property of the WHOLE board, so it cannot be scored per
            // placement like everything else -- it is only knowable here.
            var stranded = _strandedPenalty > 0 ? board.StrandedCells() : [];

            // A chart on a stranded square is FORFEIT, not merely taxed.
            //
            // The voyage starts in the bottom-left chart and travels by connections, so a
            // square cut off from that route is never visited and whatever sits there
            // pays nothing. A flat penalty cannot express that: 40 points is most of a
            // board under "currency" and a rounding error under "sulphur", so one setting
            // forbade stranding in one profile and priced it as a bargain in another.
            // Measured on a real 42-chart panel, five of eleven profiles returned a
            // layout with a square cut off, having correctly decided that one good
            // strongbox was worth more than the fee.
            //
            // Only the chart's OWN value is voided. What its Adjacent Modifier gives
            // NEIGHBOURS is left alone -- those modifiers speak of "adjacent Areas", and
            // whether they still reach from an unvisited square is not something the game
            // states either way.
            // A joined board is known to exist, so this one is simply not a candidate.
            if (_requireConnected && stranded.Count > 0) return;

            var forfeited = stranded.Sum(cell =>
                board.At(cell) is { } lost ? Math.Max(0, _score(lost.Chart, lost.Cell)) : 0);

            // The score reported is the honest one. The tie-breakers below only decide
            // which of two equal boards is kept and must not leak into the number shown --
            // "90.000000009" is not a score anybody asked for.
            var final = value - forfeited - stranded.Count * _strandedPenalty;
            var rank = final - stranded.Count * StrandedEpsilon
                             + board.FilledCount * FullnessEpsilon;

            if (rank > bestRank)
            {
                bestRank = rank;
                best = new Solution(board.Placements.ToList(), final)
                    { StrandedCells = stranded };
            }
            return;
        }

        // Nothing below can beat the incumbent -- stop.
        if (value + bound[index] <= best.Value) return;

        var cell = new Cell(index / _cols, index % _cols);

        // A cap on how many charts may be spent. Once it is reached the rest of the board
        // has to stay empty, which is legal -- the game says "up to nine".
        if (board.FilledCount >= _maxPlacements)
        {
            if (_allowEmpty && LeavingEmptyIsLegal(board, cell))
                Recurse(index + 1, board, used, value, bound, ref best, sw, deadline, ct);
            return;
        }

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
            var gain = _score(chart, cell) + AdjacencyGain(board, cell, chart);

            // CONTINUE, not break.
            //
            // Breaking here assumed the candidates arrive in descending gain, but they
            // are ordered by the chart's own score while gain also includes ADJACENCY --
            // what the chart gives its neighbours and they give it. A chart with a
            // middling score and a huge adjacency modifier sorts late, so the first weak
            // candidate ended the loop and took it down with it.
            //
            // That is how the strongbox profile came to skip every strongbox chart it
            // had: chart 33 carried +112 quantity AND three Operative's Strongboxes,
            // worth several times anything chosen, and was never even tried.
            if (value + gain + bound[index + 1] <= best.Value) continue;

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

        /// <summary>Blank until the chart has been hovered, so it is simply left out.</summary>
        private string NamePart => string.IsNullOrWhiteSpace(Chart.Name) ? "" : Chart.Name + " ";

        public override string ToString() =>
            $"square {Square} <- chart {ChartNumber,-3} {NamePart}({Chart.Shape}, {RotationText})";
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
