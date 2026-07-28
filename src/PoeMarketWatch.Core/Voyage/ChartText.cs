using System.Globalization;
using System.Text.RegularExpressions;

namespace PoeMarketWatch.Core.Voyage;

/// <summary>
/// Turns a chart's hover text into a <see cref="Chart"/>.
///
/// The panel screenshot gives shape, rotation and area level; everything the RULES score
/// -- quantity, pack size, sulphur, and the two special modifier lines -- lives only in
/// the hover tooltip. That text is not template-readable the way "L:83" is, so it comes
/// in as TEXT: the user hovers a chart in game and presses Ctrl+C, and the app reads the
/// clipboard. Nothing is sent to the client; the copy is the user's own keypress.
///
/// Parsing is deliberately LABEL-DRIVEN and order-independent: it scans every line for a
/// recognised label rather than expecting a fixed layout. Tooltip framing (section rules,
/// "(augmented)" suffixes, item-class headers) varies between item types and across
/// patches, and a positional parser breaks the first time GGG adds a line. Anything it
/// does not recognise is kept verbatim in <see cref="Chart.Modifiers"/>, so a rule can
/// still match text this parser has no field for.
/// </summary>
public static class ChartText
{
    /// <summary>Labelled numeric lines, e.g. "Item Quantity: +42% (augmented)".</summary>
    private static readonly (string Label, Action<Values, double> Set)[] Numbers =
    [
        ("Item Quantity", (v, d) => v.ItemQuantity = d),
        ("Item Rarity", (v, d) => v.ItemRarity = d),
        ("Monster Pack Size", (v, d) => v.MonsterPackSize = d),
        ("Gold Found", (v, d) => v.GoldFound = d),
        ("Dead Man's Sulphur", (v, d) => v.Sulphur = d),
        ("Area Level", (v, d) => v.AreaLevel = (int)d),
    ];

    private sealed class Values
    {
        public int AreaLevel;
        public int RequiresLevel;
        public double ItemQuantity, ItemRarity, MonsterPackSize, GoldFound, Sulphur;
    }

    private static readonly Regex Number =
        new(@"[-+]?\d+(?:\.\d+)?", RegexOptions.Compiled);

    private static readonly Regex RequiresLevel =
        new(@"^\s*Requires\s+Level\s+(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ShapeLine =
        new(@"^\s*(?:Chart\s+)?Shape\s*:\s*(\w+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Parse hover text. Returns null when the text holds nothing chart-like at all, so a
    /// stray clipboard copy is ignored rather than becoming an empty chart.
    /// </summary>
    /// <param name="id">Stable identity, normally the panel index the text was read for.</param>
    /// <param name="shapeHint">Shape from the panel reader, which the text rarely states.</param>
    public static Chart? Parse(string? text, string? id = null, ChartShape? shapeHint = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
                        .Select(l => l.Trim())
                        .Where(l => l.Length > 0 && !IsSeparator(l))
                        .ToList();
        if (lines.Count == 0) return null;

        var v = new Values();
        string? voyageMod = null, adjacentMod = null, areaName = null;
        ChartShape? shape = shapeHint;
        var name = "";
        var other = new List<string>();
        var recognised = 0;

        foreach (var line in lines)
        {
            if (TryLabelledNumber(line, v)) { recognised++; continue; }

            if (RequiresLevel.Match(line) is { Success: true } rl)
            {
                v.RequiresLevel = int.Parse(rl.Groups[1].Value, CultureInfo.InvariantCulture);
                recognised++;
                continue;
            }

            if (ShapeLine.Match(line) is { Success: true } sl
                && Enum.TryParse<ChartShape>(sl.Groups[1].Value, ignoreCase: true, out var parsed))
            {
                shape = parsed;
                recognised++;
                continue;
            }

            if (TryPrefixed(line, "Voyage Modifier", ref voyageMod)) { recognised++; continue; }
            if (TryPrefixed(line, "Adjacent Modifier", ref adjacentMod)) { recognised++; continue; }

            // PoE's copy format opens with these; they identify the text but say nothing
            // the planner needs.
            if (StripPrefix(line, "Item Class") is not null) { recognised++; continue; }
            if (StripPrefix(line, "Rarity") is not null) { recognised++; continue; }

            if (StripPrefix(line, "Area") is { } area) { areaName = area; recognised++; continue; }

            // The first unlabelled line is the chart's name; the second is its base type,
            // which doubles as the area name when no explicit "Area:" line appeared.
            if (name.Length == 0) name = line;
            else if (areaName is null && other.Count == 0) areaName = line;
            else other.Add(line);
        }

        if (recognised == 0 && other.Count == 0 && name.Length == 0) return null;

        return new Chart(
            id ?? name,
            name.Length > 0 ? name : "unnamed chart",
            shape ?? ChartShape.Crossing,
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
    /// Every line a rule could match, including the Adjacent Modifier. For SCORING use
    /// <see cref="Chart.OwnLines"/> instead -- adjacency is paid through neighbours, and
    /// this list would count it twice.
    /// </summary>
    public static IEnumerable<string> ScorableLines(Chart chart)
    {
        foreach (var line in chart.OwnLines()) yield return line;
        if (!string.IsNullOrEmpty(chart.AdjacentModifier)) yield return chart.AdjacentModifier!;
    }

    /// <summary>Rows of dashes separate tooltip sections and carry nothing.</summary>
    private static bool IsSeparator(string line) => line.All(c => c is '-' or '=' or '—');

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

    private static bool TryPrefixed(string line, string label, ref string? target)
    {
        if (StripPrefix(line, label) is not { } rest) return false;
        // The tooltip sometimes puts the text on the line below the label.
        target = rest.Length > 0 ? rest : target;
        return true;
    }

    /// <summary>
    /// The value after "Label:", or null when the line is not that label.
    ///
    /// The colon is REQUIRED. Without it "Area contains many Totems" parses as the area
    /// name and the modifier line disappears, and "Area Levelling Grounds" parses as an
    /// area level -- both silently, both wrong. Every label in the tooltip has a colon;
    /// "Requires Level 80" is the one that does not and it has its own pattern.
    /// </summary>
    private static string? StripPrefix(string line, string label)
    {
        if (!line.StartsWith(label, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = line[label.Length..].TrimStart();
        if (!rest.StartsWith(':')) return null;
        return rest[1..].Trim();
    }
}
