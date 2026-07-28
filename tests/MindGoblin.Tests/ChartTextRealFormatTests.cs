using MindGoblin.Core.Voyage;

namespace MindGoblin.Tests;

/// <summary>
/// The parser against what the game actually puts on the clipboard.
///
/// Every fixture here is real text from a captured session, not an invented tooltip. The
/// first version of this parser was written against a guess at the format and got four
/// things wrong that only real copies revealed.
/// </summary>
public class ChartTextRealFormatTests
{
    /// <summary>Chart 2 of a real board, copied verbatim from the game.</summary>
    private const string DeepwaterDescent = """
        Item Class: Voyage Charts
        Rarity: Rare
        Deepwater Descent
        Seafloor Ridges
        --------
        Item Quantity: +76% (augmented)
        Gold Found: +70% (augmented)
        Dead Man's Sulphur: +30% (augmented)
        --------
        Requirements:
        Level: 54
        --------
        Item Level: 71
        --------
        { Implicit Modifier }
        Adjacent Areas contain 9(8-10) additional packs of Crabs
        --------
        { Prefix Modifier "Unwavering" (Tier: 1) — Life }
        17(10-20)% more Monster Life
        Monsters cannot be Stunned
        { Prefix Modifier "Resistant" (Tier: 3) — Elemental, Chaos, Resistance }
        +18(10-19)% Monster Chaos Resistance
        +16(11-19)% Monster Elemental Resistances
        { Suffix Modifier "of Carnage" (Tier: 1) }
        Monsters Maim on Hit with Attacks
        (Maimed enemies have 30% reduced Movement Speed)
        30% increased Dead Man's Sulphur found in this Area
        --------
        Take this item to Valerie aboard the Sovereign to Chart this area.
        """;

    /// <summary>Chart 42: a global modifier, and GGG's "Qauntity" typo.</summary>
    private const string OceanicExcursion = """
        Item Class: Voyage Charts
        Rarity: Rare
        Oceanic Excursion
        Undersea Groves
        --------
        Item Quantity: +120% (augmented)
        Dead Man's Sulphur: +30% (augmented)
        --------
        Item Level: 83
        --------
        { Implicit Modifier }
        10% increased Qauntity of Items found in all Voyage Areas
        --------
        { Prefix Modifier "Armoured" (Tier: 1) — Physical }
        +29(21-35)% Monster Physical Damage Reduction
        { Suffix Modifier "of Carnage" (Tier: 1) }
        Monsters Maim on Hit with Attacks
        30% increased Dead Man's Sulphur found in this Area
        --------
        Take this item to Valerie aboard the Sovereign to Chart this area.
        """;

    [Fact]
    public void TheImplicitIsRecognisedAsAnAdjacencyModifierWithNoLabel()
    {
        // The copied text has no "Adjacent Modifier:" label at all -- the scope has to be
        // read from the wording. Missing this scored every adjacency effect as the
        // chart's own value, so no chart was ever pulled towards a busy square.
        var c = ChartText.Parse(DeepwaterDescent, "id", ChartShape.Straight)!;
        Assert.Equal("Adjacent Areas contain 9 additional packs of Crabs", c.AdjacentModifier);
        Assert.Null(c.VoyageModifier);
        Assert.True(c.HasAdjacentModifier);
    }

    [Fact]
    public void AGlobalImplicitIsRecognisedAsVoyageWide()
    {
        var c = ChartText.Parse(OceanicExcursion, "id", ChartShape.Straight)!;
        Assert.Equal("10% increased Qauntity of Items found in all Voyage Areas", c.VoyageModifier);
        Assert.Null(c.AdjacentModifier);
    }

    [Fact]
    public void RolledRangesAreReducedToTheRolledValue()
    {
        // "9(8-10) additional packs" -- a rule matching "(\d+) additional packs" fails on
        // that, because after the digits comes a bracket rather than a space.
        var c = ChartText.Parse(DeepwaterDescent, "id", ChartShape.Straight)!;
        Assert.DoesNotContain("(", c.AdjacentModifier!);
        Assert.Contains("17% more Monster Life", c.Modifiers);
        Assert.Contains("+18% Monster Chaos Resistance", c.Modifiers);
    }

    [Fact]
    public void NegativeRangesReduceCorrectly()
    {
        var c = ChartText.Parse("Briny Trek\nAbyssal Plain\n--------\n"
                                + "Players have -8(-9--6)% to all maximum Resistances")!;
        Assert.Contains("Players have -8% to all maximum Resistances", c.Modifiers);
    }

