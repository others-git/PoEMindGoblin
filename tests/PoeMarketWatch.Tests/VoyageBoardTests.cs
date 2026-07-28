using PoeMarketWatch.Core.Voyage;

namespace PoeMarketWatch.Tests;

public class ChartShapeTests
{
    [Theory]
    [InlineData(ChartShape.End, 1)]
    [InlineData(ChartShape.Corner, 2)]
    [InlineData(ChartShape.Straight, 2)]
    [InlineData(ChartShape.Junction, 3)]
    [InlineData(ChartShape.Crossing, 4)]
    public void OpenEdgeCountMatchesTheShape(ChartShape shape, int expected)
    {
        Assert.Equal(expected, new ChartFace(shape, 0).OpenCount());
    }

    [Fact]
    public void EndIsTheHalfStepDeadEnd()
    {
        var end = new ChartFace(ChartShape.End, 0);
        Assert.Single(end.OpenSides());
        Assert.True(end.IsOpen(Side.North));
        Assert.False(end.IsOpen(Side.South));
    }

    [Fact]
    public void RotationMovesEdgesClockwise()
    {
        Assert.True(new ChartFace(ChartShape.End, 0).IsOpen(Side.North));
        Assert.True(new ChartFace(ChartShape.End, 1).IsOpen(Side.East));
        Assert.True(new ChartFace(ChartShape.End, 2).IsOpen(Side.South));
        Assert.True(new ChartFace(ChartShape.End, 3).IsOpen(Side.West));
    }

    [Fact]
    public void RotationWrapsAndAcceptsNegatives()
    {
        Assert.Equal(0, new ChartFace(ChartShape.Corner, 4).Rotation);
        Assert.Equal(3, new ChartFace(ChartShape.Corner, -1).Rotation);
        Assert.True(new ChartFace(ChartShape.End, -4).IsOpen(Side.North));
    }

    [Fact]
    public void SymmetryCollapsesRedundantRotations()
    {
        // Without this the solver searches four identical Crossings and two identical
        // Straights per cell, multiplying the work for nothing.
        Assert.Single(ChartFace.DistinctRotations(ChartShape.Crossing));
        Assert.Equal(2, ChartFace.DistinctRotations(ChartShape.Straight).Count);
        Assert.Equal(4, ChartFace.DistinctRotations(ChartShape.Corner).Count);
        Assert.Equal(4, ChartFace.DistinctRotations(ChartShape.End).Count);
    }

    [Fact]
    public void StraightAtNinetyDegreesIsHorizontal()
    {
        var h = new ChartFace(ChartShape.Straight, 1);
        Assert.True(h.IsOpen(Side.East));
        Assert.True(h.IsOpen(Side.West));
        Assert.False(h.IsOpen(Side.North));
    }

    [Theory]
    [InlineData(Side.North, Side.South)]
    [InlineData(Side.East, Side.West)]
    [InlineData(Side.South, Side.North)]
    [InlineData(Side.West, Side.East)]
    public void OppositeSidesPair(Side a, Side b) => Assert.Equal(b, a.Opposite());

    [Fact]
    public void DeltasTreatRowAsIncreasingSouthwards()
    {
        Assert.Equal((-1, 0), Side.North.Delta());
        Assert.Equal((1, 0), Side.South.Delta());
        Assert.Equal((0, 1), Side.East.Delta());
        Assert.Equal((0, -1), Side.West.Delta());
    }
}

public class VoyageBoardTests
{
    private static Chart C(string id, ChartShape shape, double value = 1) =>
        new(id, id, shape, 80, Array.Empty<string>()) { Value = value };

    private static VoyageBoard Board(int rows = 3, int cols = 3) => new(rows, cols);

    [Fact]
    public void EmptyBoardIsTriviallyValid()
    {
        Assert.True(Board().IsValid());
    }

    [Fact]
    public void PathIntoTheBorderIsSatisfied()
    {
        // "every path must connect to another, or the border"
        var b = new VoyageBoard(1, 1);
        b.Place(new Placement(C("a", ChartShape.End), new Cell(0, 0), 0)); // points north, off-board
        Assert.True(b.IsValid());
    }

    [Fact]
    public void PathIntoAnEmptyCellIsAViolation()
    {
        var b = Board();
        // End pointing east, into an empty neighbour rather than the border
        b.Place(new Placement(C("a", ChartShape.End), new Cell(1, 1), 1));
        var problems = b.Validate();
        var v = Assert.Single(problems);
        Assert.Equal(Side.East, v.Side);
        Assert.Contains("empty cell", v.Reason);
    }

    [Fact]
    public void TwoEndsFacingEachOtherConnect()
    {
        var b = new VoyageBoard(1, 2);
        b.Place(new Placement(C("a", ChartShape.End), new Cell(0, 0), 1)); // east
        b.Place(new Placement(C("b", ChartShape.End), new Cell(0, 1), 3)); // west
        Assert.True(b.IsValid());
    }

