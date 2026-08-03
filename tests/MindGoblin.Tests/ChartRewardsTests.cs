using MindGoblin.Core.Voyage;

namespace MindGoblin.Tests;

/// <summary>
/// Keeps every ChartRewards test in one collection, so they do not run in parallel.
///
/// The catalogue is a process-wide static, and a couple of these swap it to check the
/// fallback paths. xUnit parallelises across CLASSES by default, so without this a test
/// reading the real table can be handed a stand-in mid-run -- which showed up as one
/// arbitrary line failing to be found.
/// </summary>
[CollectionDefinition("ChartRewards", DisableParallelization = true)]
public sealed class ChartRewardsCollection;

/// <summary>
/// Reward versus difficulty, against wording taken from real charts and from poedb's
/// tables for the three Voyage bases. A rare chart carries a dozen affixes and most are
/// monster difficulty, which is not what you are asking when you look at a planned square.
/// </summary>
[Collection("ChartRewards")]
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

/// <summary>
/// The mod split is data, not judgement: Voyage charts have three bases and poedb
/// publishes the full table for each, so the set is closed. These check the shipped
/// catalogue actually loads and covers what the game rolls.
/// </summary>
[Collection("ChartRewards")]
public class ChartCatalogueTests
{
    [Fact]
    public void TheShippedCatalogueLoads()
    {
        // If the asset is missing the built-in floor takes over silently and the reward
        // filter quietly gets much worse. The corpus ships EMBEDDED in the assembly --
        // the app is one exe with no assets folder beside it -- so assert the resource
        // is really in there and covers both categories in bulk.
        Assert.NotNull(typeof(ChartRewards).Assembly
            .GetManifestResourceStream("assets/voyage-mods.json"));

        var lines = ChartRewards.Current.Lines;
        Assert.True(lines.Count >= 80, $"only {lines.Count} lines loaded");
        Assert.True(lines.Values.Count(v => v == "reward") >= 35, "too few reward lines");
        Assert.True(lines.Values.Count(v => v == "difficulty") >= 35, "too few difficulty lines");

        // Nothing may reach the app unclassified; the generator refuses to emit that.
        Assert.DoesNotContain(lines.Values, v => v is not ("reward" or "difficulty"));
    }

    [Fact]
    public void EveryPatternInTheCatalogueCompiles()
    {
        // A bad pattern would throw on the first hover, not at load.
        foreach (var pattern in ChartRewards.Current.Reward.Concat(ChartRewards.Current.Difficulty))
            _ = new System.Text.RegularExpressions.Regex(pattern);
    }

    [Fact]
    public void NoPatternIsClassedBothWays()
    {
        // Reward wins, so an overlap would silently reclassify a difficulty mod.
        var both = ChartRewards.Current.Reward
            .Intersect(ChartRewards.Current.Difficulty)
            .ToList();
        Assert.Empty(both);
    }

    [Fact]
    public void ABrokenCatalogueFallsBackInsteadOfThrowing()
    {
        var original = ChartRewards.Current;
        try
        {
            ChartRewards.Use(new ChartRewards.Catalogue());     // empty
            // With nothing to match on, difficulty never matches, so everything is a
            // reward -- the safe direction.
            Assert.True(ChartRewards.IsReward("34% more Monster Life"));
        }
        finally
        {
            ChartRewards.Use(original);
        }
    }

    [Fact]
    public void TheCatalogueDrivesTheClassification()
    {
        var original = ChartRewards.Current;
        try
        {
            ChartRewards.Use(new ChartRewards.Catalogue
            {
                Reward = ["Mysterious Barnacle"],
                Difficulty = [@"\bMonsters?\b"],
            });
            Assert.True(ChartRewards.IsReward("Area contains a Mysterious Barnacle"));
            Assert.False(ChartRewards.IsReward("34% more Monster Life"));
        }
        finally
        {
            ChartRewards.Use(original);
        }
    }
}

/// <summary>
/// The corpus is generated by tools/fetch_voyage_mods.py straight from poedb's tables, so
/// classification is a lookup rather than a judgement. These check the two sides agree --
/// a normalisation that drifts would silently drop every line back to the pattern
/// fallback and nothing would look broken.
/// </summary>
[Collection("ChartRewards")]
public class ChartCorpusTests
{
    [Fact]
    public void TheGeneratedCorpusIsLoaded()
    {
        Assert.True(ChartRewards.Current.Lines.Count >= 80,
                    $"only {ChartRewards.Current.Lines.Count} lines loaded from the mod table");
    }

