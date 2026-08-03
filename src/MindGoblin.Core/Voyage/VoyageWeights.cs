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

    /// <summary>
    /// Slider names that have been renamed, old to new.
    ///
    /// The originals were shorthand nobody outside the catalog could read -- "LootLock"
    /// for the mod that deletes your equipment, "Meta" for modifier magnitude, "Player"
    /// for Soul Eater. They are also the KEYS a tuned voyage-weights.json is written
    /// under, so the rename has to carry the user's sliders across rather than silently
    /// resetting every one of them to the shipped baseline.
    /// </summary>
    private static readonly Dictionary<string, string> Renamed =
        new(StringComparer.Ordinal)
        {
            ["Boxes"] = nameof(Stat.Strongboxes),
            ["Containers"] = nameof(Stat.Openables),
            ["Preserve"] = nameof(Stat.ChartRefunds),
            ["LootLock"] = nameof(Stat.NoEquipmentDrops),
            ["Meta"] = nameof(Stat.ModifierMagnitude),
            ["Player"] = nameof(Stat.PlayerPower),
        };

    /// <summary>The current name for a category, whatever it was called when it was
    /// written down.</summary>
    public static string Current(string category) =>
        Renamed.GetValueOrDefault(category, category);

    /// <summary>What the panel PRINTS for a category. The enum name is an identifier;
    /// this is the thing a person reads off a slider.</summary>
    public static string LabelOf(string category) =>
        category == Other ? "Strategy extras"
        : Enum.TryParse<Stat>(Current(category), out var stat) ? StatText.Label(stat)
        : category;

    /// <summary>What it says on hover: one sentence on what the stat covers, and which
    /// way to weight it when that is not obvious. Empty for a category with nothing to
    /// add, so a caller can skip the tooltip rather than show a blank one.</summary>
    public static string DescriptionOf(string category) =>
        category == Other
            ? "Rules belonging to this strategy alone — tileset preferences and field "
              + "lessons that no shared stat can carry."
            : Enum.TryParse<Stat>(Current(category), out var stat) ? StatText.Describe(stat)
            : "";

    /// <summary>
    /// The stats every upright shipped strategy prices NEGATIVE -- the traps the game's
    /// own tables file as rewards.
    ///
    /// The catalog stores them positive by design: a Danger entry is a SEVERITY, and
    /// "cannot drop Equipment" is a +1 magnitude, because what a mod IS and how much a
    /// plan cares are separate questions. A borrowed rule taken at catalog sign therefore
    /// answers "I care about danger" with charts most likely to end the voyage -- and the
    /// slider is the only place a stat can arrive without a strategy's opinion attached.
    ///
    /// Which way a stat points is not a new hand-list: the shipped strategies already
    /// say, unanimously, and <c>EveryTrapStatIsWeightedNegativeInWordsAndInSign</c>
    /// fails the build if that ever stops agreeing with what the tooltip tells the user.
    /// The dump is excluded by the same test the borrow path uses, because it inverts
    /// EVERY stat -- and being inverted it never borrows at all.
    /// </summary>
    private static readonly HashSet<Stat> NegativeByConvention =
        Strategies.Shipped()
            .Where(s => s.ChartBaseValue <= 0)
            .SelectMany(s => s.Weights)
            .GroupBy(w => w.Key)
            .Where(g => g.All(w => w.Value < 0))
            .Select(g => g.Key)
            .ToHashSet();

    /// <summary>Which way a slider on this stat points: a trap is steered AWAY from, so
    /// its borrowed rules enter negative at the slider's magnitude.</summary>
    public static double SignOf(Stat stat) => NegativeByConvention.Contains(stat) ? -1 : 1;

    /// <summary>Only a FALLBACK now, for rules written before
    /// <see cref="VoyageRule.Category"/> existed; a compiled profile stamps its own.</summary>
    private static readonly Dictionary<string, Stat> StatByPattern =
        ModCatalog.Entries.ToDictionary(e => e.Pattern, e => e.Stat, StringComparer.Ordinal);

    /// <summary>
    /// The slider that scales this rule.
    ///
    /// The stamp wins where there is one, because it can tell a catalog mod from a
    /// strategy's own rule that happens to share its wording -- and several do, at
    /// deliberately different weights. Untagged rules keep the pattern lookup, so an
    /// existing hand-edited rule file behaves exactly as it did.
    /// </summary>
    public static string CategoryOf(VoyageRule rule) =>
        rule.Category is { } stamped ? Current(stamped)
        : StatByPattern.TryGetValue(rule.Pattern, out var stat) ? stat.ToString()
        : Other;

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
        {
            // A borrowed mod the profile ALREADY carries would score twice -- and it is
            // not hypothetical: uniques prices the fracture line at 0.4 where the
            // catalog says 0.5, deliberately, and borrowing Rares would stack both.
            var carried = profile.Rules.Select(r => r.Pattern).ToHashSet(StringComparer.Ordinal);
            foreach (var stat in Enum.GetValues<Stat>())
            {
                var name = stat.ToString();
                if (own.Contains(name)) continue;
                var v = sliders.GetValueOrDefault(name, 0);
                if (v <= 0) continue;
                rules.AddRange(ModCatalog.Entries
                    .Where(e => e.Stat == stat && !carried.Contains(e.Pattern))
                    .Select(e => new VoyageRule
                    {
                        Pattern = e.Pattern,
                        // The slider gives magnitude, the stat gives sign: a trap
                        // borrowed at catalog sign would make the planner PREFER it.
                        Weight = SignOf(stat) * e.UnitValue * Multiplier(v),
                        ScalesWithBoard = e.ScalesWithBoard,
                        Comment = e.Comment,
                        Category = name,
                    }));
            }
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
            {
                var loaded = JsonSerializer
                    .Deserialize<Dictionary<string, Dictionary<string, int>>>(
                        File.ReadAllText(path), Json);
                if (loaded is not null) return Migrate(loaded);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException) { }
        return [];
    }

    /// <summary>
    /// Carry sliders written under a category's old name across the rename.
    ///
    /// Without this, renaming "LootLock" to "NoEquipmentDrops" would leave the user's
    /// deliberate -40 sitting under a key nothing reads -- their tuning would silently
    /// revert to the shipped baseline and the file would keep the evidence.
    /// </summary>
    private static Dictionary<string, Dictionary<string, int>> Migrate(
        Dictionary<string, Dictionary<string, int>> byProfile)
    {
        foreach (var sliders in byProfile.Values)
            foreach (var (key, value) in sliders.ToList())
            {
                var current = WeightCategories.Current(key);
                if (current == key) continue;
                sliders.Remove(key);
                // A file already carrying the new key keeps it: it is the later opinion.
                sliders.TryAdd(current, value);
            }
        return byProfile;
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
