using MindGoblin.Core.Voyage;

namespace MindGoblin.Tests;

public class ChartTextTests
{
    private const string Copied = """
        Item Class: Voyage Charts
        Rarity: Rare
        Tempest Reach
        Seafloor Ridges
        --------
        Area Level: 83
        Item Quantity: +42% (augmented)
        Item Rarity: +18% (augmented)
        Monster Pack Size: +12% (augmented)
        Gold Found: +75% (augmented)
        Dead Man's Sulphur: +9 (augmented)
        --------
        Requires Level 80
        --------
        Voyage Modifier: 8% increased Quantity of Items found in all Voyage Areas
        Adjacent Modifier: Adjacent Areas contain 2 additional Strongboxes
        --------
        Monsters deal 25% extra Physical Damage as Fire
        Area contains many Totems
        """;

    [Fact]
    public void ReadsEveryLabelledNumber()
    {
        var c = ChartText.Parse(Copied)!;
        Assert.Equal(83, c.AreaLevel);
        Assert.Equal(42, c.ItemQuantity);
        Assert.Equal(18, c.ItemRarity);
        Assert.Equal(12, c.MonsterPackSize);
        Assert.Equal(75, c.GoldFound);
        Assert.Equal(9, c.Sulphur);
        Assert.Equal(80, c.RequiresLevel);
    }

    [Fact]
    public void SeparatesTheTwoModifierKinds()
    {
        // These behave completely differently in the solver -- one is global, one only
        // touches neighbours -- so conflating them would silently misprice every layout.
        var c = ChartText.Parse(Copied)!;
        Assert.Equal("8% increased Quantity of Items found in all Voyage Areas", c.VoyageModifier);
        Assert.Equal("Adjacent Areas contain 2 additional Strongboxes", c.AdjacentModifier);
        Assert.True(c.HasAdjacentModifier);
    }

    [Fact]
    public void KeepsUnrecognisedLinesSoRulesCanStillMatchThem()
    {
        var c = ChartText.Parse(Copied)!;
        Assert.Contains("Monsters deal 25% extra Physical Damage as Fire", c.Modifiers);
        Assert.Contains("Area contains many Totems", c.Modifiers);
        Assert.DoesNotContain(c.Modifiers, m => m.StartsWith("Item Quantity"));
    }

    [Fact]
    public void TakesNameAndAreaFromTheUnlabelledOpeningLines()
    {
        var c = ChartText.Parse(Copied)!;
        Assert.Equal("Tempest Reach", c.Name);
        Assert.Equal("Seafloor Ridges", c.AreaName);
    }

    [Fact]
    public void OrderIsIrrelevant()
    {
        // The parser scans for labels rather than expecting a layout, so a patch that
        // reorders the tooltip does not break it.
        var shuffled = string.Join("\n", Copied.Split('\n').Reverse());
        var c = ChartText.Parse(shuffled)!;
        Assert.Equal(83, c.AreaLevel);
        Assert.Equal(9, c.Sulphur);
        Assert.Equal("Adjacent Areas contain 2 additional Strongboxes", c.AdjacentModifier);
    }

    [Fact]
    public void SurvivesTooltipTextWithoutSectionRules()
    {
        // Hover text read by eye has no "--------" separators at all.
        var plain = """
            Storm Hollow
            Area Level: 76
            Monster Pack Size: +20%
            Adjacent Modifier: Adjacent Areas contain 8 additional packs of Sea Beasts
            """;
        var c = ChartText.Parse(plain)!;
        Assert.Equal("Storm Hollow", c.Name);
        Assert.Equal(76, c.AreaLevel);
        Assert.Equal(20, c.MonsterPackSize);
        Assert.StartsWith("Adjacent Areas contain 8", c.AdjacentModifier);
    }

    [Fact]
    public void NegativeAndDecimalValuesParse()
    {
        var c = ChartText.Parse("X\nItem Quantity: -15%\nDead Man's Sulphur: +2.5")!;
        Assert.Equal(-15, c.ItemQuantity);
        Assert.Equal(2.5, c.Sulphur);
    }

    [Fact]
    public void UsesTheShapeHintBecauseTheTooltipDoesNotStateIt()
    {
        var c = ChartText.Parse(Copied, "panel-7", ChartShape.Junction)!;
        Assert.Equal(ChartShape.Junction, c.Shape);
        Assert.Equal("panel-7", c.Id);
    }

    [Fact]
    public void AnExplicitShapeLineWinsOverTheHint()
    {
        var c = ChartText.Parse("X\nShape: Corner", "id", ChartShape.Crossing)!;
        Assert.Equal(ChartShape.Corner, c.Shape);
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("   ")] [InlineData("\n--------\n")]
    public void EmptyOrDecorativeTextIsNotAChart(string? text) =>
        Assert.Null(ChartText.Parse(text));

    [Fact]
    public void ScorableLinesCoverBothModifierKindsAndTheRest()
    {
        var lines = ChartText.ScorableLines(ChartText.Parse(Copied)!).ToList();
        Assert.Contains(lines, l => l.StartsWith("8% increased Quantity"));
        Assert.Contains(lines, l => l.StartsWith("Adjacent Areas contain 2"));
        Assert.Contains(lines, l => l.StartsWith("Monsters deal 25%"));
    }

    [Fact]
    public void ALabelWithNoNumberDoesNotBecomeAModifierLine()
    {
        // "Item Quantity:" with the value stripped must not fall through and get scored
        // as though it were a monster mod.
        var c = ChartText.Parse("X\nItem Quantity:")!;
        Assert.Equal(0, c.ItemQuantity);
        Assert.DoesNotContain(c.Modifiers, m => m.Contains("Item Quantity"));
    }

    [Fact]
    public void AWordStartingWithALabelIsNotTreatedAsThatLabel()
    {
        // "Area Levelling" must not be read as "Area Level".
        var c = ChartText.Parse("X\nArea Levelling Grounds of the Deep")!;
        Assert.Equal(0, c.AreaLevel);
    }
}
