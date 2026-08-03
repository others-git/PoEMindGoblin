using MindGoblin.Core.Voyage;

namespace MindGoblin.Tests;

/// <summary>
/// Scarabs, and the parts of scoring them that survive their dedicated strategy.
///
/// Scarabs arrive three ways and the corpus is explicit about all three: a per-rare drop
/// ("Rare Monsters in adjacent Areas drop # additional Scarabs"), a multiplier ("#% more
/// Scarabs found in adjacent Areas"), and an Operative's Strongbox, which simply contains
/// them. Both of the first two are ADJACENT-scoped and both exist as figurine mods, so
/// this is about placement more than most.
///
/// The "scarabs" preset is retired; the Scarabs STAT is not, and "rare monsters" weights
/// it highest of the survivors.
/// </summary>
public class ScarabScoringTests
{
    private static VoyageProfile Rares => VoyageRules.Defaults().Single(p => p.Name == "rare monsters");

    [Theory]
    [InlineData("Rare Monsters in adjacent Areas drop 2 additional Scarabs")]
    [InlineData("Rare Monsters in Area drop an additional Scarab")]
    [InlineData("15% more Scarabs found in adjacent Areas")]
    [InlineData("Adjacent Areas contain 3 additional Operative's Strongboxes")]
    public void EveryWayAScarabArrivesIsScored(string line) =>
        Assert.True(Rares.ScoreText([line]) > 0, $"'{line}' scores zero");

    /// <summary>
    /// The catalog's ordering of the boxes, which the retired scarab preset used to invert
    /// for its own purposes: valued AS A BOX, a Diviner's (25) beats an Operative's (20).
    /// Checked through the profile that still weights the stat uniformly, so nothing but
    /// the catalog decides it.
    /// </summary>
    [Fact]
    public void ValuedAsBoxesTheDivinersBeatsTheOperatives()
    {
        var boxes = VoyageRules.Defaults().Single(p => p.Name == "bottles");
        Assert.True(boxes.ScoreText(["Adjacent Areas contain 2 additional Diviner's Strongboxes"])
                    > boxes.ScoreText(["Adjacent Areas contain 2 additional Operative's Strongboxes"]));
    }

    /// <summary>
    /// A box pulls its weight through the machinery rather than the rules: every strongbox
    /// counts as ~4 rares, so one placed beside a per-rare scarab payout feeds it.
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
    /// The loot-lock is priced per PLAN, not copied across: the two profiles that price it
    /// at all must not agree by accident, or the number is a constant wearing a weight's
    /// clothes.
    /// </summary>
    [Fact]
    public void TheEquipmentLockIsPricedPerPlan()
    {
        const string line = "Monsters in this Area cannot drop Equipment, Flasks or Tinctures";
        var bottles = VoyageRules.Defaults().Single(p => p.Name == "bottles").ScoreText([line]);
        var currency = VoyageRules.Defaults().Single(p => p.Name == "currency").ScoreText([line]);

        Assert.True(bottles < 0, "the lock should cost the bottles plan something");
        Assert.True(currency < 0, "the lock should cost the currency plan something");
        Assert.NotEqual(bottles, currency);
    }
}
