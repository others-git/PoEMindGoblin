using MindGoblin.Core.Voyage;

namespace MindGoblin.Tests;

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
    /// The figurine modifiers, straight from the generated table.
    ///
    /// These are what the carvings around the board grant, read per square off the Area
    /// Modifiers panel rather than copied from an item. They were a hand-kept list of
    /// eight until poedb's Deep Water Border Mods table turned up; there are forty.
    /// </summary>
    private static IReadOnlyList<string> BoardModifiers() =>
        ChartRewards.Current.BoardLines.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Wordings the game uses that no table contains, from real captures.
    ///
    /// Two ways they diverge. The game writes the SINGULAR when a roll comes out at one,
    /// where the table only ever shows the numbered plural. And the Area Modifiers panel
    /// shows a board modifier RESOLVED for the square hovered -- "Rare Monsters in Area
    /// drop an additional Chaos Orb" -- where the table has the template it came from,
    /// "Rare Monsters in adjacent Areas drop # additional Chaos Orbs".
    /// </summary>
    private static readonly string[] ObservedChartWordings =
    [
        "Adjacent Areas contain an additional cage of Tormented Spirits",
        "Rare Monsters in Area drop an additional Chaos Orb",
        "Area contains 4 additional Treasure Anchors",
        "Area contains Filthscrabble",
        "32% increased Pack size",
    ];

    /// <summary>
    /// The tilesets, as a chart states them. Generated now: the room list came from
    /// poedb's Maiden Voyage page, so these are no longer hand-kept.
    /// </summary>
    private static IReadOnlyList<string> Tilesets() =>
        ChartRewards.Current.Rooms.Keys.Select(r => "Area: " + r).ToList();

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
        all.AddRange(BoardModifiers().SelectMany(Fillings));
        all.AddRange(ObservedChartWordings);
        all.AddRange(Tilesets());

        var dead = new List<string>();
        foreach (var profile in VoyageRules.Defaults())
            foreach (var rule in profile.Rules)
                if (!all.Any(line => rule.Score(line) != 0))
                    dead.Add($"{profile.Name}: {rule.Pattern}");

        Assert.True(dead.Count == 0,
            "rules that match nothing in the mod table:\n  " + string.Join("\n  ", dead));
    }

    [Fact]
    public void EveryFigurineModifierIsScored()
    {
        // Board modifiers decide which SQUARE is worth what, so one no profile values
        // makes that square invisible to the solver.
        var profiles = VoyageRules.Defaults();
        var unscored = BoardModifiers()
            .Where(line => profiles.All(p => Fillings(line).All(f => p.ScoreText([f]) == 0)))
            .ToList();

        Assert.True(unscored.Count == 0,
            "figurine modifiers no profile scores:\n  " + string.Join("\n  ", unscored));
    }

    [Fact]
    public void TheFigurineTableIsLoaded()
    {
        Assert.True(BoardModifiers().Count >= 35,
                    $"only {BoardModifiers().Count} figurine modifiers loaded");
    }

    [Fact]
    public void EveryRoomIsKnownWithItsBase()
    {
        // The tilesets, and which chart base opens each. Nineteen in the data, one of
        // which is a [DNT] placeholder the generator drops.
        var rooms = ChartRewards.Current.Rooms;
        Assert.True(rooms.Count >= 15, $"only {rooms.Count} rooms loaded");
        Assert.Equal("Sandy Seabed", rooms["Anchorfield"]);
        Assert.Equal("Coral Reef", rooms["Seafloor Ridges"]);
        Assert.DoesNotContain(rooms.Keys, r => r.StartsWith("[DNT]"));
    }

    [Fact]
    public void TheSingularWordingsScoreTheSameAsTheirNumberedForm()
    {
        // A roll of one must not be worth nothing just because the game drops the digit.
        var currency = VoyageRules.Defaults().Single(p => p.Name == "currency");
        var singular = currency.ScoreText(["Adjacent Areas contain an additional cage of Tormented Spirits"]);
        var numbered = currency.ScoreText(["Adjacent Areas contain 1 additional cages of Tormented Spirits"]);
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
    public void ARaisedDangerSliderPenalisesTheDangerousHalfOfTheTable()
    {
        // Not all of it -- plenty of monster mods are survivable and should not drag a
        // chart down. But if it covered only a handful, the Danger slider would be
        // decorative: no shipped strategy weights the stat, so borrowing it from the
        // catalog is the ONLY way a plan can see the difficulty table at all.
        var safe = WeightCategories.Blended(
            VoyageRules.Defaults().Single(p => p.Name == "sulphur"),
            new Dictionary<string, int> { ["Danger"] = WeightCategories.Max },
            VoyageRules.Defaults());
        var penalised = Corpus("difficulty")
            .Count(line => Fillings(line).Any(f => safe.ScoreText([f]) < 0));

        Assert.True(penalised >= 15,
            $"the danger slider only penalises {penalised} of {Corpus("difficulty").Count} difficulty lines");
    }

    [Fact]
    public void NoProfileRewardsADifficultyLine()
    {
        // A positive score on a monster mod would make a chart look better for being more
        // dangerous. Every shipped profile is subject to this now: the one preset that was
        // allowed an opinion on difficulty has been retired, and Danger reaches a plan only
        // through the slider, which enters negative by convention.
        var offenders = new List<string>();
        foreach (var profile in VoyageRules.Defaults())
            foreach (var line in Corpus("difficulty"))
                if (profile.ScoreText([WithValues(line)]) > 0)
                    offenders.Add($"{profile.Name} scores +{profile.ScoreText([WithValues(line)])}: {line}");

        Assert.True(offenders.Count == 0, string.Join("\n  ", offenders));
    }

    [Fact]
    public void TheBoxWeightingProfilePrefersTheValuableTypes()
    {
        // Arcanist's, Diviner's and Operative's boxes are the ones worth planning a board
        // around; the plain roll gives random types. The dedicated strongbox preset was
        // retired, but the ordering is the CATALOG's, so it has to survive with it.
        var bottles = VoyageRules.Defaults().Single(p => p.Name == "bottles");

        double Score(string line) => bottles.ScoreText([line]);

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
        var bottles = VoyageRules.Defaults().Single(p => p.Name == "bottles");
        var plainRule = bottles.Rules.Single(r => r.Pattern.Contains(@"additional Strongbox"));

        Assert.Equal(0, plainRule.Score("Adjacent Areas contain 3 additional Diviner's Strongboxes"));
        Assert.Equal(0, plainRule.Score("Adjacent Areas contain an additional Diviner's Strongbox"));
        Assert.NotEqual(0, plainRule.Score("Adjacent Areas contain 3 additional Strongboxes"));
        Assert.NotEqual(0, plainRule.Score("Adjacent Areas contain an additional Strongbox"));
    }

    [Fact]
    public void TheBoxWeightingProfileValuesTheQuantityThatFillsTheBoxes()
    {
        // Box contents roll against area quantity, so a tile's own quantity is part of
        // what a strongbox is worth -- which is why the profile that plans around boxes
        // has to weight both.
        var bottles = VoyageRules.Defaults().Single(p => p.Name == "bottles");
        var chart = new Chart("id", "x", ChartShape.Crossing, 80, []) { ItemQuantity = 96 };
        Assert.True(bottles.ScoreChart(chart) > 0);
    }

    [Fact]
    public void LootProfilesPenaliseTheRollThatDeletesTheLoot()
    {
        // "Monsters in all Voyage Areas cannot drop Equipment, Flasks or Tinctures" is
        // filed as a reward line because it is about the payout -- but it is a negative
        // one, and a profile that ignored it would rank a gutted chart normally.
        const string line = "Monsters in all Voyage Areas cannot drop Equipment, Flasks or Tinctures";
        foreach (var name in new[] { "bottles", "currency" })
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

/// <summary>
/// Relationships between a profile's NAME and what it actually values.
///
/// The coverage tests check that every reward is scored by someone; they cannot see that
/// a profile has ignored something central to its own objective. "rare monsters" valued
/// every per-rare payout and not one thing that produces rares.
/// </summary>
[Collection("ChartRewards")]
public class ProfileCoherenceTests
{
    private static VoyageProfile Profile(string name) =>
        VoyageRules.Defaults().Single(p => p.Name == name);

    [Theory]
    // Every profile that gets paid out of monsters -- rares and magic monsters both spawn
    // out of packs, and currency comes off the monsters too, so pack size multiplies what
    // all three are really after.
    [InlineData("currency")]
    [InlineData("rare monsters")]
    [InlineData("magic monsters")]
    public void MonsterFedProfilesValuePackSize(string name)
    {
        var profile = Profile(name);

        Assert.True(profile.ScoreText(["Monster Pack Size: +42%"]) > 0,
                    $"{name} ignores the chart's own pack size");
        Assert.True(profile.ScoreText(["18% increased Pack Size in adjacent Areas"]) > 0,
                    $"{name} ignores adjacent pack size");
        Assert.True(profile.ScoreText(["Adjacent Areas contain 12 additional packs of Crabs"]) > 0,
                    $"{name} ignores additional packs");
    }

    [Fact]
    public void MorePacksBeatsFewerForEveryMonsterFedProfile()
    {
        foreach (var name in new[] { "currency", "rare monsters", "magic monsters" })
        {
            var profile = Profile(name);
            var small = new Chart("s", "s", ChartShape.Crossing, 80, []) { MonsterPackSize = 14 };
            var large = small with { Id = "l", MonsterPackSize = 42 };
            Assert.True(profile.ScoreChart(large) > profile.ScoreChart(small),
                        $"{name} does not prefer the bigger packs");
        }
    }

    [Fact]
    public void ExtraRaresAreNotPenalisedByTheMagicProfile()
    {
        // "increased number of Rare Monsters" ADDS rares; it does not convert magic ones,
        // so it costs a magic-focused board nothing.
        Assert.True(Profile("magic monsters")
                        .ScoreText(["60% increased number of Rare Monsters in adjacent Areas"]) >= 0);
    }

    [Fact]
    public void EachMonsterProfileStillLeadsWithItsOwnObjective()
    {
        // Pack size is a multiplier on all three, not the point of any of them -- if it
        // dominated, the three would return the same board.
        var rare = Profile("rare monsters");
        Assert.True(rare.ScoreText(["Rare monsters that are natural inhabitants of all Voyage Areas are imprisoned by Essences"])
                    > rare.ScoreText(["Monster Pack Size: +42%"]));

        var magic = Profile("magic monsters");
        Assert.True(magic.ScoreText(["Monsters in all Voyage Areas are at least Magic"])
                    > magic.ScoreText(["Monster Pack Size: +42%"]));
    }
}
