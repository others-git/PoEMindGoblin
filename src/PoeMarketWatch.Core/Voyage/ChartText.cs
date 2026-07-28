using System.Globalization;
using System.Text.RegularExpressions;

namespace PoeMarketWatch.Core.Voyage;

/// <summary>
/// Turns a chart's copied item text into a <see cref="Chart"/>.
///
/// Written against REAL captures, which look nothing like a tidy tooltip:
///
///     Item Class: Voyage Charts
///     Rarity: Rare
///     Deepwater Descent
///     Seafloor Ridges
///     --------
///     Item Quantity: +76% (augmented)
///     Dead Man's Sulphur: +30% (augmented)
///     --------
///     Requirements:
///     Level: 54
///     --------
///     Item Level: 71
///     --------
///     { Implicit Modifier }
///     Adjacent Areas contain 9(8-10) additional packs of Crabs
///     --------
///     { Prefix Modifier "Unwavering" (Tier: 1) - Life }
///     17(10-20)% more Monster Life
///     Monsters cannot be Stunned
///     --------
///     Take this item to Valerie aboard the Sovereign to Chart this area.
///
/// Four things about that shaped this parser, each of which broke the previous one:
///
///   * There are NO "Voyage Modifier:" or "Adjacent Modifier:" labels. The special
///     modifier is the IMPLICIT, and what kind it is has to be read from its wording:
///     "in all Voyage Areas" is global, "adjacent Areas" affects neighbours. Waiting for
///     a label meant neither was ever detected and every adjacency effect was silently
///     scored as the chart's own value.
///   * Numbers carry their roll range inline -- "9(8-10) additional packs". A rule
///     matching "(\d+) additional packs" does not match that, because after the digits
///     comes a bracket rather than a space.
///   * Affix headers "{ Prefix Modifier ... }", parenthetical explanations, requirements
///     and the vendor instruction are all chrome, and all of it was being stored and
///     scored as modifier text.
///   * The headline stats are AGGREGATES of the affix lines. A chart showing
///     "Dead Man's Sulphur: +90%" carries three separate "30% increased Dead Man's
///     Sulphur found in this Area" lines, so scoring both counts it twice.
/// </summary>
public static class ChartText
{
    private sealed class Values
    {
        public int AreaLevel;
        public int RequiresLevel;
        public double ItemQuantity, ItemRarity, MonsterPackSize, GoldFound, Sulphur;
    }

    /// <summary>Labelled numeric lines, e.g. "Item Quantity: +76% (augmented)".</summary>
    private static readonly (string Label, Action<Values, double> Set)[] Numbers =
    [
        ("Item Quantity", (v, d) => v.ItemQuantity = d),
        ("Item Rarity", (v, d) => v.ItemRarity = d),
        ("Monster Pack Size", (v, d) => v.MonsterPackSize = d),
        ("Gold Found", (v, d) => v.GoldFound = d),
        ("Dead Man's Sulphur", (v, d) => v.Sulphur = d),
        ("Area Level", (v, d) => v.AreaLevel = (int)d),
        // The chart's item level IS the level of the area it opens.
        ("Item Level", (v, d) => v.AreaLevel = (int)d),
    ];

    private static readonly Regex Number = new(@"[-+]?\d+(?:\.\d+)?", RegexOptions.Compiled);

