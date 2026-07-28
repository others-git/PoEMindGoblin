using PoeMarketWatch.Core.Voyage;

namespace PoeMarketWatch.Tests;

/// <summary>
/// The shipped profiles, checked against the generated mod table.
///
/// This is what pulling the real table buys. A rule used to be a guess that looked
/// plausible and could silently match nothing -- "(\d+) additional packs" missed every
/// real chart for weeks because the game writes "9(8-10) additional packs". Now a rule
/// that matches nothing the game can roll is a failing test, and so is a payout that no
/// profile scores.
/// </summary>
[Collection("ChartRewards")]
public class ProfileCoverageTests
{
    /// <summary>The reward and difficulty lines the game can actually roll.</summary>
    private static IReadOnlyList<string> Corpus(string category) =>
        ChartRewards.Current.Lines
            .Where(kv => kv.Value.Equals(category, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// A corpus line with its '#' placeholders filled in, so a rule that captures a
    /// number has something to capture. 12 is arbitrary but non-zero, which matters:
    /// a weight multiplied by a captured 0 scores 0 and would look like a miss.
    /// </summary>
    private static string WithValues(string line) => line.Replace("#", "12");

    /// <summary>
    /// The same line with a NEGATIVE value.
    ///
    /// The generator strips signs, so a corpus entry reads "Players have #% to all
    /// maximum Resistances" while the game says "-8%". Filling in a bare 12 hid a rule
    /// anchored on (\d+) that could never match past the minus.
    /// </summary>
    private static string WithNegativeValues(string line) => line.Replace("#", "-8");

    /// <summary>Both fillings, since a rule has to cope with whichever the game uses.</summary>
    private static IEnumerable<string> Fillings(string line)
    {
        yield return WithValues(line);
        if (line.Contains('#')) yield return WithNegativeValues(line);
    }

    /// <summary>
    /// Board modifiers, read verbatim off the in-game Area Modifiers panel.
    ///
    /// These come from the FIGURINES around the board, not from a chart, so they are not
    /// in poedb's chart mod table and there is no published list of them. Captured ones
    /// are recorded here so a rule aimed at them does not look dead.
    /// </summary>
    private static readonly string[] ObservedBoardModifiers =
    [
        "Area contains 4 additional Treasure Anchors",
        "Rare Monsters in Area drop an additional Chaos Orb",
        "30% Chance for Chart to not be consumed when beginning a Voyage",
        "Area contains Filthscrabble",
        "32% increased Pack size",
        "40% increased explicit modifier magnitudes",
        "Area contains 12 additional packs of Crabs",
        "Area contains 16 additional packs of Sea Beasts",
    ];

    /// <summary>
    /// Wordings the game uses that poedb's table does not contain.
    ///
    /// poedb renders every roll as its tiered form with a number, but the game writes the
    /// SINGULAR when a roll comes out at one -- "an additional cage of Tormented Spirits"
    /// where the table says "# additional cages". A corpus lookup misses these, so the
    /// pattern fallback has to cover them, and they are listed here so a rule that exists
    /// only for them is not reported as dead.
    /// </summary>
    private static readonly string[] ObservedChartWordings =
    [
        "Adjacent Areas contain an additional cage of Tormented Spirits",
    ];

    /// <summary>
    /// Tilesets, as they appear on a chart.
    ///
    /// These are the AREA a chart opens, not a modifier, so they are in no mod table --
    /// poedb documents the four chart bases and nothing about the areas. Observed ones
    /// are listed so a rule that prefers a tileset is not reported as dead.
    /// </summary>
    private static readonly string[] ObservedTilesets =
    [
        "Area: Anchorfield",
        "Area: Seafloor Ridges",
        "Area: Abyssal Plain",
        "Area: Undersea Groves",
    ];

    [Fact]
    public void EveryRuleMatchesSomethingTheGameCanRoll()
    {
        // A rule matching nothing is dead weight at best and a silent scoring hole at
        // worst -- the profile looks like it covers a reward it never sees.
        var all = ChartRewards.Current.Lines.Keys.SelectMany(Fillings).ToList();

        // Stat rules read the headline block, which is generated rather than rolled, so
        // those wordings are not in the table.
        var statLines = new Chart("id", "x", ChartShape.Crossing, 80, [])
        {
            ItemQuantity = 12, ItemRarity = 12, MonsterPackSize = 12,
            GoldFound = 12, Sulphur = 12,
        }.StatLines().ToList();
        all.AddRange(statLines);
        all.AddRange(ObservedBoardModifiers);
        all.AddRange(ObservedChartWordings);
        all.AddRange(ObservedTilesets);

        var dead = new List<string>();
        foreach (var profile in VoyageRules.Defaults())
            foreach (var rule in profile.Rules)
                if (!all.Any(line => rule.Score(line) != 0))
                    dead.Add($"{profile.Name}: {rule.Pattern}");

        Assert.True(dead.Count == 0,
            "rules that match nothing in the mod table:\n  " + string.Join("\n  ", dead));
    }

    [Fact]
    public void EveryObservedBoardModifierIsScored()
    {
        // Board modifiers decide which SQUARE is worth what, so one no profile values
        // makes that square invisible to the solver.
        var profiles = VoyageRules.Defaults();
        var unscored = ObservedBoardModifiers
            .Where(line => profiles.All(p => p.ScoreText([line]) == 0))
            .ToList();

        Assert.True(unscored.Count == 0,
            "board modifiers no profile scores:\n  " + string.Join("\n  ", unscored));
    }

    [Fact]
    public void TheSingularWordingsScoreTheSameAsTheirNumberedForm()
    {
        // A roll of one must not be worth nothing just because the game drops the digit.
        var containers = VoyageRules.Defaults().Single(p => p.Name == "containers");
        var singular = containers.ScoreText(["Adjacent Areas contain an additional cage of Tormented Spirits"]);
        var numbered = containers.ScoreText(["Adjacent Areas contain 1 additional cages of Tormented Spirits"]);
        Assert.True(singular > 0, "the singular wording scores nothing");
        Assert.Equal(numbered, singular);
    }

    [Fact]
    public void EveryRewardIsScoredByAtLeastOneProfile()
    {
        // A payout no profile values cannot influence any plan, which means the tool
        // silently ignores part of what the league gives you.
        var profiles = VoyageRules.Defaults();
        var unscored = Corpus("reward")
            .Where(line => profiles.All(p => Fillings(line).All(f => p.ScoreText([f]) == 0)))
            .ToList();

        Assert.True(unscored.Count == 0,
            "rewards no profile scores:\n  " + string.Join("\n  ", unscored));
    }

    [Fact]
    public void TheSafeProfilePenalisesTheDangerousHalfOfTheTable()
    {
        // Not all of it -- plenty of monster mods are survivable and should not drag a
        // chart down. But if it covered only a handful, "safe" would be decorative.
        var safe = VoyageRules.Defaults().Single(p => p.Name == "safe");
        var penalised = Corpus("difficulty")
            .Count(line => Fillings(line).Any(f => safe.ScoreText([f]) < 0));

        Assert.True(penalised >= 15,
            $"safe only penalises {penalised} of {Corpus("difficulty").Count} difficulty lines");
    }

    [Fact]
    public void NoProfileRewardsADifficultyLine()
    {
        // A positive score on a monster mod would make a chart look better for being
        // more dangerous. The "safe" profile is the only one meant to have an opinion.
        var offenders = new List<string>();
        foreach (var profile in VoyageRules.Defaults().Where(p => p.Name != "safe"))
            foreach (var line in Corpus("difficulty"))
                if (profile.ScoreText([WithValues(line)]) > 0)
                    offenders.Add($"{profile.Name} scores +{profile.ScoreText([WithValues(line)])}: {line}");

        Assert.True(offenders.Count == 0, string.Join("\n  ", offenders));
    }

    [Fact]
    public void TheStrongboxProfilePrefersTheValuableTypes()
    {
        // Arcanist's, Diviner's and Operative's boxes are the ones worth planning a board
        // around; the plain roll gives random types.
        var strongbox = VoyageRules.Defaults().Single(p => p.Name == "strongbox");

        double Score(string line) => strongbox.ScoreText([line]);

        var plain = Score("Adjacent Areas contain 3 additional Strongboxes");
        foreach (var kind in new[] { "Arcanist's", "Diviner's", "Operative's" })
        {
            var special = Score($"Adjacent Areas contain 3 additional {kind} Strongboxes");
            Assert.True(special > plain,
                $"{kind} ({special}) should beat the plain roll ({plain})");
        }
    }

    [Fact]
    public void TheStrongboxRulesDoNotScoreTheSameBoxTwice()
    {
        // "additional Strongboxes" must not also match "additional Diviner's
        // Strongboxes", or the good types are paid for twice over.
        var strongbox = VoyageRules.Defaults().Single(p => p.Name == "strongbox");
        var plainRule = strongbox.Rules.Single(r => r.Pattern.Contains(@"additional Strongboxes"));

        Assert.Equal(0, plainRule.Score("Adjacent Areas contain 3 additional Diviner's Strongboxes"));
        Assert.NotEqual(0, plainRule.Score("Adjacent Areas contain 3 additional Strongboxes"));
    }

    [Fact]
    public void TheStrongboxProfileValuesTheQuantityThatFillsTheBoxes()
    {
        // Box contents roll against area quantity, so a tile's own quantity is part of
        // what a strongbox is worth.
        var strongbox = VoyageRules.Defaults().Single(p => p.Name == "strongbox");
        var chart = new Chart("id", "x", ChartShape.Crossing, 80, []) { ItemQuantity = 96 };
        Assert.True(strongbox.ScoreChart(chart) > 0);
    }

    [Fact]
    public void LootProfilesPenaliseTheRollThatDeletesTheLoot()
    {
        // "Monsters in all Voyage Areas cannot drop Equipment, Flasks or Tinctures" is
        // filed as a reward line because it is about the payout -- but it is a negative
        // one, and a profile that ignored it would rank a gutted chart normally.
        const string line = "Monsters in all Voyage Areas cannot drop Equipment, Flasks or Tinctures";
        foreach (var name in new[] { "quantity", "uniques", "strongbox", "flasks" })
        {
            var profile = VoyageRules.Defaults().Single(p => p.Name == name);
            Assert.True(profile.ScoreText([line]) < 0, $"{name} does not penalise it");
        }
    }

    [Fact]
    public void ProfileNamesAreUniqueAndDescribed()
    {
        var profiles = VoyageRules.Defaults();
        Assert.Equal(profiles.Count, profiles.Select(p => p.Name).Distinct().Count());
        Assert.All(profiles, p => Assert.False(string.IsNullOrWhiteSpace(p.Description)));
        Assert.All(profiles, p => Assert.NotEmpty(p.Rules));
    }

    [Fact]
    public void EveryProfileActuallySeparatesCharts()
    {
        // A profile that scores every chart the same cannot influence a plan. Each is
        // given the whole corpus as one chart and a bare one; they must differ.
        var rich = new Chart("rich", "rich", ChartShape.Crossing, 80,
            ChartRewards.Current.Lines.Keys.Select(WithValues).ToList())
        {
            ItemQuantity = 96, ItemRarity = 45, MonsterPackSize = 42,
            GoldFound = 70, Sulphur = 90,
        };
        var bare = new Chart("bare", "bare", ChartShape.Crossing, 80, []);

        foreach (var profile in VoyageRules.Defaults())
            Assert.True(profile.ScoreChart(rich) != profile.ScoreChart(bare),
                        $"{profile.Name} cannot tell a loaded chart from an empty one");
    }
}
