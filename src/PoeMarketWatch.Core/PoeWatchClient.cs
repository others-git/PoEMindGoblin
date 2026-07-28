using System.Text.Json;

namespace PoeMarketWatch.Core;

/// <summary>
/// Economy data from api.poe.watch.
///
/// Why not poe.ninja: it is behind a Cloudflare challenge for non-browser clients. Its
/// documented /api/data/* endpoints return 404 on every league including Standard, the
/// api.poe.ninja host does not resolve, and the page HTML served to a plain client has no
/// script tags at all -- a challenge shell, not the app. poe.watch is an open JSON API
/// carrying the same leagues and, crucially, gemLevel/gemQuality/gemIsCorrupted per row,
/// which is exactly the axis a gem-flip calculator needs.
///
/// Caveat worth remembering when reading results: this is a different dataset to
/// poe.ninja and may disagree or lag. The `min` field in particular is noisy -- mispriced
/// and bait listings live in the low tail -- which is why the RoI maths uses `mean`.
/// </summary>
public sealed class PoeWatchClient : IDisposable
{
    public const string BaseUrl = "https://api.poe.watch";

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public PoeWatchClient(string userAgent, HttpClient? http = null)
    {
        _ownsHttp = http is null;
        _http = http ?? HttpFactory.Create(new Uri(BaseUrl));
        _http.BaseAddress ??= new Uri(BaseUrl);
        if (!_http.DefaultRequestHeaders.UserAgent.TryParseAdd(userAgent))
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
    }

    public sealed record Row(
        string Name, double Mean, double Min, double Max, int Daily,
        bool LowConfidence, int GemLevel, int GemQuality, bool GemCorrupted);

    public async Task<IReadOnlyList<Row>> GetAsync(
        string category, string league, CancellationToken ct = default)
    {
        var path = $"/get?category={Uri.EscapeDataString(category)}&league={Uri.EscapeDataString(league)}";
        using var resp = await _http.GetAsync(path, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new TradeApiException($"poe.watch returned {(int)resp.StatusCode} for {category}/{league}");

        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new TradeApiException("poe.watch response was not an array");

        var rows = new List<Row>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var name = Str(e, "name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            rows.Add(new Row(
                Name: name,
                Mean: Num(e, "mean"),
                Min: Num(e, "min"),
                Max: Num(e, "max"),
                Daily: (int)Num(e, "daily"),
                LowConfidence: e.TryGetProperty("lowConfidence", out var lc)
                               && lc.ValueKind == JsonValueKind.True,
                GemLevel: (int)Num(e, "gemLevel"),
                GemQuality: (int)Num(e, "gemQuality"),
                GemCorrupted: e.TryGetProperty("gemIsCorrupted", out var gc)
                              && gc.ValueKind == JsonValueKind.True));
        }
        return rows;
    }

    /// <summary>Gem rows collapsed into per-gem price ladders, ready for <see cref="GemRoi"/>.</summary>
    public static IReadOnlyDictionary<string, GemRoi.GemPrices> ToLadders(
        IEnumerable<Row> rows, int minDailyVolume = 0, bool excludeLowConfidence = true)
    {
        var byGem = new Dictionary<string, Dictionary<(int, int, bool), double>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            // A price backed by almost no listings is worse than no price: it produces a
            // confident-looking row you cannot actually trade against.
            if (excludeLowConfidence && r.LowConfidence) continue;
            if (r.Daily < minDailyVolume) continue;
            if (r.Mean <= 0) continue;

            if (!byGem.TryGetValue(r.Name, out var ladder))
                byGem[r.Name] = ladder = new Dictionary<(int, int, bool), double>();
            ladder[(r.GemLevel, r.GemQuality, r.GemCorrupted)] = r.Mean;
        }
        return byGem.ToDictionary(
            kv => kv.Key,
            kv => new GemRoi.GemPrices(kv.Key, kv.Value),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Chaos price of a named currency, e.g. "Gemcutter's Prism".</summary>
    public static double? CurrencyChaos(IEnumerable<Row> currencyRows, string name) =>
        currencyRows.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Mean;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
        && v.TryGetDouble(out var d) ? d : 0;

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
