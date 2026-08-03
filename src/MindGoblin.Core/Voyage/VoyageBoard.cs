namespace MindGoblin.Core.Voyage;

/// <summary>
/// One chart, as the Voyage planner shows it on hover.
///
/// Note the two special modifier lines, which behave completely differently:
///
///   Voyage Modifier:   "8% increased Quantity of Items found in all Voyage Areas"
///        Global. Applies wherever the chart sits, so position is irrelevant to it.
///
///   Adjacent Modifier: "Adjacent Areas contain 2 additional Strongboxes"
///        Applies to the NEIGHBOURS of wherever this chart is placed. This is what makes
///        the objective non-separable: a chart's worth depends on what is next to it, so
///        a Strongbox chart in the centre (4 neighbours) is worth twice a corner (2).
/// </summary>
public sealed record Chart(
    string Id,
    string Name,
    ChartShape Shape,
    int AreaLevel,
    IReadOnlyList<string> Modifiers)
{
    /// <summary>Area name shown under the title, e.g. "Seafloor Ridges".</summary>
    public string AreaName { get; init; } = "";

    /// <summary>Applies to every voyage area regardless of placement. Null when absent.</summary>
    public string? VoyageModifier { get; init; }

    /// <summary>Applies to the neighbours of wherever this is placed. Null when absent.</summary>
    public string? AdjacentModifier { get; init; }

    public int RequiresLevel { get; init; }
    public double ItemQuantity { get; init; }
    public double ItemRarity { get; init; }
    public double MonsterPackSize { get; init; }
    public double GoldFound { get; init; }
    public double Sulphur { get; init; }

    /// <summary>Own value at a cell, before any neighbour effects.</summary>
    public double Value { get; init; }

    /// <summary>The FLAT part of what this chart's Adjacent Modifier is worth to ONE
    /// neighbour. Per-monster payouts live in <see cref="AdjacentPerMonsterValue"/>.</summary>
    public double AdjacentValue { get; init; }

    /// <summary>
    /// The per-monster part of the Adjacent Modifier's worth to one neighbour: "Rare
    /// Monsters in adjacent Areas drop # additional Chaos Orbs" pays per rare, so what
    /// it is worth depends on how monster-dense the NEIGHBOUR is. The solver multiplies
    /// this by (1 + the neighbour's <see cref="MonsterDensity"/>) when the pair meets.
    /// </summary>
    public double AdjacentPerMonsterValue { get; init; }

    /// <summary>
    /// How monster-dense this tile is, as a fraction (0.5 = half again the monsters),
    /// pre-scaled by the profile's synergy knob. Derived from the tile's own stats;
    /// multiplies every per-monster payout that lands on it.
    /// </summary>
    public double MonsterDensity { get; init; }

    /// <summary>
    /// How much RARE density this chart's Adjacent Modifier GIVES each neighbour
    /// ("Adjacent Areas contain 2 additional packs..."), pre-scaled by the synergy knob.
    /// Realised wherever a neighbouring square carries a per-rare border payout.
    /// </summary>
    public double AdjacentMonsterDensity { get; init; }

    /// <summary>
    /// The POPULATION channel: how pack-dense this tile is (pack size), pre-scaled.
    /// Whole-population payouts like "at least Magic" multiply with this, NOT with
    /// <see cref="MonsterDensity"/> -- a rare-dense room gains nothing from an upgrade
    /// its rares already exceed.
    /// </summary>
    public double PackDensity { get; init; }

    /// <summary>Population density this chart's Adjacent Modifier gives each neighbour
    /// (added packs, pack size), pre-scaled. Pays beside population-payout squares.</summary>
    public double AdjacentPackDensity { get; init; }

    /// <summary>
    /// The CONTAINER-gift part of the Adjacent Modifier's worth to one neighbour:
    /// "Adjacent Areas contain # additional Messages in a Bottle" puts openables INTO
    /// the neighbour, so what they pay scales with how much quantity the RECEIVING
    /// tile has. The solver multiplies this by (1 + the neighbour's
    /// <see cref="QuantityDensity"/>) when the pair meets.
    /// </summary>
    public double AdjacentPerQuantityValue { get; init; }

    /// <summary>The tile's Item Quantity as a fraction (0.4 = +40%), pre-scaled by the
    /// synergy knob. Multiplies every container payout that lands on it.</summary>
    public double QuantityDensity { get; init; }

    /// <summary>Quantity this chart's Adjacent Modifier GIVES each neighbour
    /// ("#% increased Quantity ... in adjacent Areas"), pre-scaled. Realised wherever
    /// a neighbouring square carries a container payout.</summary>
    public double AdjacentQuantityDensity { get; init; }

    /// <summary>
    /// Value of a drop CONVERSION this chart's Adjacent Modifier gives its neighbours
    /// ("Basic Currency items dropped by Monsters ... will instead drop as Stacked
    /// Decks"). Two-factor, alone among the channels: what it converts is monster
    /// drops, so the count rides on the neighbour's MONSTERS and on the Quantity that
    /// multiplies what they drop. Scored on quantity alone it ignored pack size
    /// entirely -- a +150% pack tile paid a conversion exactly what a blank one did,
    /// so the solver had no reason to feed it monsters and bought barrels instead,
    /// whose loot no monster drops and which the conversion therefore cannot touch.
    /// </summary>
    public double AdjacentConversionValue { get; init; }

    /// <summary>Which channel this chart's per-monster ADJACENT payout scales with:
    /// true for population payouts (x the neighbour's pack density), false for
    /// per-rare payouts (x the neighbour's rare density).</summary>
    public bool AdjacentPayoutOnPopulation { get; init; }

    /// <summary>
    /// This tile's EXPLICIT-modifier value: its rolled affixes and the stats that
    /// aggregate them, priced by the profile. What an amplifier multiplies, and the
    /// reason an amplifier is worth everything beside a fat chart and nothing beside a
    /// blank one. Excludes the implicit -- see <see cref="VoyageProfile.ExplicitValue"/>.
    /// </summary>
    public double ExplicitValue { get; init; }

    /// <summary>
    /// The AMPLIFIER part of this chart's Adjacent Modifier, as a fraction: "Adjacent
    /// Areas have 60% increased explicit modifier magnitudes" is 0.6, pre-scaled by the
    /// synergy knob. Multiplied by the NEIGHBOUR's <see cref="ExplicitValue"/> when the
    /// pair meets -- it pays a share of what it lands beside, never a sum of its own.
    /// </summary>
    public double AdjacentMagnitudeValue { get; init; }

    public bool HasAdjacentModifier => AdjacentValue != 0 || !string.IsNullOrEmpty(AdjacentModifier);

    /// <summary>
    /// The headline stats rendered back as tooltip lines.
    ///
    /// Parsing pulls these into typed fields, which then puts them out of reach of the
    /// rules -- and rules are regexes over text. Rather than bolt a second, stat-shaped
    /// scoring system beside the regex one, the stats are offered back in the exact
    /// wording they have in game, so a "sulphur" profile is written the way the user
    /// reads the tooltip and one mechanism covers everything.
    /// </summary>
    public IEnumerable<string> StatLines()
    {
        if (ItemQuantity != 0) yield return $"Item Quantity: +{ItemQuantity:0.##}%";
        if (ItemRarity != 0) yield return $"Item Rarity: +{ItemRarity:0.##}%";
        if (MonsterPackSize != 0) yield return $"Monster Pack Size: +{MonsterPackSize:0.##}%";
        if (GoldFound != 0) yield return $"Gold Found: +{GoldFound:0.##}%";
        if (Sulphur != 0) yield return $"Dead Man's Sulphur: +{Sulphur:0.##}";
    }

    /// <summary>
    /// Everything scored into this chart's OWN value.
    ///
    /// Excludes <see cref="AdjacentModifier"/> on purpose: that value is realised through
    /// the neighbours it buffs and is counted there, via <see cref="AdjacentValue"/>.
    /// Counting it here as well would pay for it twice.
    /// </summary>
    public IEnumerable<string> OwnLines()
    {
        // The tileset, as a line a rule can match: "Area: Anchorfield".
        //
        // Which tileset a chart opens is a real reward difference -- Anchorfield is thick
        // with Sunken Loot chests, other sets are not -- and it is stated on every chart.
        // Exposing it here means preferring a tileset needs no new machinery, just a rule.
        if (!string.IsNullOrWhiteSpace(AreaName)) yield return "Area: " + AreaName;

        foreach (var line in StatLines()) yield return line;
        if (!string.IsNullOrEmpty(VoyageModifier)) yield return VoyageModifier!;
        foreach (var m in Modifiers) yield return m;
    }

    public override string ToString() => $"{Name} ({Shape}, L{AreaLevel})";
}

