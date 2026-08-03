using MindGoblin.Core.Voyage;

namespace MindGoblin.Tests;

/// <summary>
/// The tileset a chart opens -- Anchorfield, Seafloor Ridges, Abyssal Plain, Undersea
/// Groves -- is stated on every chart and is a real reward difference: some are thick with
/// Sunken Loot chests, which no chart modifier accounts for.
///
/// There is no published list of them. poedb documents the four chart bases and nothing
/// about the areas they open, so this is captured from what the user is holding rather
/// than looked up.
/// </summary>
[Collection("ChartRewards")]
public class TilesetTests
{
    /// <summary>The in-game HOVER, which labels the special modifier and puts the tileset third.</summary>
    private const string Hover = """
        Oceanic Quest
        Sandy Seabed Chart
        Anchorfield
        Area Level: 81
        Item Quantity: +112%
        Dead Man's Sulphur: +30%
        Requires Level 64
        Adjacent Modifier:
        Adjacent Areas contain 3 additional Operative's Strongboxes
        +20% Monster Chaos Resistance
        Monsters cannot be Taunted
        30% increased Dead Man's Sulphur found in this Area
        """;

    [Fact]
    public void ABareLabelClaimsTheLineBeneathIt()
    {
        // The hover puts "Adjacent Modifier:" on its own line with the value under it,
        // where the copied text uses "{ Implicit Modifier }". Untreated, the bare label
        // fell through and was taken for the chart's name.
        var chart = ChartText.Parse(Hover, "id", ChartShape.Straight)!;
        Assert.Equal("Adjacent Areas contain 3 additional Operative's Strongboxes",
                     chart.AdjacentModifier);
        Assert.Equal("Oceanic Quest", chart.Name);
    }

    [Fact]
    public void TheHoverStillYieldsEveryStat()
    {
        var chart = ChartText.Parse(Hover, "id", ChartShape.Straight)!;
        Assert.Equal(81, chart.AreaLevel);
        Assert.Equal(112, chart.ItemQuantity);
        Assert.Equal(30, chart.Sulphur);
        Assert.Equal(64, chart.RequiresLevel);
        Assert.Contains("Monsters cannot be Taunted", chart.Modifiers);
    }

    [Fact]
    public void TheTilesetIsScorable()
    {
        // Exposed as a line so preferring a tileset needs no new machinery, just a rule.
        var chart = new Chart("id", "x", ChartShape.Crossing, 81, []) { AreaName = "Anchorfield" };
        Assert.Contains("Area: Anchorfield", chart.OwnLines());

        var profile = new VoyageProfile
        {
            Name = "t",
            Rules = [new VoyageRule { Pattern = "Area: Anchorfield", Weight = 25 }],
        };
        Assert.Equal(25, profile.ScoreChart(chart));
    }

    [Fact]
    public void AChartWithNoTilesetAddsNoLine()
    {
        var chart = new Chart("id", "x", ChartShape.Crossing, 81, []);
        Assert.DoesNotContain(chart.OwnLines(), l => l.StartsWith("Area:"));
    }

    [Fact]
    public void TheShippedRuleValuesTheObservedTileset()
    {
        var bottles = VoyageRules.Defaults().Single(p => p.Name == "bottles");
        var anchorfield = new Chart("a", "a", ChartShape.Crossing, 81, []) { AreaName = "Anchorfield" };
        var elsewhere = new Chart("b", "b", ChartShape.Crossing, 81, []) { AreaName = "Abyssal Plain" };

        Assert.True(bottles.ScoreChart(anchorfield) > bottles.ScoreChart(elsewhere));
    }

    [Fact]
    public void TheSessionListsTheTilesetsYouHold()
    {
        // The only way to learn which tilesets exist is to look at the panel, so the app
        // has to report them.
        var session = new VoyageSession();
        session.ApplyPanelRead(Enumerable.Range(1, 4).Select(i =>
            new ChartPanelReader.ReadCell(i, 0, i - 1, true, true, true, true) { Level = 80 })
            .ToList());

        session.ApplyChartText(1, "A\nAnchorfield\nItem Quantity: +10%");
        session.ApplyChartText(2, "B\nAnchorfield\nItem Quantity: +10%");
        session.ApplyChartText(3, "C\nAbyssal Plain\nItem Quantity: +10%");

        var tilesets = session.Tilesets;
        Assert.Equal(2, tilesets.Count);                       // chart 4 was never hovered
        Assert.Equal(("Anchorfield", 2), tilesets[0]);         // most charts first
        Assert.Equal(("Abyssal Plain", 1), tilesets[1]);
    }