    [Fact]
    public void AffixHeadersAndRemindersAreNotModifiers()
    {
        var c = ChartText.Parse(DeepwaterDescent, "id", ChartShape.Straight)!;
        Assert.DoesNotContain(c.Modifiers, m => m.StartsWith("{"));
        Assert.DoesNotContain(c.Modifiers, m => m.StartsWith("("));
        Assert.DoesNotContain(c.Modifiers, m => m.Contains("Take this item to Valerie"));
        Assert.DoesNotContain(c.Modifiers, m => m.Contains("Requirements"));
        Assert.DoesNotContain(c.Modifiers, m => m == "Item Level: 71");
    }

    [Fact]
    public void StatContributionsAreNotAlsoScoredAsModifiers()
    {
        // "Dead Man's Sulphur: +30%" IS the sum of the "30% increased Dead Man's Sulphur
        // found in this Area" affix lines. Keeping both counts the same sulphur twice --
        // on a chart with three such lines it trebles the score.
        var c = ChartText.Parse(DeepwaterDescent, "id", ChartShape.Straight)!;
        Assert.Equal(30, c.Sulphur);
        Assert.DoesNotContain(c.Modifiers, m => m.Contains("Dead Man's Sulphur"));

        var sulphur = VoyageRules.Defaults().Single(p => p.Name == "sulphur");
        Assert.Equal(30 * 5.0, sulphur.ScoreChart(c));      // the stat, once
    }

    [Fact]
    public void NameAndAreaComeFromTheFirstTwoUnlabelledLines()
    {
        var c = ChartText.Parse(DeepwaterDescent, "id", ChartShape.Straight)!;
        Assert.Equal("Deepwater Descent", c.Name);
        Assert.Equal("Seafloor Ridges", c.AreaName);
    }

    [Fact]
    public void ItemLevelIsTheAreaLevel()
    {
        Assert.Equal(71, ChartText.Parse(DeepwaterDescent, "id", ChartShape.Straight)!.AreaLevel);
        Assert.Equal(54, ChartText.Parse(DeepwaterDescent, "id", ChartShape.Straight)!.RequiresLevel);
    }

    [Fact]
    public void AllTheHeadlineStatsAreRead()
    {
        var c = ChartText.Parse(DeepwaterDescent, "id", ChartShape.Straight)!;
        Assert.Equal(76, c.ItemQuantity);
        Assert.Equal(70, c.GoldFound);
        Assert.Equal(30, c.Sulphur);
    }

    [Fact]
    public void MonsterModifiersSurviveForTheSafetyProfile()
    {
        var c = ChartText.Parse(DeepwaterDescent, "id", ChartShape.Straight)!;
        Assert.Contains("Monsters cannot be Stunned", c.Modifiers);
        Assert.Contains("Monsters Maim on Hit with Attacks", c.Modifiers);
    }

    [Fact]
    public void AnImplicitWithNoStatedScopeStillTravelsWithTheChart()
    {
        // "Atziri's Influence" is an implicit that names no scope. It is still the
        // chart's special modifier and still affects its neighbours.
        var c = ChartText.Parse("""
            Offshore Voyage
            Undersea Groves
            --------
            Item Level: 83
            --------
            { Implicit Modifier }
            Atziri's Influence
            --------
            { Prefix Modifier "Fecund" (Tier: 1) — Life }
            46(46-60)% more Monster Life
            """, "id", ChartShape.Corner)!;

        Assert.Equal("Atziri's Influence", c.AdjacentModifier);
        Assert.Contains("46% more Monster Life", c.Modifiers);
    }

    [Fact]
    public void OnlyTheFirstImplicitBlockCounts()
    {
        var c = ChartText.Parse("""
            X
            Y
            --------
            { Implicit Modifier }
            Adjacent Areas contain 4 additional Golden Lanterns
            --------
            { Prefix Modifier "Savage" (Tier: 1) — Damage }
            29(26-35)% increased Monster Damage
            """, "id", ChartShape.Straight)!;

        Assert.Equal("Adjacent Areas contain 4 additional Golden Lanterns", c.AdjacentModifier);
        Assert.Contains("29% increased Monster Damage", c.Modifiers);
    }

    [Fact]
    public void ScopeIsWhatSeparatesOwnValueFromAdjacency()
    {
        // The whole reason scope matters: an adjacency modifier must NOT count towards
        // the chart's own value, or it is paid for twice.
        var profile = new VoyageProfile
        {
            Name = "packs",
            Rules = [new VoyageRule { Pattern = @"(\d+) additional packs", Weight = 10 }],
        };
        var c = ChartText.Parse(DeepwaterDescent, "id", ChartShape.Straight)!;

        Assert.Equal(0, profile.ScoreChart(c));
        Assert.Equal(90, profile.ScoreAdjacent(c));      // 9 packs x 10
    }
}