public readonly record struct Cell(int Row, int Col);

public sealed record Placement(Chart Chart, Cell Cell, int Rotation)
{
    public ChartFace Face => new(Chart.Shape, Rotation);
}

/// <summary>
/// The Voyage board: a grid of cells, some filled with rotated charts.
///
/// The rule, as observed in game: every path must connect to another path, or to the
/// border. So an open edge is satisfied by either
///   * pointing off the grid entirely (the border), or
///   * a neighbouring placed chart with an open edge facing back.
///
/// It is NOT satisfied by pointing at an empty cell, and a closed edge facing a
/// neighbour's open edge is equally invalid -- connections are mutual, which is what
/// makes this an edge-matching problem rather than a simple reachability one.
/// </summary>
public sealed class VoyageBoard
{
    private readonly Placement?[,] _cells;

    public VoyageBoard(int rows, int cols)
    {
        if (rows <= 0 || cols <= 0) throw new ArgumentOutOfRangeException(nameof(rows));
        Rows = rows;
        Cols = cols;
        _cells = new Placement?[rows, cols];
    }

    public int Rows { get; }
    public int Cols { get; }

    public bool InBounds(Cell c) => c.Row >= 0 && c.Row < Rows && c.Col >= 0 && c.Col < Cols;

    public Placement? At(Cell c) => InBounds(c) ? _cells[c.Row, c.Col] : null;

