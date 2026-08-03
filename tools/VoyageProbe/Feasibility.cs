using System.Runtime.Versioning;
using MindGoblin.Core.Voyage;

/// <summary>
/// Is a fully connected board reachable from a given chart set at all?
///
/// Worth answering directly rather than inferring from the optimiser. A plan with a dead
/// corner looks like a solver failure, and the honest answer may be that the charts on
/// hand cannot tile the board any other way -- in which case the tool should say so
/// instead of implying it settled for less.
///
/// This is a pure constraint search: no scoring, no bounds, and it stops at the first
/// connected board. That makes it far faster than asking the optimiser, which has to keep
/// searching for a BETTER board and cannot stop at merely a good one.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Feasibility
{
    public static int Run(string screenshot)
    {
        using var px = new FilePixels(screenshot);
        var session = new VoyageSession();
        // The app's reader: "these charts cannot tile the board" is only an answer about
        // the real panel if the shapes came off it the way the app reads them.
        session.ApplyPanelRead(new ChartPanelReader(ChartPanelReader.Options.Load(),
                                                    LevelReader.LoadWithUserTemplates()).Read(px));
        var charts = session.Charts;
        Console.WriteLine($"{charts.Count} charts read");
        foreach (var g in charts.GroupBy(c => c.Shape).OrderByDescending(g => g.Count()))
            Console.WriteLine($"  {g.Key,-9} x{g.Count()}");

        var board = new VoyageBoard(3, 3);
        var used = new bool[charts.Count];
        long legal = 0, nodes = 0;
        VoyageBoard? connected = null;

        bool Search(int index)
        {
            nodes++;
            if (index == 9)
            {
                if (!board.IsValid()) return false;
                legal++;
                if (board.StrandedCells().Count > 0) return false;
                connected = board;
                return true;
            }

            var cell = new Cell(index / 3, index % 3);
            for (var i = 0; i < charts.Count; i++)
            {
                if (used[i]) continue;
                foreach (var rot in ChartFace.DistinctRotations(charts[i].Shape))
                {
                    var face = new ChartFace(charts[i].Shape, rot);
                    if (!board.CanPlace(cell, face)) continue;

                    // Row-major fill: north and west neighbours are already final, so an
                    // edge that disagrees there can never be reconciled later.
                    if (!Agrees(board, cell, Side.North, face)) continue;
                    if (!Agrees(board, cell, Side.West, face)) continue;

                    board.Place(new Placement(charts[i], cell, rot));
                    used[i] = true;
                    if (Search(index + 1)) return true;
                    used[i] = false;
                    board.Clear(cell);
                }
            }
            return false;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var found = Search(0);
        sw.Stop();

        Console.WriteLine($"\n{nodes:N0} nodes, {legal:N0} legal boards, {sw.ElapsedMilliseconds} ms");
        if (found)
        {
            Console.WriteLine("a fully CONNECTED board exists:");
            Console.WriteLine(connected!.Render());
        }
        else
        {
            Console.WriteLine("NO fully connected board exists for these charts "
                              + "-- every legal layout strands at least one square.");
        }
        return 0;
    }

    /// <summary>
    /// Does this face agree with the already-settled neighbour on that side?
    ///
    /// Off the grid is always fine -- the border satisfies an open path and walls off a
    /// closed one -- so only in-bounds neighbours constrain anything.
    /// </summary>
    private static bool Agrees(VoyageBoard board, Cell cell, Side side, ChartFace face)
    {
        var neighbour = VoyageBoard.Neighbour(cell, side);
        if (!board.InBounds(neighbour)) return true;
        if (board.At(neighbour) is not { } placed) return true;
        return face.IsOpen(side) == placed.Face.IsOpen(side.Opposite());
    }
}
