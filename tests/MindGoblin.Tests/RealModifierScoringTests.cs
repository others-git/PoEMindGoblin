using MindGoblin.Core.Voyage;

namespace MindGoblin.Tests;

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

    /// <summary>
    /// A profile that has been asked about danger.
    ///
    /// No shipped strategy weights Danger any more, but the stat did not retire with the
    /// preset that used it: the slider is how a user says "steer me away from run-enders",
    /// and it is the one place a stat arrives without a strategy's opinion attached. The
    /// catalog stores danger as a positive SEVERITY, so borrowing it at the wrong sign
    /// would ask for MORE hexproof -- which is why these lines still need pinning.
    /// </summary>
    private static VoyageProfile DangerAware() =>
        WeightCategories.Blended(Profile("sulphur"),
            new Dictionary<string, int> { ["Danger"] = WeightCategories.Max },
            VoyageRules.Defaults());

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
    // scores the adjacent rolls and silently misses every global one. The dedicated
    // quantity profile is retired, but "dump" still has to see these to know the
    // chart is a keeper -- the typo knowledge must not retire with it.
    [InlineData("8% increased Qauntity of Items found in all Voyage Areas")]
    [InlineData("10% increased Qauntity of Items found in all Voyage Areas")]
    [InlineData("45% increased Quantity of Items found in adjacent Areas")]
    [InlineData("20% increased Quantity of Items found in adjacent Areas")]
    public void BothSpellingsOfQuantityScore(string line) =>
        Assert.True(ScoreLine("dump", line) < 0, line);

    [Fact]
    public void TheTypoAndTheCorrectSpellingScoreTheSame()
    {
        Assert.Equal(ScoreLine("dump", "10% increased Quantity of Items found in all Voyage Areas"),
                     ScoreLine("dump", "10% increased Qauntity of Items found in all Voyage Areas"));
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
        Assert.True(ScoreLine("rare monsters", line) > 0, line);

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
        // Split across two profiles now: boxes and lanterns feed the bottles plan, while
        // barrels, anchors, cages and unique conversions are tradeable value and sit with
        // the currency plan.
        Assert.True(ScoreLine("bottles", line) > 0 || ScoreLine("currency", line) > 0, line);
    }

    [Fact]
    public void BiggerAdjacencyRollsAreWorthMore()
    {
        // The number in the text has to reach the score, or a 14-pack roll ranks level
        // with an 8-pack one and placement stops meaning anything.
        var small = ScoreLine("rare monsters", "Adjacent Areas contain 8 additional packs of Crabs");
        var large = ScoreLine("rare monsters", "Adjacent Areas contain 14 additional packs of Crabs");
        Assert.True(large > small, $"{large} should beat {small}");
    }

    [Theory]
    [InlineData("Monsters are Hexproof")]
    [InlineData("50% less effect of Curses on Monsters")]
    [InlineData("Monsters cannot be Taunted")]
    [InlineData("Monsters' Action Speed cannot be modified to below Base Value")]
    [InlineData("Monsters steal Power, Frenzy and Endurance charges on Hit")]
    [InlineData("52% more Monster Life")]
    [InlineData("Area has patches of Shocked Ground which increase Damage taken by 10%")]
    public void DangerousModsArePenalisedWhenTheDangerSliderIsRaised(string line) =>
        Assert.True(DangerAware().ScoreText([line]) < 0, line);

    [Fact]
    public void TheTierGatedProfilePrefersAHigherAreaLevel()
    {
        // "bottles" is the last upright profile that weights area level, and the reason is
        // mechanical rather than a preference: the bottle mod is hidden-until-charted and
        // only rolls on ilvl 68+, so a low chart cannot carry the thing the plan is for.
        var low = new Chart("a", "a", ChartShape.Crossing, 68, []);
        var high = low with { AreaLevel = 83 };
        Assert.True(Profile("bottles").ScoreChart(high) > Profile("bottles").ScoreChart(low));
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
        Assert.Equal(25.5, Profile("currency").ScoreAdjacent(chart)); // 17 barrels x 1.5

        // ...and the danger it rolled reaches the score too. "34(20-34)% more Monster
        // Life" is a prefix like any other, so a board asked about danger has to come out
        // BELOW the same board that was not asked -- the parse and the rule have to agree
        // on the wording for that to happen at all.
        Assert.True(DangerAware().ScoreChart(chart) < Profile("sulphur").ScoreChart(chart));
    }
}
