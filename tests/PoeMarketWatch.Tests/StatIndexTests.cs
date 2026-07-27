using System.Text.Json;
using PoeMarketWatch.Core;

namespace PoeMarketWatch.Tests;

public class StatIndexTests
{
    // Real ids, and the real spawn facts they encode.
    private const string CastSpeed = "explicit.stat_2891184298";      // ring: suffix; gloves: shaper only
    private const string AtkSpeedGlobal = "explicit.stat_681332047";  // rings/gloves, NOT weapons
    private const string EleWithAttacks = "explicit.stat_387439868";
    private const string AddFireAttacks = "explicit.stat_1573130764";

    private static StatIndex Build()
    {
        var text = new Dictionary<string, string>
        {
            [CastSpeed] = "#% increased Cast Speed",
            [AtkSpeedGlobal] = "#% increased Attack Speed",
            [EleWithAttacks] = "#% increased Elemental Damage with Attack Skills",
            [AddFireAttacks] = "Adds # to # Fire Damage to Attacks",
        };
        var spawns = new Dictionary<string, Dictionary<string, StatIndex.Spawn>>(StringComparer.OrdinalIgnoreCase)
        {
            ["accessory.ring"] = new()
            {
                [CastSpeed] = new("Suffix", "IncreasedCastSpeed", null),
                [AtkSpeedGlobal] = new("Suffix", "IncreasedAttackSpeed", null),
                [EleWithAttacks] = new("Prefix", "IncreasedWeaponElementalDamagePercent", null),
                [AddFireAttacks] = new("Prefix", "FireDamage", null),
            },
            ["armour.gloves"] = new()
            {
                [CastSpeed] = new("Suffix", "IncreasedCastSpeedSupported", "shaper"),
                [AtkSpeedGlobal] = new("Suffix", "IncreasedAttackSpeed", null),
            },
            // claws roll LOCAL variants, so none of the global ids appear here
            ["weapon.claw"] = new(),
        };
        return new StatIndex(text, spawns);
    }

