namespace PoeMarketWatch.Core.Voyage;

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
    /// the same modifier. Passing no lines clears the square, which is what an accidental
    /// capture of the empty panel should do.
    /// </summary>
    public void ApplySquareModifiers(int square, IReadOnlyList<string>? lines)
    {
        if (lines is null || lines.Count == 0) _squareModifiers.Remove(square);
        else _squareModifiers[square] = lines.ToList();
    }

    /// <summary>Squares whose Area Modifiers have not been read yet.</summary>
    public IReadOnlyList<int> SquaresAwaitingModifiers =>
        Enumerable.Range(1, Layout.Rows * Layout.Cols)
                  .Where(sq => !_squareModifiers.ContainsKey(sq))
                  .ToList();

    /// <summary>The cell a 1-based square number refers to.</summary>
    public Cell CellOf(int square) =>
        new((square - 1) / Layout.Cols, (square - 1) % Layout.Cols);

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
            var squares = Layout.Rows * Layout.Cols;
            var total = _charts.Count + squares;
            if (total == 0) return 0;
            var done = _charts.Count(kv => HasDetail(kv.Value)) + _squareModifiers.Count;
            return done / (double)total;
        }
    }

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
    public VoyageSolver.Solution Solve(
        VoyageProfile profile, TimeSpan? budget = null, CancellationToken ct = default)
    {
        // ScoreChart already folds in area level; adding it again here would weight it
        // twice as heavily as the profile asked for.
        var scored = Charts.Select(c => c with
        {
            Value = profile.ScoreChart(c),
            AdjacentValue = profile.ScoreAdjacent(c),
        }).ToList();

        var modifiers = BoardModifiers();
        var score = VoyageSolver.ScoreWith(
            modifiers, (m, _) => profile.ScoreText([m.Description]) * profile.BoardModifierWeight);

        return new VoyageSolver(Layout.Rows, Layout.Cols, scored, score,
                                strandedPenalty: profile.StrandedSquarePenalty)
            .Solve(budget, ct);
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
}
