using MindGoblin.Core.Voyage;

namespace MindGoblin.Tests;

/// <summary>
/// Review findings on the shipped rule profiles. Two systematic failure modes, both of
/// the same species as the "Qauntity" typo: the rules were written against the mod
/// TABLE, and the game does not always write what the table writes.
/// </summary>
public class RulesReviewTests
{
    private static VoyageProfile Profile(string name) =>
        VoyageRules.Defaults().Single(p => p.Name == name);

    /// <summary>
    /// THE GAME WRITES THE SINGULAR WHEN A ROLL IS 1: "an additional cage of Tormented
    /// Spirits" where the table says "# additional cages" (documented, learned from the
    /// cage line), and the Area Modifiers panel resolves board modifiers the same way --
    /// square 4 of the real session reads "Rare Monsters in Area drop an additional
    /// Scarab". A rule anchored on (\d+) scores every one of these ZERO, and a roll of 1
    /// is the COMMON roll for the most valuable lines: Divine Orbs, typed strongboxes,
    /// Golden Lanterns. The currency profile missed its own headline payouts whenever
    /// they rolled 1.
    /// </summary>
    [Theory]
    [InlineData("currency", "Rare Monsters in Area drop an additional Divine Orb")]
    [InlineData("currency", "Rare Monsters adjacent in Areas drop an additional Divine Orb")]
    [InlineData("currency", "Rare Monsters in Area drop an additional Scarab")]
    [InlineData("currency", "Rare Monsters in adjacent Areas drop an additional Exalted Orb")]
    [InlineData("currency", "Area contains an Altar to the Goddess")]
    [InlineData("strongbox", "Adjacent Areas contain an additional Diviner's Strongbox")]
    [InlineData("strongbox", "Adjacent Areas contain an additional Strongbox")]
    [InlineData("containers", "Adjacent Areas contain an additional Golden Lantern")]
    [InlineData("containers", "Adjacent Areas contain an additional Cluster of Barrels")]
    [InlineData("containers", "Adjacent Areas contain an additional cage of Tormented Spirits")]
    [InlineData("containers", "Adjacent Areas contain an additional Message in a Bottle")]
    [InlineData("containers", "Adjacent Areas contain an additional Treasure Anchor")]
    [InlineData("quantity", "Adjacent Areas contain an additional Golden Lantern")]
    [InlineData("pack size", "Adjacent Areas contain an additional pack of Sea Beasts")]
    [InlineData("gold", "Adjacent Areas contain an additional pack of Crabs")]
    [InlineData("magic monsters", "Adjacent Areas contain an additional pack of the Drowned")]
    [InlineData("rare monsters", "Adjacent Areas contain an additional Imprisoned Monster")]
    public void ARollOfOneStillScores(string profile, string line) =>
        Assert.True(Profile(profile).ScoreText([line]) > 0,
                    $"'{line}' scores zero under '{profile}'");

    /// <summary>
    /// A roll of one must be worth what the numbered rule pays for 1 -- the singular is
    /// a WORDING, not a different modifier, so it must not get a different price.
    /// </summary>
    [Fact]
    public void TheSingularIsWorthExactlyOne()
    {
        var currency = Profile("currency");
        Assert.Equal(
            currency.ScoreText(["Rare Monsters in adjacent Areas drop 1 additional Divine Orbs"]),
            currency.ScoreText(["Rare Monsters in adjacent Areas drop an additional Divine Orb"]));
    }

    /// <summary>
    /// NO LINE MAY BE SCORED BY TWO RULES OF THE SAME PROFILE. ScoreText sums every rule
    /// over every line, so two rules matching the same wording silently pay it twice.
    /// Found live twice: "quantity" had two rules for the chart-not-consumed line, and
    /// "pack size" scored the per-connection rare line with both the per-connection rule
    /// AND the general increased-rares rule (the general one anchored on "Monsters" with
    /// a capital M, but the regexes are case-insensitive and the per-connection line's
    /// lowercase "monsters" matched anyway).
    /// </summary>
    [Fact]
    public void NoCorpusLineIsScoredByTwoRulesOfOneProfile()
    {
        var corpus = ChartRewards.Current.Lines.Keys
            .Concat(ChartRewards.Current.BoardLines.Keys)
            .Distinct()
            .Select(line => line.Replace("#", "12"))
            .ToList();

        var offences = new List<string>();
        foreach (var profile in VoyageRules.Defaults())
            foreach (var line in corpus)
            {
                var hits = profile.Rules.Where(r => r.Score(line) != 0).ToList();
                if (hits.Count > 1)
                    offences.Add($"{profile.Name}: '{line}' matched by "
                                 + string.Join(" AND ", hits.Select(h => h.Pattern)));
            }

        Assert.True(offences.Count == 0, string.Join("\n", offences));
    }

    /// <summary>
    /// "Profiles differ from the shipped rules" must notice the PROFILE-LEVEL knobs too.
    /// SameRules compared only the rule list, so when the strongbox profile gained
    /// AreaLevelWeight from the 3.29.0b research, every existing rule file silently kept
    /// scoring area level at zero and the app said nothing.
    /// </summary>
    [Fact]
    public void AChangedProfileKnobCountsAsOutdated()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rules-knob-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "voyage-rules.json");
            var mine = VoyageRules.Defaults();
            mine.Single(p => p.Name == "strongbox").AreaLevelWeight = 0;   // the old file

            File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(mine));
            using var rules = new VoyageRules(file);

            Assert.Contains("strongbox", rules.CompareWithDefaults().Outdated);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
