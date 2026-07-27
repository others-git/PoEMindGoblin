using System.Text.Json;

namespace PoeMarketWatch.Core;

/// <summary>
/// Which stats can actually spawn on which item categories.
///
/// Generated from Path of Building's data (see README) rather than hand-written, so the
/// spawn rules have exactly one implementation. This is the thing the in-game filter does
/// not give you: it will happily let you build a search that can never match, because it
/// does not know that "increased Cast Speed" is Shaper-only on gloves, or that weapons use
/// a *local* attack speed mod with a different id than the one rings use.
///
/// Deliberately advisory. The index covers explicit affixes only -- implicit, crafted,
/// veiled and eldritch stats are absent -- so an unknown stat is reported as "unknown",
/// never as "invalid". Restricting on incomplete data would make this app worse than the
/// in-game filter, which is the opposite of the point.
/// </summary>
public sealed class StatIndex
{
    public sealed record Spawn(string AffixType, string Group, string? Influence);

    private readonly Dictionary<string, string> _statText;
    private readonly Dictionary<string, Dictionary<string, Spawn>> _spawns;

    public StatIndex(
        Dictionary<string, string> statText,
        Dictionary<string, Dictionary<string, Spawn>> spawns)
    {
        _statText = statText;
        _spawns = spawns;
    }

    public IReadOnlyCollection<string> Categories => _spawns.Keys;
    public int StatCount => _statText.Count;

    /// <summary>Default location beside the exe; null when the asset is missing.</summary>
    public static StatIndex? LoadDefault()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "assets", "trade-index.json");
        return File.Exists(path) ? Load(path) : null;
    }

    public static StatIndex Load(string path)
    {
        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;

        var text = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("stats", out var stats) && stats.ValueKind == JsonValueKind.Object)
            foreach (var p in stats.EnumerateObject())
                text[p.Name] = p.Value.GetString() ?? "";

        var spawns = new Dictionary<string, Dictionary<string, Spawn>>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("spawns", out var sp) && sp.ValueKind == JsonValueKind.Object)
        {
            foreach (var cat in sp.EnumerateObject())
            {
                var map = new Dictionary<string, Spawn>(StringComparer.Ordinal);
                foreach (var entry in cat.Value.EnumerateObject())
                {
                    var v = entry.Value;
                    map[entry.Name] = new Spawn(
                        v.TryGetProperty("t", out var t) ? t.GetString() ?? "" : "",
                        v.TryGetProperty("g", out var g) ? g.GetString() ?? "" : "",
                        v.TryGetProperty("i", out var i) ? i.GetString() : null);
                }
                spawns[cat.Name] = map;
            }
        }
        return new StatIndex(text, spawns);
    }

    public string? TextFor(string statId) => _statText.GetValueOrDefault(statId);

    public Spawn? SpawnOf(string category, string statId) =>
        _spawns.TryGetValue(category, out var map) ? map.GetValueOrDefault(statId) : null;

    public bool KnowsCategory(string category) => _spawns.ContainsKey(category);

    public bool KnowsStat(string statId) => _statText.ContainsKey(statId);

    // ------------------------------------------------------------------ review
    public enum Severity { Info, Warning, Error }

    public sealed record Finding(Severity Level, string StatId, string Message)
    {
        public override string ToString() => $"{Level}: {Message}";
    }

    /// <summary>
    /// Review a trade query's stat filters. Findings are advice, not gates:
    /// Error means "cannot spawn there", Warning means "needs influence" or
    /// "two filters compete for one mod group", Info means "not in the index".
    /// </summary>
    public IReadOnlyList<Finding> Review(JsonElement query)
    {
        var findings = new List<Finding>();
        var category = CategoryOf(query);
        var statIds = StatIdsOf(query);
        if (statIds.Count == 0) return findings;

        if (category is null)
        {
            findings.Add(new Finding(Severity.Info, "",
                "no item category in the query, so spawn checks were skipped"));
            return findings;
        }
        if (!KnowsCategory(category))
        {
            findings.Add(new Finding(Severity.Info, "",
                $"category '{category}' is not in the index; spawn checks were skipped"));
            return findings;
        }

        var groups = new Dictionary<string, string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in statIds)
        {
            var spawn = SpawnOf(category, id);
            var label = TextFor(id) ?? id;

            // The same filter twice is its own mistake, and it is not caught by the mod
            // group check below (which deliberately ignores a stat colliding with itself).
            if (!seen.Add(id))
            {
                findings.Add(new Finding(Severity.Warning, id,
                    $"'{label}' appears twice in the same group -- one item cannot roll it twice"));
                continue;
            }

            if (spawn is null)
            {
                findings.Add(KnowsStat(id)
                    ? new Finding(Severity.Error, id, $"'{label}' cannot spawn on {category}")
                    : new Finding(Severity.Info, id,
                        $"'{id}' is not an explicit affix in the index (implicit/crafted?), not checked"));
                continue;
            }

            if (spawn.Influence is { } infl)
                findings.Add(new Finding(Severity.Warning, id,
                    $"'{label}' on {category} requires {infl} influence"));

            // Two filters resolving to the same mod group cannot both roll on one item.
            if (groups.TryGetValue(spawn.Group, out var other) && other != id)
                findings.Add(new Finding(Severity.Warning, id,
                    $"'{label}' and '{TextFor(other) ?? other}' both come from mod group " +
                    $"'{spawn.Group}' -- one item cannot have both"));
            else
                groups[spawn.Group] = id;
        }
        return findings;
    }

    public static string? CategoryOf(JsonElement query)
    {
        if (!TryGet(query, out var q, "query")) q = query;
        if (!TryGet(q, out var filters, "filters")) return null;
        if (!TryGet(filters, out var tf, "type_filters")) return null;
        if (!TryGet(tf, out var inner, "filters")) return null;
        if (!TryGet(inner, out var cat, "category")) return null;
        return TryGet(cat, out var opt, "option") && opt.ValueKind == JsonValueKind.String
            ? opt.GetString()
            : null;
    }

    public static List<string> StatIdsOf(JsonElement query)
    {
        var ids = new List<string>();
        if (!TryGet(query, out var q, "query")) q = query;
        if (!TryGet(q, out var stats, "stats") || stats.ValueKind != JsonValueKind.Array) return ids;

        foreach (var group in stats.EnumerateArray())
        {
            if (!TryGet(group, out var filters, "filters") || filters.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var f in filters.EnumerateArray())
            {
                // A disabled filter is not part of the search.
                if (TryGet(f, out var disabled, "disabled")
                    && disabled.ValueKind == JsonValueKind.True) continue;
                if (TryGet(f, out var id, "id") && id.ValueKind == JsonValueKind.String
                    && id.GetString() is { Length: > 0 } s)
                    ids.Add(s);
            }
        }
        return ids;
    }

    private static bool TryGet(JsonElement e, out JsonElement value, string name)
    {
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out value)) return true;
        value = default;
        return false;
    }
}
