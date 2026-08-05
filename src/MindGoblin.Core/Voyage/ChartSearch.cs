namespace MindGoblin.Core.Voyage;

/// <summary>
/// Find a chart on the panel by anything written on it.
///
/// Sixty tiles showing a number, a glyph and a level cannot answer "which one had the
/// Divine figurine line?" -- that text exists only in the tooltip, one hover at a time.
/// The search reads the whole chart, so the question is asked once instead of sixty times.
///
/// Lives in Core, not the view: what counts as a match is a rule worth pinning, and the
/// view can only be checked by looking at it.
/// </summary>
public static class ChartSearch
{
    /// <summary>
    /// Everything about a chart a search may hit: its panel number, name, area, shape,
    /// level, both special modifier lines and every rolled line.
    ///
    /// The SHAPE is spelled out rather than drawn, because the panel draws it as a glyph
    /// and a glyph cannot be typed -- "crossing" is the only way to ask for one.
    /// </summary>
    public static string Haystack(Chart chart, int panelIndex)
    {
        var parts = new List<string>
        {
            panelIndex.ToString(),
            chart.Name,
            chart.AreaName,
            chart.Shape.ToString(),
        };
        if (chart.AreaLevel > 0) parts.Add(chart.AreaLevel.ToString());
        if (!string.IsNullOrWhiteSpace(chart.VoyageModifier)) parts.Add(chart.VoyageModifier!);
        if (!string.IsNullOrWhiteSpace(chart.AdjacentModifier)) parts.Add(chart.AdjacentModifier!);
        parts.AddRange(chart.Modifiers);
        return string.Join(" \n", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    /// <summary>
    /// Does this chart match the query?
    ///
    /// Every whitespace-separated term must hit, so terms NARROW rather than widen --
    /// "sulphur 83" means both, which is how a filter is expected to behave and the only
    /// way to cut sixty tiles down to a handful.
    ///
    /// An empty or whitespace query matches everything: "no filter" has to be the
    /// resting state, not "nothing found".
    /// </summary>
    public static bool Matches(string? query, Chart chart, int panelIndex) =>
        MatchesText(query, Haystack(chart, panelIndex));

    public static bool MatchesText(string? query, string haystack)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return terms.All(term => TermHits(term, haystack));
    }

    /// <summary>
    /// A term hits on a plain substring, or on a WORD it is nearly spelled like.
    ///
    /// SUBSEQUENCE MATCHING WAS THE WRONG TOOL AND IT SHIPPED. Scattering a term's
    /// letters across a whole field looks like fuzziness and is really a wildcard: over
    /// sentence-length modifier text almost anything is a subsequence of almost
    /// anything. Measured on a real 60-chart panel, "eater" matched FIFTY-EIGHT charts,
    /// "soul" thirty-nine, and "divine" hit two charts carrying no such word -- it had
    /// walked "dropped in ... have ... instead". A four-character gate did not save it,
    /// because the haystack is long, not because the term was short.
    ///
    /// What fuzzy has to mean instead is MISSPELLED, not scattered: the term must be
    /// within a small edit distance of some run of characters inside a single word. So
    /// "strngbox" still finds Strongboxes and "divne" still finds Diviner's, while
    /// "eater" finds only the charts that say Eater.
    ///
    /// The budget scales with the term because one wrong letter in four is a different
    /// word, and one in ten is a typo.
    /// </summary>
    private static bool TermHits(string term, string haystack)
    {
        if (haystack.Contains(term, StringComparison.OrdinalIgnoreCase)) return true;

        var budget = term.Length >= 8 ? 2 : term.Length >= 5 ? 1 : 0;
        if (budget == 0) return false;      // short terms must appear literally

        foreach (var word in haystack.Split(WordBreaks, StringSplitOptions.RemoveEmptyEntries))
            if (word.Length + budget >= term.Length && NearlyContains(term, word, budget))
                return true;
        return false;
    }

    private static readonly char[] WordBreaks =
        [' ', '\n', '\r', '\t', ',', '.', ';', ':', '(', ')', '\'', '"', '/', '-'];

    /// <summary>
    /// Is <paramref name="term"/> within <paramref name="budget"/> edits of a PREFIX of
    /// <paramref name="word"/>? Levenshtein anchored at the start and free at the end.
    ///
    /// Anchoring the start is what stops the second wave of noise. Free at both ends,
    /// "eater" was one edit from the "water" buried inside Deepwater, Saltwater and
    /// Underwater, so it lit eighteen charts on a panel where five say Eater. A typo is
    /// at the start of a word you meant; a match in the MIDDLE of a longer word is
    /// usually a different word.
    ///
    /// Free at the end still stands, and is what lets "strngbox" reach Strongboxes
    /// without paying for the trailing "es".
    /// </summary>
    private static bool NearlyContains(string term, string word, int budget)
    {
        var previous = new int[word.Length + 1];
        for (var j = 0; j <= word.Length; j++) previous[j] = j;   // anchored: skipping costs
        var current = new int[word.Length + 1];

        for (var i = 1; i <= term.Length; i++)
        {
            current[0] = i;                             // consumed i term chars, matched none
            var best = current[0];
            for (var j = 1; j <= word.Length; j++)
            {
                var cost = char.ToLowerInvariant(term[i - 1]) == char.ToLowerInvariant(word[j - 1])
                    ? 0 : 1;
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1),
                                      previous[j - 1] + cost);
                best = Math.Min(best, current[j]);
            }
            if (best > budget) return false;            // no alignment can recover
            (previous, current) = (current, previous);
        }

        for (var j = 0; j <= word.Length; j++)
            if (previous[j] <= budget) return true;     // end anywhere
        return false;
    }
}