    [Fact]
    public void OpenEdgeMeetingAClosedEdgeIsAViolation()
    {
        var b = new VoyageBoard(1, 2);
        b.Place(new Placement(C("a", ChartShape.End), new Cell(0, 0), 1)); // east
        b.Place(new Placement(C("b", ChartShape.End), new Cell(0, 1), 1)); // also east
        var problems = b.Validate();
        // reported from both sides: a's path hits a wall, and b leads into a wall
        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, p => p.Reason.Contains("closed edge"));
    }

    [Fact]
    public void ConnectionsAreMutualNotOneWay()
    {
        // A closed edge facing a neighbour's open edge is equally invalid; this is what
        // makes it edge matching rather than reachability.
        var b = new VoyageBoard(1, 2);
        b.Place(new Placement(C("a", ChartShape.End), new Cell(0, 0), 0));  // north, fine
        b.Place(new Placement(C("b", ChartShape.End), new Cell(0, 1), 3));  // west, into a
        var problems = b.Validate();
        Assert.Contains(problems, p => p.Cell == new Cell(0, 0) && p.Side == Side.East);
    }

    [Fact]
    public void AStraightCorridorAcrossTheBoardIsValid()
    {
        var b = new VoyageBoard(1, 3);
        for (var c = 0; c < 3; c++)
            b.Place(new Placement(C($"s{c}", ChartShape.Straight), new Cell(0, c), 1)); // horizontal
        Assert.True(b.IsValid());
    }

    [Fact]
    public void CanPlaceToleratesUndecidedNeighbours()
    {
        // During a search most of the board is empty. Rejecting an open edge that points
        // at a not-yet-filled cell would prune every partial solution.
        var b = Board();
        Assert.True(b.CanPlace(new Cell(1, 1), new ChartFace(ChartShape.Crossing, 0)));
    }

    [Fact]
    public void CanPlaceRejectsAMismatchAgainstAPlacedNeighbour()
    {
        var b = new VoyageBoard(1, 2);
        b.Place(new Placement(C("a", ChartShape.End), new Cell(0, 0), 1)); // east
        // a closed west edge cannot sit next to it
        Assert.False(b.CanPlace(new Cell(0, 1), new ChartFace(ChartShape.End, 1)));
        // an open west edge can
        Assert.True(b.CanPlace(new Cell(0, 1), new ChartFace(ChartShape.End, 3)));
    }

    [Fact]
    public void CanPlaceRejectsOccupiedAndOffBoardCells()
    {
        var b = new VoyageBoard(1, 1);
        b.Place(new Placement(C("a", ChartShape.End), new Cell(0, 0), 0));
        Assert.False(b.CanPlace(new Cell(0, 0), new ChartFace(ChartShape.End, 0)));
        Assert.False(b.CanPlace(new Cell(5, 5), new ChartFace(ChartShape.End, 0)));
    }

    [Fact]
    public void ValueSumsAcrossPlacements()
    {
        var b = new VoyageBoard(1, 2);
        b.Place(new Placement(C("a", ChartShape.End, value: 2.5), new Cell(0, 0), 1));
        b.Place(new Placement(C("b", ChartShape.End, value: 4.0), new Cell(0, 1), 3));
        Assert.Equal(6.5, b.TotalValue(), 3);
        Assert.Equal(2, b.FilledCount);
    }

    [Fact]
    public void ClearRemovesAPlacement()
    {
        var b = new VoyageBoard(1, 1);
        b.Place(new Placement(C("a", ChartShape.End), new Cell(0, 0), 0));
        b.Clear(new Cell(0, 0));
        Assert.Equal(0, b.FilledCount);
        Assert.Null(b.At(new Cell(0, 0)));
    }

    [Fact]
    public void ViolationsNameTheCellAndSide()
    {
        // With 60 charts in play, "invalid" on its own is useless feedback.
        var b = Board();
        b.Place(new Placement(C("Coral Reef", ChartShape.End), new Cell(1, 1), 1));
        var v = Assert.Single(b.Validate());
        Assert.Equal(new Cell(1, 1), v.Cell);
        Assert.Contains("(1,1)", v.ToString());
    }

    [Fact]
    public void RenderDrawsTheLayout()
    {
        var b = new VoyageBoard(1, 3);
        for (var c = 0; c < 3; c++)
            b.Place(new Placement(C($"s{c}", ChartShape.Straight), new Cell(0, c), 1));
        Assert.Equal("───", b.Render().TrimEnd());
    }

    [Fact]
    public void RejectsNonsenseDimensions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new VoyageBoard(0, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VoyageBoard(5, -1));
    }
}
