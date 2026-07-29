using MindGoblin.Core;

// Throwaway harness: run the RoI maths against LIVE poe.watch data so the numbers get
// sanity-checked against the real market before any of this reaches the UI.
var league = args.Length > 0 ? args[0] : "Allflame";
const string ua = "mindgoblin/0.1 (contact: beard1ess@theundead.live)";

using var watch = new PoeWatchClient(ua);
var gems = await watch.GetAsync("gem", league);
var currency = await watch.GetAsync("currency", league);

var gcp = PoeWatchClient.CurrencyChaos(currency, "Gemcutter's Prism") ?? 1.0;
var vaal = PoeWatchClient.CurrencyChaos(currency, "Vaal Orb") ?? 1.0;
Console.WriteLine($"league={league}  gem rows={gems.Count}  GCP={gcp:0.##}c  Vaal={vaal:0.##}c\n");

var catalog = GemCatalog.LoadDefault() ?? throw new InvalidOperationException("no gem index");
var ladders = PoeWatchClient.ToLadders(gems, minDailyVolume: 5);
var roi = new GemRoi();
var costs = new GemRoi.Costs(gcp, vaal);

var rows = new List<GemRoi.Result>();
foreach (var (name, prices) in ladders)
{
    if (!catalog.IsVendorBuyable(name)) continue;
    foreach (var r in roi.EvaluateAll(prices, costs, Enum.GetValues<GemRoi.Path>()))
        if (r.IsComplete && r.Profit > 0) rows.Add(r);
}

Console.WriteLine($"vendor gems with a complete, profitable row: {rows.Count}\n");
foreach (var path in Enum.GetValues<GemRoi.Path>())
{
    Console.WriteLine($"=== {path} - top 6 by profit ===");
    foreach (var r in rows.Where(r => r.Path == path).OrderByDescending(r => r.Profit).Take(6))
        Console.WriteLine($"  {r.Gem,-36} cost {r.TotalCost,8:0.0}c  sell {r.ExpectedRevenue,8:0.0}c  "
                          + $"profit {r.Profit,7:0.0}c  RoI {r.Roi,7:P0}");
    Console.WriteLine();
}
