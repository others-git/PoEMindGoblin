using PoeMarketWatch.Core.Voyage;

namespace PoeMarketWatch.Tests;

/// <summary>
/// Reward versus difficulty, against wording taken from real charts and from poedb's
/// tables for the three Voyage bases. A rare chart carries a dozen affixes and most are
/// monster difficulty, which is not what you are asking when you look at a planned square.
/// </summary>
public class ChartRewardsTests
{
    [Theory]
    // Containers and spawns.
    [InlineData("Adjacent Areas contain 5 additional Imprisoned Monsters")]
    [InlineData("Adjacent Areas contain 3 additional Strongboxes")]
    [InlineData("Adjacent Areas contain 3 additional Diviner's Strongboxes")]
    [InlineData("Adjacent Areas contain 3 additional Arcanist's Strongboxes")]
    [InlineData("Adjacent Areas contain 3 additional Operative's Strongboxes")]
    [InlineData("Adjacent Areas contain 12 additional packs of Crabs")]
    [InlineData("Adjacent Areas contains 10 additional packs of Octopi")]
    [InlineData("Adjacent Areas contain 2 additional Messages in Bottles")]
    [InlineData("Adjacent Areas contain 2 additional cages of Tormented Spirits")]
    [InlineData("Adjacent Areas contain an additional cage of Tormented Spirits")]
    [InlineData("Adjacent Areas contain 17 additional Clusters of Barrels")]
    [InlineData("Adjacent Areas contains 5 additional Giant Starfish")]
    [InlineData("Adjacent Areas contain 4 additional Golden Lanterns")]
    [InlineData("Area contains 4 additional Treasure Anchors")]
    // Monsters as LOOT, not as difficulty.
    [InlineData("60% increased number of Rare Monsters in adjacent Areas")]
    [InlineData("25% increased number of Magic Monsters in all Voyage Areas")]
    [InlineData("Rare monsters that are natural inhabitants of all Voyage Areas are imprisoned by Essences")]
    [InlineData("100% chance for Rare Monsters in all Voyage Areas to be Possessed")]
    [InlineData("Rare Monsters in all Voyage Areas have 50% chance to Fracture on death")]
    [InlineData("Rare Monsters in adjacent Areas will have a Pantheon Modifier")]
    [InlineData("Rare Monsters in Area drop an additional Chaos Orb")]
    [InlineData("Monsters have a chance to be Empowered by 2000 Wildwood Wisps")]
    [InlineData("Monsters in all Voyage Areas are at least Magic")]
    // Loot conversion and upgrades.
    [InlineData("80% of Equipment dropped by monsters in adjacent Areas is converted to Gold")]
    [InlineData("Items dropped in adjacent Areas have 2% chance to be Fractured")]
    [InlineData("Rings dropped in adjacent Areas have 20% chance to instead drop as a Unique Ring")]
    [InlineData("Flasks found in all Voyage Areas have 100% chance to have 20% Quality")]
    // Headline stats in every wording, including GGG's typo.
    [InlineData("45% increased Quantity of Items found in adjacent Areas")]
    [InlineData("10% increased Qauntity of Items found in all Voyage Areas")]
    [InlineData("30% increased Rarity of Items found in adjacent Areas")]
    [InlineData("18% increased Pack Size in adjacent Areas")]
    [InlineData("70% increased Gold found in this Area")]
    [InlineData("45% increased Dead Man's Sulphur found in this Area")]
    [InlineData("40% increased explicit modifier magnitudes")]
    // League set pieces.
    [InlineData("Players in all Voyage Areas have Soul Eater")]
    [InlineData("All Voyage Areas contain Friendly Jellyfish")]
    [InlineData("Adjacent Areas contain highly prized and exotic Fish")]
    [InlineData("Atziri's Influence")]
    [InlineData("Area contains Filthscrabble")]
    [InlineData("30% Chance for Chart to not be consumed when beginning a Voyage")]
    public void RewardsAreKept(string line) => Assert.True(ChartRewards.IsReward(line), line);

