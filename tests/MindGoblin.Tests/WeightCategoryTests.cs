using MindGoblin.Core.Voyage;

namespace MindGoblin.Tests;

/// <summary>
/// The sliders scale CATEGORIES, not rules: the shipped weights encode relative truths
/// (a Divine is 100 chaos) the user should not have to re-derive to say "more sulphur".
/// </summary>
public class WeightCategoryTests
{
    private static VoyageRule Rule(string pattern, double weight = 1) =>
        new() { Pattern = pattern, Weight = weight };

    [Theory]
    [InlineData(@"drop Dead Man's Sulphur", "Sulphur")]
    [InlineData(@"contains? Filthscrabble", "Sulphur")]
    [InlineData(@"Rare Monsters.*drop (?:(\d+)|an) additional (?:Divine|Exalted) Orbs?", "Currency")]
    [InlineData(@"(\d+)%\s+increased number of Rare Monsters(?!.*all Voyage Areas)", "Rares")]
    [InlineData(@"are at least Magic", "Magic monsters")]
    [InlineData(@"(?:(\d+)|an)\s+additional packs? of", "Packs")]
    [InlineData(@"(?:(\d+)|an)\s+additional Diviner's Strongbox(?:es)?", "Containers")]
    [InlineData(@"Area: Anchorfield", "Areas")]
    [InlineData(@"Item Quantity:\s*\+?(\d+)", "Loot")]
    [InlineData(@"(\d+)% chance (?:for Charts? )?to not be consumed", "Other")]
    public void RulesLandInTheRightCategory(string pattern, string expected) =>
        Assert.Equal(expected, WeightCategories.CategoryOf(Rule(pattern)));

    /// <summary>Every shipped rule must classify somewhere deliberate: a profile whose
    /// rules all fall into "Other" would give the sliders nothing to hold.</summary>
    [Fact]
    public void EveryShippedProfileHasSliderableCategories()
    {
        foreach (var profile in VoyageRules.Defaults())
        {
            var categories = WeightCategories.CategoriesIn(profile);
            Assert.True(categories.Count > 0, profile.Name);
            Assert.True(categories.Count(c => c != WeightCategories.Other) > 0,
                        $"{profile.Name}: everything fell into Other");
        }
    }

    private static VoyageProfile Shipped(string name) =>
        VoyageRules.Defaults().Single(p => p.Name == name);

    [Fact]
    public void DefaultSlidersChangeNothing()
    {
        var profile = Shipped("sulphur");
        var blended = WeightCategories.Blended(profile,
            new Dictionary<string, int> { ["Sulphur"] = WeightCategories.Baseline },
            VoyageRules.Defaults());
        Assert.Same(profile, blended);   // identity, not a copy that happens to match
    }

    [Fact]
    public void ASliderScalesItsWholeCategoryAndNothingElse()
    {
        var profile = Shipped("sulphur");
        var blended = WeightCategories.Blended(profile,
            new Dictionary<string, int> { ["Sulphur"] = 20 }, VoyageRules.Defaults());

        var line = "Rare Monsters in Area drop Dead Man's Sulphur";
        Assert.Equal(profile.ScoreText([line]) * 2, blended.ScoreText([line]), 6);
    }

    /// <summary>
    /// The reason the panel exists at all: a single-minded profile scaling only itself
    /// changes NO ordering. Raising a category the profile has no rules in borrows the
    /// owning profile's rules at that strength -- sulphur-first, but Divines count.
    /// </summary>
    [Fact]
    public void RaisingAForeignCategoryBorrowsItsOwnersRules()
    {
        var sulphur = Shipped("sulphur");
        var divine = "Rare Monsters in Area drop an additional Divine Orb";
        Assert.Equal(0, sulphur.ScoreText([divine]));

        var blended = WeightCategories.Blended(sulphur,
            new Dictionary<string, int> { ["Currency"] = 5 }, VoyageRules.Defaults());
        var owner = Shipped("currency");
        Assert.Equal(owner.ScoreText([divine]) * 0.5, blended.ScoreText([divine]), 6);

        // and its own rules are untouched while doing so
        var line = "Rare Monsters in Area drop Dead Man's Sulphur";
        Assert.Equal(sulphur.ScoreText([line]), blended.ScoreText([line]), 6);
    }

    /// <summary>Every profile except the inverted dump offers the full deck: the
    /// sulphur panel must never again be one pointless slider.</summary>
    [Fact]
    public void EveryUprightProfileOffersTheWholeDeck()
    {
        foreach (var profile in VoyageRules.Defaults().Where(p => p.ChartBaseValue <= 0))
        {
            var offered = WeightCategories.SliderCategories(profile);
            Assert.True(offered.Count >= WeightCategories.Owners.Count,
                        $"{profile.Name} offers only {offered.Count}");
        }
        // dump keeps to its own categories: borrowed positive rules would invert it
        var dump = Shipped("dump");
        Assert.Equal(WeightCategories.CategoriesIn(dump), WeightCategories.SliderCategories(dump));
    }

    [Fact]
    public void TheStoreForgetsDefaultsAndSurvivesReload()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mg-weights-{Guid.NewGuid():N}.json");
        try
        {
            var store = new VoyageWeightStore(path);
            store.Set("sulphur", "Sulphur", 16, WeightCategories.Baseline);
            store.Set("sulphur", "Currency", 0, 0);   // an un-opinion at its default

            var reloaded = new VoyageWeightStore(path);
            Assert.Equal(16, reloaded.Get("sulphur", "Sulphur", WeightCategories.Baseline));
            Assert.Equal(0, reloaded.Get("sulphur", "Currency", 0));
            Assert.Single(reloaded.For("sulphur"));   // the default was never written

            reloaded.Reset("sulphur");
            Assert.False(new VoyageWeightStore(path).AnyTuned("sulphur"));
        }
        finally { File.Delete(path); }
    }
}
