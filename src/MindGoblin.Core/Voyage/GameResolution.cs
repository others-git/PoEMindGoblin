using System.Text.Json;

namespace MindGoblin.Core.Voyage;

/// <summary>
/// What resolution the game is drawn at, and how we came to believe it.
///
/// Every calibrated coordinate in this app is measured against ONE resolution and
/// rescaled to whatever the game is actually running at. Getting that number wrong is
/// not a subtle failure: the reader looks in the wrong place and reports an empty panel,
/// and the slurp hovers a cell that is not the one the plan numbered.
///
/// So it is detected rather than assumed, in three steps, each a fallback for the last:
///   1. AUTO -- ask the game's own window how big its client area is. Correct for
///      windowed, borderless and fullscreen alike, and it needs nothing from the user.
///   2. PINNED -- a resolution the user chose, for when detection cannot find the
///      process (a launcher variant nobody has named, a remote session).
///   3. The primary screen, which is what the app assumed for its whole life before this
///      and is right exactly when the game is fullscreen on the primary monitor.
/// </summary>
public sealed record GameResolution
{
    /// <summary>Detect from the game window. The default, and the right answer.</summary>
    public bool Auto { get; init; } = true;

    /// <summary>Used only when <see cref="Auto"/> is off. Zero means "not set".</summary>
    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>Is this a usable pin? A half-filled override is worse than none.</summary>
    public bool IsPinned => !Auto && Width > 0 && Height > 0;

    public static string DefaultPath => SettingsFolder.FileIn("game-resolution.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static GameResolution Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return new GameResolution();
        try
        {
            return JsonSerializer.Deserialize<GameResolution>(File.ReadAllText(path), Json)
                   ?? new GameResolution();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new GameResolution();
        }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>
/// Which running process is the game.
///
/// Standalone, Steam, Epic and the Korean and Garena builds all ship a differently
/// suffixed executable, and a hardcoded list would quietly stop matching the day a new
/// one appears -- which reads to the user as "the tool broke" and sends them to the
/// calibration screen for a problem calibration cannot fix. Matching the STEM covers the
/// variants that exist and the ones that do not yet.
///
/// Here rather than beside the Win32 call because it is a rule, not a syscall, and a
/// rule that decides whether the app finds the game at all is worth a test.
/// </summary>
public static class GameProcess
{
    public static bool IsGame(string? processName) =>
        !string.IsNullOrWhiteSpace(processName)
        && processName.Replace("_", "").Replace("-", "").Replace(" ", "")
            .Contains("pathofexile", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The resolutions worth offering by name.
///
/// Not an attempt at a complete list -- it is the shortlist a person scans when
/// detection has failed and they need to say what they are running. The calibration
/// ships measured at 2560x1440 and every other entry is that measurement rescaled, so
/// these are starting points to nudge from, not per-resolution measurements.
/// </summary>
public sealed record ScreenPreset(int Width, int Height, string Note = "")
{
    public string Label => Note.Length == 0
        ? $"{Width} × {Height}"
        : $"{Width} × {Height}  ({Note})";

    public static IReadOnlyList<ScreenPreset> Common { get; } =
    [
        new(1920, 1080, "1080p"),
        new(2560, 1440, "1440p — measured"),
        new(3840, 2160, "4K"),
        new(1920, 1200, "16:10"),
        new(2560, 1600, "16:10"),
        new(3440, 1440, "ultrawide"),
        new(5120, 1440, "super ultrawide"),
        new(1600, 900),
        new(1366, 768),
    ];

    /// <summary>The preset matching a size exactly, or null when it is something else
    /// entirely -- which is legitimate and must not be silently rounded to a neighbour.</summary>
    public static ScreenPreset? Match(int width, int height) =>
        Common.FirstOrDefault(p => p.Width == width && p.Height == height);
}
