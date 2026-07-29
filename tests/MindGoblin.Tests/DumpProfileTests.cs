using MindGoblin.Core.Voyage;

namespace MindGoblin.Tests;

/// <summary>
/// "dump": the inverted objective. Spend the LEAST valuable charts to clear panel space,
/// on a board that still has to be full, legal and joined -- the run is a throwaway, the
/// voyage is not.
/// </summary>
public class DumpProfileTests
{
    private static VoyageProfile Dump =>
        VoyageRules.Defaults().Single(p => p.Name == "dump");

    private static VoyageSession Session(int count = 12, Func<int, int>? level = null)
    {
        var session = new VoyageSession();
        session.ApplyPanelRead(Enumerable.Range(1, count).Select(i =>
            new ChartPanelReader.ReadCell(i, (i - 1) / 6, (i - 1) % 6, true, true, true, true)
            { Level = level?.Invoke(i) ?? 80 }).ToList());
        return session;
    }

    /// <summary>
    /// The valuable charts stay in the panel; the junk gets spent. Twelve charts, three
    /// of them plainly good -- the dump plan uses the other nine.
    /// </summary>
    [Fact]
    public void TheGoodChartsAreLeftBehind()
    {
        var session = Session();
        foreach (var good in new[] { 2, 6, 11 })
            session.ApplyChartText(good,
                "Tempest Reach\nAnchorfield\nItem Quantity: +90%\nDead Man's Sulphur: +20\n"
                + "Adjacent Modifier: Adjacent Areas contain 3 additional Diviner's Strongboxes");

        var solution = session.Solve(Dump, TimeSpan.FromSeconds(3));
        var plan = session.Plan(solution);

        Assert.Equal(9, plan.Count);
        Assert.DoesNotContain(plan, s => s.ChartNumber is 2 or 6 or 11);
    }

    /// <summary>
    /// A sum of negatives would rather leave squares empty than fill them with junk --
    /// which is why the profile carries a flat per-chart base. The board must fill.
    /// </summary>
    [Fact]
    public void TheBoardStillFillsAndJoins()
    {
        var solution = Session().Solve(Dump, TimeSpan.FromSeconds(3));
        Assert.Equal(9, solution.Placements.Count);
        Assert.Empty(solution.StrandedCells);
        Assert.True(solution.Value > 0, "junk is still worth spending");
    }

    /// <summary>High tiers are worth keeping for a real voyage, so dump prefers low.</summary>
    [Fact]
    public void HighTierChartsAreKeptForARealVoyage()
    {
        // Charts 1-3 are level 83; the rest are 70. Identical otherwise.
        var session = Session(level: i => i <= 3 ? 83 : 70);
        var plan = session.Plan(session.Solve(Dump, TimeSpan.FromSeconds(3)));

        Assert.Equal(9, plan.Count);
        Assert.DoesNotContain(plan, s => s.ChartNumber <= 3);
    }

    /// <summary>
    /// The one positive weight: a chart that cannot drop equipment is junk by the game's
    /// own hand, so dump prefers it over a merely plain chart.
    /// </summary>
    [Fact]
    public void AntiLootChartsAreExactlyWhatADumpWants()
    {
        var session = Session();
        session.ApplyChartText(5,
            "Salt Barrens\nAbyssal Plain\nVoyage Modifier: Monsters in all Voyage Areas "
            + "cannot drop Equipment, Flasks or Tinctures");

        var plan = session.Plan(session.Solve(Dump, TimeSpan.FromSeconds(3)));
        Assert.Contains(plan, s => s.ChartNumber == 5);
    }

    /// <summary>Every chart must stay net positive, or the base is set too low and the
    /// solver starts refusing to spend the very charts the profile exists to burn.</summary>
    [Fact]
    public void EvenAGodChartStaysNetPositive()
    {
        var chart = new Chart("god", "god", ChartShape.Crossing, 83, [
            "40% increased Quantity of Items found in all Voyage Areas",
            "3 additional Diviner's Strongboxes",
            "Rare monsters that are natural inhabitants of all Voyage Areas are imprisoned by Essences",
            "Monsters in all Voyage Areas are at least Magic",
        ])
        {
            ItemQuantity = 100, ItemRarity = 80, MonsterPackSize = 40,
            GoldFound = 100, Sulphur = 25,
            VoyageModifier = "Players in all Voyage Areas have Soul Eater",
        };

        Assert.True(Dump.ScoreChart(chart) > 0,
                    $"scored {Dump.ScoreChart(chart)} — the base no longer covers the rules");
    }
}
