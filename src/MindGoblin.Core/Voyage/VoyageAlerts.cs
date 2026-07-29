using System.Text.RegularExpressions;

namespace MindGoblin.Core.Voyage;

/// <summary>Why a modifier is worth stopping for.</summary>
public enum AlertKind
{
    /// <summary>Rare and lucrative. Build the voyage around it.</summary>
    Jackpot,

    /// <summary>Quietly costly, and easy to miss because it reads like a reward.</summary>
    Trap,
}

/// <summary>
/// A modifier the planner should say out loud.
///
/// The rule profiles score the board as a whole and hand back one number, which is the
/// right answer to "which layout" and the wrong one to "is anything unusual in play". A
/// Divine Orb line and a Chromatic Orb line are the same SHAPE of modifier and score
/// alike within a rounding error, but one is worth a hundred times the other; the sum
/// cannot tell you that, and neither can a board that just quietly places it.
///
/// So this is deliberately NOT scoring. It is a short list of modifiers that change what
/// you would do, checked by name.
/// </summary>
public sealed record VoyageAlert(AlertKind Kind, string Headline, string Detail, string Where)
{
    /// <summary>Panel index of the chart it came from, or null when it is a board square.</summary>
    public int? ChartIndex { get; init; }

    /// <summary>Square number when it came from the board, else null.</summary>
    public int? Square { get; init; }
}

public static class VoyageAlerts
{
    /// <summary>
    /// The wording that identifies Soul Eater. Its own constant because the UI offers a
    /// button for it, so two places have to agree on what counts.
    /// </summary>
    public const string SoulEaterPattern = @"have Soul Eater";

    private sealed record Rule(AlertKind Kind, string Pattern, string Headline, string Detail);

    /// <summary>
    /// What gets called out, and why.
    ///
    /// Kept SHORT on purpose. A banner that fires on nine modifiers is a second modifier
    /// list, and the reason this one works is that it is usually empty. Everything here
    /// either changes which charts you would take or costs you a voyage.
    ///
    /// Every pattern is checked against the generated corpus by a test, so a wording that
    /// GGG changes -- or that was mistyped here -- fails the build instead of silently
    /// never firing. That test is what caught GGG's own word-order slip in the Divine Orb
    /// line, which reads "Rare Monsters adjacent in Areas" and not "in adjacent Areas"
    /// like every one of its eleven siblings.
    /// </summary>
    private static readonly Rule[] Rules =
    [
        new(AlertKind.Jackpot, @"Divine Orbs?", "Divine Orbs",
            "The biggest per-rare payout on the board. Pair it with anything adding rare "
            + "monsters, and run that square early."),

        new(AlertKind.Jackpot, SoulEaterPattern, "Soul Eater",
            "Voyage-wide, so it pays the same from any square. Take it for the implicit "
            + "alone \u2014 it does not need a good one."),

        new(AlertKind.Jackpot, @"Atziri's Influence", "Atziri's Influence",
            "A named modifier rather than a rolled one \u2014 the only one of its kind in "
            + "the tables."),

        new(AlertKind.Jackpot, @"imprisoned by Essences", "Essences",
            "Every natural rare in the voyage becomes an Essence monster. It scales with "
            + "the areas you actually reach, so route for coverage."),

        new(AlertKind.Jackpot, @"chance to be Fractured", "Fractured items",
            "Fractured bases are craft stock, not vendor fodder \u2014 worth a look even "
            + "when the profile scores it at nothing."),

        new(AlertKind.Trap, @"cannot drop Equipment, Flasks or Tinctures", "No equipment drops",
            "The game's own tables file this as a reward, and it deletes most of the loot. "
            + "Only take it when farming currency or gold."),

        new(AlertKind.Trap, @"reduced quantity of items found in adjacent Areas per connection",
            "Quantity lost per connection",
            "This gets worse the better the board is joined \u2014 the one thing the planner "
            + "is trying to maximise. Give it few connections."),

        new(AlertKind.Trap, @"Players have -?#% to all maximum Resistances",
            "Lowered maximum resistances",
            "A death ends the voyage and forfeits every square you had not reached. This is "
            + "the modifier most likely to cause one."),
    ];

    /// <summary>
    /// Match against the game's wording AND the corpus template in one pattern.
    ///
    /// The tables normalise every number to '#' while the game writes the rolled value,
    /// sometimes with its range attached ("9(8-10)"). Rather than keep two patterns per
    /// rule in step, '#' is compiled to "a number in any of those forms", so one rule
    /// covers the template it was written against and the text the player actually copied.
    /// </summary>
    private static Regex Compile(string pattern) =>
        new(pattern.Replace("#", @"\d+(?:\([\d.\-]+\))?"),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex[] Compiled = [.. Rules.Select(r => Compile(r.Pattern))];

    /// <summary>Everything notable in a session, jackpots first.</summary>
    public static IReadOnlyList<VoyageAlert> Scan(VoyageSession session)
    {
        var found = new List<VoyageAlert>();

        foreach (var (index, chart) in session.ByPanelIndex.OrderBy(kv => kv.Key))
            foreach (var line in ChartLines(chart))
                Add(found, line, $"chart {index}", chartIndex: index);

        foreach (var (square, lines) in session.SquareModifiers.OrderBy(kv => kv.Key))
            foreach (var line in lines)
                Add(found, line, $"square {square}", square: square);

        // Jackpots first, then in the order they were found. A trap is worth knowing and a
        // jackpot is worth acting on, and the one you act on belongs at the top.
        return [.. found.OrderBy(a => a.Kind == AlertKind.Trap ? 1 : 0)];
    }

    private static IEnumerable<string> ChartLines(Chart chart)
    {
        if (!string.IsNullOrWhiteSpace(chart.VoyageModifier)) yield return chart.VoyageModifier!;
        if (!string.IsNullOrWhiteSpace(chart.AdjacentModifier)) yield return chart.AdjacentModifier!;
        foreach (var line in chart.Modifiers) yield return line;
    }

    private static void Add(
        List<VoyageAlert> found, string line, string where, int? chartIndex = null, int? square = null)
    {
        for (var i = 0; i < Rules.Length; i++)
        {
            if (!Compiled[i].IsMatch(line)) continue;

            // One alert per headline per source. A chart's implicit is also listed among
            // its modifiers in some captures, and saying "Divine Orbs" twice about the
            // same chart reads as two of them.
            if (found.Any(a => a.Headline == Rules[i].Headline
                               && a.ChartIndex == chartIndex && a.Square == square)) continue;

            found.Add(new VoyageAlert(Rules[i].Kind, Rules[i].Headline, Rules[i].Detail, where)
            {
                ChartIndex = chartIndex,
                Square = square,
            });
        }
    }

    /// <summary>
    /// The chart carrying Soul Eater, if the panel has one.
    ///
    /// Soul Eater is voyage-wide, so it pays the same from any square -- and no rule
    /// profile scores it, because what it is worth is player power rather than loot. Left
    /// alone the planner therefore never places it. That is what the button is for, and
    /// this is how the button knows whether to appear.
    /// </summary>
    public static int? SoulEaterChart(VoyageSession session)
    {
        var pattern = Compile(SoulEaterPattern);
        foreach (var (index, chart) in session.ByPanelIndex.OrderBy(kv => kv.Key))
            if (ChartLines(chart).Any(pattern.IsMatch)) return index;
        return null;
    }

    /// <summary>Every rule pattern, so a test can hold them against the generated corpus.</summary>
    public static IReadOnlyList<string> Patterns => [.. Rules.Select(r => r.Pattern)];
}
