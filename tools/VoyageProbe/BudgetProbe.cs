using System.Runtime.Versioning;
using MindGoblin.Core.Voyage;

/// <summary>Does a longer budget beat what the solver called "proved"? It must not.</summary>
[SupportedOSPlatform("windows")]
public static class BudgetProbe
{
    public static int Run(string path, string profileName)
    {
        var (session, _) = VoyageSession.Restore(path);
        var profile = VoyageRules.Defaults().Single(p => p.Name == profileName);
        VoyageSolver.Trace = true;

        foreach (var seconds in new[] { 3, 10, 30, 90 })
        {
            var s = session.Solve(profile, TimeSpan.FromSeconds(seconds));
            Console.WriteLine($"{seconds,3}s -> value {s.Value,9:0.##}  stranded {s.StrandedCells.Count}  "
                              + $"{(s.ProvedOptimal ? "proved" : "budget")}  {s.NodesExplored,12:N0} nodes  "
                              + $"{s.Elapsed.TotalMilliseconds:0} ms");
        }
        return 0;
    }
}
