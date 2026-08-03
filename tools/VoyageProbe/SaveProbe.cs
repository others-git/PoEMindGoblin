using System.Runtime.Versioning;
using MindGoblin.Core.Voyage;

/// <summary>
/// Read a screenshot, save the session, read it back. Confirms the on-disk format is
/// what the app will actually produce, not only what a unit test constructs.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SaveProbe
{
    public static int Run(string screenshot, string path)
    {
        using var px = new FilePixels(screenshot);
        var session = new VoyageSession();
        // The app's reader, not a default one: the round-trip is only evidence if the
        // charts going into it are the charts the app would have produced -- the user's
        // calibration and their learned level templates included.
        session.ApplyPanelRead(new ChartPanelReader(ChartPanelReader.Options.Load(),
                                                    LevelReader.LoadWithUserTemplates()).Read(px));
        session.ApplySquareModifiers(5, ["Areas contain 8 additional packs of Sea Beasts"]);
        session.ApplyChartText(session.ByPanelIndex.Keys.Order().First(),
            "Tempest Reach\nSeafloor Ridges\nDead Man's Sulphur: +14");
        session.Save(path, profile: "sulphur");

        var (restored, state) = VoyageSession.Restore(path);
        Console.WriteLine($"saved {new FileInfo(path).Length:N0} bytes, profile {state?.Profile}");
        Console.WriteLine($"charts {session.Charts.Count} -> {restored.Charts.Count}, "
                          + $"squares {session.SquareModifiers.Count} -> {restored.SquareModifiers.Count}");
        Console.WriteLine($"levels match: "
            + $"{session.Charts.Select(c => c.AreaLevel).SequenceEqual(restored.Charts.Select(c => c.AreaLevel))}");
        Console.WriteLine($"shapes match: "
            + $"{session.Charts.Select(c => c.Shape).SequenceEqual(restored.Charts.Select(c => c.Shape))}");

        var solved = restored.Solve(VoyageRules.Defaults().Single(p => p.Name == "sulphur"),
                                    TimeSpan.FromSeconds(3));
        Console.WriteLine($"restored session solves: {solved.Placements.Count} placed, "
                          + $"value {solved.Value}, stranded {solved.StrandedCells.Count}");
        return 0;
    }
}
