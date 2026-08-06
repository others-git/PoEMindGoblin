using MindGoblin.Core.Voyage;

namespace MindGoblin.Tests;

/// <summary>
/// The model checked against the game's own help text, quoted verbatim.
///
/// Most of this was already right, having been worked out from screenshots -- but "worked
/// out from screenshots" and "stated by the game" are different degrees of confidence, and
/// these pin the difference down.
/// </summary>
[Collection("ChartRewards")]
public class HelpTextTests
{
    private static IReadOnlyList<ChartPanelReader.ReadCell> Crossings(int count) =>
        Enumerable.Range(1, count).Select(i =>
            new ChartPanelReader.ReadCell(i, (i - 1) / 6, (i - 1) % 6, true, true, true, true))
                .ToList();

    /// <summary>
    /// Corners, which can close on themselves.
    ///
    /// A capped board has to form a CLOSED cluster: every connection must reach the board
    /// edge or another connection, and a cell left empty is neither. Four Crossings cannot
    /// do it on a 3x3 -- each has four open sides and a 2x2 block leaves two of them
    /// facing in-bounds empty cells -- but four Corners form a ring that satisfies itself.
    /// </summary>
    private static IReadOnlyList<ChartPanelReader.ReadCell> Corners(int count) =>
        Enumerable.Range(1, count).Select(i =>
            new ChartPanelReader.ReadCell(i, (i - 1) / 6, (i - 1) % 6,
                                          true, true, false, false)).ToList();

    /// <summary>"Voyages will always start in the bottom left Chart of the Voyage."</summary>
    [Fact]
    public void TheVoyageStartsInTheBottomLeftSquare()
    {
        var session = new VoyageSession();
        Assert.Equal(7, session.StartSquare);                       // 3x3, row-major from 1
        Assert.Equal(new Cell(2, 0), session.CellOf(session.StartSquare));
        Assert.Equal(new Cell(2, 0), VoyageSolver.StartCell(3));
        Assert.Equal(new Cell(3, 0), VoyageSolver.StartCell(4));    // follows the board size
    }

    /// <summary>
    /// "Place up to nine Charts onto the board... Your very first Voyage will require only
    /// four Charts."
    /// </summary>
    [Fact]
    public void AChartCapIsRespected()
    {
        var session = new VoyageSession();
        session.ApplyPanelRead(Corners(12));

        var profile = new VoyageProfile { Name = "first", AreaLevelWeight = 1, MaxCharts = 4 };
        var solution = session.Solve(profile, TimeSpan.FromSeconds(2));

        Assert.Equal(4, solution.Placements.Count);
    }

    [Fact]
    public void ACappedBoardIsStillLegal()
    {
        // Fewer charts does not mean a looser rule: every connection still has to lead
        // somewhere, so a four-chart layout has to close on itself or the board edge.
        var session = new VoyageSession();
        session.ApplyPanelRead(Corners(12));

        var solution = session.Solve(
            new VoyageProfile { Name = "first", AreaLevelWeight = 1, MaxCharts = 4 },
            TimeSpan.FromSeconds(2));

        var board = new VoyageBoard(3, 3);
        foreach (var p in solution.Placements) board.Place(p);
        Assert.Empty(board.Validate());
    }

    [Fact]
    public void WithNoCapTheBoardFills()
    {
        var session = new VoyageSession();
        session.ApplyPanelRead(Crossings(12));
        var solution = session.Solve(
            new VoyageProfile { Name = "all", AreaLevelWeight = 1 }, TimeSpan.FromSeconds(2));
        Assert.Equal(9, solution.Placements.Count);
    }

    [Fact]
    public void ShapesThatCannotCloseYieldNoCappedBoard()
    {
        // Four Crossings cannot make a legal four-chart board on a 3x3: whichever way
        // they are arranged, an open side ends up facing an in-bounds empty cell, which
        // is neither the board edge nor another connection. Reporting nothing is the
        // correct answer, not a search failure.
        var session = new VoyageSession();
        session.ApplyPanelRead(Crossings(12));

        var solution = session.Solve(
            new VoyageProfile { Name = "first", AreaLevelWeight = 1, MaxCharts = 4 },
            TimeSpan.FromSeconds(2));

        Assert.Empty(solution.Placements);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(20)]
    public void ANonsenseCapIsClampedRatherThanCrashing(int cap)
    {
        var session = new VoyageSession();
        session.ApplyPanelRead(Corners(12));
        var solution = session.Solve(
            new VoyageProfile { Name = "x", AreaLevelWeight = 1, MaxCharts = cap },
            TimeSpan.FromSeconds(2));
        Assert.InRange(solution.Placements.Count, 0, 9);
    }

