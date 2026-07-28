namespace MindGoblin.Core.Voyage;

/// <summary>
/// The order to run the squares in, once the board is placed.
///
/// The voyage starts in the bottom-left chart and travels by CONNECTIONS, so the order is
/// not free: a square can only be entered from one already visited that opens onto it.
/// That still leaves choices wherever the route branches, and the choices matter for two
/// reasons, both of which point the same way -- take the valuable squares EARLY:
///
///   * Allflame Lanterns deplete. They light the way to scattered loot, and once the last
///     is placed they begin to shrink, so the areas you reach late are the ones you are
///     least equipped to strip.
///   * A death ends the voyage. Whatever you have not reached, you do not get.
///
/// So this is not shortest-path -- every square gets visited either way. It is the
/// traveling repairman's problem: minimise how long the good squares wait. A square's
/// value is multiplied by how early it comes, and the ordering that maximises the total
/// is chosen exactly, by dynamic programming over subsets. Nine squares is 512 subsets,
/// so "exactly" is affordable and there is no reason to approximate.
/// </summary>
public sealed record VoyageRoute(
    IReadOnlyList<int> Squares,
    IReadOnlyList<int> Unreachable,
    double Score)
{
    public static VoyageRoute Empty { get; } = new([], [], 0);

    public bool IsEmpty => Squares.Count == 0;

    /// <summary>
    /// Plan the visiting order.
    ///
    /// <paramref name="value"/> is what a square is worth to you -- the chart on it plus
    /// whatever the board and its neighbours give it. Squares the route cannot reach are
    /// reported rather than silently dropped: the solver refuses to strand a square when
    /// it can, but when it cannot, an unreachable square is exactly the thing worth
    /// saying out loud.
    /// </summary>
    public static VoyageRoute Plan(
        VoyageBoard board, Func<Placement, double> value, Cell? start = null)
    {
        var origin = start ?? VoyageSolver.StartCell(board.Rows);
        var cells = AllCells(board).ToList();
        var n = cells.Count;
        if (n == 0 || n > 20) return Empty;          // the subset table is 2^n

        var index = cells.Select((c, i) => (c, i)).ToDictionary(p => p.c, p => p.i);
        if (!index.TryGetValue(origin, out var first)) return Empty;

        // Who can be entered from whom. Mutual, like every other connection rule here.
        var linked = new int[n];
        for (var i = 0; i < n; i++)
        {
            foreach (var side in Enum.GetValues<Side>())
            {
                if (board.At(cells[i]) is not { } here || !here.Face.IsOpen(side)) continue;
                var neighbour = VoyageBoard.Neighbour(cells[i], side);
                if (board.At(neighbour) is not { } there
                    || !there.Face.IsOpen(side.Opposite())) continue;
                linked[i] |= 1 << index[neighbour];
            }
        }

        var worth = cells.Select(c => Math.Max(0, value(board.At(c)!))).ToArray();

        // best[S] = the most that can be scored by visiting exactly S, in some legal
        // order starting at the origin. from[S] records which square was taken last, so
        // the order can be walked back out.
        var size = 1 << n;
        var best = new double[size];
        var from = new int[size];
        Array.Fill(best, double.NegativeInfinity);
        Array.Fill(from, -1);

        var startMask = 1 << first;
        best[startMask] = worth[first] * n;          // visited first, so worth the most

        for (var visited = 0; visited < size; visited++)
        {
            if (double.IsNegativeInfinity(best[visited])) continue;
            if ((visited & startMask) == 0) continue;

            var position = System.Numerics.BitOperations.PopCount((uint)visited);
            if (position >= n) continue;

            // Anything adjacent to what has been visited can be entered next; you can
            // walk back through squares already cleared to get there.
            var reachable = 0;
            for (var i = 0; i < n; i++)
                if ((visited & (1 << i)) != 0) reachable |= linked[i];
            reachable &= ~visited;

            for (var i = 0; i < n; i++)
            {
                if ((reachable & (1 << i)) == 0) continue;
                var next = visited | (1 << i);

                // Earlier is worth more: the multiplier falls by one per square visited.
                var score = best[visited] + worth[i] * (n - position);
                if (score <= best[next]) continue;
                best[next] = score;
                from[next] = i;
            }
        }

        // The best reachable set is the one with the most squares; among those, the
        // highest score. A board the route cannot fully cover is a real outcome.
        var target = -1;
        for (var visited = 0; visited < size; visited++)
        {
            if (double.IsNegativeInfinity(best[visited])) continue;
            if ((visited & startMask) == 0) continue;
            if (target < 0) { target = visited; continue; }

            var here = System.Numerics.BitOperations.PopCount((uint)visited);
            var there = System.Numerics.BitOperations.PopCount((uint)target);
            if (here > there || (here == there && best[visited] > best[target])) target = visited;
        }
        if (target < 0) return Empty;

        var order = new List<int>();
        for (var visited = target; visited != startMask; )
        {
            var last = from[visited];
            order.Add(last);
            visited &= ~(1 << last);
        }
        order.Add(first);
        order.Reverse();

        var unreachable = Enumerable.Range(0, n)
            .Where(i => (target & (1 << i)) == 0)
            .Select(i => VoyagePlan.SquareNumber(cells[i], board.Cols))
            .Order()
            .ToList();

        return new VoyageRoute(
            order.Select(i => VoyagePlan.SquareNumber(cells[i], board.Cols)).ToList(),
            unreachable,
            best[target]);
    }

    private static IEnumerable<Cell> AllCells(VoyageBoard board)
    {
        for (var r = 0; r < board.Rows; r++)
            for (var c = 0; c < board.Cols; c++)
                if (board.At(new Cell(r, c)) is not null) yield return new Cell(r, c);
    }

    public override string ToString() =>
        IsEmpty ? "no route" : string.Join(" → ", Squares);
}
