using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoeMarketWatch.Core;

/// <summary>A saved live search.</summary>
public sealed class Watch
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Untitled";
    public string League { get; set; } = "";
    public string QueryId { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>Play a sound when this watch matches.</summary>
    public bool Notify { get; set; } = true;

    [JsonIgnore]
    public string TradeUrl => TradeUrlParser.Build(League, QueryId);
}

/// <summary>
/// Turns a pasted trade URL into (league, queryId).
///
/// This is how a search gets into the app: you build it on the trade site -- where the
/// full filter UI already exists and is always current -- then paste the URL here. The
/// alternative, reimplementing every filter, would be perpetually behind GGG.
/// </summary>
public static class TradeUrlParser
{
    public static string Build(string league, string queryId) =>
        $"{TradeClient.BaseUrl}/trade/search/{Uri.EscapeDataString(league)}/{Uri.EscapeDataString(queryId)}";

    /// <summary>
    /// Accepts a full URL (with or without scheme), or a bare "League/queryId".
    /// Returns false rather than throwing -- this is fed directly by a paste box.
    /// </summary>
    public static bool TryParse(string? input, out string league, out string queryId)
    {
        league = queryId = "";
        if (string.IsNullOrWhiteSpace(input)) return false;
        var text = input.Trim();

        // Strip any query string / fragment first (e.g. ?q=... from a live search link).
        var cut = text.IndexOfAny(['?', '#']);
        if (cut >= 0) text = text[..cut];

        const string marker = "/trade/search/";
        var idx = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            text = text[(idx + marker.Length)..];
        }
        else if (LooksLikeUrl(text) && !input.Contains("/trade2/search/", StringComparison.OrdinalIgnoreCase))
        {
            // A URL that is not a trade search is not shorthand -- without this,
            // "https://www.pathofexile.com/" parses as league "https:".
            return false;
        }

        // PoE2 paths carry an extra realm segment: /trade2/search/poe2/{league}/{id}
        const string marker2 = "/trade2/search/";
        var idx2 = input.IndexOf(marker2, StringComparison.OrdinalIgnoreCase);
        if (idx2 >= 0)
        {
            text = input[(idx2 + marker2.Length)..];
            var c2 = text.IndexOfAny(['?', '#']);
            if (c2 >= 0) text = text[..c2];
            var seg = text.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (seg.Length >= 3)
            {
                league = Uri.UnescapeDataString(seg[1]);
                queryId = seg[2];
                return queryId.Length > 0;
            }
            return false;
        }

        var parts = text.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;

        // Take the LAST two segments so a bare "Allflame/abc" and a full URL both work.
        league = Uri.UnescapeDataString(parts[^2]);
        queryId = parts[^1];
        return league.Length > 0 && queryId.Length > 0 && !league.Contains('.');
    }

    private static bool LooksLikeUrl(string text) =>
        text.Contains("://", StringComparison.Ordinal)
        || text.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
        || text.Contains(".com", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Settings + saved watches, persisted as plain JSON (no secrets here).</summary>
public sealed class AppSettings
{
    public string UserAgent { get; set; } =
        "poe-market-watch/0.1 (+https://github.com/; contact via app settings)";
    public string DefaultLeague { get; set; } = "";
    public List<Watch> Watches { get; set; } = new();

    /// <summary>Global hotkey that travels to the most recent match.</summary>
    public string TravelHotkey { get; set; } = "Ctrl+Alt+D";
    public bool PlaySound { get; set; } = true;

    // --- gem RoI -----------------------------------------------------------
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

    /// <summary>Credentials live in the DPAPI store, never in this file.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PoeMarketWatch", "settings.json");

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
