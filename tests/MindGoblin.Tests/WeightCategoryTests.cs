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

    [Fact]
    public void BaselineSlidersChangeNothing()
    {
        var profile = VoyageRules.Defaults().Single(p => p.Name == "sulphur");
        var scaled = WeightCategories.Scaled(profile,
            new Dictionary<string, int> { ["Sulphur"] = WeightCategories.Baseline });
        Assert.Same(profile, scaled);   // identity, not a copy that happens to match
    }

    [Fact]
    public void ASliderScalesItsWholeCategoryAndNothingElse()
    {
        var profile = VoyageRules.Defaults().Single(p => p.Name == "sulphur");
        var scaled = WeightCategories.Scaled(profile, new Dictionary<string, int> { ["Sulphur"] = 20 });

        var line = "Rare Monsters in Area drop Dead Man's Sulphur";
        Assert.Equal(profile.ScoreText([line]) * 2, scaled.ScoreText([line]), 6);

        // the shipped profile is untouched -- sliding back to 10 is an exact return
        var again = WeightCategories.Scaled(profile, new Dictionary<string, int>());
        Assert.Equal(profile.ScoreText([line]), again.ScoreText([line]), 6);
    }

    [Fact]
    public void TheStoreForgetsBaselineAndSurvivesReload()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mg-weights-{Guid.NewGuid():N}.json");
        try
        {
            var store = new VoyageWeightStore(path);
            store.Set("sulphur", "Sulphur", 16);
            store.Set("sulphur", "Currency", WeightCategories.Baseline);   // an un-opinion

            var reloaded = new VoyageWeightStore(path);
            Assert.Equal(16, reloaded.Get("sulphur", "Sulphur"));
            Assert.Equal(WeightCategories.Baseline, reloaded.Get("sulphur", "Currency"));
            Assert.Single(reloaded.For("sulphur"));   // baseline was never written

            reloaded.Reset("sulphur");
            Assert.False(new VoyageWeightStore(path).AnyTuned("sulphur"));
        }
        finally { File.Delete(path); }
    }
}
