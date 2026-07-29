using System.Text.Json;
using System.Text.Json.Serialization;

namespace MindGoblin.Core.Voyage;

/// <summary>
/// A saved planning session.
///
/// Reading a board is slow work -- a screenshot, then nine hovers for the Area Modifiers,
/// then a hover per chart worth scoring. Losing that because the app was closed would
/// make the tool actively worse than doing it by hand, so the session is written to disk
/// after every capture and read back on startup.
///
/// This is a deliberate DTO rather than the domain types serialised directly. The saved
/// file is a contract with the user's past self: renaming a field on <see cref="Chart"/>
/// should be a compile error here, not a silent loss of everything they read yesterday.
/// </summary>
public sealed class VoyageSessionState
{
    /// <summary>Bumped when the shape changes incompatibly; older files are discarded.</summary>
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public int Rows { get; set; } = 3;
    public int Cols { get; set; } = 3;

    /// <summary>When this was written, so the UI can say how stale it is.</summary>
    public DateTimeOffset SavedAt { get; set; }

    /// <summary>The rule profile in use, so the app reopens where it was left.</summary>
    public string? Profile { get; set; }

    /// <summary>
    /// Whether the Soul Eater chart is being forced onto the board.
    ///
    /// A decision about THIS voyage rather than a setting: it only means anything while
    /// the panel that has a Soul Eater chart is the one on screen. It rides with the
    /// session so that reopening the app does not quietly undo it.
    /// </summary>
    public bool UseSoulEater { get; set; }

    public List<ChartState> Charts { get; set; } = [];

    /// <summary>Keyed by square number as a string, because JSON object keys are strings.</summary>
    public Dictionary<string, List<string>> SquareModifiers { get; set; } = [];

    public Dictionary<string, string> Figurines { get; set; } = [];

    /// <summary>
    /// One chart as read.
    ///
    /// Value and AdjacentValue are absent on purpose: they are what a PROFILE makes of a
    /// chart, not a property of the chart, and persisting them would resurrect yesterday's
    /// scoring under today's rules.
    /// </summary>
    public sealed class ChartState
    {
        public int PanelIndex { get; set; }
        public string Name { get; set; } = "";
        public ChartShape Shape { get; set; }
        public int AreaLevel { get; set; }
        public string AreaName { get; set; } = "";
        public string? VoyageModifier { get; set; }
        public string? AdjacentModifier { get; set; }
        public int RequiresLevel { get; set; }
        public double ItemQuantity { get; set; }
        public double ItemRarity { get; set; }
        public double MonsterPackSize { get; set; }
        public double GoldFound { get; set; }
        public double Sulphur { get; set; }
        public List<string> Modifiers { get; set; } = [];
    }

    public static string DefaultPath => SettingsFolder.FileIn("voyage-session.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Written aside and moved into place: a crash mid-write would otherwise leave a
        // truncated file, and the next start would silently begin from nothing.
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Read a saved session, or null when there is nothing usable.
    ///
    /// Null covers every failure -- absent, unreadable, corrupt, or written by a version
    /// that structured things differently. Starting empty is recoverable; starting from
    /// half-parsed state is not.
    /// </summary>
    public static VoyageSessionState? Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return null;
        try
        {
            var loaded = JsonSerializer.Deserialize<VoyageSessionState>(File.ReadAllText(path), Json);
            if (loaded is null || loaded.Version != CurrentVersion) return null;
            if (loaded.Rows <= 0 || loaded.Cols <= 0) return null;
            return loaded;
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    public static void Delete(string? path = null)
    {
        path ??= DefaultPath;
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* a locked file is not worth failing a Clear over */ }
    }
}