    public IEnumerable<Placement> Placements
    {
        get
        {
            for (var r = 0; r < Rows; r++)
                for (var c = 0; c < Cols; c++)
                    if (_cells[r, c] is { } p) yield return p;
        }
    }

    /// <summary>Maintained by Place/Clear rather than counted: the solver reads this at
    /// every node of the search, and enumerating the grid there added a full board scan
    /// to the innermost loop.</summary>
    public int FilledCount { get; private set; }

    public void Place(Placement placement)
    {
        if (!InBounds(placement.Cell))
            throw new ArgumentOutOfRangeException(nameof(placement), "cell is off the board");
        if (_cells[placement.Cell.Row, placement.Cell.Col] is null) FilledCount++;
        _cells[placement.Cell.Row, placement.Cell.Col] = placement;
    }

    public void Clear(Cell c)
    {
        if (!InBounds(c) || _cells[c.Row, c.Col] is null) return;
        _cells[c.Row, c.Col] = null;
        FilledCount--;
    }

    public static Cell Neighbour(Cell c, Side side)
    {
        var (dr, dc) = side.Delta();
        return new Cell(c.Row + dr, c.Col + dc);
    }

    /// <summary>
    /// Why a particular edge is unsatisfied, for reporting rather than just a bool --
    /// "the board is invalid" is useless feedback when 60 charts are in play.
    /// </summary>
    public sealed record Violation(Cell Cell, Side Side, string Reason)
    {
        public override string ToString() => $"({Cell.Row},{Cell.Col}) {Side}: {Reason}";
    }

    /// <summary>Every unsatisfied edge on the board. Empty means a legal layout.</summary>
    public IReadOnlyList<Violation> Validate()
    {
        var problems = new List<Violation>();

        foreach (var placement in Placements)
        {
            foreach (var side in Enum.GetValues<Side>())
            {
                var open = placement.Face.IsOpen(side);
                var neighbourCell = Neighbour(placement.Cell, side);

                // Off the grid: the border satisfies an open path, and a closed edge
                // against the border is simply a wall.
                if (!InBounds(neighbourCell)) continue;

                var neighbour = At(neighbourCell);
                if (neighbour is null)
                {
                    if (open)
                        problems.Add(new Violation(placement.Cell, side,
                            "path leads into an empty cell"));
                    continue;
                }

                var facing = neighbour.Face.IsOpen(side.Opposite());
                if (open && !facing)
                    problems.Add(new Violation(placement.Cell, side,
                        $"path meets the closed edge of {neighbour.Chart.Name}"));
                else if (!open && facing)
                    problems.Add(new Violation(placement.Cell, side,
                        $"{neighbour.Chart.Name} leads a path into a closed edge"));
            }
        }
        return problems;
    }

    /// <summary>
    /// Is every connection satisfied? Equivalent to <see cref="Validate"/> being empty.
    ///
    /// Written out rather than calling Validate because it runs at every complete board
    /// in the search -- millions of them -- and Validate allocates a list and a Violation
    /// object per problem it finds. This allocates nothing and stops at the first fault.
    /// </summary>
    public bool IsValid()
    {
        for (var r = 0; r < Rows; r++)
        {
            for (var c = 0; c < Cols; c++)
            {
                if (_cells[r, c] is not { } placement) continue;
                var face = placement.Face;

                foreach (var side in Sides)
                {
                    var open = face.IsOpen(side);
                    var (dr, dc) = side.Delta();
                    var nr = r + dr;
                    var nc = c + dc;

                    // Off the grid: the border satisfies an open path and walls off a
                    // closed one.
                    if (nr < 0 || nr >= Rows || nc < 0 || nc >= Cols) continue;

                    var neighbour = _cells[nr, nc];
                    if (neighbour is null)
                    {
                        if (open) return false;      // a path into an empty cell
                        continue;
                    }
                    if (open != neighbour.Face.IsOpen(side.Opposite())) return false;
                }
            }
        }
        return true;
    }

