using System.Text.Json;
using System.Text.RegularExpressions;

namespace MindGoblin.Core.Voyage;

/// <summary>
/// Category emphasis sliders over the active profile.
///
/// The shipped rule weights encode RELATIVE truths within a category -- a Divine is
/// 100 chaos, a rolled box is ~3 rares -- that a user should not have to re-derive to
/// say "I care more about sulphur today". So tuning happens a level up: every rule
/// belongs to one CATEGORY, and a slider per category scales that whole group. 10 is
/// the shipped baseline; the 1-20 range runs x0.1 to x2.0. (Modelled on the strategy
/// weight-sets in community planners, which tune per reward stat, not per rule.)
/// </summary>
public static class WeightCategories
{
    public const int Baseline = 10;
    public const int Min = 0;
    public const int Max = 20;

    public static double Multiplier(int slider) =>
        Math.Clamp(slider, Min, Max) / (double)Baseline;

    /// <summary>Pattern -> stat, straight from the catalog: no keyword guessing.
    /// A rule the catalog does not know (a strategy's ExtraRule, a hand edit) is
    /// "Extras" and scales only with its own slider.</summary>
    public const string Other = "Extras";

    private static readonly Dictionary<string, Stat> StatByPattern =
        ModCatalog.Entries.ToDictionary(e => e.Pattern, e => e.Stat, StringComparer.Ordinal);

    public static string CategoryOf(VoyageRule rule) =>
        StatByPattern.TryGetValue(rule.Pattern, out var stat) ? stat.ToString() : Other;

    /// <summary>The stats a compiled profile has rules in, in enum order.</summary>
    public static IReadOnlyList<string> CategoriesIn(VoyageProfile profile)
    {
        var present = profile.Rules.Select(CategoryOf).ToHashSet(StringComparer.Ordinal);
        return [.. Enum.GetValues<Stat>().Select(s => s.ToString()).Append(Other)
            .Where(present.Contains)];
    }

    /// <summary>Baseline for stats the strategy weights, off for the rest -- so
    /// all-defaults is exactly the shipped preset.</summary>
    public static int DefaultFor(VoyageProfile profile, string category) =>
        CategoriesIn(profile).Contains(category) ? Baseline : 0;

    /// <summary>
    /// Every stat the panel offers: the whole enum, always -- the catalog itself is
    /// the donor for stats the strategy does not weight, so raising one from zero
    /// prices its mods at catalog value x the slider. An INVERTED strategy (dump)
    /// keeps to its own stats; borrowing positives would flip its meaning.
    /// </summary>
    public static IReadOnlyList<string> SliderCategories(VoyageProfile profile)
    {
        var own = CategoriesIn(profile);
        if (profile.ChartBaseValue > 0) return own;
        return [.. Enum.GetValues<Stat>().Select(s => s.ToString())
            .Concat(own).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The profile the solver actually runs: its rules scaled by their stat's slider,
    /// plus catalog rules at (unit x slider/10) for any raised stat the strategy has
    /// none of. All-default sliders return the profile itself, exactly.
    /// </summary>
    public static VoyageProfile Blended(
        VoyageProfile profile,
        IReadOnlyDictionary<string, int> sliders,
        IReadOnlyList<VoyageProfile> library)
    {
        var own = CategoriesIn(profile).ToHashSet(StringComparer.Ordinal);
        var allDefault = SliderCategories(profile)
            .All(c => sliders.GetValueOrDefault(c, DefaultFor(profile, c))
                      == DefaultFor(profile, c));
        if (allDefault) return profile;

        var rules = profile.Rules.Select(r => Scale(r,
            sliders.GetValueOrDefault(CategoryOf(r), Baseline))).ToList();

        if (profile.ChartBaseValue <= 0)
            foreach (var stat in Enum.GetValues<Stat>())
            {
                var name = stat.ToString();
                if (own.Contains(name)) continue;
                var v = sliders.GetValueOrDefault(name, 0);
                if (v <= 0) continue;
                rules.AddRange(ModCatalog.Entries
                    .Where(e => e.Stat == stat)
                    .Select(e => new VoyageRule
                    {
                        Pattern = e.Pattern,
                        Weight = e.UnitValue * Multiplier(v),
                        ScalesWithBoard = e.ScalesWithBoard,
                        Comment = e.Comment,
                    }));
            }

        return new VoyageProfile
        {
            Name = profile.Name,
            Description = profile.Description,
            BoardModifierWeight = profile.BoardModifierWeight,
            AreaLevelWeight = profile.AreaLevelWeight,
            ChartBaseValue = profile.ChartBaseValue,
            MonsterPayoutSynergy = profile.MonsterPayoutSynergy,
            StrandedSquarePenalty = profile.StrandedSquarePenalty,
            MaxCharts = profile.MaxCharts,
            Rules = rules,
        };
    }

    private static VoyageRule Scale(VoyageRule r, int slider) => new()
    {
        Pattern = r.Pattern,
        Weight = r.Weight * Multiplier(slider),
        ScalesWithBoard = r.ScalesWithBoard,
        Comment = r.Comment,
    };
}

/// <summary>
/// The slider positions, per profile, on disk. Deliberately NOT part of the session:
/// how much you care about sulphur outlives any one board, so clearing a session or
/// completing a voyage must not reset it.
/// </summary>
public sealed class VoyageWeightStore
{
    private readonly string _path;
    private readonly Dictionary<string, Dictionary<string, int>> _byProfile;

    public static string DefaultPath => SettingsFolder.FileIn("voyage-weights.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public VoyageWeightStore(string? path = null)
    {
        _path = path ?? DefaultPath;
        _byProfile = Load(_path);
    }

    private static Dictionary<string, Dictionary<string, int>> Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer
                           .Deserialize<Dictionary<string, Dictionary<string, int>>>(
                               File.ReadAllText(path), Json)
                       ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException) { }
        return [];
    }

    /// <summary>Sliders for a profile; absent categories are at their per-profile
    /// default (baseline for its own categories, off for borrowed ones).</summary>
    public IReadOnlyDictionary<string, int> For(string profileName) =>
        _byProfile.GetValueOrDefault(profileName) ?? [];

    public int Get(string profileName, string category, int @default) =>
        For(profileName).GetValueOrDefault(category, @default);

    public void Set(string profileName, string category, int slider, int @default)
    {
        var sliders = _byProfile.TryGetValue(profileName, out var s)
            ? s : _byProfile[profileName] = [];
        // default entries are absence, so the file only records actual opinions
        if (slider == @default) sliders.Remove(category);
        else sliders[category] = Math.Clamp(slider, WeightCategories.Min, WeightCategories.Max);
        Save();
    }

    public void Reset(string profileName)
    {
        if (_byProfile.Remove(profileName)) Save();
    }

    public bool AnyTuned(string profileName) => For(profileName).Count > 0;

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_byProfile, Json));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (IOException) { /* a locked settings file is not worth crashing over */ }
    }
}
