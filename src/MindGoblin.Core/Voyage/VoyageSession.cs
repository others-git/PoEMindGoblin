using System.Text.RegularExpressions;

namespace MindGoblin.Core.Voyage;

/// <summary>
/// A planning session: what has been read off the screen so far, and the plan that falls
/// out of it.
///
/// Reading the board happens in two passes because the game only shows half of what
/// matters without hovering:
///
///   PASS 1 -- one screenshot. <see cref="ChartPanelReader"/> gives every chart's shape,
///             rotation and area level. Enough to solve the CONNECTIVITY problem outright.
///   PASS 2 -- hover. Quantity, pack size, sulphur and the two special modifier lines
///             exist only in tooltips, so each chart worth scoring is hovered and copied.
///             Same for the 12 figurines.
///
/// The session tracks pass 2 as a checklist, because it is the tedious part and the user
/// needs to see what is still missing rather than discover it in a bad plan. Crucially,
/// pass 2 is OPTIONAL: with no hover text at all the solver still returns a valid layout,
/// scored on area level alone. Detail improves the plan; its absence does not block one.
/// </summary>
/// <summary>What the user has said the planner may do with a chart.</summary>
public enum ChartMark
{
    /// <summary>No opinion: the rules decide.</summary>
    None,

    /// <summary>The veto: never place it.</summary>
    Excluded,

    /// <summary>The mandate: place it on any board where doing so is legal.</summary>
    Required,
}

public sealed class VoyageSession
{
    private readonly Dictionary<int, Chart> _charts = new();
    private readonly HashSet<int> _excluded = new();
    private readonly HashSet<int> _required = new();
    private readonly Dictionary<int, string> _figurines = new();
    private readonly Dictionary<int, List<string>> _squareModifiers = new();

    public VoyageSession(BoardLayout? layout = null)
    {
        Layout = layout ?? BoardLayout.Default();
    }

    public BoardLayout Layout { get; }

    /// <summary>Charts read from the panel, in panel order.</summary>
    public IReadOnlyList<Chart> Charts => _charts.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();

    /// <summary>Panel index (1-based) of each chart, for "square N &lt;- chart M".</summary>
    public IReadOnlyDictionary<int, Chart> ByPanelIndex => _charts;

    /// <summary>Figurine modifier text captured so far, keyed by figurine index.</summary>
    public IReadOnlyDictionary<int, string> Figurines => _figurines;

    /// <summary>Modifiers read off the Area Modifiers panel, keyed by 1-based square.</summary>
    public IReadOnlyDictionary<int, List<string>> SquareModifiers => _squareModifiers;

    /// <summary>
    /// Record what the game says applies to a square.
    ///
    /// This is the game's OWN aggregate for that square, so it supersedes anything worked
    /// out from figurines rather than adding to it -- counting both would pay twice for
    /// the same modifier.
    ///
    /// An EMPTY list is a real answer, not a non-answer: the centre square of a 3x3
    /// touches none of the twelve perimeter figurines, so "no modifiers here" is the
    /// truth about it and has to be recordable. Pass null to un-read a square instead.
    /// </summary>
    public void ApplySquareModifiers(int square, IReadOnlyList<string>? lines)
    {
        if (lines is null) _squareModifiers.Remove(square);
        else _squareModifiers[square] = lines.ToList();
    }

    /// <summary>Forget a square's reading, putting it back on the checklist.</summary>
    public void ClearSquareModifiers(int square) => _squareModifiers.Remove(square);

    /// <summary>
    /// Squares no figurine touches, which therefore can never carry a board modifier.
    ///
    /// On the standard ring every figurine sits against an EDGE square, so the centre of
    /// a 3x3 is reachable by none of them. Its Area Modifiers panel is empty every time,
    /// and asking the user to hover it is asking them to confirm something the layout
    /// already determines.
    ///
    /// Derived from the layout rather than hardcoded to square 5: change the board size
    /// or the figurine ring and this follows.
    /// </summary>
    public IReadOnlyList<int> SquaresWithoutFigurines =>
        Enumerable.Range(1, Layout.Rows * Layout.Cols)
                  .Where(sq => !Layout.Figurines.Any(
                      f => f.Adjacent.Any(a => a.ToCell() == CellOf(sq))))
                  .ToList();

    /// <summary>Squares whose Area Modifiers still need reading.</summary>
    public IReadOnlyList<int> SquaresAwaitingModifiers
    {
        get
        {
            var unreachable = SquaresWithoutFigurines.ToHashSet();
            return Enumerable.Range(1, Layout.Rows * Layout.Cols)
                             .Where(sq => !SquareBoardKnown(sq) && !unreachable.Contains(sq))
                             .ToList();
        }
    }

    /// <summary>
    /// Where the voyage begins. "Voyages will always start in the bottom left Chart."
    /// </summary>
    public int StartSquare => VoyagePlan.SquareNumber(VoyageSolver.StartCell(Layout.Rows), Layout.Cols);

