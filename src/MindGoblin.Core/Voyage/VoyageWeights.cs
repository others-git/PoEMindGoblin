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

    /// <summary>
    /// Category per keyword, FIRST match wins -- order carries the judgement calls.
    /// "Rare Monsters drop Dead Man's Sulphur" is a sulphur payout that happens to ride
    /// rares, so Sulphur outranks Rares; the orb drops are Currency the same way.
    /// </summary>
    private static readonly (string Category, Regex Match)[] Classifier =
    [
        ("Sulphur", Rx(@"Sulphur|Filthscrabble")),
        // Boundaries carry the difference between neighbours: "Gold" is not "Golden
        // Lantern", and "Divine" is not "Diviner's Strongbox".
        ("Gold", Rx(@"Gold\b")),
        ("Currency", Rx(@"Divine\b|Exalted|Scarab|Chaos|Annulment|Regret|Ancient|Chromatic|"
                        + @"Gemcutter|Orbs?\b|Stacked Decks|Currency|Altar")),
        ("Magic monsters", Rx(@"Magic Monsters|at least Magic")),
        ("Rares", Rx(@"Rare Monsters|Imprisoned|Essences|Possessed|Fracture on death|"
                     + @"Pantheon|Starfish|Wisps|Atziri")),
        ("Packs", Rx(@"Pack Size|additional packs?")),
        ("Containers", Rx(@"Strongbox|Barrel|Bottle|Treasure|Locker|Tormented|Fish|"
                          + @"Jellyfish|Lantern")),
        ("Areas", Rx(@"^Area:")),
        ("Loot", Rx(@"Qu?au?ntity|Rarity|explicit modifier|Flasks?|Experience|Fractured|"
                    + @"Support Gem|Unique")),
    ];

    public const string Other = "Other";

    private static Regex Rx(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string CategoryOf(VoyageRule rule)
    {
        foreach (var (category, match) in Classifier)
            if (match.IsMatch(rule.Pattern)) return category;
        return Other;
    }

    /// <summary>
    /// Which shipped profile OWNS each category -- where its rules come from when the
    /// active profile has none of its own. This is what makes the sliders a blend
    /// rather than a pointless uniform rescale: a single-minded profile like sulphur
    /// classifies into ONE category, and scaling one category by itself changes no
    /// ordering at all. Pulling a silent category in from its owner does.
    /// </summary>
    public static readonly IReadOnlyList<(string Category, string Owner)> Owners =
    [
        ("Sulphur", "sulphur"),
        ("Currency", "currency"),
        ("Gold", "gold"),
        ("Rares", "rare monsters"),
        ("Magic monsters", "magic monsters"),
        ("Packs", "pack size"),
        ("Containers", "containers"),
        ("Loot", "uniques"),
    ];

    /// <summary>Baseline for the profile's own categories, off for borrowed ones --
    /// so all-defaults is exactly the plain profile.</summary>
    public static int DefaultFor(VoyageProfile profile, string category) =>
        CategoriesIn(profile).Contains(category) ? Baseline : 0;

    /// <summary>
    /// Every category the panel should offer for a profile: its own, plus each owned
    /// category it could borrow. An INVERTED profile (dump prices value negatively so
    /// junk sorts first) only gets its own -- blending positive rules into it would
    /// quietly turn the junk detector into a value detector.
    /// </summary>
    public static IReadOnlyList<string> SliderCategories(VoyageProfile profile)
    {
        var own = CategoriesIn(profile);
        if (profile.ChartBaseValue > 0) return own;
        var ordered = Owners.Select(o => o.Category)
            .Concat(own)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        // canonical order first, then any leftover profile-only categories (Areas, Other)
        return [.. Classifier.Select(c => c.Category).Append(Other)
            .Where(c => ordered.Contains(c))];
    }

    /// <summary>
    /// The profile the solver actually runs: its own rules scaled by their category
    /// sliders, plus -- for each category it has no rules in but whose slider is
    /// raised above zero -- the owning profile's rules for that category, scaled the
    /// same way. All-default sliders return the profile itself, exactly.
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
            foreach (var (category, ownerName) in Owners)
            {
                if (own.Contains(category)) continue;
                var v = sliders.GetValueOrDefault(category, 0);
                if (v <= 0) continue;
                var owner = library.FirstOrDefault(p => p.Name == ownerName);
                if (owner is null || ReferenceEquals(owner, profile)) continue;
                rules.AddRange(owner.Rules
                    .Where(r => CategoryOf(r) == category)
                    .Select(r => Scale(r, v)));
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

    /// <summary>The categories a profile actually has rules in, in display order.</summary>
    public static IReadOnlyList<string> CategoriesIn(VoyageProfile profile)
    {
        var present = profile.Rules.Select(CategoryOf).ToHashSet(StringComparer.Ordinal);
        return [.. Classifier.Select(c => c.Category).Append(Other).Where(present.Contains)];
    }

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
