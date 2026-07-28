using System.Text.Json;
using System.Text.RegularExpressions;

namespace PoeMarketWatch.Core.Voyage;

/// <summary>
/// Separates what a chart GIVES you from what it does to you.
///
/// A rare chart carries a dozen affixes and most of them are monster difficulty --
/// "34% more Monster Life", "+29% Monster Physical Damage Reduction", "Monsters take 39%
/// reduced Extra Damage from Critical Strikes". None of that answers the question you are
/// asking when you look at a planned square, which is what the run pays out.
///
/// The split is DATA, not judgement. Voyage charts have exactly three bases and poedb
/// publishes the full mod table for each, so the set is closed and enumerable rather than
/// something to infer from wording. <c>assets/voyage-mods.json</c> holds it, generated
/// from those tables, and is editable without a rebuild -- which matters for a league
/// mechanic that will change.
///
/// Two ordering rules make it work:
///   * REWARD is checked first, so a payout that happens to mention monsters -- extra
///     Rare Monsters, Essence-imprisoned monsters, Wildwood Wisps -- is not lost to the
///     difficulty list.
///   * A line matching NEITHER counts as a reward. The list is complete for this league,
///     but a patch that adds a payout must not have it silently hidden; the cost of being
///     wrong this way is an extra line, the other way it is a missing one.
/// </summary>
public static class ChartRewards
{
    public sealed class Catalogue
    {
        public List<string> Reward { get; set; } = [];
        public List<string> Difficulty { get; set; } = [];

        private Regex? _reward;
        private Regex? _difficulty;

        /// <summary>One alternation per group: dozens of separate matches per line adds up.</summary>
        internal Regex RewardPattern => _reward ??= Combine(Reward);
        internal Regex DifficultyPattern => _difficulty ??= Combine(Difficulty);

        private static Regex Combine(List<string> patterns) =>
            new(patterns.Count == 0 ? "(?!)" : string.Join("|", patterns.Select(p => $"(?:{p})")),
                RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public int Count => Reward.Count + Difficulty.Count;
    }

    private static Catalogue? _catalogue;

    /// <summary>The loaded mod tables. Reads the asset once, then caches.</summary>
    public static Catalogue Current => _catalogue ??= Load();

    /// <summary>Override the catalogue, for tests and for a user-supplied file.</summary>
    public static void Use(Catalogue catalogue) => _catalogue = catalogue;

    public static string DefaultPath =>
        Path.Combine(AppContext.BaseDirectory, "assets", "voyage-mods.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static Catalogue Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<Catalogue>(File.ReadAllText(path), Json);
                if (loaded is { Count: > 0 } && Compiles(loaded)) return loaded;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // fall through to the built-in minimum
        }

        // Without the asset the tool still has to tell rewards from monster mods, so this
        // is a floor rather than a duplicate of the file: the stat lines, the adjacency
        // payouts, and the monster pool.
        return new Catalogue
        {
            Reward =
            [
                @"increased Q(?:uantity|auntity) of Items found",
                @"increased Rarity of Items found",
                @"increased Pack Size",
                @"increased Gold found",
                @"increased Dead Man's Sulphur",
                @"additional (?:Strongboxes|packs of|Clusters of Barrels|Imprisoned Monsters)",
                @"increased number of (?:Rare|Magic) Monsters",
            ],
            Difficulty = [@"\bMonsters?\b", @"Players have [-+]?\d", @"Area has patches of"],
        };
    }

    /// <summary>A pattern that does not compile would throw at the first hover.</summary>
    private static bool Compiles(Catalogue catalogue)
    {
        try
        {
            _ = catalogue.RewardPattern.IsMatch("");
            _ = catalogue.DifficultyPattern.IsMatch("");
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Is this line something the chart pays out?</summary>
    public static bool IsReward(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        if (Current.RewardPattern.IsMatch(line)) return true;
        return !Current.DifficultyPattern.IsMatch(line);
    }

    /// <summary>Keep only the payout lines, in order.</summary>
    public static IReadOnlyList<string> Filter(IEnumerable<string>? lines) =>
        lines?.Where(IsReward).ToList() ?? [];

    /// <summary>
    /// Everything a chart pays out: its headline stats, its global modifier, its
    /// adjacency modifier, and whichever of its affixes are rewards.
    ///
    /// The two special modifiers are labelled because their SCOPE is the whole point --
    /// one applies wherever the chart sits and the other only to its neighbours, and a
    /// flat list would make them look interchangeable.
    /// </summary>
    public static IReadOnlyList<string> Describe(Chart chart)
    {
        var lines = new List<string>();
        lines.AddRange(chart.StatLines());

        if (!string.IsNullOrWhiteSpace(chart.VoyageModifier))
            lines.Add("Voyage-wide: " + chart.VoyageModifier);
        if (!string.IsNullOrWhiteSpace(chart.AdjacentModifier))
            lines.Add("Adjacent: " + chart.AdjacentModifier);

        lines.AddRange(Filter(chart.Modifiers));
        return lines;
    }

    /// <summary>How many difficulty lines were held back, for an honest "and N more".</summary>
    public static int DifficultyCount(Chart chart) => chart.Modifiers.Count(l => !IsReward(l));
}