    private static readonly Regex RequiresLevel =
        new(@"^\s*(?:Requires\s+)?Level[:\s]+(\d+)\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>An affix header: <c>{ Prefix Modifier "Savage" (Tier: 3) - Damage }</c>.</summary>
    private static readonly Regex AffixHeader = new(@"^\s*\{.*\}\s*$", RegexOptions.Compiled);

    /// <summary>The implicit is the special modifier; its header marks what follows.</summary>
    private static readonly Regex ImplicitHeader =
        new(@"^\s*\{\s*Implicit", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>A whole line in brackets is a reminder, e.g. "(Poison deals Chaos...)".</summary>
    private static readonly Regex Parenthetical = new(@"^\s*\(.*\)\s*$", RegexOptions.Compiled);

    /// <summary>
    /// A rolled value with its range attached: "9(8-10)", "+18(10-19)%", "-8(-9--6)%".
    /// Reduced to the rolled number so one rule can read every chart.
    /// </summary>
    private static readonly Regex RolledRange =
        new(@"(?<=\d)\((?:[-+]?\d+(?:\.\d+)?)(?:\s*[-–—]+\s*[-+]?\d+(?:\.\d+)?)?\)",
            RegexOptions.Compiled);

    /// <summary>Global: applies wherever the chart is placed.</summary>
    private static readonly Regex GlobalScope =
        new(@"\ball\s+Voyage\s+Areas\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Adjacency: applies to the squares around wherever the chart is placed.</summary>
    private static readonly Regex AdjacentScope =
        new(@"\badjacent\s+Areas\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Per-affix lines already totalled into the headline stats.
    ///
    /// A chart showing "Dead Man's Sulphur: +90%" carries three "30% increased Dead Man's
    /// Sulphur found in this Area" lines -- the stat is their sum, so scoring both counts
    /// the same sulphur twice and inflates the chart threefold.
    /// </summary>
    private static readonly Regex CountedInStats =
        new(@"increased\s+(?:Dead Man's Sulphur|Quantity of Items|Rarity of Items|Pack Size|Gold)"
            + @"[\w\s']*\bin this Area\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Lines that are neither stat nor modifier.</summary>
    private static readonly Regex Chrome =
        new(@"^\s*(?:Requirements:|Item Class[:\s]|Rarity[:\s]|Take this item to)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Parse copied chart text. Returns null when it holds nothing chart-like, so a
    /// stray clipboard copy is ignored rather than becoming an empty chart.
    /// </summary>
    /// <param name="id">Stable identity, normally the panel index it was read for.</param>
    /// <param name="shapeHint">Shape from the panel reader; the text never states it.</param>
    public static Chart? Parse(string? text, string? id = null, ChartShape? shapeHint = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var v = new Values();
        string? voyageMod = null, adjacentMod = null, areaName = null;
        var name = "";
        var other = new List<string>();
        var recognised = 0;
        var nextIsImplicit = false;
        var seenImplicit = false;

        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || IsSeparator(line)) continue;

            if (AffixHeader.IsMatch(line))
            {
                // Only the FIRST implicit block is the special modifier; later affix
                // headers close it.
                nextIsImplicit = ImplicitHeader.IsMatch(line) && !seenImplicit;
                recognised++;
                continue;
            }

            if (Parenthetical.IsMatch(line) || Chrome.IsMatch(line)) { recognised++; continue; }

            if (TryLabelledNumber(line, v)) { recognised++; continue; }

            if (RequiresLevel.Match(line) is { Success: true } rl)
            {
                v.RequiresLevel = int.Parse(rl.Groups[1].Value, CultureInfo.InvariantCulture);
                recognised++;
                continue;
            }

            if (TryShape(line) is { } parsedShape) { shapeHint = parsedShape; recognised++; continue; }

            // Explicit labels are still honoured -- a hand-typed entry may use them.
            if (StripPrefix(line, "Voyage Modifier") is { Length: > 0 } vm)
            { voyageMod ??= vm; recognised++; continue; }
            if (StripPrefix(line, "Adjacent Modifier") is { Length: > 0 } am)
            { adjacentMod ??= am; recognised++; continue; }
            if (StripPrefix(line, "Area") is { Length: > 0 } area && !line.StartsWith("Area Level"))
            { areaName = area; recognised++; continue; }

            var mod = RolledRange.Replace(line, "");

            // The special modifier is identified by WORDING, not by a label -- the copied
            // text has none. Scope is the whole difference between a chart's own value
            // and what it gives its neighbours.
            if (GlobalScope.IsMatch(mod))
            {
                if (voyageMod is null) { voyageMod = mod; recognised++; if (nextIsImplicit) seenImplicit = true; continue; }
            }
            else if (AdjacentScope.IsMatch(mod))
            {
                if (adjacentMod is null) { adjacentMod = mod; recognised++; if (nextIsImplicit) seenImplicit = true; continue; }
            }

            if (nextIsImplicit)
            {
                // An implicit that mentions neither scope still travels with the chart
                // and buffs its neighbours -- "Atziri's Influence", "Monsters have a
                // chance to be Empowered by 2000 Wildwood Wisps".
                seenImplicit = true;
                adjacentMod ??= mod;
                recognised++;
                continue;
            }

            if (CountedInStats.IsMatch(mod)) { recognised++; continue; }

            if (name.Length == 0) name = mod;
            else if (areaName is null && other.Count == 0) areaName = mod;
            else other.Add(mod);
        }

        if (recognised == 0 && other.Count == 0 && name.Length == 0) return null;

        return new Chart(
            id ?? name,
            name,
            shapeHint ?? ChartShape.Crossing,
            v.AreaLevel,
            other)
        {
            AreaName = areaName ?? "",
            VoyageModifier = voyageMod,
            AdjacentModifier = adjacentMod,
            RequiresLevel = v.RequiresLevel,
            ItemQuantity = v.ItemQuantity,
            ItemRarity = v.ItemRarity,
            MonsterPackSize = v.MonsterPackSize,
            GoldFound = v.GoldFound,
            Sulphur = v.Sulphur,
        };
    }

    /// <summary>
    /// Every line a rule could match, adjacency included. For SCORING use
    /// <see cref="Chart.OwnLines"/> -- adjacency is paid through neighbours and this
    /// list would count it twice.
    /// </summary>
    public static IEnumerable<string> ScorableLines(Chart chart)
    {
        foreach (var line in chart.OwnLines()) yield return line;
        if (!string.IsNullOrEmpty(chart.AdjacentModifier)) yield return chart.AdjacentModifier!;
    }

    /// <summary>
    /// Re-derive scope and clean up a chart whose modifier list was parsed by an older,
    /// wronger version of this file.
    ///
    /// Sessions on disk hold the RESULT of parsing, not the text, so a fix to the parser
    /// does not reach charts already read. Rather than discard a session -- which would
    /// mean copying every chart again -- the stored lines are put back through the same
    /// classification: scope by wording, chrome dropped, rolled ranges reduced, and the
    /// affix lines already summed into the headline stats removed.
    ///
    /// Only applied when no scope was detected at all, so it cannot undo a good parse.
    /// </summary>
    public static Chart Refine(Chart chart)
    {
        if (!string.IsNullOrEmpty(chart.VoyageModifier)
            || !string.IsNullOrEmpty(chart.AdjacentModifier)) return chart;
        if (chart.Modifiers.Count == 0) return chart;

        string? voyage = null, adjacent = null;
        var kept = new List<string>();

        foreach (var raw in chart.Modifiers)
        {
            var line = raw.Trim();
            if (line.Length == 0 || IsSeparator(line)) continue;
            if (AffixHeader.IsMatch(line) || Parenthetical.IsMatch(line)) continue;
            if (Chrome.IsMatch(line) || RequiresLevel.IsMatch(line)) continue;
            if (StripPrefix(line, "Item Level") is not null) continue;
            if (StripPrefix(line, "Level") is not null) continue;

            var mod = RolledRange.Replace(line, "");
            if (GlobalScope.IsMatch(mod)) { voyage ??= mod; continue; }
            if (AdjacentScope.IsMatch(mod)) { adjacent ??= mod; continue; }
            if (CountedInStats.IsMatch(mod)) continue;

            kept.Add(mod);
        }

        return chart with
        {
            Modifiers = kept,
            VoyageModifier = voyage,
            AdjacentModifier = adjacent,
        };
    }

    private static bool IsSeparator(string line) => line.All(c => c is '-' or '=' or '—' or '–');

    private static ChartShape? TryShape(string line)
    {
        var rest = StripPrefix(line, "Shape") ?? StripPrefix(line, "Chart Shape");
        return rest is not null && Enum.TryParse<ChartShape>(rest, true, out var shape)
            ? shape
            : null;
    }

    private static bool TryLabelledNumber(string line, Values v)
    {
        foreach (var (label, set) in Numbers)
        {
            if (StripPrefix(line, label) is not { } rest) continue;
            if (Number.Match(rest) is not { Success: true } m) return true;   // label, no value
            set(v, double.Parse(m.Value, CultureInfo.InvariantCulture));
            return true;
        }
        return false;
    }

    /// <summary>
    /// The value after "Label:", or null when the line is not that label.
    ///
    /// The colon is REQUIRED. Without it "Area contains many Totems" parses as the area
    /// name and the modifier line disappears, and "Area Levelling Grounds" parses as an
    /// area level -- both silently, both wrong.
    /// </summary>
    private static string? StripPrefix(string line, string label)
    {
        if (!line.StartsWith(label, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = line[label.Length..].TrimStart();
        if (!rest.StartsWith(':')) return null;
        return rest[1..].Trim();
    }
}