    private static JsonElement Q(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static string Query(string category, params string[] ids)
    {
        var filters = string.Join(",", ids.Select(i => "{\"id\":\"" + i + "\"}"));
        return "{\"query\":{\"filters\":{\"type_filters\":{\"filters\":{\"category\":{\"option\":\""
               + category + "\"}}}},\"stats\":[{\"type\":\"and\",\"filters\":[" + filters + "]}]}}";
    }

    [Fact]
    public void ExtractsCategoryAndStatIds()
    {
        var q = Q(Query("accessory.ring", CastSpeed, AtkSpeedGlobal));
        Assert.Equal("accessory.ring", StatIndex.CategoryOf(q));
        Assert.Equal(new[] { CastSpeed, AtkSpeedGlobal }, StatIndex.StatIdsOf(q));
    }

    [Fact]
    public void SkipsDisabledFilters()
    {
        var q = Q("{\"query\":{\"stats\":[{\"filters\":["
                  + "{\"id\":\"" + CastSpeed + "\"},"
                  + "{\"id\":\"" + AtkSpeedGlobal + "\",\"disabled\":true}]}]}}");
        Assert.Equal(new[] { CastSpeed }, StatIndex.StatIdsOf(q));
    }

    [Fact]
    public void CleanQueryProducesNoFindings()
    {
        var findings = Build().Review(Q(Query("accessory.ring", CastSpeed, AddFireAttacks)));
        Assert.Empty(findings);
    }

    [Fact]
    public void FlagsStatThatCannotSpawnOnThatCategory()
    {
        // The exact bug the path-of-claude validator caught: global attack speed on a claw.
        var findings = Build().Review(Q(Query("weapon.claw", AtkSpeedGlobal)));
        var f = Assert.Single(findings);
        Assert.Equal(StatIndex.Severity.Error, f.Level);
        Assert.Contains("cannot spawn on weapon.claw", f.Message);
    }

    [Fact]
    public void WarnsWhenInfluenceIsRequired()
    {
        var findings = Build().Review(Q(Query("armour.gloves", CastSpeed)));
        var f = Assert.Single(findings);
        Assert.Equal(StatIndex.Severity.Warning, f.Level);
        Assert.Contains("shaper influence", f.Message);
    }

    [Fact]
    public void WarnsWhenTheSameFilterAppearsTwice()
    {
        var findings = Build().Review(Q(Query("accessory.ring", CastSpeed, CastSpeed)));
        Assert.Contains(findings, f => f.Message.Contains("appears twice"));
    }

    [Fact]
    public void WarnsWhenTwoDifferentFiltersShareAModGroup()
    {
        // Two distinct stat ids that resolve to one exclusive mod group cannot both roll.
        var text = new Dictionary<string, string> { ["a"] = "Stat A", ["b"] = "Stat B" };
        var spawns = new Dictionary<string, Dictionary<string, StatIndex.Spawn>>(StringComparer.OrdinalIgnoreCase)
        {
            ["accessory.ring"] = new()
            {
                ["a"] = new("Suffix", "SharedGroup", null),
                ["b"] = new("Suffix", "SharedGroup", null),
            },
        };
        var findings = new StatIndex(text, spawns).Review(Q(Query("accessory.ring", "a", "b")));
        Assert.Contains(findings, f => f.Message.Contains("mod group"));
    }

    [Fact]
    public void UnknownStatIsInfoNotError()
    {
        // Implicit/crafted/veiled stats are outside the index. Reporting them as invalid
        // would make this worse than the in-game filter.
        var findings = Build().Review(Q(Query("accessory.ring", "implicit.stat_999")));
        var f = Assert.Single(findings);
        Assert.Equal(StatIndex.Severity.Info, f.Level);
        Assert.Contains("not checked", f.Message);
    }

    [Fact]
    public void UnknownCategorySkipsChecksRatherThanFailing()
    {
        var findings = Build().Review(Q(Query("armour.nonsense", CastSpeed)));
        var f = Assert.Single(findings);
        Assert.Equal(StatIndex.Severity.Info, f.Level);
        Assert.Contains("skipped", f.Message);
    }

    [Fact]
    public void NoStatsMeansNothingToSay()
    {
        Assert.Empty(Build().Review(Q("{\"query\":{}}")));
    }

    [Fact]
    public void MissingCategoryIsReportedNotCrashed()
    {
        var q = Q("{\"query\":{\"stats\":[{\"filters\":[{\"id\":\"" + CastSpeed + "\"}]}]}}");
        var f = Assert.Single(Build().Review(q));
        Assert.Equal(StatIndex.Severity.Info, f.Level);
    }

    // ------------------------------------------------------- the shipped asset
    [Fact]
    public void ShippedIndexLoadsAndEncodesRealSpawnRules()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "assets", "trade-index.json");
        Assert.True(File.Exists(path), $"asset missing at {path}");

        var index = StatIndex.Load(path);
        Assert.True(index.StatCount > 500, $"only {index.StatCount} stats");
        Assert.True(index.Categories.Count > 15);

        // ring cast speed: plain suffix, no influence
        var ring = index.SpawnOf("accessory.ring", CastSpeed);
        Assert.NotNull(ring);
        Assert.Equal("Suffix", ring!.AffixType);
        Assert.Null(ring.Influence);

        // gloves cast speed: same stat, Shaper-gated
        var gloves = index.SpawnOf("armour.gloves", CastSpeed);
        Assert.NotNull(gloves);
        Assert.Equal("shaper", gloves!.Influence);

        // weapons use a LOCAL attack speed id, so the global one must be absent
        Assert.Null(index.SpawnOf("weapon.claw", AtkSpeedGlobal));
        Assert.NotNull(index.SpawnOf("weapon.claw", "explicit.stat_210067635"));
    }

    [Fact]
    public void ReviewsARealQueryAgainstTheShippedIndex()
    {
        var index = StatIndex.Load(
            Path.Combine(AppContext.BaseDirectory, "assets", "trade-index.json"));

        Assert.Empty(index.Review(Q(Query("accessory.ring", AddFireAttacks, EleWithAttacks))));

        var bad = index.Review(Q(Query("weapon.claw", AtkSpeedGlobal)));
        Assert.Contains(bad, f => f.Level == StatIndex.Severity.Error);
    }
}
