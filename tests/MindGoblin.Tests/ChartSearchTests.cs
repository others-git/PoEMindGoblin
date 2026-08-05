using MindGoblin.Core.Voyage;

namespace MindGoblin.Tests;

/// <summary>
/// The panel search. Sixty tiles show a number, a glyph and a level; everything else a
/// chart says lives in a tooltip one hover at a time, which is what makes "which one had
/// the Divine line?" an unanswerable question without this.
/// </summary>
public class ChartSearchTests
{
    private static Chart Chart(
        string name = "Underwater Descent",
        string area = "Sandy Seabed Chart",
        ChartShape shape = ChartShape.Corner,
        int level = 83,
        string? voyage = null,
        string? adjacent = null,
        params string[] modifiers) =>
        new("c", name, shape, level, modifiers)
        {
            AreaName = area,
            VoyageModifier = voyage,
            AdjacentModifier = adjacent,
        };

    [Fact]
    public void AnEmptyQueryMatchesEverything()
    {
        // "No filter" is the resting state. A blank box that matched NOTHING would blank
        // the panel every time the search was cleared.
        Assert.True(ChartSearch.Matches("", Chart(), 1));
        Assert.True(ChartSearch.Matches("   ", Chart(), 1));
        Assert.True(ChartSearch.Matches(null, Chart(), 1));
    }

    [Theory]
    [InlineData("underwater")]          // name
    [InlineData("UNDERWATER")]          // case-insensitive
    [InlineData("sandy")]               // area
    [InlineData("corner")]              // shape, which the panel can only DRAW
    [InlineData("83")]                  // level
    [InlineData("7")]                   // panel index
    public void EveryFieldOnAChartIsSearchable(string query) =>
        Assert.True(ChartSearch.Matches(query, Chart(), 7));

    [Fact]
    public void ModifierTextIsSearchable()
    {
        // The reason the feature exists: these lines are only ever visible one hover at
        // a time, so finding the chart that carries one is otherwise a manual sweep.
        var chart = Chart(
            voyage: "Players in all Voyage Areas have Soul Eater",
            adjacent: "Adjacent Areas contain 13 additional packs of Octopi",
            modifiers: ["Monsters inflict 3 Grasping Vines on Hit"]);

        Assert.True(ChartSearch.Matches("octopi", chart, 1));
        Assert.True(ChartSearch.Matches("soul eater", chart, 1));
        Assert.True(ChartSearch.Matches("grasping", chart, 1));
        Assert.False(ChartSearch.Matches("strongbox", chart, 1));
    }

    [Fact]
    public void EveryTermMustHitSoTermsNarrow()
    {
        // A filter whose extra words WIDEN the result cannot cut sixty tiles down, which
        // is the only thing it is for.
        var chart = Chart(adjacent: "Adjacent Areas contain 13 additional packs of Octopi");

        Assert.True(ChartSearch.Matches("octopi 83", chart, 1));
        Assert.False(ChartSearch.Matches("octopi 71", chart, 1));
    }

    [Theory]
    [InlineData("strngbox")]        // dropped letter
    [InlineData("divne")]           // dropped letter
    public void AFourLetterTermStillHitsThroughATypo(string typo)
    {
        var chart = Chart(adjacent: "Adjacent Areas contain 2 additional Diviner's Strongboxes");
        Assert.True(ChartSearch.Matches(typo, chart, 1));
    }

    /// <summary>
    /// THE BUG THE FIRST VERSION SHIPPED WITH, and which the tests above all passed
    /// through. Subsequence matching scatters a term's letters across a whole field,
    /// which over sentence-length modifier text is a wildcard: on a real 60-chart panel
    /// "eater" matched FIFTY-EIGHT charts and "soul" thirty-nine. These are the ordinary
    /// lines that were being hit -- none of them contains the term.
    /// </summary>
    [Theory]
    [InlineData("soul", "Monsters gain 60% of Maximum Life as Extra Maximum Energy Shield")]
    [InlineData("divine", "Rings dropped in adjacent Areas have 10% chance to instead drop as a Unique Ring")]
    [InlineData("eater", "Adjacent Areas contain an additional cage of Tormented Spirits")]
    [InlineData("bottle", "Monsters have a 40% chance to avoid Poison, Impale, and Bleeding")]
    public void ATermDoesNotMatchAChartThatMerelyContainsItsLettersInOrder(
        string term, string modifier)
    {
        var chart = Chart(name: "Offshore Quest", area: "Sandy Seabed Chart",
                          modifiers: [modifier]);
        Assert.False(ChartSearch.Matches(term, chart, 1),
                     $"'{term}' matched a line that does not contain it: {modifier}");
    }

    [Fact]
    public void TheTermStillFindsTheChartThatReallySaysIt()
    {
        // The other half: tightening must not cost the true positives.
        var real = Chart(voyage: "Players in all Voyage Areas have Soul Eater");
        Assert.True(ChartSearch.Matches("soul eater", real, 1));
        Assert.True(ChartSearch.Matches("eater", real, 1));
    }

    /// <summary>
    /// Fuzzy means a MISSPELLED word, not a run of characters buried mid-word. Free at
    /// both ends, "eater" sat one edit from the "water" inside Deepwater, Saltwater and
    /// Underwater -- eighteen charts on a panel where five say Eater.
    /// </summary>
    [Fact]
    public void AFuzzyTermMustMatchFromTheStartOfAWord()
    {
        var watery = Chart(name: "Deepwater Venture", area: "Saltwater Journey");

        Assert.False(ChartSearch.Matches("eater", watery, 1));
        Assert.True(ChartSearch.Matches("deepwater", watery, 1));   // the word itself
        Assert.True(ChartSearch.Matches("deepwatr", watery, 1));    // ...and its typo
    }

    [Fact]
    public void ShortTermsMustAppearLiterally()
    {
        // Subsequence matching is gated at four characters because two or three letters
        // in order occur in almost any sentence: ungated, "ae" would light the whole
        // panel and the filter would say nothing at all.
        var chart = Chart(name: "Kelp Forest", area: "Abyssal Plain", level: 68);

        Assert.False(ChartSearch.MatchesText("ae", "Kelp Forest"));
        Assert.False(ChartSearch.Matches("kf", chart, 1));
        Assert.True(ChartSearch.Matches("kelp", chart, 1));
    }

    [Fact]
    public void TheHaystackDoesNotRunWordsTogetherAcrossFields()
    {
        // Joined without a separator, the end of one field and the start of the next
        // would form words that are on no chart -- and a filter that invents matches is
        // worse than one that misses them.
        var chart = Chart(name: "Kelp", area: "Forest");
        Assert.False(ChartSearch.Matches("kelpforest", chart, 1));
        Assert.True(ChartSearch.Matches("kelp forest", chart, 1));
    }

    [Fact]
    public void AChartWithNoDetailIsStillFindableByWhatThePanelKnows()
    {
        // Before the hover pass a chart has only a shape, a level and its number. Those
        // must still search, or the filter is useless during exactly the pass where
        // finding a specific tile matters most.
        var bare = new Chart("panel-12", "", ChartShape.Crossing, 76, []);

        Assert.True(ChartSearch.Matches("crossing", bare, 12));
        Assert.True(ChartSearch.Matches("76", bare, 12));
        Assert.False(ChartSearch.Matches("sulphur", bare, 12));
    }
}