    /// <summary>The cell a 1-based square number refers to.</summary>
    public Cell CellOf(int square) =>
        new((square - 1) / Layout.Cols, (square - 1) % Layout.Cols);

    /// <summary>
    /// Every global "Voyage Modifier" in play, with the chart it came from.
    ///
    /// These apply to the whole voyage wherever the chart sits, so unlike everything else
    /// on this board their position is irrelevant -- which is exactly why they belong
    /// grouped at the top rather than buried per square.
    /// </summary>
    public IReadOnlyList<(int PanelIndex, string Modifier)> VoyageWideModifiers =>
        _charts.Where(kv => !string.IsNullOrWhiteSpace(kv.Value.VoyageModifier))
               .OrderBy(kv => kv.Key)
               .Select(kv => (kv.Key, kv.Value.VoyageModifier!))
               .ToList();

    /// <summary>
    /// Take a panel read. Charts already carrying hover detail keep it: a re-read after
    /// hovering half the panel must not wipe the half that was done.
    /// </summary>
    public void ApplyPanelRead(IEnumerable<ChartPanelReader.ReadCell> cells)
    {
        var seen = new HashSet<int>();
        foreach (var cell in cells)
        {
            if (cell.Shape is not { } shape) continue;    // unreadable glyph, skip rather than guess
            seen.Add(cell.Index);

            if (_charts.TryGetValue(cell.Index, out var existing))
            {
                _charts[cell.Index] = existing with
                {
                    Shape = shape,
                    AreaLevel = cell.Level ?? existing.AreaLevel,
                };
                continue;
            }

            // Nameless until hovered: the panel shows no name, and inventing "chart 12"
            // would print twice in a plan that already leads with the panel number.
            _charts[cell.Index] = new Chart(
                $"panel-{cell.Index}", "", shape, cell.Level ?? 0, Array.Empty<string>());
        }

        // A chart that is gone from the panel has been used or sold.
        foreach (var stale in _charts.Keys.Where(k => !seen.Contains(k)).ToList())
            _charts.Remove(stale);
    }

    /// <summary>
    /// Attach hover text to a chart. The shape from the panel read wins over anything the
    /// text claims -- the glyph is measured, the text is parsed.
    /// </summary>
    public bool ApplyChartText(int panelIndex, string? text)
    {
        if (!_charts.TryGetValue(panelIndex, out var existing)) return false;

        var parsed = ChartText.Parse(text, $"panel-{panelIndex}", existing.Shape);
        if (parsed is null) return false;

        _charts[panelIndex] = parsed with
        {
            Shape = existing.Shape,
            AreaLevel = parsed.AreaLevel > 0 ? parsed.AreaLevel : existing.AreaLevel,
        };
        return true;
    }

