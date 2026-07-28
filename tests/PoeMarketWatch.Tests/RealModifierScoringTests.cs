using PoeMarketWatch.Core.Voyage;

namespace PoeMarketWatch.Tests;

/// <summary>
/// The shipped profiles against modifier text taken verbatim from real charts and from
/// poedb's tables for the three Voyage chart bases. A profile that cannot score the mods
/// the game actually rolls is decorative, and that is not visible from synthetic text.
/// </summary>
public class RealModifierScoringTests
{
    private static VoyageProfile Profile(string name) =>
        VoyageRules.Defaults().Single(p => p.Name == name);

    private static double ScoreLine(string profile, string line) =>
        Profile(profile).ScoreText([line]);

    [Theory]
    // Global sulphur, all three rolls.
    [InlineData("15% increased Dead Man's Sulphur found in all Voyage Areas")]
    [InlineData("20% increased Dead Man's Sulphur found in all Voyage Areas")]
    [InlineData("25% increased Dead Man's Sulphur found in all Voyage Areas")]
    // Adjacent, and the in-area affix wording.
    [InlineData("30% increased Dead Man's Sulphur found in adjacent Areas")]
    [InlineData("45% increased Dead Man's Sulphur found in this Area")]
    public void SulphurRollsAllScore(string line) =>
        Assert.True(ScoreLine("sulphur", line) > 0, line);

    [Theory]
    // GGG spells it "Qauntity" in the GLOBAL lines only. A rule matching "Quantity"
    // scores the adjacent rolls and silently misses every global one.
    [InlineData("8% increased Qauntity of Items found in all Voyage Areas")]
    [InlineData("10% increased Qauntity of Items found in all Voyage Areas")]
    [InlineData("45% increased Quantity of Items found in adjacent Areas")]
    [InlineData("20% increased Quantity of Items found in adjacent Areas")]
    public void BothSpellingsOfQuantityScore(string line) =>
        Assert.True(ScoreLine("quantity", line) > 0, line);

    [Fact]
    public void TheTypoAndTheCorrectSpellingScoreTheSame()
    {
        Assert.Equal(ScoreLine("quantity", "10% increased Quantity of Items found in all Voyage Areas"),
                     ScoreLine("quantity", "10% increased Qauntity of Items found in all Voyage Areas"));
    }

    [Theory]
    [InlineData("5% increased Pack Size in all Voyage Areas")]
    [InlineData("18% increased Pack Size in adjacent Areas")]
    [InlineData("Adjacent Areas contain 9 additional packs of Crabs")]
    [InlineData("Adjacent Areas contains 12 additional packs of Octopi")]
    [InlineData("Adjacent Areas contain 16 additional packs of Sea Beasts")]
    [InlineData("60% increased number of Rare Monsters in adjacent Areas")]
    [InlineData("Adjacent Areas contain 5 additional Imprisoned Monsters")]
    public void PackSizeRollsAllScore(string line) =>
        Assert.True(ScoreLine("pack size", line) > 0, line);

    [Theory]
    [InlineData("Adjacent Areas contain 3 additional Strongboxes")]
    [InlineData("Adjacent Areas contain 3 additional Diviner's Strongboxes")]
    [InlineData("Adjacent Areas contain 17 additional Clusters of Barrels")]
    [InlineData("Area contains 4 additional Treasure Anchors")]
    [InlineData("Adjacent Areas contain 4 additional Golden Lanterns")]
    [InlineData("Rings dropped in adjacent Areas have 20% chance to instead drop as a Unique Ring")]
    [InlineData("Adjacent Areas contain 2 additional cages of Tormented Spirits")]
    [InlineData("Adjacent Areas contain an additional cage of Tormented Spirits")]
    public void LootRollsAllScore(string line)
    {
        // Split across two profiles now: strongboxes are worth planning a board around,
        // the rest of the openables are their own objective.
        Assert.True(ScoreLine("strongbox", line) > 0 || ScoreLine("containers", line) > 0
                    || ScoreLine("uniques", line) > 0,
                    line);
    }

    [Fact]
    public void BiggerAdjacencyRollsAreWorthMore()
    {
        // The number in the text has to reach the score, or a 14-pack roll ranks level
        // with an 8-pack one and placement stops meaning anything.
        var small = ScoreLine("pack size", "Adjacent Areas contain 8 additional packs of Crabs");
        var large = ScoreLine("pack size", "Adjacent Areas contain 14 additional packs of Crabs");
        Assert.True(large > small, $"{large} should beat {small}");
    }

    [Theory]
    [InlineData("Players have -8% to all maximum Resistances")]
    [InlineData("Monsters are Hexproof")]
    [InlineData("50% less effect of Curses on Monsters")]
    [InlineData("Monsters cannot be Taunted")]
    [InlineData("Monsters' Action Speed cannot be modified to below Base Value")]
    [InlineData("Monsters steal Power, Frenzy and Endurance charges on Hit")]
    [InlineData("52% more Monster Life")]
    [InlineData("Area has patches of Shocked Ground which increase Damage taken by 10%")]
    public void DangerousModsArePenalisedByTheSafeProfile(string line) =>
        Assert.True(ScoreLine("safe", line) < 0, line);

    [Fact]
    public void TheSafeProfileStillPrefersHigherTier()
    {
        var low = new Chart("a", "a", ChartShape.Crossing, 68, []);
        var high = low with { AreaLevel = 83 };
        Assert.True(Profile("safe").ScoreChart(high) > Profile("safe").ScoreChart(low));
    }

    [Fact]
    public void EveryShippedPatternCompiles()
    {
        // A malformed pattern throws on first use, which would be at solve time.
        foreach (var profile in VoyageRules.Defaults())
            foreach (var rule in profile.Rules)
                Assert.True(rule.Score("harmless text") is >= double.MinValue,
                            $"{profile.Name}: {rule.Pattern}");
    }

    [Fact]
    public void ARealChartScoresThroughTheWholeChain()
    {
        // Parse real text, then score it -- the two halves have to agree on wording.
        var chart = ChartText.Parse("""
            Item Class: Voyage Charts
            Rarity: Rare
            Deep Sea Dive
            Seafloor Ridges
            --------
            Item Quantity: +96% (augmented)
            Dead Man's Sulphur: +90% (augmented)
            --------
            Item Level: 78
            --------
            { Implicit Modifier }
            Adjacent Areas contain 17(16-20) additional Clusters of Barrels
            --------
            { Prefix Modifier "Fecund" (Tier: 3) — Life }
            34(20-34)% more Monster Life
            30% increased Dead Man's Sulphur found in this Area
            """, "id", ChartShape.Straight)!;

        Assert.Equal(90, chart.Sulphur);
        Assert.Equal(96, chart.ItemQuantity);
        Assert.Equal(450, Profile("sulphur").ScoreChart(chart));      // 90 x 5, counted once
        Assert.Equal(96, Profile("quantity").ScoreChart(chart));
        Assert.Equal(25.5, Profile("containers").ScoreAdjacent(chart)); // 17 barrels x 1.5
        Assert.True(Profile("safe").ScoreChart(chart) < 78);          // level, less the danger
    }
}
