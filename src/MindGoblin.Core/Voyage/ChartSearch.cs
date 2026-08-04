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
        var fields = haystack.Split('\n');
        return terms.All(term => TermHits(term, haystack, fields));
    }

    /// <summary>
    /// A term hits on a plain substring, or -- for terms long enough to mean something --
    /// as a SUBSEQUENCE, so a typo or a half-remembered word still finds the chart
    /// ("strngbox", "divne").
    ///
    /// Two guards, both learned by watching it match things that are not there:
    ///
    /// Subsequence matching is gated at four characters because it is extremely loose --
    /// two or three letters in order occur in almost any sentence, so ungated it would
    /// light the whole panel up and the filter would say nothing.
    ///
    /// And it runs per FIELD, never across the joined text, because a subsequence walks
    /// straight through a separator: on a chart named "Kelp" in an area called "Forest",
    /// "kelpforest" matched a word that is on no chart anywhere. A filter that invents
    /// matches is worse than one that misses them. Substring needs no such guard: a term
    /// never contains whitespace, and the fields are joined with it.
    /// </summary>
    private static bool TermHits(string term, string haystack, string[] fields)
    {
        if (haystack.Contains(term, StringComparison.OrdinalIgnoreCase)) return true;
        return term.Length >= 4 && fields.Any(f => IsSubsequence(term, f));
    }

    private static bool IsSubsequence(string term, string haystack)
    {
        var t = 0;
        foreach (var c in haystack)
        {
            if (char.ToLowerInvariant(c) == char.ToLowerInvariant(term[t]) && ++t == term.Length)
                return true;
        }
        return false;
    }
}