    public void ApplyFigurineText(int figurineIndex, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) _figurines.Remove(figurineIndex);
        else _figurines[figurineIndex] = text.Trim();
    }

    /// <summary>Charts read from the panel but not yet hovered.</summary>
    public IReadOnlyList<int> ChartsAwaitingDetail =>
        _charts.Where(kv => !HasDetail(kv.Value)).Select(kv => kv.Key).Order().ToList();

    /// <summary>Figurines not yet hovered.</summary>
    public IReadOnlyList<BoardLayout.FigurineSlot> FigurinesAwaitingDetail =>
        Layout.Unread(_figurines);

    private static bool HasDetail(Chart c) =>
        !string.IsNullOrEmpty(c.VoyageModifier) || !string.IsNullOrEmpty(c.AdjacentModifier)
        || c.Modifiers.Count > 0 || c.ItemQuantity != 0 || c.MonsterPackSize != 0
        || c.Sulphur != 0 || c.GoldFound != 0 || c.ItemRarity != 0;

    /// <summary>
    /// How far through the read the user is, as a fraction. Counts the panel as done once
    /// any chart is known, then weights the two hover checklists by their item counts.
    /// </summary>
    public double ReadProgress
    {
        get
        {
            // Only squares that CAN carry a modifier count towards the read; the centre
            // of a 3x3 touches no figurine, so it is not work the user has to do.
            var readable = Layout.Rows * Layout.Cols - SquaresWithoutFigurines.Count;
            var total = _charts.Count + readable;
            if (total == 0) return 0;
            var done = _charts.Count(kv => HasDetail(kv.Value))
                       + _squareModifiers.Keys.Count(sq => !SquaresWithoutFigurines.Contains(sq));
            return Math.Min(1.0, done / (double)total);
        }
    }

    /// <summary>
    /// The tilesets in the panel, with how many charts open each.
    ///
    /// Surfaced because there is no published list of them -- poedb documents the chart
    /// bases and nothing about the areas they open -- so the only way to learn which
    /// tilesets exist, and which are worth preferring, is to look at what you are holding.
    /// </summary>
    public IReadOnlyList<(string Tileset, int Charts)> Tilesets =>
        _charts.Values
            .Where(c => !string.IsNullOrWhiteSpace(c.AreaName))
            .GroupBy(c => c.AreaName, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, g.Count()))
            .OrderByDescending(g => g.Item2)
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// What each square gets from the board, whatever the source.
    ///
    /// A square read straight off the Area Modifiers panel uses that and nothing else:
    /// the panel is the game's own total for that square, so adding figurine-derived
    /// modifiers on top would count the same effect twice. Figurines still cover the
    /// squares that have not been read.
    /// </summary>
    public IReadOnlyList<BoardModifier> BoardModifiers()
    {
        var result = new List<BoardModifier>();

        // FIGURINES are the authoritative border source: their tooltip is the game's
        // own per-figurine text, where the Area Modifiers panel is an aggregate VIEW
        // of the same figurines. A square read therefore only stands in for cells
        // whose figurines have not been read. The reverse once held, and one stale
        // panel read silently muted every figurine behind it -- the board showed old
        // mods while twelve fresh reads sat ignored.
        var figurineCovered = Layout.Figurines
            .Where(f => _figurines.TryGetValue(f.Index, out var t)
                        && !string.IsNullOrWhiteSpace(t))
            .SelectMany(f => f.Adjacent.Select(a => a.ToCell()))
            .ToHashSet();

        foreach (var (square, lines) in _squareModifiers)
        {
            var cell = CellOf(square);
            if (figurineCovered.Contains(cell)) continue;
            result.AddRange(lines.Select(l => new BoardModifier(l, [cell])));
        }

        result.AddRange(Layout.Bind(_figurines));
        return result;
    }

    /// <summary>
    /// What the board grants ONE square, from whichever source is authoritative for
    /// it. This is the view every square-shaped UI shows: the checklist row, the
    /// square tooltip, the read-state colour -- so a figurine read lights its square
    /// up exactly like a panel read used to.
    /// </summary>
    public IReadOnlyList<string> EffectiveSquareModifiers(int square)
    {
        var cell = CellOf(square);
        return [.. BoardModifiers()
            .Where(m => m.AffectedCells.Contains(cell))
            .Select(m => m.Description)];
    }

    /// <summary>Is the board's contribution to this square known -- a panel read, or
    /// at least one of its figurines read?</summary>
    public bool SquareBoardKnown(int square) =>
        _squareModifiers.ContainsKey(square)
        || Layout.Figurines.Any(f =>
            f.Adjacent.Any(a => a.ToCell() == CellOf(square))
            && _figurines.TryGetValue(f.Index, out var t)
            && !string.IsNullOrWhiteSpace(t));

    /// <summary>
    /// Score every chart against a profile and hand the result to the solver.
    ///
    /// The two values a chart carries are computed here rather than at parse time because
    /// they depend on the PROFILE: the same chart is worth a lot under a sulphur profile
    /// and nothing under a pack-size one, so scoring cannot be baked into the chart.
    /// </summary>
    public VoyageSolver.Solution Solve(
        VoyageProfile profile, TimeSpan? budget = null, CancellationToken ct = default)
    {
        // ScoreChart already folds in area level; adding it again here would weight it
        // twice as heavily as the profile asked for.
        //
        // The Adjacent Modifier splits by KIND. A per-monster payout ("Rare Monsters in
        // adjacent Areas drop # additional Chaos Orbs") is worth more beside a dense
        // neighbour, so its value rides in AdjacentPerMonsterValue and the solver
        // multiplies it by the neighbour it actually meets. A density gift ("Adjacent
        // Areas contain 2 additional packs...") keeps its flat rule value AND carries
        // AdjacentMonsterDensity, realised wherever a neighbouring square holds a
        // per-monster border payout.
        var syn = profile.MonsterPayoutSynergy;

        // What a full board of the BEST charts would total, at face value. Global
        // percentage mods ("20% increased ... in all Voyage Areas") multiply everything
        // the voyage produces, so they are scored as their fraction of this estimate --
        // the one number that turns "20%" from a token 40 points into the board-sized
        // value it actually is.
        var eligible = ByPanelIndex
            .Where(kv => !_excluded.Contains(kv.Key))
            .OrderBy(kv => kv.Key)
            .ToList();

        var boardEstimate = eligible
            .Select(kv => profile.ScoreChart(kv.Value))
            .OrderByDescending(v => v)
            .Take(Layout.Rows * Layout.Cols)
            .Where(v => v > 0)
            .Sum();

        var scored = eligible.Select(kv =>
        {
            var c = kv.Value;
            var adjacent = profile.ScoreAdjacent(c);
            var channel = c.AdjacentModifier is { } adj
                ? VoyageProfile.PayoutChannelOf(adj) : VoyageProfile.PayoutChannel.None;
            var payoutAdj = channel != VoyageProfile.PayoutChannel.None;
            var containerAdj = !payoutAdj && c.AdjacentModifier is { } gift
                               && VoyageProfile.IsContainerGift(gift);
            return c with
            {
                // Required is a value, not a constraint: the bonus makes any board that
                // seats the chart beat every board that does not, while the search still
                // chooses WHERE it goes -- which quietly subsumes the old "cheapest
                // square" rule, since the layout that loses least is the optimum.
                Value = profile.ScoreChart(c, boardEstimate)
                        + (_required.Contains(kv.Key) ? RequiredBonus : 0),
                AdjacentValue = payoutAdj || containerAdj ? 0 : adjacent,
                AdjacentPerMonsterValue = payoutAdj ? adjacent : 0,
                AdjacentPerQuantityValue = containerAdj ? adjacent : 0,
                AdjacentPayoutOnPopulation = channel == VoyageProfile.PayoutChannel.Population,
                MonsterDensity = syn * (c.MonsterPackSize / 100
                                        + AreaPopulation.RoomRareBonus(c)),
                PackDensity = syn * c.MonsterPackSize / 100,
                AdjacentMonsterDensity = c.AdjacentModifier is { } line
                    ? syn * VoyageProfile.MonsterDensityOf(line) : 0,
                AdjacentPackDensity = c.AdjacentModifier is { } packLine
                    ? syn * VoyageProfile.PackDensityOf(packLine) : 0,
                QuantityDensity = syn * c.ItemQuantity / 100,
                AdjacentQuantityDensity = c.AdjacentModifier is { } qtyLine
                    ? syn * VoyageProfile.QuantityDensityOf(qtyLine) : 0,
            };
        }).ToList();

        // Precompute what the BOARD gives each square, once.
        //
        // The scorer is called for every chart at every cell at every node -- millions of
        // times -- and the obvious version ran the profile's regexes over each board
        // modifier on every one of those calls. A square's board value depends only on
        // the square, so it is a lookup. Chart value is already computed once above, so
        // scoring stays a handful of arithmetic ops.
        //
        // Split flat vs PER-MONSTER, because on a full board a purely per-cell term is a
        // constant over every layout and steers nothing. Per-monster payouts multiply
        // with the tile's pack size instead, which is what lets a Divine Orb square pull
        // the monster-dense chart onto itself. See VoyageProfile.MonsterPayoutSynergy.
        var (flat, perRare, perPack, perQty) = BoardValueByCell(profile);

        // Per-cell constants for BOTH channels, precomputed so scoring stays
        // arithmetic: the density the border itself grants a square, and the payout
        // mass on the neighbouring squares -- which is what a chart's density gifts
        // are worth from this cell, channel by channel.
        var borderRare = new Dictionary<Cell, double>();
        var borderPack = new Dictionary<Cell, double>();
        var borderQty = new Dictionary<Cell, double>();
        foreach (var modifier in BoardModifiers())
        {
            var r = syn * VoyageProfile.MonsterDensityOf(modifier.Description);
            var k = syn * VoyageProfile.PackDensityOf(modifier.Description);
            var q = syn * VoyageProfile.QuantityDensityOf(modifier.Description);
            foreach (var cell in modifier.AffectedCells)
            {
                if (r != 0) borderRare[cell] = borderRare.GetValueOrDefault(cell) + r;
                if (k != 0) borderPack[cell] = borderPack.GetValueOrDefault(cell) + k;
                if (q != 0) borderQty[cell] = borderQty.GetValueOrDefault(cell) + q;
            }
        }
        var nbrRare = new Dictionary<Cell, double>();
        var nbrPack = new Dictionary<Cell, double>();
        var nbrQty = new Dictionary<Cell, double>();
        foreach (var (dict, into) in new[] { (perRare, nbrRare), (perPack, nbrPack), (perQty, nbrQty) })
            foreach (var (cell, worth) in dict)
                foreach (var side in Enum.GetValues<Side>())
                {
                    var n = VoyageBoard.Neighbour(cell, side);
                    if (n.Row >= 0 && n.Row < Layout.Rows && n.Col >= 0 && n.Col < Layout.Cols)
                        into[n] = into.GetValueOrDefault(n) + worth;
                }

        double score(Chart chart, Cell cell) =>
            chart.Value
            + flat.GetValueOrDefault(cell)
            + perRare.GetValueOrDefault(cell)
              * (1 + chart.MonsterDensity + borderRare.GetValueOrDefault(cell))
            + perPack.GetValueOrDefault(cell)
              * (1 + chart.PackDensity + borderPack.GetValueOrDefault(cell))
            + perQty.GetValueOrDefault(cell)
              * (1 + chart.QuantityDensity + borderQty.GetValueOrDefault(cell))
            + chart.AdjacentMonsterDensity * nbrRare.GetValueOrDefault(cell)
            + chart.AdjacentPackDensity * nbrPack.GetValueOrDefault(cell)
            + chart.AdjacentQuantityDensity * nbrQty.GetValueOrDefault(cell);

        var solution = new VoyageSolver(Layout.Rows, Layout.Cols, scored, score,
                                        strandedPenalty: profile.StrandedSquarePenalty,
                                        maxPlacements: profile.MaxCharts)
            .Solve(budget, ct);

        // The bonus buys inclusion, not points. Reporting it would claim ten million
        // where the board earned six hundred, so peel it back off for every required
        // chart the solver actually seated.
        var requiredIds = _required.Where(_charts.ContainsKey)
            .Select(i => _charts[i].Id).ToHashSet(StringComparer.Ordinal);
        var seated = solution.Placements.Count(p => requiredIds.Contains(p.Chart.Id));
        return seated == 0 ? solution
            : solution with { Value = solution.Value - seated * RequiredBonus };
    }

    /// <summary>
    /// Added to a Required chart's value. Larger than any real board can score, so
    /// "seats the chart" always outranks "does not" -- and identical across layouts
    /// that seat it, so it steers nothing else.
    /// </summary>
    private const double RequiredBonus = 1e7;

    /// <summary>
    /// What each square grants, split into flat value and per-monster payouts. The
    /// per-monster half multiplies with the pack size of whatever chart stands there;
    /// the flat half is position money regardless of the tile.
    /// </summary>
    private (Dictionary<Cell, double> Flat, Dictionary<Cell, double> PerRare,
             Dictionary<Cell, double> PerPack, Dictionary<Cell, double> PerQty)
        BoardValueByCell(VoyageProfile profile)
    {
        var flat = new Dictionary<Cell, double>();
        var perRare = new Dictionary<Cell, double>();
        var perPack = new Dictionary<Cell, double>();
        var perQty = new Dictionary<Cell, double>();
        foreach (var modifier in BoardModifiers())
        {
            var worth = profile.ScoreText([modifier.Description]) * profile.BoardModifierWeight;
            if (worth == 0) continue;
            var into = VoyageProfile.PayoutChannelOf(modifier.Description) switch
            {
                VoyageProfile.PayoutChannel.Rares => perRare,
                VoyageProfile.PayoutChannel.Population => perPack,
                _ when VoyageProfile.IsContainerGift(modifier.Description) => perQty,
                _ => flat,
            };
            foreach (var cell in modifier.AffectedCells)
                into[cell] = into.GetValueOrDefault(cell) + worth;
        }
        return (flat, perRare, perPack, perQty);
    }

    /// <summary>
    /// The order to run the squares in, for a solved board.
    ///
    /// A square's worth here is what you actually collect standing on it: the chart's own
    /// value, what the board grants that square, and what its neighbours push into it.
    /// That last part is why the route cannot be read off the plan -- a modest chart
    /// surrounded by generous ones can be worth more than a strong chart in a corner.
    /// </summary>
    public VoyageRoute Route(VoyageProfile profile, VoyageSolver.Solution solution)
    {
        if (solution.IsEmpty) return VoyageRoute.Empty;

        var board = new VoyageBoard(Layout.Rows, Layout.Cols);
        foreach (var placement in solution.Placements) board.Place(placement);

        var (flat, perRare, perPack, perQty) = BoardValueByCell(profile);
        var syn = profile.MonsterPayoutSynergy;

        // Golden Lanterns granted TO each square, from the board and from neighbours'
        // gifts. Collecting them buffs everything killed afterwards, so the router
        // wants them early -- see VoyageRoute.Plan's buff parameter.
        var lanterns = new Dictionary<Cell, double>();
        foreach (var modifier in BoardModifiers())
        {
            var count = GoldenLanternCount(modifier.Description);
            if (count == 0) continue;
            foreach (var cell in modifier.AffectedCells)
                lanterns[cell] = lanterns.GetValueOrDefault(cell) + count;
        }

        double LanternBuff(Placement placement)
        {
            var count = lanterns.GetValueOrDefault(placement.Cell);
            foreach (var side in Enum.GetValues<Side>())
                if (board.At(VoyageBoard.Neighbour(placement.Cell, side)) is { } n
                    && n.Chart.AdjacentModifier is { } gift)
                    count += GoldenLanternCount(gift);
            return count * AreaPopulation.QuantityPerGoldenLantern;
        }

        return VoyageRoute.Plan(board, placement =>
        {
            var rareDensity = syn * (placement.Chart.MonsterPackSize / 100
                                     + AreaPopulation.RoomRareBonus(placement.Chart));
            var packDensity = syn * placement.Chart.MonsterPackSize / 100;
            var qtyDensity = syn * placement.Chart.ItemQuantity / 100;
            var worth = profile.ScoreChart(placement.Chart)
                        + flat.GetValueOrDefault(placement.Cell)
                        + perRare.GetValueOrDefault(placement.Cell) * (1 + rareDensity)
                        + perPack.GetValueOrDefault(placement.Cell) * (1 + packDensity)
                        + perQty.GetValueOrDefault(placement.Cell) * (1 + qtyDensity);

            // What the neighbours push in. Received only -- what this chart gives THEM
            // is counted when they are visited, not here. A neighbour's per-monster
            // payout is received scaled by THIS tile's density on the payout's channel.
            foreach (var side in Enum.GetValues<Side>())
                if (board.At(VoyageBoard.Neighbour(placement.Cell, side)) is { } neighbour)
                {
                    var received = profile.ScoreAdjacent(neighbour.Chart);
                    if (neighbour.Chart.AdjacentModifier is { } adj)
                        received *= VoyageProfile.PayoutChannelOf(adj) switch
                        {
                            VoyageProfile.PayoutChannel.Rares => 1 + rareDensity,
                            VoyageProfile.PayoutChannel.Population => 1 + packDensity,
                            _ when VoyageProfile.IsContainerGift(adj) => 1 + qtyDensity,
                            _ => 1,
                        };
                    worth += received;
                }

            return worth;
        }, buff: LanternBuff);
    }

    /// <summary>
    /// What a planned square CONTAINS that is worth an icon: lanterns to grab and named
    /// bosses to expect. Computed from the board lines affecting the square plus the
    /// neighbours' adjacent gifts -- the same sources the scoring uses, so an icon never
    /// promises something the plan did not price.
    /// </summary>
    public sealed record SquareBadges(
        double GoldenLanterns,
        IReadOnlyList<string> Bosses,
        IReadOnlyList<string> Payouts,
        IReadOnlyList<string> Strongboxes,
        IReadOnlyList<string> Dangers);

    public IReadOnlyDictionary<int, SquareBadges> Badges(VoyageSolver.Solution solution)
    {
        var result = new Dictionary<int, SquareBadges>();
        if (solution.IsEmpty) return result;

        var board = new VoyageBoard(Layout.Rows, Layout.Cols);
        foreach (var p in solution.Placements) board.Place(p);

        var boardLines = new Dictionary<Cell, List<string>>();
        foreach (var modifier in BoardModifiers())
            foreach (var cell in modifier.AffectedCells)
                (boardLines.TryGetValue(cell, out var l) ? l : boardLines[cell] = [])
                    .Add(modifier.Description);

        foreach (var placement in solution.Placements)
        {
            var lines = new List<string>(boardLines.GetValueOrDefault(placement.Cell) ?? []);
            foreach (var side in Enum.GetValues<Side>())
                if (board.At(VoyageBoard.Neighbour(placement.Cell, side)) is { } n
                    && n.Chart.AdjacentModifier is { } gift)
                    lines.Add(gift);

            var lanterns = lines.Sum(GoldenLanternCount);
            var bosses = lines
                .Select(l => NamedBoss.Match(l))
                .Where(m => m.Success)
                .Select(m => m.Groups[1].Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Per-rare payouts landing on this square: the coin that says "farm HERE".
            var payouts = lines
                .Where(l => VoyageProfile.PayoutChannelOf(l) == VoyageProfile.PayoutChannel.Rares)
                .Select(PayoutName)
                .Where(n => n.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var boxes = lines
                .Select(l => StrongboxLine.Match(l))
                .Where(m => m.Success)
                .Select(m => m.Groups[1].Success ? m.Groups[1].Value + " Strongboxes" : "Strongboxes")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Danger travels with the CHART, not the square: the run-enders among its
            // monster mods, so you know where to fight carefully.
            var dangers = placement.Chart.Modifiers
                .Where(l => DangerLine.IsMatch(l))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (lanterns > 0 || bosses.Count > 0 || payouts.Count > 0
                || boxes.Count > 0 || dangers.Count > 0)
                result[VoyagePlan.SquareNumber(placement.Cell, Layout.Cols)] =
                    new SquareBadges(lanterns, bosses, payouts, boxes, dangers);
        }
        return result;
    }

    /// <summary>What a per-rare payout line pays, shortly: "Dead Man's Sulphur",
    /// "Divine Orbs", "Scarabs" -- for the coin icon's tooltip.</summary>
    private static string PayoutName(string line)
    {
        var m = PayoutWhat.Match(line);
        return m.Success ? m.Groups[1].Value.Trim() : "";
    }

    private static readonly System.Text.RegularExpressions.Regex PayoutWhat =
        new(@"drop (?:an |\d+ )?(?:additional )?(Dead Man's Sulphur|[A-Z][\w' ]+?s?)(?:\s*$)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly System.Text.RegularExpressions.Regex StrongboxLine =
        new(@"additional (Arcanist's|Diviner's|Operative's)? ?Strongbox(?:es)?",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>The run-enders: a death forfeits every square not yet reached.</summary>
    private static readonly System.Text.RegularExpressions.Regex DangerLine =
        // Max-res left this list in 3.29.1: it never functioned and no longer rolls.
        // Grasping Vines is its replacement wording ("same upsides").
        new(@"Grasping Vines|Monster Damage Penetrates",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>The named creatures the tables can put in a square. Weird names on
    /// purpose -- that is how you know they are bosses.</summary>
    private static readonly System.Text.RegularExpressions.Regex NamedBoss =
        new(@"contains?\s+(Captainsbane|Filthscrabble)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Golden Lanterns a modifier line grants, in any of the game's wordings.</summary>
    public static double GoldenLanternCount(string line)
    {
        var m = GoldenLanterns.Match(line);
        if (!m.Success) return 0;
        return m.Groups[1].Success ? double.Parse(m.Groups[1].Value) : 1;
    }

    private static readonly System.Text.RegularExpressions.Regex GoldenLanterns =
        new(@"(?:(\d+)|an)\s+additional Golden Lanterns?",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// The plan, as "square N &lt;- chart M" steps.
    ///
    /// Chart numbers are PANEL indices, not positions in some internal list: the whole
    /// point is that the user can read the number off the plan and find that square in
    /// the panel without counting.
    /// </summary>
    public IReadOnlyList<VoyagePlan.Step> Plan(VoyageSolver.Solution solution)
    {
        var indexOf = _charts.ToDictionary(kv => kv.Value.Id, kv => kv.Key, StringComparer.Ordinal);
        return solution.Placements
            .Select(p => new VoyagePlan.Step(
                VoyagePlan.SquareNumber(p.Cell, Layout.Cols),
                indexOf.GetValueOrDefault(p.Chart.Id, 0),
                p.Chart,
                p.Rotation))
            .OrderBy(s => s.Square)
            .ToList();
    }

    // ---- persistence -------------------------------------------------------------

    /// <summary>Panel indices the planner must ignore. The user's veto: an X on a chart
    /// says "not this one", whatever the rules think it is worth.</summary>
    public IReadOnlyCollection<int> Excluded => _excluded;

    public bool IsExcluded(int panelIndex) => _excluded.Contains(panelIndex);

    /// <summary>Panel indices the solver must place. The X's opposite: the rules price
    /// Soul Eater at zero, and the star says "take it anyway".</summary>
    public IReadOnlyCollection<int> Required => _required;

    public bool IsRequired(int panelIndex) => _required.Contains(panelIndex);

    /// <summary>
    /// Advance a chart's mark one step: none -> excluded -> required -> none.
    ///
    /// One cycle on one gesture because it is ONE question -- what may the planner do
    /// with this chart -- and the three answers are mutually exclusive. Two independent
    /// toggles would invite the meaningless fourth state, excluded-and-required.
    /// </summary>
    public ChartMark CycleMark(int panelIndex)
    {
        if (_excluded.Remove(panelIndex)) { _required.Add(panelIndex); return ChartMark.Required; }
        if (_required.Remove(panelIndex)) return ChartMark.None;
        _excluded.Add(panelIndex);
        return ChartMark.Excluded;
    }

    private static readonly Regex NotConsumed =
        new(@"chance (?:for Charts? )?to not be consumed",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Is a "Charts aren't consumed" chance in play for this board -- on any placed
    /// chart's own lines, or on any square modifier? When it is, spending all nine
    /// on completion would silently discard the refund the mod just paid for.
    /// </summary>
    public bool PreserveChanceInPlay(IEnumerable<int> placedCharts)
    {
        foreach (var index in placedCharts)
            if (_charts.TryGetValue(index, out var chart) && ChartLines(chart).Any(NotConsumed.IsMatch))
                return true;
        return _squareModifiers.Values.Any(lines => lines.Any(NotConsumed.IsMatch));
    }

    private static IEnumerable<string> ChartLines(Chart chart)
    {
        if (!string.IsNullOrWhiteSpace(chart.VoyageModifier)) yield return chart.VoyageModifier!;
        if (!string.IsNullOrWhiteSpace(chart.AdjacentModifier)) yield return chart.AdjacentModifier!;
        foreach (var line in chart.Modifiers) yield return line;
    }

    /// <summary>
    /// An in-game chart-stash search string that highlights exactly these charts: the
    /// most distinctive word of each name, deduplicated, quoted the way the game's
    /// search box expects ("armoured|pelagic|brine"). Charts read without a name
    /// contribute nothing -- the string covers what it can and stays honest about it.
    /// </summary>
    public string StashSearch(IEnumerable<int> placedCharts)
    {
        var tokens = placedCharts
            .Select(i => _charts.GetValueOrDefault(i))
            .Where(c => c is not null && !string.IsNullOrWhiteSpace(c.Name))
            .Select(c => c!.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length >= 4 && !w.Equals("Chart", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(w => w.Length)
                .FirstOrDefault())
            .Where(t => t is not null)
            .Select(t => t!.ToLowerInvariant())
            .Distinct()
            .ToList();
        return tokens.Count == 0 ? "" : $"\"{string.Join("|", tokens)}\"";
    }

    /// <summary>
    /// The voyage was run: spend its charts and clear the board.
    ///
    /// The placed charts are consumed by the game, so they leave the inventory; every
    /// other chart keeps its panel index, because those numbers point at physical panel
    /// positions and renumbering would break the pointing. The square modifiers and
    /// figurines go too -- the border rerolls each voyage, so yesterday's reads are not
    /// stale data but data about a board that no longer exists.
    /// </summary>
    /// <returns>How many charts were spent.</returns>
    public int CompleteVoyage(IEnumerable<int> placedCharts)
    {
        var spent = 0;
        foreach (var index in placedCharts)
            if (_charts.Remove(index)) { _excluded.Remove(index); _required.Remove(index); spent++; }

        _squareModifiers.Clear();
        _figurines.Clear();
        return spent;
    }

    /// <summary>Capture everything read so far, for writing to disk.</summary>
    public VoyageSessionState ToState(string? profile = null) => new()
    {
        Version = VoyageSessionState.CurrentVersion,
        Rows = Layout.Rows,
        Cols = Layout.Cols,
        Profile = profile,
        Excluded = _excluded.Order().ToList(),
        Required = _required.Order().ToList(),
        Charts = _charts.OrderBy(kv => kv.Key).Select(kv => new VoyageSessionState.ChartState
        {
            PanelIndex = kv.Key,
            Name = kv.Value.Name,
            Shape = kv.Value.Shape,
            AreaLevel = kv.Value.AreaLevel,
            AreaName = kv.Value.AreaName,
            VoyageModifier = kv.Value.VoyageModifier,
            AdjacentModifier = kv.Value.AdjacentModifier,
            RequiresLevel = kv.Value.RequiresLevel,
            ItemQuantity = kv.Value.ItemQuantity,
            ItemRarity = kv.Value.ItemRarity,
            MonsterPackSize = kv.Value.MonsterPackSize,
            GoldFound = kv.Value.GoldFound,
            Sulphur = kv.Value.Sulphur,
            Modifiers = kv.Value.Modifiers.ToList(),
        }).ToList(),
        SquareModifiers = _squareModifiers.ToDictionary(
            kv => kv.Key.ToString(), kv => kv.Value.ToList()),
        Figurines = _figurines.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
    };

    /// <summary>
    /// Rebuild a session from a saved one.
    ///
    /// The board layout comes from the SAVED state, not the current default: a session
    /// read against a 3x3 board has square numbers that only mean anything on a 3x3
    /// board, and quietly reinterpreting them on a different size would move every
    /// modifier to the wrong place.
    /// </summary>
    public static VoyageSession FromState(VoyageSessionState state)
    {
        var session = new VoyageSession(BoardLayout.Default(state.Rows, state.Cols));

        foreach (var c in state.Charts)
        {
            // Refined on the way in: a session saved by an older parser has its scope
            // undetected and chrome in its modifier list, and re-copying every chart to
            // pick up a parser fix is not a reasonable thing to ask.
            session._charts[c.PanelIndex] = ChartText.Refine(new Chart(
                $"panel-{c.PanelIndex}", c.Name, c.Shape, c.AreaLevel, c.Modifiers.ToList())
            {
                AreaName = c.AreaName,
                VoyageModifier = c.VoyageModifier,
                AdjacentModifier = c.AdjacentModifier,
                RequiresLevel = c.RequiresLevel,
                ItemQuantity = c.ItemQuantity,
                ItemRarity = c.ItemRarity,
                MonsterPackSize = c.MonsterPackSize,
                GoldFound = c.GoldFound,
                Sulphur = c.Sulphur,
            });
        }

        foreach (var (key, lines) in state.SquareModifiers)
            if (int.TryParse(key, out var square))
                // Restitched on load: sessions saved before the joiner learned a wording
                // keep their wrapped fragments forever otherwise, silently scoring zero.
                session._squareModifiers[square] =
                    AreaModifierPanel.Restitch(lines.ToList()).ToList();

        foreach (var (key, text) in state.Figurines)
            if (int.TryParse(key, out var index)) session._figurines[index] = text;

        session._excluded.UnionWith(state.Excluded);
        session._required.UnionWith(state.Required);
        session._required.ExceptWith(session._excluded);   // a hand-edited file cannot make both true
        return session;
    }

    /// <summary>Write to disk. Cheap enough to call after every capture.</summary>
    public void Save(string? path = null, string? profile = null)
    {
        var state = ToState(profile);
        state.SavedAt = DateTimeOffset.Now;
        state.Save(path);
    }

    /// <summary>Restore the last session, or a fresh one when there is nothing to restore.</summary>
    public static (VoyageSession Session, VoyageSessionState? State) Restore(string? path = null)
    {
        var state = VoyageSessionState.Load(path);
        return state is null ? (new VoyageSession(), null) : (FromState(state), state);
    }
}
