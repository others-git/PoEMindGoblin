using System.Text.Json;

namespace MindGoblin.Core;

/// <summary>
/// What the app remembers between runs.
///
/// Everything to do with the trade site is gone -- there are no credentials, no session
/// cookie, no watch list and no user agent, because nothing here talks to an authenticated
/// endpoint any more. What is left is the gem calculator's inputs.
///
/// The Voyage tools keep their own files (voyage-rules.json, voyage-session.json,
/// panel-calibration.json, area-modifier-panel.json) because they are edited by hand and
/// benefit from being separate; this is only for what the UI collects.
/// </summary>
public sealed class AppSettings
{
    /// <summary>League to price against, e.g. "Allflame". Empty means poe.watch's default.</summary>
    public string DefaultLeague { get; set; } = "";

    // --- gem RoI --------------------------------------------------------------------

    public double GemcutterChaos { get; set; } = 1.0;
    public double VaalOrbChaos { get; set; } = 1.0;

    /// <summary>
    /// Vaal Orb outcome odds. Persisted because they could NOT be verified against a
    /// primary source (see GemRoi.CorruptionOdds) -- the user must be able to correct
    /// them, and the correction must survive a restart.
    /// </summary>
    public double VaalNoChange { get; set; } = 0.25;
    public double VaalLevelUp { get; set; } = 0.25;
    public double VaalLevelDown { get; set; } = 0.25;
    public double VaalQualityChange { get; set; } = 0.25;

    public GemRoi.CorruptionOdds Corruption()
    {
        var odds = new GemRoi.CorruptionOdds(
            VaalNoChange, VaalLevelUp, VaalLevelDown, VaalQualityChange);
        // A corrupt settings file must not crash the calculator.
        return odds.IsNormalised ? odds : GemRoi.CorruptionOdds.Default;
    }

    public static string DefaultPath => SettingsFolder.FileIn("settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return new AppSettings();
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options)
                   ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A corrupt settings file must not stop the app starting.
            return new AppSettings();
        }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
        File.Move(tmp, path, overwrite: true);
    }
}
