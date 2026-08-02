using MindGoblin.Core.Voyage;

namespace MindGoblin.Tests;

/// <summary>
/// The scarab plan, and the one thing it has to get right that no stat can express.
///
/// Scarabs arrive three ways and the corpus is explicit about all three: a per-rare drop
/// ("Rare Monsters in adjacent Areas drop # additional Scarabs"), a multiplier ("#% more
/// Scarabs found in adjacent Areas"), and an Operative's Strongbox, which simply contains
/// them. Both of the first two are ADJACENT-scoped and both exist as figurine mods, so
/// this is a plan about placement more than most.
/// </summary>
public class ScarabStrategyTests
{
    private static VoyageProfile Scarabs => VoyageRules.Defaults().Single(p => p.Name == "scarabs");

    [Theory]
    [InlineData("Rare Monsters in adjacent Areas drop 2 additional Scarabs")]
    [InlineData("Rare Monsters in Area drop an additional Scarab")]
    [InlineData("15% more Scarabs found in adjacent Areas")]
    [InlineData("Adjacent Areas contain 3 additional Operative's Strongboxes")]
    public void EveryWayAScarabArrivesIsScored(string line) =>
        Assert.True(Scarabs.ScoreText([line]) > 0, $"'{line}' scores zero");

    /// <summary>
    /// THE POINT OF THE STRATEGY. An Operative's Strongbox contains scarabs, so to this
    /// plan it must outrank the boxes that do not — and the catalog, which can file a mod
    /// under only one stat, prices it at 20 against Arcanist's and Diviner's 25. Left to
    /// the Strongboxes stat a scarab farmer would be steered at exactly the wrong boxes.
    /// </summary>
    [Fact]
    public void OperativesOutranksEveryOtherBox()
    {
        var operatives = Scarabs.ScoreText(["Adjacent Areas contain 2 additional Operative's Strongboxes"]);
        foreach (var other in new[]
                 {
                     "Adjacent Areas contain 2 additional Arcanist's Strongboxes",
                     "Adjacent Areas contain 2 additional Diviner's Strongboxes",
                     "Adjacent Areas contain 2 additional Strongboxes",
                 })
            Assert.True(operatives > Scarabs.ScoreText([other]),
                        $"Operative's ({operatives}) does not beat '{other}'");
    }

    /// <summary>
    /// The inversion is real and specific to this plan: under "strongbox" the catalog's
    /// ordering stands, because there a box is valued as a box.
    /// </summary>
    [Fact]
    public void TheStrongboxPlanStillPrefersDiviners()
    {
        var boxes = VoyageRules.Defaults().Single(p => p.Name == "strongbox");
        Assert.True(boxes.ScoreText(["Adjacent Areas contain 2 additional Diviner's Strongboxes"])
                    > boxes.ScoreText(["Adjacent Areas contain 2 additional Operative's Strongboxes"]));
    }

    /// <summary>
    /// A box's generic loot scores nothing here, deliberately: it is not scarabs. Stated
    /// as a test because it looks like an omission and is a decision — the box rules are
    /// left out so the Operative's rule can REPLACE the catalog's price rather than stack
    /// on it, which would pay that line twice.
    /// </summary>
    [Fact]
    public void APlainBoxIsWorthNothingAsABox()
    {
        Assert.Equal(0, Scarabs.ScoreText(["Adjacent Areas contain 2 additional Strongboxes"]));
        Assert.Equal(0, Scarabs.ScoreText(["Adjacent Areas contain 2 additional Diviner's Strongboxes"]));
    }

    /// <summary>
    /// And yet a box still pulls its weight, through the machinery rather than the rules:
    /// every strongbox counts as ~4 rares, so one placed beside a per-rare scarab payout
    /// feeds it. That is why the box rules can be dropped without losing anything.
    /// </summary>
    [Fact]
    public void ABoxStillFeedsThePerRarePayout()
    {
        var density = VoyageProfile.MonsterDensityOf(
            "Adjacent Areas contain 2 additional Diviner's Strongboxes");
        Assert.Equal(2 * AreaPopulation.RaresPerRolledStrongbox / AreaPopulation.RaresPerArea,
                     density, 6);
    }

    /// <summary>
    /// The scarab drop is a per-RARE payout, so the solver must route it through the rare
    /// channel: it is worth more beside a rare-dense tile than a dead one, and pricing it
    /// flat would make placement decide nothing on a full board.
    /// </summary>
    [Fact]
    public void TheScarabDropRidesTheRareChannel()
    {
        Assert.Equal(VoyageProfile.PayoutChannel.Rares,
            VoyageProfile.PayoutChannelOf("Rare Monsters in adjacent Areas drop 2 additional Scarabs"));
    }

    /// <summary>
    /// Tier is a precondition, not a nicety: 3.29.0b gates Operative's, Diviner's and
    /// Arcanist's to spawn by default only above area level 67.
    /// </summary>
    [Fact]
    public void HigherTierChartsBreakTies() =>
        Assert.True(Scarabs.AreaLevelWeight > 0);

    /// <summary>
    /// The requirement, end to end rather than as a score comparison: with one seat too
    /// few, the plan takes the Operative's box and leaves the Diviner's. Scoring it right
    /// is only half the job — it has to survive into an actual board.
    ///
    /// The eight filler charts carry REAL value, and the Diviner's chart carries a little:
    /// without that, every candidate scores zero, the last seat is an arbitrary tie-break
    /// and this passes whatever the Operative's rule is worth. Checked by zeroing the rule
    /// and watching it fail.
    /// </summary>
    [Fact]
    public void ThePlanSeatsTheOperativesChartAndDropsTheDiviners()
    {
        var session = new VoyageSession();
        // Ten Crossings for nine squares: every subset tiles, so nothing but VALUE can
        // decide which chart is left in the panel.
        session.ApplyPanelRead(Enumerable.Range(1, 10).Select(i =>
            new ChartPanelReader.ReadCell(i, (i - 1) / 6, (i - 1) % 6, true, true, true, true)
            { Level = 80 }).ToList());

        // Its whole worth is the box; no quantity to prop it up.
        session.ApplyChartText(1,
            "Operative Reach\nAnchorfield\nAdjacent Areas contain 2 additional Operative's Strongboxes");
        // Worth a little, and none of it from the box: a scarab farmer wants the other one.
        session.ApplyChartText(2,
            "Diviner Reach\nAnchorfield\nItem Quantity: +10%\n"
            + "Adjacent Areas contain 2 additional Diviner's Strongboxes");
        for (var i = 3; i <= 10; i++)
            session.ApplyChartText(i, $"Filler {i}\nAnchorfield\nItem Quantity: +30%");

        var plan = session.Plan(session.Solve(Scarabs, TimeSpan.FromSeconds(3)));

        Assert.Equal(9, plan.Count);
        Assert.Contains(plan, s => s.ChartNumber == 1);
        Assert.DoesNotContain(plan, s => s.ChartNumber == 2);
    }

    /// <summary>
    /// The loot-lock costs a scarab run far less than a currency one — scarabs are not
    /// equipment — and the weights should say so rather than copying a number across.
    /// </summary>
    [Fact]
    public void TheEquipmentLockIsCheaperHereThanForCurrency()
    {
        const string line = "Monsters in this Area cannot drop Equipment, Flasks or Tinctures";
        var scarabs = Scarabs.ScoreText([line]);
        var currency = VoyageRules.Defaults().Single(p => p.Name == "currency").ScoreText([line]);

        Assert.True(scarabs < 0, "the lock should still cost something");
        Assert.True(scarabs > currency, $"scarabs {scarabs} should be cheaper than currency {currency}");
    }
}
