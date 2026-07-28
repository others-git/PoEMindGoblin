using MindGoblin.Core.Voyage;

namespace MindGoblin.Tests;

/// <summary>
/// The rule file is written once and then left alone, which is right for something the
/// user edits -- and meant no shipped profile ever reached anybody who had already run the
/// app. A file created when there were four profiles stayed at four, so seven new
/// objectives and a set of fixes to rules that matched nothing sat in the binary,
/// invisible, with no hint as to why.
/// </summary>
public class RulesUpgradeTests : IDisposable
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), $"voyage-rules-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    /// <summary>A rule file exactly as an older build would have written it.</summary>
    private void WriteOldFile(params string[] profileNames)
    {
        var shipped = VoyageRules.Defaults().ToDictionary(p => p.Name);
        var kept = profileNames.Select(n => shipped[n]).ToList();
        File.WriteAllText(_path, System.Text.Json.JsonSerializer.Serialize(kept,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public void ANewProfileIsAddedToAnExistingFile()
    {
        WriteOldFile("sulphur", "quantity", "high tier");

        using var rules = new VoyageRules(_path);
        Assert.DoesNotContain(rules.Profiles, p => p.Name == "strongbox");

        var added = rules.AddMissingDefaults();

        Assert.Contains("strongbox", added);
        Assert.Contains(rules.Profiles, p => p.Name == "strongbox");
        Assert.Equal(VoyageRules.Defaults().Count, rules.Profiles.Count);
    }

    [Fact]
    public void AddingIsAdditiveAndLeavesTunedProfilesAlone()
    {
        // A profile the user has tuned must not be overwritten because the shipped
        // weights moved; that is what Restore is for.
        WriteOldFile("sulphur");
        using var rules = new VoyageRules(_path);
        rules.Profiles.Single(p => p.Name == "sulphur").Rules[0].Weight = 999;
        rules.Save();

        using var reopened = new VoyageRules(_path);
        reopened.AddMissingDefaults();

        Assert.Equal(999, reopened.Profiles.Single(p => p.Name == "sulphur").Rules[0].Weight);
        Assert.Contains(reopened.Profiles, p => p.Name == "currency");
    }

    [Fact]
    public void AddingTwiceChangesNothingTheSecondTime()
    {
        WriteOldFile("sulphur");
        using var rules = new VoyageRules(_path);

        Assert.NotEmpty(rules.AddMissingDefaults());
        Assert.Empty(rules.AddMissingDefaults());
    }

    [Fact]
    public void ATunedProfileIsReportedAsDifferingFromTheShippedOne()
    {
        // The user cannot otherwise tell that a rule fix exists but is not in effect.
        WriteOldFile("sulphur", "quantity");
        using var rules = new VoyageRules(_path);
        rules.Profiles.Single(p => p.Name == "quantity").Rules.Clear();
        rules.Save();

        using var reopened = new VoyageRules(_path);
        var status = reopened.CompareWithDefaults();

        Assert.Contains("quantity", status.Outdated);
        Assert.DoesNotContain("sulphur", status.Outdated);
        Assert.True(status.AnythingToDo);
    }

    [Fact]
    public void AnUntouchedFileMatchesTheShippedRules()
    {
        using var rules = new VoyageRules(_path);
        rules.WriteDefaultsIfMissing();

        var status = rules.CompareWithDefaults();
        Assert.Empty(status.Missing);
        Assert.Empty(status.Outdated);
        Assert.False(status.AnythingToDo);
    }

    [Fact]
    public void RestoreReplacesEverything()
    {
        WriteOldFile("sulphur");
        using var rules = new VoyageRules(_path);
        rules.Profiles.Single(p => p.Name == "sulphur").Rules[0].Weight = 999;
        rules.Save();

        using var reopened = new VoyageRules(_path);
        reopened.RestoreDefaults();

        Assert.Equal(VoyageRules.Defaults().Count, reopened.Profiles.Count);
        Assert.Equal(VoyageRules.Defaults().Single(p => p.Name == "sulphur").Rules[0].Weight,
                     reopened.Profiles.Single(p => p.Name == "sulphur").Rules[0].Weight);
        Assert.False(reopened.CompareWithDefaults().AnythingToDo);
    }

    [Fact]
    public void TheAdditionSurvivesAReopen()
    {
        WriteOldFile("sulphur");
        using (var rules = new VoyageRules(_path)) rules.AddMissingDefaults();

        using var reopened = new VoyageRules(_path);
        Assert.Contains(reopened.Profiles, p => p.Name == "strongbox");
    }

    [Fact]
    public void TheFourOriginalProfilesGainTheEverythingElse()
    {
        // Exactly the situation on disk: a file written when there were four.
        WriteOldFile("sulphur", "pack size", "quantity", "high tier");
        using var rules = new VoyageRules(_path);

        var added = rules.AddMissingDefaults();

        Assert.Contains("strongbox", added);
        Assert.Contains("containers", added);
        Assert.Contains("currency", added);
        Assert.Contains("rare monsters", added);
        Assert.Contains("uniques", added);
        Assert.Contains("gold", added);
        Assert.Contains("flasks", added);
    }
}
