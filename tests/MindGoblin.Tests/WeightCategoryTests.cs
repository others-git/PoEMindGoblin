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
    [InlineData(@"drop (?:(\d+)|an) additional Divine Orbs?", "Currency")]
    [InlineData(@"(\d+)%\s+increased number of Rare Monsters(?!.*all Voyage Areas)(?!.*per connection)", "Rares")]
    [InlineData(@"are at least Magic", "Magic")]
    [InlineData(@"(?:(\d+)|an)\s+additional packs? of", "Packs")]
    [InlineData(@"(?:(\d+)|an)\s+additional Diviner's Strongbox(?:es)?", "Strongboxes")]
    [InlineData(@"cannot drop Equipment, Flasks or Tinctures", "NoEquipmentDrops")]
    [InlineData(@"have Soul Eater", "PlayerPower")]
    [InlineData(@"(\d+)%\s+increased explicit modifier magnitudes", "ModifierMagnitude")]
    [InlineData(@"(?:(\d+)|an)\s+additional Messages? in (?:a )?Bottles?", "Bottles")]
    [InlineData(@"Item Quantity:\s*\+?(\d+)", "Quantity")]
    [InlineData(@"a rule the catalog never heard of", "Extras")]
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
                        $"{profile.Name}: everything fell into Extras");
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
    /// The reason the panel exists at all: a single-minded strategy scaling only
    /// itself changes NO ordering. Raising a stat it does not weight prices those
    /// mods straight from the CATALOG at slider strength -- sulphur-first, but
    /// Divines count.
    /// </summary>
    [Fact]
    public void RaisingAForeignCategoryDrawsFromTheCatalog()
    {
        var sulphur = Shipped("sulphur");
        var divine = "Rare Monsters in Area drop an additional Divine Orb";
        Assert.Equal(0, sulphur.ScoreText([divine]));

        var blended = WeightCategories.Blended(sulphur,
            new Dictionary<string, int> { ["Currency"] = 5 }, VoyageRules.Defaults());
        // catalog: 162c x 10 rares, at half strength
        Assert.Equal(162 * 10 * 0.5, blended.ScoreText([divine]), 6);

        // and its own rules are untouched while doing so
        var line = "Rare Monsters in Area drop Dead Man's Sulphur";
        Assert.Equal(sulphur.ScoreText([line]), blended.ScoreText([line]), 6);
    }

    /// <summary>Every strategy except the inverted dump offers the whole stat deck.</summary>
    [Fact]
    public void EveryUprightProfileOffersTheWholeDeck()
    {
        var statCount = Enum.GetValues<Stat>().Length;
        foreach (var profile in VoyageRules.Defaults().Where(p => p.ChartBaseValue <= 0))
        {
            var offered = WeightCategories.SliderCategories(profile);
            Assert.True(offered.Count >= statCount,
                        $"{profile.Name} offers only {offered.Count}");
        }
        // dump keeps to its own stats: borrowed positive rules would invert it
        var dump = Shipped("dump");
        Assert.Equal(WeightCategories.CategoriesIn(dump), WeightCategories.SliderCategories(dump));
    }

    /// <summary>
    /// A strategy's OWN rule is Extras even when it reuses a catalog wording.
    ///
    /// Two shipped strategies deliberately do: containers counts starfish as openables
    /// rather than as rares, and scarabs REPLACES the catalog's opinion of an Operative's
    /// Strongbox -- 20 as a box, 50 as the thing that holds scarabs. Filed by pattern they
    /// looked like that stat's catalog, which made the stat count as one the strategy
    /// weights -- so raising its slider scaled that single rule and could never borrow the
    /// rest of the stat, the exact thing the slider exists to do.
    /// </summary>
    [Theory]
    [InlineData("containers", @"(?:(\d+)|an)\s+additional Giant Starfish", "Rares")]
    [InlineData("scarabs", @"(?:(\d+)|an)\s+additional Operative's Strongbox(?:es)?",
                "Strongboxes")]
    public void AStrategysOwnRuleIsExtrasEvenWhenItSharesACatalogWording(
        string profileName, string pattern, string donorStat)
    {
        var rule = Shipped(profileName).Rules.Single(r => r.Pattern == pattern);
        Assert.Equal(WeightCategories.Other, WeightCategories.CategoryOf(rule));
        Assert.DoesNotContain(donorStat, WeightCategories.CategoriesIn(Shipped(profileName)));
    }

    /// <summary>Catalog rules still carry their own stat, stamped rather than looked up.</summary>
    [Fact]
    public void CatalogRulesCarryTheirStat()
    {
        var sulphur = Shipped("sulphur");
        Assert.All(sulphur.Rules, r => Assert.Equal("Sulphur", r.Category));
    }

    /// <summary>An untagged rule -- an older file, a hand edit -- keeps the old pattern
    /// lookup, so an existing voyage-rules.json behaves exactly as it did.</summary>
    [Fact]
    public void AnUntaggedRuleFallsBackToThePatternLookup()
    {
        Assert.Equal("Currency",
            WeightCategories.CategoryOf(Rule(@"drop (?:(\d+)|an) additional Divine Orbs?")));
        Assert.Null(Rule("anything").Category);
    }

    /// <summary>
    /// The consequence: scarabs can now borrow the box economy, and borrowing does NOT
    /// stack a second copy of the Operative's line the strategy already prices itself.
    /// </summary>
    [Fact]
    public void BorrowingAStatDoesNotDuplicateARuleTheProfileAlreadyHas()
    {
        const string operatives = @"(?:(\d+)|an)\s+additional Operative's Strongbox(?:es)?";
        var scarabs = Shipped("scarabs");
        var scarabBox = "Adjacent Areas contain 3 additional Operative's Strongboxes";
        var plainBox = "Adjacent Areas contain 3 additional Diviner's Strongboxes";
        Assert.Equal(0, scarabs.ScoreText([plainBox]));

        var blended = WeightCategories.Blended(scarabs,
            new Dictionary<string, int> { ["Strongboxes"] = WeightCategories.Baseline },
            VoyageRules.Defaults());

        // the rest of the box catalog arrives at catalog value...
        Assert.Equal(75, blended.ScoreText([plainBox]), 6);   // 25 x 3
        // ...and the one rule scarabs already had is still priced once, its own way
        Assert.Equal(scarabs.ScoreText([scarabBox]), blended.ScoreText([scarabBox]), 6);
        Assert.Single(blended.Rules, r => r.Pattern == operatives);
    }

    /// <summary>Borrowing has to stay opt-in: touching one slider must not quietly pull
    /// in a stat the user never raised.</summary>
    [Fact]
    public void AStatLeftAtItsDefaultIsNeverBorrowed()
    {
        var scarabs = Shipped("scarabs");
        var blended = WeightCategories.Blended(scarabs,
            new Dictionary<string, int> { ["Scarabs"] = 15 }, VoyageRules.Defaults());

        Assert.Equal(0,
            blended.ScoreText(["Adjacent Areas contain 3 additional Diviner's Strongboxes"]));
    }

    /// <summary>
    /// Every stat names itself in words and explains itself in a sentence.
    ///
    /// The sliders are the thing most worth touching and the enum names were shorthand
    /// from the catalog -- "LootLock" for the mod that deletes your equipment, "Meta",
    /// "Player". A stat added later with no label would silently fall back to its
    /// identifier and put the shorthand straight back on screen, so this fails the build
    /// instead.
    /// </summary>
    [Fact]
    public void EveryStatHasALabelAndADescription()
    {
        foreach (var stat in Enum.GetValues<Stat>())
        {
            var label = StatText.Label(stat);
            Assert.False(string.IsNullOrWhiteSpace(label), $"{stat} has no label");
            Assert.True(StatText.Describe(stat).Length > 30,
                        $"{stat} has no description worth showing");
            // A label that is just the identifier is the fallback, not a decision --
            // acceptable only where the identifier is already a plain English word.
            if (label == stat.ToString())
                Assert.DoesNotContain(label, new[] { "NoEquipmentDrops", "ModifierMagnitude",
                                                     "PlayerPower", "ChartRefunds", "Openables" });
        }
    }

    /// <summary>Extras is a category too, and the one most in need of explaining.</summary>
    [Fact]
    public void ExtrasNamesAndExplainsItself()
    {
        Assert.Equal("Strategy extras", WeightCategories.LabelOf(WeightCategories.Other));
        Assert.Contains("strategy", WeightCategories.DescriptionOf(WeightCategories.Other));
    }

    /// <summary>Labels are for reading, so they must not be identifiers in disguise.</summary>
    [Theory]
    [InlineData("NoEquipmentDrops", "No equipment drops")]
    [InlineData("ModifierMagnitude", "Modifier magnitude")]
    [InlineData("PlayerPower", "Player power")]
    [InlineData("Openables", "Other openables")]
    [InlineData("ChartRefunds", "Chart refunds")]
    [InlineData("Danger", "Monster danger")]
    public void CategoriesPrintTheirLabel(string category, string expected) =>
        Assert.Equal(expected, WeightCategories.LabelOf(category));

    /// <summary>The trap stats say WHICH WAY to weight them: nothing else on the row
    /// can, and getting the sign wrong on either is the expensive mistake.</summary>
    [Theory]
    [InlineData("NoEquipmentDrops")]
    [InlineData("Danger")]
    public void TrapStatsSayWhichWayToWeightThem(string category) =>
        Assert.Contains("negative", WeightCategories.DescriptionOf(category),
                        StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// ...and the slider must PRODUCE that sign, not merely advise it.
    ///
    /// Multiplier() is never negative, and the catalog stores a trap positive on purpose
    /// (a Danger entry is a severity, the loot-lock a +1 magnitude), so a borrowed trap
    /// arrived ATTRACTIVE: raising "Monster danger" on a profile that does not weight it
    /// made the planner prefer the charts most likely to end the voyage, which is the
    /// exact opposite of what the row says it does.
    /// </summary>
    [Theory]
    [InlineData("Danger", "Monsters are Hexproof", -20)]
    [InlineData("Danger", "Monsters cannot be Taunted", -12)]
    [InlineData("NoEquipmentDrops",
                "Monsters in Area cannot drop Equipment, Flasks or Tinctures", -2)]
    public void ATrapStatBorrowedBySliderScoresNegative(
        string category, string line, double expected)
    {
        var sulphur = Shipped("sulphur");   // weights neither trap
        Assert.Equal(0, sulphur.ScoreText([line]));

        var blended = WeightCategories.Blended(sulphur,
            new Dictionary<string, int> { [category] = WeightCategories.Max },
            VoyageRules.Defaults());

        Assert.Equal(expected, blended.ScoreText([line]), 6);
    }

    /// <summary>A profile that already prices a trap keeps its OWN opinion: that path
    /// goes through Scale, where the strategy's sign is already right and the slider
    /// only says how much.</summary>
    [Fact]
    public void AProfileCarryingATrapScalesItsOwnNegativeWeight()
    {
        var bottles = Shipped("bottles");
        var line = "Monsters in Area cannot drop Equipment, Flasks or Tinctures";
        Assert.True(bottles.ScoreText([line]) < 0, "bottles prices the loot-lock at -40");

        var blended = WeightCategories.Blended(bottles,
            new Dictionary<string, int> { ["NoEquipmentDrops"] = WeightCategories.Max },
            VoyageRules.Defaults());

        Assert.Equal(bottles.ScoreText([line]) * 2, blended.ScoreText([line]), 6);
    }

    /// <summary>
    /// The two statements of a trap must not drift apart: the sentence telling the user to
    /// weight it negative, and the sign a slider applies. The sign used to be DERIVED from
    /// what the shipped strategies happened to price, which made it hostage to the preset
    /// list -- retiring the only strategy that weighted Danger deleted the evidence that
    /// Danger is a trap, and the slider went back to making Hexproof a reward. It is an
    /// explicit set now, and this is what holds it to what the tooltip says.
    /// </summary>
    [Fact]
    public void EveryTrapStatIsWeightedNegativeInWordsAndInSign()
    {
        foreach (var stat in Enum.GetValues<Stat>())
        {
            var saysNegative = StatText.Describe(stat)
                .Contains("negative", StringComparison.OrdinalIgnoreCase);
            Assert.True(saysNegative == (WeightCategories.SignOf(stat) < 0),
                        $"{stat}: the description and the slider's sign disagree");
        }
    }

    /// <summary>
    /// A renamed slider keeps its old name's meaning, wherever that name was written
    /// down -- the store, and a rule stamped before the rename.
    /// </summary>
    [Theory]
    [InlineData("Boxes", "Strongboxes")]
    [InlineData("Containers", "Openables")]
    [InlineData("Preserve", "ChartRefunds")]
    [InlineData("LootLock", "NoEquipmentDrops")]
    [InlineData("Meta", "ModifierMagnitude")]
    [InlineData("Player", "PlayerPower")]
    public void OldCategoryNamesStillResolve(string old, string current)
    {
        Assert.Equal(current, WeightCategories.Current(old));
        Assert.Equal(StatText.Label(Enum.Parse<Stat>(current)), WeightCategories.LabelOf(old));
        Assert.Equal(current,
            WeightCategories.CategoryOf(new VoyageRule { Pattern = "x", Category = old }));
    }

    /// <summary>
    /// A tuned file written under the old names keeps its tuning.
    ///
    /// The keys ARE the category names, so a rename that did not migrate would leave a
    /// deliberate -40 sitting under a key nothing reads: the user's weights would revert
    /// to the shipped baseline and the file would still hold the evidence.
    /// </summary>
    [Fact]
    public void TuningWrittenUnderTheOldNamesSurvivesTheRename()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mg-weights-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path,
                """{ "bottles": { "LootLock": 0, "Boxes": 17, "Sulphur": 4 } }""");
            var store = new VoyageWeightStore(path);

            Assert.Equal(0, store.Get("bottles", "NoEquipmentDrops", WeightCategories.Baseline));
            Assert.Equal(17, store.Get("bottles", "Strongboxes", WeightCategories.Baseline));
            Assert.Equal(4, store.Get("bottles", "Sulphur", WeightCategories.Baseline));
            Assert.Equal(3, store.For("bottles").Count);   // migrated, not duplicated
        }
        finally { File.Delete(path); }
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