    [Theory]
    [InlineData("34% more Monster Life")]
    [InlineData("29% increased Monster Damage")]
    [InlineData("+29% Monster Physical Damage Reduction")]
    [InlineData("+24% Monster Chaos Resistance")]
    [InlineData("+24% Monster Elemental Resistances")]
    [InlineData("Monsters deal 30% extra Physical Damage as Cold")]
    [InlineData("Monsters cannot be Stunned")]
    [InlineData("Monsters cannot be Taunted")]
    [InlineData("Monsters are Hexproof")]
    [InlineData("Monsters' Action Speed cannot be modified to below Base Value")]
    [InlineData("Monsters have 287% increased Critical Strike Chance")]
    [InlineData("+41% to Monster Critical Strike Multiplier")]
    [InlineData("Monsters gain 62% of Maximum Life as Extra Maximum Energy Shield")]
    [InlineData("Monsters have 80% chance to Avoid Elemental Ailments")]
    [InlineData("Monsters have +80% chance to Suppress Spell Damage")]
    [InlineData("Monsters Maim on Hit with Attacks")]
    [InlineData("Monsters Poison on Hit")]
    [InlineData("Monsters Hinder on Hit with Spells")]
    [InlineData("Monsters gain a Frenzy Charge on Hit")]
    [InlineData("Monsters steal Power, Frenzy and Endurance charges on Hit")]
    [InlineData("Monsters' skills Chain 2 additional times")]
    [InlineData("Monsters fire 2 additional Projectiles")]
    [InlineData("Monsters take 39% reduced Extra Damage from Critical Strikes")]
    [InlineData("Monsters have 60% increased Area of Effect")]
    [InlineData("30% increased Monster Movement Speed")]
    [InlineData("Players have -8% to all maximum Resistances")]
    [InlineData("50% less effect of Curses on Monsters")]
    [InlineData("Area has patches of Shocked Ground which increase Damage taken by 10%")]
    [InlineData("Monsters Inflict Withered for 2 seconds on Hit")]
    public void DifficultyIsHidden(string line) => Assert.False(ChartRewards.IsReward(line), line);

    [Fact]
    public void AnUnrecognisedLineIsShownRatherThanSwallowed()
    {
        // The reward vocabulary is open-ended and league-specific; the difficulty pool is
        // the standard, stable one. Defaulting the unknown to "reward" costs an occasional
        // extra line. Defaulting the other way would hide the next new payout.
        Assert.True(ChartRewards.IsReward("Area contains a Mysterious Barnacle"));
    }

    [Fact]
    public void DescribeLabelsTheTwoScopesBecauseTheyBehaveDifferently()
    {
        var chart = new Chart("id", "X", ChartShape.Crossing, 80,
            ["34% more Monster Life", "Area contains Filthscrabble"])
        {
            Sulphur = 90,
            ItemQuantity = 96,
            VoyageModifier = "10% increased Qauntity of Items found in all Voyage Areas",
            AdjacentModifier = "Adjacent Areas contain 17 additional Clusters of Barrels",
        };

        var lines = ChartRewards.Describe(chart);
        Assert.Contains("Item Quantity: +96%", lines);
        Assert.Contains("Dead Man's Sulphur: +90", lines);
        Assert.Contains(lines, l => l.StartsWith("Voyage-wide: "));
        Assert.Contains(lines, l => l.StartsWith("Adjacent: "));
        Assert.Contains("Area contains Filthscrabble", lines);
        Assert.DoesNotContain("34% more Monster Life", lines);
        Assert.Equal(1, ChartRewards.DifficultyCount(chart));
    }

    [Fact]
    public void ARealChartShowsOnlyItsPayout()
    {
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
            { Prefix Modifier "Savage" (Tier: 3) — Damage }
            17(15-20)% increased Monster Damage
            { Suffix Modifier "of Carnage" (Tier: 1) }
            Monsters Maim on Hit with Attacks
            """, "id", ChartShape.Straight)!;

        var lines = ChartRewards.Describe(chart);
        Assert.DoesNotContain(lines, l => l.Contains("Monster"));
        Assert.Contains(lines, l => l.Contains("Clusters of Barrels"));
        Assert.Contains("Item Quantity: +96%", lines);
        Assert.Equal(3, ChartRewards.DifficultyCount(chart));
    }
}
