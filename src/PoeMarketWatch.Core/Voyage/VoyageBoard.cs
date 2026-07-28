namespace PoeMarketWatch.Core.Voyage;

/// <summary>One chart available to place, independent of where it ends up.</summary>
public sealed record Chart(
    string Id,
    string Name,
    ChartShape Shape,
    int AreaLevel,
    IReadOnlyList<string> Modifiers)
{
    /// <summary>Weight used by the optimiser. Set from whatever you are chasing.</summary>
    public double Value { get; init; }

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

    public int FilledCount => Placements.Count();

    public void Place(Placement placement)
    {
        if (!InBounds(placement.Cell))
            throw new ArgumentOutOfRangeException(nameof(placement), "cell is off the board");
        _cells[placement.Cell.Row, placement.Cell.Col] = placement;
    }

    public void Clear(Cell c)
    {
        if (InBounds(c)) _cells[c.Row, c.Col] = null;
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

    public bool IsValid() => Validate().Count == 0;

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
