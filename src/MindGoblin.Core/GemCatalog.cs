using System.Text.Json;

namespace MindGoblin.Core;

/// <summary>
/// Which gems can be bought from a vendor, generated from Path of Building's gem data.
///
/// The RoI maths is only meaningful for gems with a cheap level-1 entry point. Vaal,
/// Awakened and exceptional (Empower/Enlighten/Enhance) gems are drop-only.
///
/// Two joins-related facts, both learned the hard way:
///
/// 1. PoB stores support gems WITHOUT the " Support" suffix while the trade site and
///    poe.watch use it, so lookups normalise the suffix away on both sides.
/// 2. The PoB clone lags the live game. Measured against a live league, 63 gem names on
///    poe.watch were absent from PoB entirely (transfigured gems like "Chain Hook of
///    Angling"). Unknown gems therefore default to NOT vendor-buyable: a stale catalogue
///    must never invent a cheap entry price that does not exist.
/// </summary>
public sealed class GemCatalog
{
    public sealed record Gem(string Name, string PobName, bool Support, int MaxLevel, bool Vendor, string Why);

    private readonly Dictionary<string, Gem> _byNormalised;

    public GemCatalog(IEnumerable<Gem> gems)
    {
        _byNormalised = new Dictionary<string, Gem>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in gems) _byNormalised[Normalise(g.Name)] = g;
    }

    public int Count => _byNormalised.Count;
    public IEnumerable<Gem> All => _byNormalised.Values;
    public IEnumerable<Gem> VendorGems => _byNormalised.Values.Where(g => g.Vendor);

    /// <summary>Strip the " Support" suffix so PoB and market names join.</summary>
    public static string Normalise(string name)
    {
        var n = name.Trim();
        return n.EndsWith(" Support", StringComparison.OrdinalIgnoreCase)
            ? n[..^" Support".Length]
            : n;
    }

    public Gem? Find(string name) =>
        _byNormalised.GetValueOrDefault(Normalise(name));

    /// <summary>
    /// True only when the catalogue positively says the gem is vendor-buyable.
    /// Unknown gems are excluded on purpose -- see the class remarks.
    /// </summary>
    public bool IsVendorBuyable(string name) => Find(name)?.Vendor ?? false;

    public static GemCatalog? LoadDefault()
    {
        // A file beside the exe overrides the embedded copy; the shipped app is one
        // exe and normally has neither the folder nor the need for it.
        var path = Path.Combine(AppContext.BaseDirectory, "assets", "gem-index.json");
        if (File.Exists(path)) return Load(path);

        return typeof(GemCatalog).Assembly.GetManifestResourceStream("assets/gem-index.json")
            is { } embedded ? Load(embedded) : null;
    }

    public static GemCatalog Load(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    public static GemCatalog Load(Stream stream)
    {
        using var doc = JsonDocument.Parse(stream);
        var gems = new List<Gem>();

        if (doc.RootElement.TryGetProperty("gems", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in arr.EnumerateArray())
            {
                var name = Str(e, "name");
                if (string.IsNullOrWhiteSpace(name)) continue;
                gems.Add(new Gem(
                    Name: name,
                    PobName: Str(e, "pobName") ?? name,
                    Support: e.TryGetProperty("support", out var s) && s.ValueKind == JsonValueKind.True,
                    MaxLevel: e.TryGetProperty("maxLevel", out var m) && m.TryGetInt32(out var lvl) ? lvl : 20,
                    Vendor: e.TryGetProperty("vendor", out var v) && v.ValueKind == JsonValueKind.True,
                    Why: Str(e, "why") ?? ""));
            }
        }
        return new GemCatalog(gems);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
