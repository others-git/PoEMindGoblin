using System.Text.Json;
using System.Text.RegularExpressions;

namespace MindGoblin.Core.Voyage;

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
        /// <summary>
        /// Every line the three bases can roll, numbers reduced to '#', mapped to
        /// "reward" or "difficulty". This is the primary lookup: an exact match against
        /// the real table beats guessing from wording.
        /// </summary>
        public Dictionary<string, string> Lines { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The subset of <see cref="Lines"/> that comes from the FIGURINES rather than a
        /// chart. They arrive by a different route -- read off the Area Modifiers panel
        /// per square, not copied from an item -- so it is worth knowing which is which.
        /// </summary>
        public Dictionary<string, string> BoardLines { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Every tileset a chart can open, mapped to the chart base it belongs to.
        /// "Anchorfield" -> "Sandy Seabed".
        /// </summary>
        public Dictionary<string, string> Rooms { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Fallback for a line not in the table -- one added by a patch.</summary>
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

        public int Count => Lines.Count + Reward.Count + Difficulty.Count;
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
            // A file beside the exe OVERRIDES the embedded copy: rerunning
            // fetch_voyage_mods.py after a patch must not require a rebuild.
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<Catalogue>(File.ReadAllText(path), Json);
                if (loaded is { Count: > 0 } && Compiles(loaded)) return loaded;
            }

            // The copy embedded at build time -- the normal case for the shipped app,
            // which is one exe with no assets folder beside it.
            if (typeof(ChartRewards).Assembly.GetManifestResourceStream("assets/voyage-mods.json")
                is { } embedded)
            {
                using var reader = new StreamReader(embedded);
                var loaded = JsonSerializer.Deserialize<Catalogue>(reader.ReadToEnd(), Json);
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

    private static readonly Regex Digits = new(@"\d+(?:\.\d+)?", RegexOptions.Compiled);
    private static readonly Regex SignedHash = new(@"[-+]#", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Reduce a modifier to its table form: every number becomes '#', signs go with the
    /// number, whitespace collapses.
    ///
    /// This MUST match tools/fetch_voyage_mods.py exactly or the lookup misses. The sign
    /// rule is the subtle half: poedb renders "+&lt;span&gt;18&lt;/span&gt;%" with the plus outside
    /// the value, while a copied chart says "+18%", so keeping signs would make the two
    /// sides disagree on precisely the modifiers that carry one.
    /// </summary>
    public static string Normalise(string line) =>
        Spaces.Replace(SignedHash.Replace(Digits.Replace(line, "#"), "#"), " ").Trim(' ', '.');

    /// <summary>Is this line something the chart pays out?</summary>
    public static bool IsReward(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;

        // The table first: it is the actual mod list, so a hit here is not a judgement.
        if (Current.Lines.TryGetValue(Normalise(line), out var category))
            return category.Equals("reward", StringComparison.OrdinalIgnoreCase);

        // Only a line the table does not have reaches the patterns.
        if (Current.RewardPattern.IsMatch(line)) return true;
        return !Current.DifficultyPattern.IsMatch(line);
    }

    /// <summary>Was this line found in the mod table, or only guessed at?</summary>
    public static bool IsKnown(string? line) =>
        !string.IsNullOrWhiteSpace(line) && Current.Lines.ContainsKey(Normalise(line));

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
    /// <summary>Marks the boundary between the headline stats and the modifiers in
    /// <see cref="Describe"/>; the UI draws it as a rule rather than printing it.</summary>
    public const string SectionBreak = "\u2500\u2500\u2500";

    public static IReadOnlyList<string> Describe(Chart chart)
    {
        var lines = new List<string>();
        lines.AddRange(chart.StatLines());

        // The stats above are what the tile IS; everything below is what it DOES. A
        // break between the two is only emitted when both sides exist -- a rule over
        // nothing is furniture.
        var stats = lines.Count;

        if (!string.IsNullOrWhiteSpace(chart.VoyageModifier))
            lines.Add("Voyage-wide: " + chart.VoyageModifier);
        if (!string.IsNullOrWhiteSpace(chart.AdjacentModifier))
            lines.Add("Adjacent: " + chart.AdjacentModifier);

        lines.AddRange(Filter(chart.Modifiers));

        if (stats > 0 && lines.Count > stats) lines.Insert(stats, SectionBreak);
        return lines;
    }

    /// <summary>How many difficulty lines were held back, for an honest "and N more".</summary>
    public static int DifficultyCount(Chart chart) => chart.Modifiers.Count(l => !IsReward(l));
}
