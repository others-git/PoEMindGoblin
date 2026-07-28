using MindGoblin.Core.Voyage;

namespace MindGoblin.Tests;

/// <summary>
/// The order to run the squares in. Constrained by the connections -- a square can only be
/// entered from one already visited that opens onto it -- and chosen to take the valuable
/// squares early, because lanterns deplete and a death ends the voyage.
/// </summary>
public class VoyageRouteTests
{
    private static Chart Chart(string id, double value) =>
        new(id, id, ChartShape.Crossing, 80, []) { Value = value };

    /// <summary>A full board of Crossings: every square connects to every neighbour.</summary>
    private static VoyageBoard FullBoard(Func<int, double> valueOfSquare)
    {
        var board = new VoyageBoard(3, 3);
        for (var r = 0; r < 3; r++)
            for (var c = 0; c < 3; c++)
            {
                var square = r * 3 + c + 1;
                board.Place(new Placement(Chart($"c{square}", valueOfSquare(square)), new Cell(r, c), 0));
            }
        return board;
    }

    [Fact]
    public void TheRouteStartsBottomLeft()
    {
        // "Voyages will always start in the bottom left Chart of the Voyage."
        var route = VoyageRoute.Plan(FullBoard(_ => 1), p => p.Chart.Value);
        Assert.Equal(7, route.Squares[0]);
    }

    [Fact]
    public void EverySquareIsVisitedExactlyOnce()
    {
        var route = VoyageRoute.Plan(FullBoard(s => s), p => p.Chart.Value);
        Assert.Equal(9, route.Squares.Count);
        Assert.Equal(9, route.Squares.Distinct().Count());
        Assert.Empty(route.Unreachable);
    }

    [Fact]
    public void EachStepIsConnectedToSomethingAlreadyVisited()
    {
        // You cannot teleport: the next square has to be reachable from the ones cleared.
        var board = FullBoard(s => s);
        var route = VoyageRoute.Plan(board, p => p.Chart.Value);

        var visited = new HashSet<int> { route.Squares[0] };
        foreach (var square in route.Squares.Skip(1))
        {
            var cell = new Cell((square - 1) / 3, (square - 1) % 3);
            var touches = Enum.GetValues<Side>().Any(side =>
            {
                var neighbour = VoyageBoard.Neighbour(cell, side);
                if (!board.InBounds(neighbour)) return false;
                var square2 = VoyagePlan.SquareNumber(neighbour, 3);
                return visited.Contains(square2);
            });
            Assert.True(touches, $"square {square} was entered from nowhere");
            visited.Add(square);
        }
    }

    [Fact]
    public void TheValuableSquareIsTakenAsEarlyAsTheConnectionsAllow()
    {
        // Square 3 is the opposite corner from the start: four steps away, whichever way
        // round you go (7-4-1-2-3 or 7-8-9-6-3). So it can be no earlier than the fifth
        // square, and a valuable one should not be left any later than it has to be.
        var board = FullBoard(s => s == 3 ? 1000 : 1);
        var route = VoyageRoute.Plan(board, p => p.Chart.Value);

        Assert.Equal(5, route.Squares.ToList().IndexOf(3) + 1);
    }

    [Fact]
    public void AWorthlessBoardStillProducesALegalRoute()
    {
        var route = VoyageRoute.Plan(FullBoard(_ => 0), p => p.Chart.Value);
        Assert.Equal(9, route.Squares.Count);
        Assert.Equal(7, route.Squares[0]);
    }

    [Fact]
    public void TheOrderFollowsValueWhenTheBoardIsFullyConnected()
    {
        // Value falls off with each step, so a board where value decreases away from the
        // start should be run in roughly that order -- most valuable first.
        var worth = new Dictionary<int, double>
        {
            [7] = 100, [8] = 90, [9] = 80, [4] = 70, [5] = 60, [6] = 50,
            [1] = 40, [2] = 30, [3] = 20,
        };
        var route = VoyageRoute.Plan(FullBoard(s => worth[s]), p => p.Chart.Value);

        // Each square taken must be at least as valuable as the next.
        var taken = route.Squares.Select(s => worth[s]).ToList();
        Assert.Equal(taken.OrderByDescending(v => v), taken);
    }

    [Fact]
    public void ASquareTheRouteCannotReachIsReportedNotDropped()
    {
        // An End in the far corner, pointing at the border: legal, and unreachable from
        // the start. Saying so is the whole point -- it is a chart that will never be run.
        var board = new VoyageBoard(3, 3);
        var eastWest = Enumerable.Range(0, 4)
            .First(r => new ChartFace(ChartShape.Straight, r).IsOpen(Side.East));
        var north = Enumerable.Range(0, 4)
            .First(r => new ChartFace(ChartShape.End, r).IsOpen(Side.North));

        board.Place(new Placement(Chart("a", 1), new Cell(2, 0), eastWest));   // square 7, start
        board.Place(new Placement(Chart("b", 1), new Cell(2, 1), eastWest));   // square 8
        board.Place(new Placement(Chart("cut", 99), new Cell(0, 2), north));   // square 3, alone

        var route = VoyageRoute.Plan(board, p => p.Chart.Value);

        Assert.Equal([7, 8], route.Squares);
        Assert.Equal([3], route.Unreachable);
    }

    [Fact]
    public void AnEmptyBoardHasNoRoute()
    {
        Assert.True(VoyageRoute.Plan(new VoyageBoard(3, 3), p => p.Chart.Value).IsEmpty);
    }

    [Fact]
    public void AStartOnAnEmptySquareYieldsNoRoute()
    {
        // Nothing in the bottom-left means nowhere to begin.
        var board = new VoyageBoard(3, 3);
        board.Place(new Placement(Chart("a", 1), new Cell(0, 0), 0));
        Assert.True(VoyageRoute.Plan(board, p => p.Chart.Value).IsEmpty);
    }

    [Fact]
    public void ARouteReadsAsAnOrder()
    {
        Assert.Equal("7 → 8 → 9", new VoyageRoute([7, 8, 9], [], 0).ToString());
    }
}
