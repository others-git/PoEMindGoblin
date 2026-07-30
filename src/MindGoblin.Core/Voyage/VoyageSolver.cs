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
        // Optimistic remaining value. Never underestimates, so pruning against it is
        // safe -- the tests hold it to that.
        PrepareBounds();
        _ordering = BuildOrdering();

        var best = new Solution(Array.Empty<Placement>(), double.NegativeInfinity);
        _bestRank = double.NegativeInfinity;

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
        if (SeedConnected(sw, deadline * SeedShare) is { } seed)
        {
            // Polish the seed BEFORE the search: same-shape swaps turn "a connected
            // board" into "a connected board of the best charts these shapes allow",
            // and a strong incumbent is what makes the bound prune at all.
            best = Polish(seed);
            _bestRank = best.Value + best.Placements.Count * FullnessEpsilon;
            _requireConnected = true;
        }


        // Search the RESTRICTED space first, then the whole of it.
        //
        // Forbidding edges that open onto the border makes connected boards dense, so a
        // short pass there harvests strong incumbents that the real search then prunes
        // with. Measured on a real 42-chart panel this is not a nicety: with the passes,
        // sulphur proves 3525 and currency finds 96; without them, 3440 and 66.
        //
        // But a restricted pass is a SCOUT, and a scout that spends the army's budget
        // loses the war: the high-tier profile scores every chart so alike that only a
        // near-complete unrestricted pass improves on the seed, and letting the scouts
        // run 250k nodes each starved it from 714 down to 571. Hence the tight node cap
        // and the hard clock share in Recurse -- on a small instance the scouts finish
        // for free, on a big one they pay for themselves, and either way the final pass
        // keeps the bulk of the budget. Only that final, unrestricted pass can prove
        // anything: the scouts deliberately ignore legal layouts, so finishing one says
        // nothing about what it was forbidden from considering.
        var exhausted = false;
        foreach (var rule in new[] { BorderRule.All, BorderRule.NorthAndWest, BorderRule.None })
        {
            _searchRule = rule;
            var completed = true;
            _passStartNodes = NodesExplored;

            // A FRESH board and used-set per pass: DeadlineReached unwinds through every
            // recursive frame, and those frames undo their placement AFTER the recursive
            // call returns -- an aborted pass leaves the board half filled. Reusing it
            // once made the next pass bottom out in two nodes and claim a proof.
            var board = new VoyageBoard(_rows, _cols);
            var used = new bool[_charts.Count];
            if (Trace)
                Console.Error.WriteLine(
                    $"    pass {rule,-13} start best={best.Value,10:0.##} bound0={Bound(0, 0):0.##} "
                    + $"nodes={NodesExplored} t={sw.Elapsed.TotalMilliseconds:0}ms");
            try
            {
                Recurse(0, board, used, 0.0, 0.0, ref best, sw, deadline, ct);
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

        // One more polish on the way out. On a plateaued panel -- many charts of
        // similar value over 60 candidates -- the branch and bound can fail to improve
        // the incumbent at all: connected boards are too rare for blind descent to hit
        // and the bound too flat to steer, measured at 28 MILLION nodes without a
        // single improvement while a plainly better board existed. Swapping never
        // breaks a layout, so it harvests exactly the value the search could not.
        if (best.Value != double.NegativeInfinity)
        {
            var polished = Polish(best);
            if (polished.Value > best.Value)
            {
                best = polished;
                exhausted = false;    // the search demonstrably had not seen everything
            }
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
    /// Improve a solution by swaps that cannot break its route.
    ///
    /// Same-shape swaps preserve every edge by construction; CROSS-shape moves are
    /// legal too whenever a rotation reproduces the cell's inner edges, because border
    /// edges connect nothing -- checked cheaply per move (IsValid plus a stranded-count
    /// comparison on a nine-cell board). Greedy best-first until nothing improves; the
    /// space is 9 cells x 60 charts x 4 rotations, still microseconds against a search
    /// measured in millions of nodes.
    ///
    /// This exists because branch and bound dies on plateaus. With many near-equal
    /// charts, the bound stops pruning and the search wanders a space where connected
    /// boards are one in millions -- 28M nodes without one improvement, while the seed
    /// held junk that ANY same-shape swap would better. The search finds the layout;
    /// the polish makes the layout spend the best charts it can hold.
    /// </summary>
    private Solution Polish(Solution solution)
    {
        if (solution.IsEmpty) return solution;

        var board = new VoyageBoard(_rows, _cols);
        foreach (var p in solution.Placements) board.Place(p);
        var used = new bool[_charts.Count];
        var indexOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < _charts.Count; i++) indexOf[_charts[i].Id] = i;
        foreach (var p in solution.Placements)
            if (indexOf.TryGetValue(p.Chart.Id, out var i)) used[i] = true;

        // The connectivity of a layout lives ENTIRELY in its inner edges: border-facing
        // edges connect nothing, and both open and closed are legal against the border.
        // So any chart whose SOME rotation reproduces a cell's inner edges can take that
        // cell -- whatever its shape -- and the route is untouched. The first polish
        // only swapped like shapes, and a rare-dense Corner sat one tile away from a
        // 15000-point payout square held by a Junction it could not displace.
        var strandedBefore = board.StrandedCells().Count;

        bool KeepsRoute() => board.IsValid() && board.StrandedCells().Count <= strandedBefore;

        var improved = true;
        while (improved)
        {
            improved = false;
            var placements = board.Placements.ToList();

            foreach (var placement in placements)
            {
                var baseline = Evaluate(board);

                // Any unused chart, any rotation that fits the cell's inner edges.
                for (var i = 0; i < _charts.Count && !improved; i++)
                {
                    if (used[i]) continue;
                    foreach (var rotation in ChartFace.DistinctRotations(_charts[i].Shape))
                    {
                        board.Clear(placement.Cell);
                        var candidate = new Placement(_charts[i], placement.Cell, rotation);
                        board.Place(candidate);
                        if (KeepsRoute() && Evaluate(board) > baseline + 1e-9)
                        {
                            used[i] = true;
                            if (indexOf.TryGetValue(placement.Chart.Id, out var old))
                                used[old] = false;
                            improved = true;
                            break;
                        }
                        board.Clear(placement.Cell);
                        board.Place(placement);
                    }
                }
                if (improved) break;

                // Two placed charts trading cells, re-choosing both rotations.
                foreach (var other in placements)
                {
                    if (other.Cell == placement.Cell) continue;
                    board.Clear(placement.Cell);
                    board.Clear(other.Cell);
                    foreach (var ra in ChartFace.DistinctRotations(other.Chart.Shape))
                    {
                        foreach (var rb in ChartFace.DistinctRotations(placement.Chart.Shape))
                        {
                            var a = new Placement(other.Chart, placement.Cell, ra);
                            var b = new Placement(placement.Chart, other.Cell, rb);
                            board.Place(a);
                            board.Place(b);
                            if (KeepsRoute() && Evaluate(board) > baseline + 1e-9)
                            {
                                improved = true;
                                break;
                            }
                            board.Clear(placement.Cell);
                            board.Clear(other.Cell);
                        }
                        if (improved) break;
                    }
                    if (improved) break;
                    board.Place(placement);
                    board.Place(other);
                }
                if (improved) break;
            }
        }

        var value = Evaluate(board);
        var stranded = board.StrandedCells();
        var forfeited = stranded.Sum(cell =>
            board.At(cell) is { } lost ? Math.Max(0, _score(lost.Chart, lost.Cell)) : 0);
        return new Solution(board.Placements.ToList(),
                            value - forfeited - stranded.Count * _strandedPenalty)
            { StrandedCells = stranded };
    }

    /// <summary>A whole board's raw value, scored exactly as the search accumulates it.</summary>
    private double Evaluate(VoyageBoard board)
    {
        var total = 0.0;
        foreach (var p in board.Placements)
        {
            total += _score(p.Chart, p.Cell);
            foreach (var side in Sides)
                if (board.At(VoyageBoard.Neighbour(p.Cell, side)) is { } n)
                    total += PairAdjacency(n.Chart, p.Chart);
        }
        return total;
    }

    /// <summary>
    /// How much of the budget the seed may spend.
    ///
    /// It needs a clock as well as a node cap. With six dives to try -- three border
    /// rules by two orderings -- a node cap alone let seeding run for nine seconds on a
    /// three-second budget, which is not a slow answer, it is the wrong answer to the
    /// question that was asked.
    /// </summary>
    private const double SeedShare = 0.3;

    /// <summary>
    /// Find one connected board fast, to start the search from something decent.
    ///
    /// The trick is closing every edge that faces the BORDER: such an edge is precisely
    /// a path that leads nowhere, so forbidding them removes almost every dead corner by
    /// construction -- and it collapses the space enough to hit a connected board in a
    /// handful of nodes where the unrestricted search needed eleven million.
    ///
    /// It is only a seed, never the answer: the restriction rules out perfectly good
    /// layouts, so whatever it finds is scored honestly and handed to the real search as
    /// an incumbent to beat. Returns null when no such board exists, which is legitimate
    /// -- some chart sets cannot close every border.
    /// </summary>
    private Solution? SeedConnected(System.Diagnostics.Stopwatch sw, TimeSpan deadline)
    {
        // Try several dives and keep the BEST, rather than the first that works.
        //
        // The two orderings do different jobs. Plain panel order settles EXISTENCE in a
        // handful of nodes -- and existence is what licenses forbidding stranded squares
        // at all -- but the board it finds is arbitrary in value. Score order finds a
        // well-valued board that prunes the real search hard, but can wander for seconds
        // and is profile-dependent, so relying on it alone meant a profile that ordered
        // the charts badly was told no joined board existed on a board that plainly had
        // one.
        //
        // Returning the first success got this wrong in both directions: score-first blew
        // the clock, and plain-first handed back a poor incumbent the search then could
        // not improve on inside its budget -- sulphur fell from 3515 to 2250 doing
        // exactly that. So run the cheap one first for the existence proof, then keep
        // going while the clock allows and take the best board any dive produced.
        var plain = BuildNeutralOrdering();
        Solution? best = null;

        foreach (var closed in new[] { BorderRule.All, BorderRule.NorthAndWest, BorderRule.None })
        {
            foreach (var ordering in new[] { plain, _ordering })
            {
                if (sw.Elapsed > deadline) return best;
                if (SeedConnected(closed, ordering, sw, deadline) is not { } seed) continue;
                if (best is null || seed.Value > best.Value) best = seed;
            }

            // A joined board under a stricter rule is worth more than one under a weaker
            // rule, and the weaker rules are the expensive ones. Stop once anything works.
            if (best is not null) return best;
        }

        return best;
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

    /// <summary>Panel order, the same for every profile.</summary>
    private int[][] BuildNeutralOrdering()
    {
        var natural = Enumerable.Range(0, _charts.Count).ToArray();
        return Enumerable.Range(0, _rows * _cols).Select(_ => natural).ToArray();
    }

    private Solution? SeedConnected(
        BorderRule rule, int[][] ordering, System.Diagnostics.Stopwatch sw, TimeSpan deadline)
    {
        var board = new VoyageBoard(_rows, _cols);
        var used = new bool[_charts.Count];
        var budget = 200_000;      // a seed that costs more than the search is no use
        var target = Math.Min(_maxPlacements, Math.Min(_rows * _cols, _charts.Count));

        bool Dive(int index)
        {
            if (budget-- <= 0) return false;

            // The clock, not just the node count. Checked sparsely -- reading a stopwatch
            // per node would cost more than the dive it is protecting.
            //
            // Spending the BUDGET is what stops the dive. Returning false alone only
            // fails the current branch: the loops above carry on with the next candidate,
            // and every one of them costs its placement checks before its child returns
            // false again. So an expired clock still let each dive grind through all
            // 200,000 nodes -- six dives of it turned a 500ms budget into 2.9 seconds on
            // a board of Ends. Zeroing the budget collapses the whole dive at once.
            if ((budget & 0xFF) == 0 && sw.Elapsed > deadline)
            {
                budget = 0;
                return false;
            }

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

            foreach (var i in ordering[index])
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
                    total += PairAdjacency(n.Chart, p.Chart);   // each ordered pair once

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
            // They buff us, we buff them -- and a per-monster payout is worth more the
            // denser the tile it lands on, which is the chart-x-chart interaction that
            // makes a Chaos-per-rare chart seek out the packed neighbour.
            gain += PairAdjacency(giver: neighbour.Chart, receiver: chart);
            gain += PairAdjacency(giver: chart, receiver: neighbour.Chart);
        }
        return gain;
    }

    /// <summary>
    /// What ONE chart's adjacent modifier pays when it lands beside another -- flat part
    /// plus the per-monster part scaled by the RECEIVER's density on the payout's
    /// channel. The single source of truth: search gain, seed scoring and the polish
    /// all call this, because the first time they each had their own copy the polish
    /// kept pricing population payouts by rare density after the channel split.
    /// </summary>
    private static double PairAdjacency(Chart giver, Chart receiver) =>
        giver.AdjacentValue
        + giver.AdjacentPerMonsterValue
          * (1 + (giver.AdjacentPayoutOnPopulation
                      ? receiver.PackDensity : receiver.MonsterDensity));

    /// <summary>Per-chart score ceiling: the best this chart scores on ANY cell, floored
    /// at zero. Indexed like <see cref="_charts"/>; maintained as a running sum down the
    /// search so the chart bound can subtract what the placed charts have used up.</summary>
    private double[] _chartCeil = [];

    /// <summary>Sum of the top boardsize chart ceilings -- the most ANY full board's
    /// placements could be worth, ignoring shape and position.</summary>
    private double _topChartSum;

    /// <summary>Suffix sums of each cell's best possible score, from cell i onward.</summary>
    private double[] _cellSuffix = [];

    /// <summary>Adjacency headroom from cell i onward: unscored pairs x 2 x best modifier.</summary>
    private double[] _pairSlack = [];

    /// <summary>
    /// Precompute the pieces of the upper bound on the value still obtainable from cell
    /// <c>i</c> onwards, given what the placed charts have already consumed.
    ///
    /// Three admissible parts; the score term takes the smaller of two:
    ///
    ///   BY CHART, dynamic: topChartSum minus the ceilings of the charts already placed.
    ///   The future charts are distinct from each other AND from the placed ones, so
    ///   placed + future together can never out-sum the best boardsize ceilings. This
    ///   must be DYNAMIC. The old static version assumed the cells filled so far had
    ///   taken the top-ranked charts, so a branch that opened with a modest chart while
    ///   a top chart waited for its true cell was UNDER-bounded -- and with per-cell
    ///   scoring, which is exactly what board modifiers create, the search pruned the
    ///   real optimum and still reported the answer proved (see
    ///   SolverSoundnessTests.PerCellScoringCannotPruneTheTrueOptimum: 13 "proved" on an
    ///   instance whose optimum is 18). On the greedy first descent the subtraction
    ///   equals the old suffix exactly, so the pruning that made the search fast is kept.
    ///
    ///   BY CELL, static: the best any chart could score on each remaining cell. Captures
    ///   the scarcity of buffed squares -- without it, one buffed square lets every
    ///   remaining chart pretend it will land there, and the bound prunes nothing.
    ///
    ///   ADJACENCY, per remaining PAIR: scored once per pair when its second cell is
    ///   placed, each worth at most both charts' modifiers. Charging every cell for four
    ///   neighbours both ways triple-counts (72 x best on a 3x3 where the truth is 24 x)
    ///   and is why nine charts on nine cells once could not be proved.
    /// </summary>
    private void PrepareBounds()
    {
        var cells = AllCells().ToList();
        var n = cells.Count;

        _chartCeil = [.. _charts.Select(c => Math.Max(0, cells.Max(cell => _score(c, cell))))];
        _topChartSum = _chartCeil.OrderByDescending(v => v).Take(n).Sum();

        var cellBest = cells
            .Select(cell => _charts.Count == 0 ? 0 : Math.Max(0, _charts.Max(c => _score(c, cell))))
            .ToList();
        // The adjacency ceiling has to allow for the per-monster pairing at its best:
        // the richest payout landing beside the densest tile in the panel.
        var maxDensity = _charts.Count == 0 ? 0
            : Math.Max(0, _charts.Max(c => Math.Max(c.MonsterDensity, c.PackDensity)));
        var bestAdjacent = _charts.Count == 0 ? 0 : Math.Max(0, _charts.Max(c =>
            c.AdjacentValue + c.AdjacentPerMonsterValue * (1 + maxDensity)));
        var pairsRemaining = AdjacentPairsFrom();

        _cellSuffix = new double[n + 1];
        _pairSlack = new double[n + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            _cellSuffix[i] = _cellSuffix[i + 1] + cellBest[i];
            _pairSlack[i] = 2 * bestAdjacent * pairsRemaining[i];
        }
    }

    /// <summary>Most that filling cells i.. can still add, with usedCeil already spent.</summary>
    private double Bound(int index, double usedCeil) =>
        Math.Min(_topChartSum - usedCeil, _cellSuffix[index]) + _pairSlack[index];

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
            // Ordered by the chart's own score plus DOUBLE the most its adjacency can
            // pay (four neighbours is the true cap -- what neighbours give back does not
            // depend on which candidate is chosen). Deliberately overweighted: this only
            // orders candidates, and surfacing adjacency charts early is what matters --
            // ordering on the own-score alone buried the charts whose whole value is
            // what they give the squares around them.
            result[i] = Enumerable.Range(0, _charts.Count)
                .OrderByDescending(idx => _score(_charts[idx], cell)
                                          + Math.Max(0, _charts[idx].AdjacentValue
                                                        + _charts[idx].AdjacentPerMonsterValue * 2) * 8)
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

    private double _bestRank = double.NegativeInfinity;

    /// <summary>Set once the seed proves a fully joined board is possible for these charts.</summary>
    private bool _requireConnected;

    /// <summary>Print search diagnostics. Set by a probe, never by the app.</summary>
    public static bool Trace { get; set; }

    /// <summary>Border restriction in force for the current search pass.</summary>
    private BorderRule _searchRule = BorderRule.None;

    /// <summary>
    /// Work allowed to a restricted scout pass, in nodes. Small on purpose: the scouts'
    /// job is a strong incumbent in the first few descents, and everything past that is
    /// budget taken from the one pass that can prove the answer.
    /// </summary>
    private const long RestrictedPassNodes = 60_000;

    private long _passStartNodes;

    private void Recurse(
        int index, VoyageBoard board, bool[] used, double value,
        double usedCeil, ref Solution best,
        System.Diagnostics.Stopwatch sw, TimeSpan deadline, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        NodesExplored++;

        // Checking the clock every node would cost more than the search, but 4096 was too
        // sparse: work per node varies enormously. On a board of Ends almost every
        // candidate fails its placement checks, so thousands of candidates are examined
        // between one node and the next, and a 500ms budget ran for five seconds.
        if ((NodesExplored & 0xFF) == 0)
        {
            if (sw.Elapsed > deadline) throw new DeadlineReached();

            // A scout pass ends at its node cap or at half the clock, whichever bites
            // first -- the unrestricted pass must keep the bulk of the budget.
            if (_searchRule != BorderRule.None
                && (NodesExplored - _passStartNodes > RestrictedPassNodes
                    || sw.Elapsed > deadline * 0.5))
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
            // placement like everything else -- it is only knowable here. Computed even
            // when nothing is charged for it: with a zero penalty it still gates the
            // require-connected constraint and still has to be REPORTED -- a solution
            // claiming every square is joined while one is dead is worse than a fee.
            // Few boards get this far past the value gate, so the BFS is cheap.
            var stranded = board.StrandedCells();

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

            if (rank > _bestRank)
            {
                _bestRank = rank;
                best = new Solution(board.Placements.ToList(), final)
                    { StrandedCells = stranded };
            }
            return;
        }

        // Nothing below can beat the incumbent -- stop.
        if (value + Bound(index, usedCeil) <= best.Value) return;

        var cell = new Cell(index / _cols, index % _cols);

        // A cap on how many charts may be spent. Once it is reached the rest of the board
        // has to stay empty, which is legal -- the game says "up to nine".
        if (board.FilledCount >= _maxPlacements)
        {
            if (_allowEmpty && LeavingEmptyIsLegal(board, cell))
                Recurse(index + 1, board, used, value, usedCeil, ref best, sw, deadline, ct);
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
            if (value + gain + Bound(index + 1, usedCeil + _chartCeil[i]) <= best.Value)
                continue;

            foreach (var rotation in ChartFace.DistinctRotations(chart.Shape))
            {
                var face = new ChartFace(chart.Shape, rotation);
                // What makes a scout pass a scout: no edge may open onto the border it
                // is scouting. (For years this check only existed in the seed, so the
                // "restricted" passes silently searched the full space with a node cap.)
                if (OpensOntoBorder(board, cell, face, _searchRule)) continue;
                if (!board.CanPlace(cell, face)) continue;
                if (!SatisfiesDecidedNeighbours(board, cell, face)) continue;

                used[i] = true;
                board.Place(new Placement(chart, cell, rotation));
                Recurse(index + 1, board, used, value + gain,
                        usedCeil + _chartCeil[i], ref best, sw, deadline, ct);
                board.Clear(cell);
                used[i] = false;
            }
        }

        if (_allowEmpty && LeavingEmptyIsLegal(board, cell))
            Recurse(index + 1, board, used, value, usedCeil, ref best, sw, deadline, ct);
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
