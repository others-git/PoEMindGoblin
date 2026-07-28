using PoeMarketWatch.Core.Voyage;

static List<Chart> Make(int n, int seed)
{
    var rng = new Random(seed);
    var shapes = Enum.GetValues<ChartShape>();
    return Enumerable.Range(0, n).Select(i => new Chart(
            $"c{i}", $"chart{i}", shapes[rng.Next(shapes.Length)],
            rng.Next(68, 84), Array.Empty<string>())
        { Value = rng.Next(5, 120) }).ToList();
}

var mods = new[]
{
    new BoardModifier("Adjacent Areas contain 8 additional packs of Sea Beasts", [new Cell(1, 0)]),
    new BoardModifier("Adjacent Areas have increased Quantity", [new Cell(0, 1)]),
};
var score = VoyageSolver.ScoreWith(mods, (_, c) => c.Value * 0.5);

foreach (var ms in new[] { 100, 1000, 5000 })
{
    var charts = Make(60, 7);
    var s = new VoyageSolver(3, 3, charts, score).Solve(TimeSpan.FromMilliseconds(ms));
    Console.WriteLine($"budget {ms,5} ms -> value {s.Value,7:0.#}  placed {s.Placements.Count}  "
                      + $"nodes {s.NodesExplored,10:N0}  optimal={s.ProvedOptimal}  actual {s.Elapsed.TotalMilliseconds:0} ms");
}

Console.WriteLine();
var final = new VoyageSolver(3, 3, Make(60, 7), score).Solve(TimeSpan.FromSeconds(2));
var board = new VoyageBoard(3, 3);
foreach (var p in final.Placements) board.Place(p);
Console.WriteLine($"layout (valid={board.IsValid()}):");
Console.WriteLine(board.Render());
Console.WriteLine("PLAN");
foreach (var step in VoyagePlan.Describe(final, 3, Make(60, 7)))
    Console.WriteLine("  " + step);