    [Fact]
    public void ThePanelSubHeadingIsNotAModifier()
    {
        // The Area Modifiers panel prints "Modifiers from Adjacent:" above the list; it
        // groups what follows rather than being one of them.
        var reading = AreaModifierPanel.Read([
            "Area Modifiers",
            "Modifiers from Adjacent:",
            "Area contains 4 additional Treasure Anchors",
        ]);
        Assert.Equal(["Area contains 4 additional Treasure Anchors"], reading.Lines);
    }
}

/// <summary>
/// Every chart carries exactly one implicit, and it is either a Voyage modifier (global,
/// applies wherever the chart sits) or an Adjacent one (applies to its neighbours). That
/// split is the whole reason placement matters, so the parser has to land on exactly one
/// of them for any chart it reads.
/// </summary>
[Collection("ChartRewards")]
public class ImplicitScopeTests
{
    private static Chart Parse(string implicitLine) => ChartText.Parse(
        string.Join("\n",
            "Item Class: Voyage Charts",
            "Rarity: Rare",
            "Oceanic Quest",
            "Anchorfield",
            "--------",
            "Item Quantity: +112% (augmented)",
            "--------",
            "Item Level: 81",
            "--------",
            "{ Implicit Modifier }",
            implicitLine,
            "--------",
            "{ Prefix Modifier \"Fecund\" (Tier: 1) — Life }",
            "46(46-60)% more Monster Life"),
        "id", ChartShape.Straight)!;

    [Theory]
    // Global wordings, from the generated table.
    [InlineData("10% increased Qauntity of Items found in all Voyage Areas", true)]
    [InlineData("7% increased Pack Size in all Voyage Areas", true)]
    [InlineData("Players in all Voyage Areas have Soul Eater", true)]
    [InlineData("Rare monsters that are natural inhabitants of all Voyage Areas are imprisoned by Essences", true)]
    // Adjacency wordings.
    [InlineData("Adjacent Areas contain 3 additional Operative's Strongboxes", false)]
    [InlineData("45% increased Quantity of Items found in adjacent Areas", false)]
    [InlineData("Rings dropped in adjacent Areas have 20% chance to instead drop as a Unique Ring", false)]
    // Neither phrase; still the chart's implicit, so it must land somewhere.
    [InlineData("Atziri's Influence", false)]
    [InlineData("Monsters have a chance to be Empowered by 2000 Wildwood Wisps", false)]
    public void EveryImplicitLandsInExactlyOneScope(string line, bool global)
    {
        var chart = Parse(line);

        var scopes = new[] { chart.VoyageModifier, chart.AdjacentModifier }
            .Count(m => !string.IsNullOrEmpty(m));
        Assert.True(scopes == 1, $"landed in {scopes} scopes, not 1");

        Assert.Equal(global, !string.IsNullOrEmpty(chart.VoyageModifier));
        Assert.Equal(line, global ? chart.VoyageModifier : chart.AdjacentModifier);

        // And it must not ALSO be counted among the ordinary modifiers.
        Assert.DoesNotContain(line, chart.Modifiers);
    }

    [Fact]
    public void AGlobalImplicitCountsTowardsTheChartAndAnAdjacentOneTowardsItsNeighbours()
    {
        // The whole point of the split: one is paid where the chart sits, the other is
        // paid to whatever is next to it.
        var profile = new VoyageProfile
        {
            Name = "q",
            Rules = [new VoyageRule { Pattern = @"(\d+)% increased Q(?:uantity|auntity) of Items", Weight = 1 }],
        };

        var global = Parse("10% increased Qauntity of Items found in all Voyage Areas");
        Assert.Equal(10, profile.ScoreChart(global));
        Assert.Equal(0, profile.ScoreAdjacent(global));

        var adjacent = Parse("45% increased Quantity of Items found in adjacent Areas");
        Assert.Equal(0, profile.ScoreChart(adjacent));
        Assert.Equal(45, profile.ScoreAdjacent(adjacent));
    }
}
