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
    [InlineData("bottles", "Adjacent Areas contain an additional Diviner's Strongbox")]
    [InlineData("bottles", "Adjacent Areas contain an additional Strongbox")]
    [InlineData("bottles", "Adjacent Areas contain an additional Golden Lantern")]
    [InlineData("bottles", "Adjacent Areas contain an additional Message in a Bottle")]
    [InlineData("currency", "Adjacent Areas contain an additional Cluster of Barrels")]
    [InlineData("currency", "Adjacent Areas contain an additional cage of Tormented Spirits")]
    [InlineData("currency", "Adjacent Areas contain an additional Treasure Anchor")]
    // Monsters are where currency comes from, so a currency plan that scores added
    // packs at zero is not a currency plan: "13 additional packs of Octopi" lost
    // outright to 19 Clusters of Barrels because the pack rules compiled away.
    [InlineData("currency", "Adjacent Areas contain an additional pack of Octopi")]
    [InlineData("currency", "Adjacent Areas contain an additional pack of Crabs")]
    [InlineData("magic monsters", "Adjacent Areas contain an additional pack of the Drowned")]
    [InlineData("rare monsters", "Adjacent Areas contain an additional pack of Sea Beasts")]
    [InlineData("rare monsters", "Adjacent Areas contain an additional Imprisoned Monster")]
    [InlineData("rare monsters", "Rare Monsters in Area drop an additional Scarab")]
    [InlineData("rare monsters", "Adjacent Areas contain an additional Operative's Strongbox")]
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
    /// A GLOBAL percentage mod multiplies the whole board, and must be scored that way.
    ///
    /// The real case: a chart carrying "20% increased Dead Man's Sulphur found in all
    /// Voyage Areas" on a board whose best nine charts totalled ~4300 points of flat
    /// sulphur. Twenty percent of the board is ~860 points -- the single best chart in
    /// the panel -- and the flat rule scored it 40, ranking it seventeenth and leaving
    /// it behind. The board-scaling rule prices it as its fraction of the board.
    /// </summary>
    [Fact]
    public void AGlobalPercentIsWorthItsFractionOfTheBoard()
    {
        var session = new VoyageSession();
        session.ApplyPanelRead(Enumerable.Range(1, 12).Select(i =>
            new ChartPanelReader.ReadCell(i, (i - 1) / 6, (i - 1) % 6, true, true, true, true)
            { Level = 80 }).ToList());

        // Nine strong flat-sulphur charts, two mediocre spares, and ONE chart whose
        // whole worth is the global multiplier. Flat scoring ranks it dead last.
        for (var i = 1; i <= 9; i++)
            session.ApplyChartText(i, $"Reach\nAnchorfield\nDead Man's Sulphur: +{70 + i * 5}");
        session.ApplyChartText(10, "Reach\nAnchorfield\nDead Man's Sulphur: +60");
        session.ApplyChartText(11, "Reach\nAnchorfield\nDead Man's Sulphur: +60");
        session.ApplyChartText(12, "Reach\nAnchorfield\nVoyage Modifier: "
            + "20% increased Dead Man's Sulphur found in all Voyage Areas");

        var sulphur = VoyageRules.Defaults().Single(p => p.Name == "sulphur");
        var plan = session.Plan(session.Solve(sulphur, TimeSpan.FromSeconds(3)));

        Assert.Contains(plan, s => s.ChartNumber == 12);
    }

    /// <summary>
    /// EVERY true board multiplier must be priced as one, in every profile that scores
    /// it. The six "X% increased ... in all Voyage Areas" stat lines multiply what the
    /// whole voyage produces; a flat weight prices them at a rounding error, which is
    /// exactly how the best sulphur chart in a real panel came to rank seventeenth. The
    /// chance-type globals (Possessed, Fracture on death, flask quality) are per-entity
    /// odds, not board multipliers, and rightly stay flat. "dump" is exempt: its board
    /// estimate is dominated by ChartBaseValue, so it prices globals with strong flat
    /// negatives instead -- asserted here too, because a dump that burns a board-wide
    /// multiplier is a dump that cost more than it cleared.
    /// </summary>
    [Fact]
    public void EveryGlobalMultiplierScalesWithTheBoard()
    {
        var multipliers = ChartRewards.Current.Lines.Keys
            .Where(l => l.Contains("all Voyage Areas") && l.StartsWith("#% increased"))
            .Select(l => l.Replace("#", "12"))
            .ToList();
        Assert.True(multipliers.Count >= 6, "the corpus lost its global multiplier lines?");

        var offences = new List<string>();
        foreach (var profile in VoyageRules.Defaults())
            foreach (var line in multipliers)
                foreach (var rule in profile.Rules.Where(r => r.Score(line) != 0))
                {
                    if (profile.Name == "dump")
                    {
                        if (rule.Score(line) > -40)   // 12% x a casual -1 is not a deterrent
                            offences.Add($"dump prices '{line}' at only {rule.Score(line)}");
                    }
                    else if (!rule.ScalesWithBoard)
                    {
                        offences.Add($"{profile.Name}: '{line}' priced FLAT by {rule.Pattern}");
                    }
                }

        Assert.True(offences.Count == 0, string.Join("\n", offences));
    }

    /// <summary>
    /// "Profiles differ from the shipped rules" must notice the PROFILE-LEVEL knobs too.
    /// SameRules compared only the rule list, so when a profile gained AreaLevelWeight
    /// from the 3.29.0b research, every existing rule file silently kept scoring area
    /// level at zero and the app said nothing. "bottles" is the surviving profile that
    /// carries such a knob.
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
            mine.Single(p => p.Name == "bottles").AreaLevelWeight = 0;   // the old file

            File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(mine));
            using var rules = new VoyageRules(file);

            Assert.Contains("bottles", rules.CompareWithDefaults().Outdated);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