    [Theory]
    // Real lines from captured charts, exactly as ChartText produces them after it
    // reduces the rolled ranges. Each must be FOUND in the table, not merely guessed.
    [InlineData("Adjacent Areas contain 9 additional packs of Crabs")]
    [InlineData("Adjacent Areas contain 5 additional Imprisoned Monsters")]
    [InlineData("Adjacent Areas contain 17 additional Clusters of Barrels")]
    [InlineData("Adjacent Areas contain 4 additional Golden Lanterns")]
    [InlineData("Adjacent Areas contain 2 additional cages of Tormented Spirits")]
    [InlineData("10% increased Qauntity of Items found in all Voyage Areas")]
    [InlineData("7% increased Pack Size in all Voyage Areas")]
    [InlineData("9% increased Rarity of Items found in all Voyage Areas")]
    [InlineData("30% increased number of Rare Monsters in adjacent Areas")]
    [InlineData("Atziri's Influence")]
    [InlineData("17% more Monster Life")]
    [InlineData("Monsters cannot be Stunned")]
    [InlineData("+18% Monster Chaos Resistance")]
    [InlineData("+16% Monster Elemental Resistances")]
    [InlineData("Monsters are Hexproof")]
    [InlineData("Monsters Maim on Hit with Attacks")]
    [InlineData("Players have -8% to all maximum Resistances")]
    [InlineData("Monsters have 186% increased Critical Strike Chance")]
    [InlineData("+35% to Monster Critical Strike Multiplier")]
    [InlineData("Monsters take 25% reduced Extra Damage from Critical Strikes")]
    [InlineData("Monsters' skills Chain 2 additional times")]
    [InlineData("Monsters fire 2 additional Projectiles")]
    [InlineData("Area has patches of Shocked Ground which increase Damage taken by 10%")]
    [InlineData("50% less effect of Curses on Monsters")]
    public void RealChartLinesAreFoundInTheTable(string line)
    {
        Assert.True(ChartRewards.IsKnown(line),
                    $"not in the mod table -- normalised to \"{ChartRewards.Normalise(line)}\"");
    }

    [Theory]
    // The sign is the fiddly half: poedb puts it outside the value span, a chart puts it
    // in the text. Both must land on the same key.
    [InlineData("+18% Monster Chaos Resistance", "#% Monster Chaos Resistance")]
    [InlineData("-8% to all maximum Resistances", "#% to all maximum Resistances")]
    [InlineData("Monsters gain 62% of Maximum Life as Extra Maximum Energy Shield",
                "Monsters gain #% of Maximum Life as Extra Maximum Energy Shield")]
    [InlineData("Monsters  fire   2 additional Projectiles", "Monsters fire # additional Projectiles")]
    [InlineData("Adjacent Areas contain 12 additional packs of Crabs.",
                "Adjacent Areas contain # additional packs of Crabs")]
    public void NormalisationMatchesTheGenerator(string input, string expected) =>
        Assert.Equal(expected, ChartRewards.Normalise(input));

    [Fact]
    public void TheTableBeatsThePatternFallback()
    {
        var original = ChartRewards.Current;
        try
        {
            // Patterns say reward, the table says difficulty. The table must win, because
            // it is the actual mod list.
            ChartRewards.Use(new ChartRewards.Catalogue
            {
                Lines = { ["Something #% odd"] = "difficulty" },
                Reward = ["odd"],
            });
            Assert.False(ChartRewards.IsReward("Something 5% odd"));
        }
        finally
        {
            ChartRewards.Use(original);
        }
    }

    [Fact]
    public void ALineTheTableDoesNotHaveStillGetsAnAnswer()
    {
        // A modifier added by a patch, before the corpus is regenerated.
        Assert.False(ChartRewards.IsKnown("Areas contain 3 additional Sunken Chests"));
        Assert.True(ChartRewards.IsReward("Areas contain 3 additional Sunken Chests"));
    }

    [Fact]
    public void GraspingVinesIsDifficultyWithoutTheTableSayingSo()
    {
        var original = ChartRewards.Current;
        try
        {
            // 3.29.1 introduced it in the patch notes and no mod table publishes it, so
            // a regenerated corpus can arrive without it -- and "matches neither" would
            // then file the league's headline danger as a payout.
            ChartRewards.Use(new ChartRewards.Catalogue
            {
                Reward = original.Reward,
                Difficulty = original.Difficulty,
            });
            Assert.False(ChartRewards.IsKnown("Monsters apply Grasping Vines on Hit"));
            Assert.False(ChartRewards.IsReward("Monsters apply Grasping Vines on Hit"));
        }
        finally
        {
            ChartRewards.Use(original);
        }
    }

    [Fact]
    public void AnUnclassifiedLineIsShownRatherThanDropped()
    {
        var original = ChartRewards.Current;
        try
        {
            // What the generator writes when no rule covered a new modifier. Only
            // "difficulty" may hide a line: an unjudged payout that never appears is the
            // one way of being wrong nobody can spot.
            ChartRewards.Use(new ChartRewards.Catalogue
            {
                Lines = { ["Areas contain # additional Sunken Chests"] = "unclassified" },
            });
            Assert.True(ChartRewards.IsKnown("Areas contain 3 additional Sunken Chests"));
            Assert.True(ChartRewards.IsReward("Areas contain 3 additional Sunken Chests"));
        }
        finally
        {
            ChartRewards.Use(original);
        }
    }
}
