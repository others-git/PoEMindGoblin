namespace MindGoblin.Core.Voyage;

/// <summary>
/// What a solved board actually yields.
///
/// This replaced a list that repeated the plan -- "square 1 &lt;- chart 23" nine times,
/// beside a board already showing exactly that in 34pt. Restating the answer is not
/// information. What the board is WORTH is, and it cannot be read off the squares: the
/// stats have to be totalled, the global modifiers gathered, and the adjacency payouts
/// multiplied out by how many neighbours each one actually reaches.
/// </summary>
public sealed record VoyageSummary(
    int Charts,
    int LowestLevel,
    int HighestLevel,
    double AverageLevel,
    IReadOnlyList<(string Stat, double Total)> Stats,
    IReadOnlyList<string> VoyageWide,
    IReadOnlyList<VoyageSummary.Adjacency> Adjacencies)
{
    /// <summary>
    /// One chart's adjacency payout and the squares it lands on.
    ///
    /// Reach is the point: the same modifier is worth twice as much in the middle as in a
    /// corner, and that is invisible unless the neighbour count is stated.
    /// </summary>
    public sealed record Adjacency(int Square, int ChartNumber, string Modifier, int Reach)
    {
        public override string ToString() =>
            $"square {Square} · chart {ChartNumber} → {Reach} neighbour{(Reach == 1 ? "" : "s")}";
    }

    public static VoyageSummary Empty { get; } =
        new(0, 0, 0, 0, [], [], []);

    /// <summary>
    /// Total what is on the board.
    ///
    /// Stats are SUMMED rather than averaged: every square is its own area, so nine
    /// charts at +80% quantity is nine areas each rolling at +80%, and the sum is what
    /// the voyage pays across all of them. An average would understate a full board and
    /// make a four-chart one look identical.
    /// </summary>
    public static VoyageSummary Build(
        VoyageBoard board, IReadOnlyDictionary<string, int> chartNumbers, int boardCols)
    {
        var placements = board.Placements.ToList();
        if (placements.Count == 0) return Empty;

        var levels = placements.Select(p => p.Chart.AreaLevel).Where(l => l > 0).ToList();

        var stats = new List<(string, double)>();
        void Total(string name, Func<Chart, double> pick)
        {
            var sum = placements.Sum(p => pick(p.Chart));
            if (sum != 0) stats.Add((name, sum));
        }

        Total("Item Quantity", c => c.ItemQuantity);
        Total("Item Rarity", c => c.ItemRarity);
        Total("Monster Pack Size", c => c.MonsterPackSize);
        Total("Gold Found", c => c.GoldFound);
        Total("Dead Man's Sulphur", c => c.Sulphur);

        // Global modifiers apply wherever their chart sits, so only the ones actually ON
        // the board are in effect -- a chart left in the panel contributes nothing.
        var global = placements
            .Where(p => !string.IsNullOrWhiteSpace(p.Chart.VoyageModifier))
            .Select(p => p.Chart.VoyageModifier!)
            .ToList();

        var adjacencies = new List<Adjacency>();
        foreach (var placement in placements)
        {
            if (string.IsNullOrWhiteSpace(placement.Chart.AdjacentModifier)) continue;

            var reach = Enum.GetValues<Side>()
                .Count(side => board.At(VoyageBoard.Neighbour(placement.Cell, side)) is not null);
            if (reach == 0) continue;

            adjacencies.Add(new Adjacency(
                VoyagePlan.SquareNumber(placement.Cell, boardCols),
                chartNumbers.GetValueOrDefault(placement.Chart.Id, 0),
                placement.Chart.AdjacentModifier!,
                reach));
        }

        return new VoyageSummary(
            placements.Count,
            levels.Count > 0 ? levels.Min() : 0,
            levels.Count > 0 ? levels.Max() : 0,
            levels.Count > 0 ? levels.Average() : 0,
            stats,
            global,
            adjacencies.OrderByDescending(a => a.Reach).ThenBy(a => a.Square).ToList());
    }

    /// <summary>A one-line headline: how many charts, and over what tiers.</summary>
    public string Headline => Charts == 0
        ? "Nothing placed"
        : LowestLevel == 0
            ? $"{Charts} charts"
            : LowestLevel == HighestLevel
                ? $"{Charts} charts · all level {HighestLevel}"
                : $"{Charts} charts · levels {LowestLevel}–{HighestLevel} (avg {AverageLevel:0.#})";
}
