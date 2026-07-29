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
public sealed class VoyageSession
{
    private readonly Dictionary<int, Chart> _charts = new();
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
                             .Where(sq => !_squareModifiers.ContainsKey(sq)
                                          && !unreachable.Contains(sq))
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

        foreach (var (square, lines) in _squareModifiers)
        {
            var cell = CellOf(square);
            result.AddRange(lines.Select(l => new BoardModifier(l, [cell])));
        }

        var covered = _squareModifiers.Keys.Select(CellOf).ToHashSet();
        foreach (var modifier in Layout.Bind(_figurines))
        {
            var cells = modifier.AffectedCells.Where(c => !covered.Contains(c)).ToList();
            if (cells.Count > 0) result.Add(modifier with { AffectedCells = cells });
        }

        return result;
    }

    /// <summary>
    /// Score every chart against a profile and hand the result to the solver.
    ///
    /// The two values a chart carries are computed here rather than at parse time because
    /// they depend on the PROFILE: the same chart is worth a lot under a sulphur profile
    /// and nothing under a pack-size one, so scoring cannot be baked into the chart.
    /// </summary>
    /// <param name="pinChart">
    /// Panel index of a chart to force onto the board, at the square with least to offer.
    ///
    /// For modifiers worth taking that no profile can price -- Soul Eater grants player
    /// power rather than loot, so it scores zero and the planner drops the chart entirely.
    /// Since a voyage-wide modifier pays the same from anywhere, the square it occupies is
    /// pure opportunity cost, and the cheapest square is the right one to spend.
    /// </param>
    public VoyageSolver.Solution Solve(
        VoyageProfile profile, TimeSpan? budget = null, CancellationToken ct = default,
        int? pinChart = null)
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
        var boardEstimate = Charts
            .Select(c => profile.ScoreChart(c))
            .OrderByDescending(v => v)
            .Take(Layout.Rows * Layout.Cols)
            .Where(v => v > 0)
            .Sum();

        var scored = Charts.Select(c =>
        {
            var adjacent = profile.ScoreAdjacent(c);
            var payoutAdj = c.AdjacentModifier is { } adj && VoyageProfile.IsPerMonsterPayout(adj);
            return c with
            {
                Value = profile.ScoreChart(c, boardEstimate),
                AdjacentValue = payoutAdj ? 0 : adjacent,
                AdjacentPerMonsterValue = payoutAdj ? adjacent : 0,
                MonsterDensity = syn * c.MonsterPackSize / 100,
                AdjacentMonsterDensity = c.AdjacentModifier is { } line
                    ? syn * VoyageProfile.MonsterDensityOf(line) : 0,
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
        var (flat, perMonster) = BoardValueByCell(profile);

        // Two more per-cell constants, precomputed so scoring stays arithmetic:
        // the density the BORDER itself grants a square (a figurine adding packs sits
        // under the same payout it multiplies), and the payout mass on the NEIGHBOURING
        // squares -- which is what a chart's own density gift is worth from this cell.
        var borderDensity = new Dictionary<Cell, double>();
        foreach (var modifier in BoardModifiers())
        {
            var d = syn * VoyageProfile.MonsterDensityOf(modifier.Description);
            if (d == 0) continue;
            foreach (var cell in modifier.AffectedCells)
                borderDensity[cell] = borderDensity.GetValueOrDefault(cell) + d;
        }
        var neighbourPayout = new Dictionary<Cell, double>();
        foreach (var (cell, worth) in perMonster)
            foreach (var side in Enum.GetValues<Side>())
            {
                var n = VoyageBoard.Neighbour(cell, side);
                if (n.Row >= 0 && n.Row < Layout.Rows && n.Col >= 0 && n.Col < Layout.Cols)
                    neighbourPayout[n] = neighbourPayout.GetValueOrDefault(n) + worth;
            }

        double score(Chart chart, Cell cell) =>
            chart.Value
            + flat.GetValueOrDefault(cell)
            + perMonster.GetValueOrDefault(cell)
              * (1 + chart.MonsterDensity + borderDensity.GetValueOrDefault(cell))
            + chart.AdjacentMonsterDensity * neighbourPayout.GetValueOrDefault(cell);

        (string, Cell)? pin = null;
        if (pinChart is { } index && _charts.TryGetValue(index, out var pinned)
            && CheapestCellFor(pinned, CombinedBoardValue(profile, pinned)) is { } where)
            pin = (pinned.Id, where);

        return new VoyageSolver(Layout.Rows, Layout.Cols, scored, score,
                                strandedPenalty: profile.StrandedSquarePenalty,
                                maxPlacements: profile.MaxCharts,
                                pin: pin)
            .Solve(budget, ct);
    }

    /// <summary>
    /// The square with least to offer that this chart can actually occupy.
    ///
    /// Two things make a square valuable and both are known before the search: what the
    /// board grants it, and how many neighbours it has to trade adjacency with. A corner
    /// touches two squares, an edge three, the centre four -- so among squares the board
    /// treats alike, the corner is the cheapest thing to spend.
    ///
    /// Shape is why this is a search rather than a minimum. Every open edge has to face
    /// another square, so a Crossing cannot sit in a corner at any rotation and a chart
    /// pinned there would make the whole board unsolvable. Cells are therefore taken
    /// cheapest-first and the first one the chart FITS wins -- a necessary condition, not
    /// a sufficient one, but it rules out exactly the case that has no answer.
    /// </summary>
    /// <summary>
    /// What each square grants, split into flat value and per-monster payouts. The
    /// per-monster half multiplies with the pack size of whatever chart stands there;
    /// the flat half is position money regardless of the tile.
    /// </summary>
    private (Dictionary<Cell, double> Flat, Dictionary<Cell, double> PerMonster)
        BoardValueByCell(VoyageProfile profile)
    {
        var flat = new Dictionary<Cell, double>();
        var perMonster = new Dictionary<Cell, double>();
        foreach (var modifier in BoardModifiers())
        {
            var worth = profile.ScoreText([modifier.Description]) * profile.BoardModifierWeight;
            if (worth == 0) continue;
            var into = VoyageProfile.IsPerMonsterPayout(modifier.Description) ? perMonster : flat;
            foreach (var cell in modifier.AffectedCells)
                into[cell] = into.GetValueOrDefault(cell) + worth;
        }
        return (flat, perMonster);
    }

    /// <summary>The two halves combined for ONE known chart, for the pin's cell choice.</summary>
    private Dictionary<Cell, double> CombinedBoardValue(VoyageProfile profile, Chart chart)
    {
        var (flat, perMonster) = BoardValueByCell(profile);
        var factor = 1 + profile.MonsterPayoutSynergy * chart.MonsterPackSize / 100;
        var combined = new Dictionary<Cell, double>(flat);
        foreach (var (cell, worth) in perMonster)
            combined[cell] = combined.GetValueOrDefault(cell) + worth * factor;
        return combined;
    }

    private Cell? CheapestCellFor(Chart chart, IReadOnlyDictionary<Cell, double> boardValue)
    {
        var board = new VoyageBoard(Layout.Rows, Layout.Cols);

        bool Fits(Cell cell) =>
            ChartFace.DistinctRotations(chart.Shape).Any(rotation =>
            {
                var face = new ChartFace(chart.Shape, rotation);
                return Enum.GetValues<Side>().All(side =>
                    !face.IsOpen(side) || board.InBounds(VoyageBoard.Neighbour(cell, side)));
            });

        int Neighbours(Cell cell) =>
            Enum.GetValues<Side>().Count(s => board.InBounds(VoyageBoard.Neighbour(cell, s)));

        return AllCells()
            .Where(Fits)
            .OrderBy(c => boardValue.GetValueOrDefault(c))
            .ThenBy(Neighbours)
            .ThenBy(c => VoyagePlan.SquareNumber(c, Layout.Cols))
            .Cast<Cell?>()
            .FirstOrDefault();
    }

    private IEnumerable<Cell> AllCells()
    {
        for (var r = 0; r < Layout.Rows; r++)
            for (var c = 0; c < Layout.Cols; c++)
                yield return new Cell(r, c);
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

        var (flat, perMonster) = BoardValueByCell(profile);
        var syn = profile.MonsterPayoutSynergy;

        return VoyageRoute.Plan(board, placement =>
        {
            var density = syn * placement.Chart.MonsterPackSize / 100;
            var worth = profile.ScoreChart(placement.Chart)
                        + flat.GetValueOrDefault(placement.Cell)
                        + perMonster.GetValueOrDefault(placement.Cell) * (1 + density);

            // What the neighbours push in. Received only -- what this chart gives THEM is
            // counted when they are visited, not here. A neighbour's per-monster payout
            // is received scaled by THIS tile's density, same as the solver priced it.
            foreach (var side in Enum.GetValues<Side>())
                if (board.At(VoyageBoard.Neighbour(placement.Cell, side)) is { } neighbour)
                {
                    var received = profile.ScoreAdjacent(neighbour.Chart);
                    if (neighbour.Chart.AdjacentModifier is { } adj
                        && VoyageProfile.IsPerMonsterPayout(adj))
                        received *= 1 + density;
                    worth += received;
                }

            return worth;
        });
    }

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
            if (_charts.Remove(index)) spent++;

        _squareModifiers.Clear();
        _figurines.Clear();
        return spent;
    }

    /// <summary>Capture everything read so far, for writing to disk.</summary>
    public VoyageSessionState ToState(string? profile = null, bool useSoulEater = false) => new()
    {
        Version = VoyageSessionState.CurrentVersion,
        UseSoulEater = useSoulEater,
        Rows = Layout.Rows,
        Cols = Layout.Cols,
        Profile = profile,
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
            if (int.TryParse(key, out var square)) session._squareModifiers[square] = lines.ToList();

        foreach (var (key, text) in state.Figurines)
            if (int.TryParse(key, out var index)) session._figurines[index] = text;

        return session;
    }

    /// <summary>Write to disk. Cheap enough to call after every capture.</summary>
    public void Save(string? path = null, string? profile = null, bool useSoulEater = false)
    {
        var state = ToState(profile, useSoulEater);
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