    /// <summary>
    /// "Each edge of the board will also apply a modifier to the Chart which touches that
    /// edge."
    ///
    /// A 3x3 board has twelve edge segments, three per side. A corner square touches two
    /// of them, an edge-centre one, and the middle none -- which is why the middle can
    /// never carry a board modifier and is left off the read checklist.
    /// </summary>
    [Fact]
    public void EachBoardEdgeTouchesExactlyOneSquare()
    {
        var layout = BoardLayout.Default();
        Assert.Equal(12, layout.Figurines.Count);
        Assert.All(layout.Figurines, f => Assert.Single(f.Adjacent));

        int Touching(int row, int col) => layout.Figurines
            .Count(f => f.Adjacent.Any(a => a.ToCell() == new Cell(row, col)));

        Assert.Equal(2, Touching(0, 0));   // corner: two edge segments
        Assert.Equal(2, Touching(2, 2));
        Assert.Equal(1, Touching(0, 1));   // edge centre: one
        Assert.Equal(1, Touching(1, 0));
        Assert.Equal(0, Touching(1, 1));   // middle: none
    }

    /// <summary>
    /// "Charts will all have an implicit modifier that either affects the Charts placed
    /// adjacent to them, or the Voyage as a whole."
    /// </summary>
    [Fact]
    public void AnImplicitIsEitherAdjacentOrGlobalAndNeverBoth()
    {
        var adjacent = ChartText.Parse(string.Join("\n",
            "X", "Anchorfield", "--------", "{ Implicit Modifier }",
            "Adjacent Areas contain 3 additional Strongboxes"), "id", ChartShape.Crossing)!;
        Assert.NotNull(adjacent.AdjacentModifier);
        Assert.Null(adjacent.VoyageModifier);

        var global = ChartText.Parse(string.Join("\n",
            "X", "Anchorfield", "--------", "{ Implicit Modifier }",
            "8% increased Pack Size in all Voyage Areas"), "id", ChartShape.Crossing)!;
        Assert.NotNull(global.VoyageModifier);
        Assert.Null(global.AdjacentModifier);
    }

    /// <summary>
    /// "All Connections of every placed Chart must lead to either the edge of the board or
    /// to another Connection, in order to have a valid Voyage."
    /// </summary>
    [Fact]
    public void AConnectionMustReachTheBoardEdgeOrAnotherConnection()
    {
        var board = new VoyageBoard(3, 3);
        var chart = new Chart("c", "c", ChartShape.End, 80, []);
        var north = Enumerable.Range(0, 4)
            .First(r => new ChartFace(ChartShape.End, r).IsOpen(Side.North));
        var south = Enumerable.Range(0, 4)
            .First(r => new ChartFace(ChartShape.End, r).IsOpen(Side.South));

        // Pointing off the board: legal.
        board.Place(new Placement(chart, new Cell(0, 0), north));
        Assert.Empty(board.Validate());

        // Pointing into an empty cell: not legal -- an empty cell is neither the board
        // edge nor a connection.
        board.Clear(new Cell(0, 0));
        board.Place(new Placement(chart, new Cell(0, 0), south));
        Assert.NotEmpty(board.Validate());
    }

    /// <summary>
    /// "...with these modifiers being rerolled randomly for every new Voyage."
    ///
    /// Which is why board modifiers are read per session and cleared with it, rather than
    /// remembered as a property of the board.
    /// </summary>
    [Fact]
    public void BoardModifiersBelongToTheSessionNotTheBoard()
    {
        var session = new VoyageSession();
        session.ApplySquareModifiers(1, ["Adjacent Areas contain 8 additional packs"]);
        Assert.NotEmpty(session.BoardModifiers());

        Assert.Empty(new VoyageSession().BoardModifiers());
    }
}