    /// <summary>Cached: Enum.GetValues allocates a fresh array on every call.</summary>
    private static readonly Side[] Sides = Enum.GetValues<Side>();

    /// <summary>
    /// Groups of squares joined by paths that actually meet, largest first.
    ///
    /// Edge matching alone permits a square that is legal and useless: an End pointing at
    /// the border, closed on its other three sides, satisfies every rule while touching
    /// nothing. It is a dead corner -- whatever chart goes there is cut off from the rest
    /// of the route.
    ///
    /// This is reported and weighted rather than forbidden. Connecting the whole board is
    /// clearly preferable, but a stranded square is not illegal, and there are boards
    /// where one dead corner buys a far better nine charts. That trade is the user's to
    /// make, so the penalty is a profile setting and the UI names the stranded squares.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<Cell>> Components()
    {
        var seen = new HashSet<Cell>();
        var groups = new List<IReadOnlyList<Cell>>();

        foreach (var start in Placements.Select(p => p.Cell))
        {
            if (!seen.Add(start)) continue;

            var group = new List<Cell> { start };
            var queue = new Queue<Cell>();
            queue.Enqueue(start);

            while (queue.Count > 0)
            {
                var cell = queue.Dequeue();
                if (At(cell) is not { } here) continue;

                foreach (var side in Sides)
                {
                    if (!here.Face.IsOpen(side)) continue;
                    var next = Neighbour(cell, side);
                    // Mutual: a path only joins where both sides open onto each other.
                    if (At(next) is not { } there || !there.Face.IsOpen(side.Opposite())) continue;
                    if (!seen.Add(next)) continue;
                    group.Add(next);
                    queue.Enqueue(next);
                }
            }
            groups.Add(group);
        }

        return groups.OrderByDescending(g => g.Count).ToList();
    }

    /// <summary>Squares outside the largest connected group — the dead corners.</summary>
    public IReadOnlyList<Cell> StrandedCells()
    {
        var components = Components();
        return components.Count <= 1
            ? []
            : components.Skip(1).SelectMany(g => g).ToList();
    }

    /// <summary>Sum of the values of everything placed.</summary>
    public double TotalValue() => Placements.Sum(p => p.Chart.Value);

    /// <summary>
    /// Can this face legally sit here, given what is already placed?
    ///
    /// Deliberately tolerant of empty neighbours: during a search most of the board is
    /// still empty, and rejecting an open edge that points at a not-yet-filled cell would
    /// prune every partial solution. Emptiness is only fatal in <see cref="Validate"/>,
    /// once the layout is final.
    /// </summary>
    public bool CanPlace(Cell cell, ChartFace face)
    {
        if (!InBounds(cell) || At(cell) is not null) return false;

        foreach (var side in Enum.GetValues<Side>())
        {
            var open = face.IsOpen(side);
            var neighbourCell = Neighbour(cell, side);
            if (!InBounds(neighbourCell)) continue;   // border satisfies either state

            if (At(neighbourCell) is not { } neighbour) continue;  // undecided, allow

            if (open != neighbour.Face.IsOpen(side.Opposite())) return false;
        }
        return true;
    }

    public string Render()
    {
        var sb = new System.Text.StringBuilder();
        for (var r = 0; r < Rows; r++)
        {
            for (var c = 0; c < Cols; c++)
                sb.Append(_cells[r, c] is { } p ? Glyph(p.Face) : '.');
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static char Glyph(ChartFace face)
    {
        var n = face.IsOpen(Side.North);
        var e = face.IsOpen(Side.East);
        var s = face.IsOpen(Side.South);
        var w = face.IsOpen(Side.West);
        return (n, e, s, w) switch
        {
            (true, true, true, true) => '┼',
            (true, true, true, false) => '├',
            (true, true, false, true) => '┴',
            (true, false, true, true) => '┤',
            (false, true, true, true) => '┬',
            (true, false, true, false) => '│',
            (false, true, false, true) => '─',
            (true, true, false, false) => '└',
            (false, true, true, false) => '┌',
            (false, false, true, true) => '┐',
            (true, false, false, true) => '┘',
            (true, false, false, false) => '╵',
            (false, true, false, false) => '╶',
            (false, false, true, false) => '╷',
            (false, false, false, true) => '╴',
            _ => '?',
        };
    }
}
