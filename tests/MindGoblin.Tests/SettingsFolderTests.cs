using MindGoblin.Core;
using MindGoblin.Core.Voyage;

namespace MindGoblin.Tests;

/// <summary>
/// Renaming the app renamed its settings folder, which would have orphaned everything in
/// it: the Voyage session -- a screenshot and dozens of hovers -- the panel calibration,
/// and any tuned rule profiles. All still on disk, under a name nothing looked at, with
/// the app starting empty and saying nothing.
/// </summary>
public class SettingsFolderTests : IDisposable
{
    private readonly string _old = Path.Combine(Path.GetTempPath(), $"old-{Guid.NewGuid():N}");
    private readonly string _new = Path.Combine(Path.GetTempPath(), $"new-{Guid.NewGuid():N}");

    public void Dispose()
    {
        foreach (var dir in new[] { _old, _new })
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    private void WriteOld(string name, string content)
    {
        Directory.CreateDirectory(_old);
        File.WriteAllText(Path.Combine(_old, name), content);
    }

    /// <summary>
    /// Portable on purpose: state lives in MindGoblin_data/ BESIDE THE EXE, where it can
    /// be found, backed up, or deleted with the folder -- never in a hidden appdata dir.
    /// </summary>
    [Fact]
    public void StorageLivesNextToTheExe()
    {
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "MindGoblin_data"), SettingsFolder.Path_);
        Assert.Equal(Path.Combine(SettingsFolder.Path_, "voyage-rules.json"),
                     SettingsFolder.FileIn("voyage-rules.json"));
        Assert.DoesNotContain("AppData", SettingsFolder.Path_, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SettingsAreCarriedOverOnTheFirstRun()
    {
        WriteOld("voyage-session.json", "{}");
        WriteOld("voyage-rules.json", "[]");
        WriteOld("panel-calibration.json", "{}");

        var carried = SettingsFolder.MigrateFromPreviousName(_old, _new);

        Assert.Equal(3, carried.Count);
        Assert.True(File.Exists(Path.Combine(_new, "voyage-session.json")));
        Assert.True(File.Exists(Path.Combine(_new, "voyage-rules.json")));
    }

    [Fact]
    public void TheOldFolderIsLeftAlone()
    {
        // A copy, not a move: if this goes wrong the originals are still there.
        WriteOld("voyage-session.json", "{}");
        SettingsFolder.MigrateFromPreviousName(_old, _new);
        Assert.True(File.Exists(Path.Combine(_old, "voyage-session.json")));
    }

    [Fact]
    public void AnExistingFileIsNeverOverwritten()
    {
        // Whatever is already under the new name is the current one. Running an older
        // build afterwards must not be able to clobber it.
        WriteOld("voyage-session.json", "OLD");
        Directory.CreateDirectory(_new);
        File.WriteAllText(Path.Combine(_new, "voyage-session.json"), "CURRENT");

        var carried = SettingsFolder.MigrateFromPreviousName(_old, _new);

        Assert.Empty(carried);
        Assert.Equal("CURRENT", File.ReadAllText(Path.Combine(_new, "voyage-session.json")));
    }

    [Fact]
    public void TheDeadCredentialFileIsNotCarriedOver()
    {
        // It held a DPAPI-encrypted trade session cookie. Nothing reads it any more, so
        // copying it forward would only spread an encrypted secret to a second place.
        WriteOld("credentials.dat", "encrypted");
        WriteOld("voyage-rules.json", "[]");

        var carried = SettingsFolder.MigrateFromPreviousName(_old, _new);

        Assert.Equal(["voyage-rules.json"], carried);
        Assert.False(File.Exists(Path.Combine(_new, "credentials.dat")));
    }

    [Fact]
    public void NoOldFolderIsNotAnError()
    {
        Assert.Empty(SettingsFolder.MigrateFromPreviousName(_old, _new));
    }

    [Fact]
    public void MigratingTwiceCarriesNothingTheSecondTime()
    {
        WriteOld("voyage-rules.json", "[]");
        Assert.Single(SettingsFolder.MigrateFromPreviousName(_old, _new));
        Assert.Empty(SettingsFolder.MigrateFromPreviousName(_old, _new));
    }

    [Fact]
    public void EverySettingsFileAgreesOnTheFolder()
    {
        // One of these pointing at the old name would quietly split the app's state
        // across two directories.
        foreach (var path in new[]
                 {
                     AppSettings.DefaultPath,
                     VoyageRules.DefaultPath,
                     VoyageSessionState.DefaultPath,
                     BoardLayout.DefaultPath,
                     ChartPanelReader.Options.DefaultPath,
                     AreaModifierPanel.Options.DefaultPath,
                     LevelReader.DefaultPath,
                 })
        {
            Assert.Equal(SettingsFolder.Path_, Path.GetDirectoryName(path));
        }
    }
}
