using MindGoblin.Core;

namespace MindGoblin.Tests;

/// <summary>
/// Portable on purpose: state lives in MindGoblin_data/ BESIDE THE EXE, where it can be
/// found, backed up, or deleted with the folder -- never in a hidden appdata dir. And
/// only there: a legacy %LOCALAPPDATA% migration was removed after it made every fresh
/// copy of the exe resurrect stale state from a folder the user thought was gone.
/// </summary>
public class SettingsFolderTests
{
    [Fact]
    public void StorageLivesNextToTheExe()
    {
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "MindGoblin_data"), SettingsFolder.Path_);
        Assert.Equal(Path.Combine(SettingsFolder.Path_, "voyage-rules.json"),
                     SettingsFolder.FileIn("voyage-rules.json"));
        Assert.DoesNotContain("AppData", SettingsFolder.Path_, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Nothing in Core may compose a path through appdata any more.</summary>
    [Fact]
    public void NothingReachesIntoAppData()
    {
        var core = typeof(SettingsFolder).Assembly;
        Assert.DoesNotContain(core.GetType("MindGoblin.Core.SettingsFolder")!.GetMethods(),
                              m => m.Name.Contains("Migrate"));
    }
}
