using System.Runtime.Versioning;
using PoeMarketWatch.Core.Voyage;

/// <summary>Load a real saved session and report what refining recovered from it.</summary>
[SupportedOSPlatform("windows")]
public static class RefineProbe
{
    public static int Run(string path)
    {
        var (session, state) = VoyageSession.Restore(path);
        if (state is null) { Console.WriteLine("no usable session"); return 1; }

        Console.WriteLine($"{session.Charts.Count} charts, profile {state.Profile}\n");

        var adjacency = session.ByPanelIndex.Where(kv => kv.Value.HasAdjacentModifier).ToList();
        var global = session.VoyageWideModifiers;
        Console.WriteLine($"adjacency modifiers recovered: {adjacency.Count}");
        Console.WriteLine($"voyage-wide modifiers recovered: {global.Count}\n");

        foreach (var (i, m) in global) Console.WriteLine($"  GLOBAL chart {i,2}: {m}");
        Console.WriteLine();
        foreach (var (i, c) in adjacency.Take(8))
            Console.WriteLine($"  ADJ    chart {i,2}: {c.AdjacentModifier}");

        Console.WriteLine("\nleftover modifier lines still containing chrome:");
        var chrome = session.Charts.SelectMany(c => c.Modifiers)
            .Where(m => m.StartsWith("{") || m.StartsWith("(") || m.Contains("Take this item")
                        || m.StartsWith("Requirements") || m.StartsWith("Item Level"))
            .ToList();
        Console.WriteLine($"  {chrome.Count}");

        foreach (var name in new[] { "sulphur", "pack size", "quantity", "loot boxes", "safe" })
        {
            var profile = VoyageRules.Defaults().Single(p => p.Name == name);
            var s = session.Solve(profile, TimeSpan.FromSeconds(3));
            var plan = session.Plan(s);
            Console.WriteLine($"\n{name,-11} value {s.Value,8:0.#}  stranded {s.StrandedCells.Count}  "
                + $"{(s.ProvedOptimal ? "proved" : "budget")}  -> "
                + string.Join(" ", plan.Select(p => $"{p.Square}:{p.ChartNumber}")));
        }
        return 0;
    }
}
