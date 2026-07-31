using System.Text.RegularExpressions;

namespace MindGoblin.Core.Voyage;

/// <summary>Why a modifier is worth stopping for.</summary>
public enum AlertKind
{
    /// <summary>The two build-the-whole-voyage modifiers -- the Divine figurine and
    /// Messages in Bottles. Sorted above everything and displayed louder.</summary>
    Grail,

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
public sealed record VoyageAlert(AlertKind Kind, string Headline, string Detail)
{
    /// <summary>Panel indices of every chart carrying it, ascending.</summary>
    public IReadOnlyList<int> Charts { get; init; } = [];

    /// <summary>Board squares carrying it, ascending.</summary>
    public IReadOnlyList<int> Squares { get; init; } = [];

    /// <summary>First chart, for callers that act on one. Null when it is board-only.</summary>
    public int? ChartIndex => Charts.Count == 0 ? null : Charts[0];

    /// <summary>First square, or null when it came from charts.</summary>
    public int? Square => Squares.Count == 0 ? null : Squares[0];

    /// <summary>
    /// Where it came from, as one phrase.
    ///
    /// One row per SOURCE was the obvious build and the wrong one: a difficulty modifier
    /// like lowered maximum resistances sits on a quarter of the panel, so the banner
    /// filled with nine identical paragraphs and buried the two that were rare. Grouping
    /// turns that into the more useful statement anyway -- not "this exists" nine times
    /// over, but exactly which charts carry it.
    /// </summary>
    public string Where
    {
        get
        {
            var parts = new List<string>();
            if (Charts.Count == 1) parts.Add($"chart {Charts[0]}");
            else if (Charts.Count > 1) parts.Add($"charts {string.Join(", ", Charts)}");
            if (Squares.Count == 1) parts.Add($"square {Squares[0]}");
            else if (Squares.Count > 1) parts.Add($"squares {string.Join(", ", Squares)}");
            return string.Join(" · ", parts);
        }
    }
}

public static class VoyageAlerts
{
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
        new(AlertKind.Grail, @"Divine Orbs?", "Divine Orbs",
            "The biggest per-rare payout on the board. Pair it with anything adding rare "
            + "monsters, and run that square early."),

        new(AlertKind.Jackpot, @"have Soul Eater", "Soul Eater",
            "Voyage-wide, so it pays the same from any square \u2014 and priced "
            + "at zero, because player power is not loot. Right-click its chart twice to "
            + "require it."),

        new(AlertKind.Grail, @"additional Messages? in (?:a )?Bottles?", "Messages in a Bottle",
            "The most profitable chase: ~39c each, sellable UNOPENED. A hidden-until-"
            + "charted voyage mod (ilvl 68+, max +2) — it cannot be crafted for, only "
            + "revealed. Seat it centrally beside the highest-quantity tiles."),

        new(AlertKind.Jackpot, @"drop Dead Man's Sulphur", "Sulphur off rares",
            "Every rare drops sulphur -- the currency that pays for board rerolls and "
            + "Allflame crafts. Pair it with anything adding rares; under a sulphur "
            + "profile this square is the whole plan."),

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

        new(AlertKind.Jackpot, @"Players have -?#% to all maximum Resistances",
            "Free difficulty roll",
            "3.29.1: this modifier never functioned and no longer generates. A chart still "
            + "carrying it keeps the juiced upsides and pays NO downside — run it."),
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

    /// <summary>
    /// Everything notable in a session: one row per MODIFIER, naming every source.
    ///
    /// Jackpots first. A trap is worth knowing and a jackpot is worth acting on, and the
    /// one you act on belongs at the top.
    /// </summary>
    public static IReadOnlyList<VoyageAlert> Scan(VoyageSession session)
    {
        // Keyed by rule, so the same modifier found on nine charts is one row that names
        // nine charts. HashSets because a chart lists its implicit among its modifiers in
        // some captures, and one modifier read twice is still one modifier.
        var charts = new Dictionary<int, SortedSet<int>>();
        var squares = new Dictionary<int, SortedSet<int>>();

        foreach (var (index, chart) in session.ByPanelIndex.OrderBy(kv => kv.Key))
            foreach (var rule in ChartLines(chart).SelectMany(Matching))
                (charts.TryGetValue(rule, out var s) ? s : charts[rule] = []).Add(index);

        foreach (var (square, lines) in session.SquareModifiers.OrderBy(kv => kv.Key))
            foreach (var rule in lines.SelectMany(Matching))
                (squares.TryGetValue(rule, out var s) ? s : squares[rule] = []).Add(square);

        return [.. Enumerable.Range(0, Rules.Length)
            .Where(i => charts.ContainsKey(i) || squares.ContainsKey(i))
            .OrderBy(i => (int)Rules[i].Kind)
            .ThenBy(i => i)
            .Select(i => new VoyageAlert(Rules[i].Kind, Rules[i].Headline, Rules[i].Detail)
            {
                Charts = [.. charts.GetValueOrDefault(i) ?? []],
                Squares = [.. squares.GetValueOrDefault(i) ?? []],
            })];
    }

    /// <summary>Indices of every rule this line trips.</summary>
    private static IEnumerable<int> Matching(string line)
    {
        for (var i = 0; i < Rules.Length; i++)
            if (Compiled[i].IsMatch(line)) yield return i;
    }

    private static IEnumerable<string> ChartLines(Chart chart)
    {
        if (!string.IsNullOrWhiteSpace(chart.VoyageModifier)) yield return chart.VoyageModifier!;
        if (!string.IsNullOrWhiteSpace(chart.AdjacentModifier)) yield return chart.AdjacentModifier!;
        foreach (var line in chart.Modifiers) yield return line;
    }

    /// <summary>Every rule pattern, so a test can hold them against the generated corpus.</summary>
    public static IReadOnlyList<string> Patterns => [.. Rules.Select(r => r.Pattern)];
}
