using MindGoblin.Core.Voyage;

namespace MindGoblin.Tests;

public class VoyageRuleTests
{
    [Fact]
    public void CapturedNumberScalesTheWeight()
    {
        // "8 additional packs" must beat "2 additional packs" -- a flat per-match score
        // would rank them identically, which is the whole point of the rule.
        var rule = new VoyageRule { Pattern = @"(\d+)\s+additional packs", Weight = 4 };
        Assert.Equal(32, rule.Score("Adjacent Areas contain 8 additional packs of Sea Beasts"), 3);
        Assert.Equal(8, rule.Score("Adjacent Areas contain 2 additional packs of Sea Beasts"), 3);
    }

    [Fact]
    public void PatternWithoutACaptureScoresFlat()
    {
        var rule = new VoyageRule { Pattern = "Monsters cannot be Taunted", Weight = -8 };
        Assert.Equal(-8, rule.Score("Monsters cannot be Taunted"), 3);
    }

    [Fact]
    public void MissReturnsZero()
    {
        var rule = new VoyageRule { Pattern = @"(\d+)% increased Quantity", Weight = 1 };
        Assert.Equal(0, rule.Score("Monsters Hinder on Hit with Spells"), 3);
    }

    [Fact]
    public void MatchingIsCaseInsensitive()
    {
        var rule = new VoyageRule { Pattern = "dead man's sulphur", Weight = 5 };
        Assert.Equal(5, rule.Score("30% increased Dead Man's Sulphur found in this Area"), 3);
    }

    [Fact]
    public void EmptyPatternIsInert()
    {
        Assert.Equal(0, new VoyageRule { Pattern = "", Weight = 99 }.Score("anything"), 3);
    }
}

public class VoyageProfileTests
{
    private static Chart Chart(params string[] mods) =>
        new("id", "Coral Reef Chart", ChartShape.Corner, 80, mods);

    [Fact]
    public void SulphurProfileScoresTheSulphurLine()
    {
        // 30 x 2.0 from the "% increased Dead Man's Sulphur" rule.
        //
        // Note this constructs the Chart directly. Parsed from real text this line would
        // be DROPPED, because the "Dead Man's Sulphur: +30%" headline stat is its sum and
        // scoring both counts the same sulphur twice -- see ChartTextRealFormatTests.
        var profile = VoyageRules.Defaults().First(p => p.Name == "sulphur");
        var chart = Chart("30% increased Dead Man's Sulphur found in this Area");
        Assert.Equal(60, profile.ScoreChart(chart), 3);
    }

    [Fact]
    public void DifferentProfilesRankTheSameChartsDifferently()
    {
        // There is no single best board -- that is why the objective is a profile.
        var sulphur = VoyageRules.Defaults().First(p => p.Name == "sulphur");
        var packs = VoyageRules.Defaults().First(p => p.Name == "pack size");

        var sulphurChart = Chart("40% increased Dead Man's Sulphur found in this Area");
        var packChart = Chart("25% increased Pack Size");

        Assert.True(sulphur.ScoreChart(sulphurChart) > sulphur.ScoreChart(packChart));
        Assert.True(packs.ScoreChart(packChart) > packs.ScoreChart(sulphurChart));
    }

    [Fact]
    public void ARaisedDangerSliderPenalisesDangerousMods()
    {
        // No shipped strategy weights Danger, so this guarantee now rides the slider: the
        // catalog stores danger as a positive SEVERITY, and borrowing it at catalog sign
        // would rank the chart most likely to end the voyage ABOVE the clean one.
        var safe = WeightCategories.Blended(
            VoyageRules.Defaults().First(p => p.Name == "sulphur"),
            new Dictionary<string, int> { ["Danger"] = WeightCategories.Max },
            VoyageRules.Defaults());
        var nasty = Chart("Monsters cannot be Taunted", "Monsters reflect Physical Damage");
        var clean = Chart("30% increased Dead Man's Sulphur found in this Area");
        Assert.True(safe.ScoreChart(clean) > safe.ScoreChart(nasty));
    }

    [Fact]
    public void AreaLevelCanBeWeighted()
    {
        var profile = new VoyageProfile { AreaLevelWeight = 2 };
        Assert.Equal(160, profile.ScoreChart(Chart()), 3);   // level 80 x 2
    }

