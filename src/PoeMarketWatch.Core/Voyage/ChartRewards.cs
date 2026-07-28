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
/// Rewards WIN over difficulty, and anything unrecognised is treated as a reward. That
/// asymmetry is deliberate: the reward vocabulary is open-ended and league-specific, so a
/// deny-list that hides everything it does not know would quietly swallow the next new
/// good modifier. The difficulty vocabulary is the standard, stable map-mod pool, so
/// listing it is tractable. The cost of the choice is an occasional extra line, not a
/// hidden payout.
///
/// Every pattern is taken from real chart text or from poedb's tables for the three
/// Voyage bases (Coral Reef, Coral Forest, Sandy Seabed).
/// </summary>
public static class ChartRewards
{
    /// <summary>
    /// Things a chart pays out. Checked FIRST, so a reward that happens to mention
    /// monsters -- "25% increased number of Rare Monsters", "Rare monsters ... are
    /// imprisoned by Essences" -- is not lost to the difficulty patterns below.
    /// </summary>
    private static readonly Regex Reward = new(string.Join("|",
    [
        // Containers and spawns.
        @"additional Imprisoned Monsters",
        @"additional (?:Diviner's |Arcanist's |Operative's )?Strongboxes",
        @"additional packs of",
        @"additional Messages? in Bottles",
        @"additional cages? of Tormented Spirits",
        @"additional cage of Tormented Spirits",
        @"additional Clusters of Barrels",
        @"additional Giant Starfish",
        @"additional Golden Lanterns",
        @"additional Treasure",
        @"increased number of (?:Rare|Magic) Monsters",

        // Loot conversion and upgrades.
        @"converted to Gold",
        @"chance to be Fractured",
        @"chance to Fracture on death",
        @"chance to instead drop as",
        @"imprisoned by Essences",
        @"to be Possessed",
        @"Pantheon Modifier",
        @"drop an additional",
        @"Flasks found.*Quality",

        // The headline stats, in every wording -- note GGG's "Qauntity" typo, which
        // appears only in the global lines.
        @"increased Q(?:uantity|auntity) of Items",
        @"increased Rarity of Items",
        @"increased Pack Size",
        @"increased Gold found",
        @"increased Dead Man's Sulphur",
        @"increased explicit modifier magnitudes",

        // League and set-piece effects.
        @"Soul Eater",
        @"Friendly Jellyfish",
        @"exotic Fish",
        @"Wildwood Wisps",
        @"Atziri's Influence",
        @"Filthscrabble",
        @"are at least Magic",
        @"Chart to not be consumed",

        // A payout REDUCTION still belongs in the payout list -- it is the loot that
        // changes, and hiding it would flatter the chart.
        @"cannot drop Equipment, Flasks or Tinctures",
    ]), RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Monster and area difficulty. Only consulted when nothing above matched, so these
    /// can be broad.
    /// </summary>
    private static readonly Regex Difficulty = new(string.Join("|",
    [
        @"\bMonsters?\b",
        @"Players have [-+]?\d",
        @"patches of (?:Chilled|Shocked|Burning|Desecrated) Ground",
        @"less effect of Curses",
        @"maximum Resistances",
    ]), RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Is this line something the chart pays out?</summary>
    public static bool IsReward(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        if (Reward.IsMatch(line)) return true;
        return !Difficulty.IsMatch(line);
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