    [Fact]
    public void ScorerRoutesBoardModifierValueToAdjacentCells()
    {
        // Composed the way VoyageSession.Solve composes it. VoyageProfile used to offer
        // its own Scorer() helper for this, but nothing in the app called it and it ran
        // the profile regexes inside the per-node lambda -- the exact O(1)-per-call trap
        // the session's precomputed dictionary exists to avoid -- so it was deleted
        // rather than left as an attractive wrong turn.
        var profile = new VoyageProfile
        {
            BoardModifierWeight = 2.0,
            Rules = [new VoyageRule { Pattern = @"(\d+)\s+additional packs", Weight = 1 }],
        };
        var buffed = new Cell(1, 0);
        var mods = new[] { new BoardModifier("Adjacent Areas contain 8 additional packs", [buffed]) };

        var score = VoyageSolver.ScoreWith(
            mods, (m, _) => profile.ScoreText([m.Description]) * profile.BoardModifierWeight);
        var chart = Chart();

        Assert.Equal(16, score(chart, buffed) - chart.Value, 3);        // 8 x 1 x 2.0
        Assert.Equal(0, score(chart, new Cell(0, 0)) - chart.Value, 3); // untouched cell
    }
}

public class VoyageRulesFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pmw-rules-" + Guid.NewGuid().ToString("N"));
    private string File_ => Path.Combine(_dir, "voyage-rules.json");

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void MissingFileFallsBackToDefaults()
    {
        using var rules = new VoyageRules(File_);
        Assert.NotEmpty(rules.Profiles);
        Assert.NotNull(rules.Find("sulphur"));
    }

    [Fact]
    public void WritesDefaultsAsAStartingPoint()
    {
        using var rules = new VoyageRules(File_);
        rules.WriteDefaultsIfMissing();
        Assert.True(File.Exists(File_));
        Assert.Contains("sulphur", File.ReadAllText(File_));
    }

    [Fact]
    public void RoundTripsEditedProfiles()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(File_, """
        [
          {
            "name": "my sulphur",
            "boardModifierWeight": 3.0,
            "rules": [ { "pattern": "(\\d+)% increased Dead Man's Sulphur", "weight": 2.0 } ]
          }
        ]
        """);

        using var rules = new VoyageRules(File_);
        var p = rules.Find("my sulphur");
        Assert.NotNull(p);
        Assert.Equal(3.0, p!.BoardModifierWeight, 3);
        Assert.Equal(60, p.ScoreChart(new Chart("x", "x", ChartShape.End, 80,
            ["30% increased Dead Man's Sulphur found in this Area"])), 3);
    }

    [Fact]
    public void ReloadPicksUpEditsWithoutANewInstance()
    {
        // Tuning weights is iterative: try, look at the plan, adjust. Needing a restart
        // for each tweak would make the tool useless for the thing it exists to do.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(File_, """[ { "name": "a", "rules": [] } ]""");
        using var rules = new VoyageRules(File_);
        Assert.NotNull(rules.Find("a"));

        File.WriteAllText(File_, """[ { "name": "b", "rules": [] } ]""");
        rules.Reload();

        Assert.Null(rules.Find("a"));
        Assert.NotNull(rules.Find("b"));
    }

    [Fact]
    public void BrokenJsonKeepsTheLastGoodProfilesAndReportsWhy()
    {
        // A half-saved file mid-edit must not blank the tool, but silently ignoring the
        // edit would be worse -- you would think a weight applied when it did not.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(File_, """[ { "name": "good", "rules": [] } ]""");
        using var rules = new VoyageRules(File_);

        string? reported = null;
        rules.Error += e => reported = e;

        File.WriteAllText(File_, "{ this is not json");
        rules.Reload();

        Assert.NotNull(rules.Find("good"));   // still serving the last good set
        Assert.NotNull(reported);
    }

    [Fact]
    public void CommentsAndTrailingCommasAreTolerated()
    {
        // The file is meant to be hand-edited, so it should not reject the things people
        // naturally write in a config.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(File_, """
        [
          // my farming profile
          { "name": "c", "rules": [ { "pattern": "x", "weight": 1 }, ], },
        ]
        """);
        using var rules = new VoyageRules(File_);
        Assert.NotNull(rules.Find("c"));
    }

    [Fact]
    public void LookupIsCaseInsensitive()
    {
        using var rules = new VoyageRules(File_);
        Assert.NotNull(rules.Find("SULPHUR"));
        Assert.NotNull(rules.Find("Pack Size"));
    }
}
