using MindGoblin.Core.Voyage;

namespace MindGoblin.Tests;

/// <summary>
/// Two post-solve conveniences: knowing when spending all nine charts would discard a
/// refund the board just paid for, and finding the nine in the in-game stash at all.
/// </summary>
public class PreserveAndSearchTests
{
    private static VoyageSession Session()
    {
        var session = new VoyageSession();
        session.ApplyPanelRead(Enumerable.Range(1, 12).Select(i =>
            new ChartPanelReader.ReadCell(i, (i - 1) / 6, (i - 1) % 6, true, true, true, true)
            { Level = 80 }).ToList());
        return session;
    }

    [Fact]
    public void AChartRefundChanceOnAChartIsNoticed()
    {
        var session = Session();
        session.ApplyChartText(3,
            "Reach\nAnchorfield\nVoyage Modifier: 30% chance for Charts to not be consumed");

        Assert.True(session.PreserveChanceInPlay([3, 5, 7]));
        Assert.False(session.PreserveChanceInPlay([5, 7]));   // the carrier is not placed
    }

    [Fact]
    public void AChartRefundChanceOnTheBorderIsNoticed()
    {
        var session = Session();
        session.ApplySquareModifiers(4, ["20% chance for Charts to not be consumed"]);
        Assert.True(session.PreserveChanceInPlay([1, 2]));
    }

    [Fact]
    public void NoRefundChanceMeansNoQuestion() =>
        Assert.False(Session().PreserveChanceInPlay([1, 2, 3]));

    [Fact]
    public void TheSearchStringNamesEveryNamedChartOnce()
    {
        var session = Session();
        session.ApplyChartText(1, "Armoured Coral Forest\nAnchorfield\nItem Quantity: +10%");
        session.ApplyChartText(2, "Sunken Pelagic Reach\nPelagic Abyss\nItem Quantity: +10%");
        // same most-distinctive word as chart 1 -- deduplicated, not repeated
        session.ApplyChartText(3, "Armoured Kelp Bed\nDiving Shoals\nItem Quantity: +10%");

        Assert.Equal("\"armoured|pelagic\"", session.StashSearch([1, 2, 3]));
    }

    [Fact]
    public void UnnamedChartsProduceNoSearchString() =>
        Assert.Equal("", Session().StashSearch([1, 2]));
}

/// <summary>
/// Figurine tooltips are the authoritative border text (field-confirmed: the Area
/// Modifiers panel never lists a figurine's adjacent-scope lines), and the slurp
/// reads them with OCR over a generous region -- so the filter must keep mods and
/// drop whatever chrome sat behind the tooltip.
/// </summary>
public class FigurineTooltipTests
{
    [Fact]
    public void ChromeIsDroppedAndModifiersSurvive()
    {
        var lines = AreaModifierPanel.TooltipLines(
        [
            "Area Modifiers",
            "50% reduced quantity of items found in adjacent Areas per connection",
            "120% increased Quantity of Items found in adjacent Areas",
            "Hover a square of the Voyage",
            "Board to see the relevant Area",
        ]);
        Assert.Equal(2, lines.Count);
        Assert.Contains(lines, l => l.Contains("per connection"));
        Assert.Contains(lines, l => l.Contains("120% increased Quantity"));
    }

    [Fact]
    public void WrappedTooltipLinesAreStitched()
    {
        var lines = AreaModifierPanel.TooltipLines(
        [
            "Adjacent Areas contain 3 additional",
            "Messages in a Bottle",
        ]);
        Assert.Contains("Adjacent Areas contain 3 additional Messages in a Bottle",
                        Assert.Single(lines));
    }

    /// <summary>Real capture noise, verbatim from a live session: chart captions OCR
    /// as tokens inside good lines, the HUD leaks in, headings arrive mangled.</summary>
    [Fact]
    public void LiveSessionNoiseIsScrubbed()
    {
        var lines = AreaModifierPanel.TooltipLines(
        [
            "0:16",
            "WAYPOI NT",
            "-A Plaw pur vovo*",
            "L..83",
            "'ü,Adjacent Areas contain 12 additional packs of Sea Beasts",
            "50% increased number of Rare Monsters in adjacent Areas",
            "Life",
            "3,918/3,918",
            "Shield",
            "CLEAR BC",
        ]);
        Assert.Equal(2, lines.Count);
        Assert.Equal("Adjacent Areas contain 12 additional packs of Sea Beasts", lines[0]);
        Assert.Equal("50% increased number of Rare Monsters in adjacent Areas", lines[1]);
    }

    [Fact]
    public void ChartLevelCaptionsAreScrubbedInsideLines()
    {
        var lines = AreaModifierPanel.TooltipLines(
            ["50% more Rarity of Items found in adjacent Areas L..83 L:81"]);
        Assert.Equal("50% more Rarity of Items found in adjacent Areas",
                     Assert.Single(lines));
    }

    /// <summary>
    /// Figurines are AUTHORITATIVE for the border. A stale panel read on the same
    /// square must stand aside once the figurine is read -- one old read used to mute
    /// every figurine behind it -- while squares whose figurines are unread keep it.
    /// </summary>
    [Fact]
    public void AReadFigurineOverridesTheStaleSquareRead()
    {
        var session = new VoyageSession();
        session.ApplySquareModifiers(1, ["Area contains 8 additional packs of Crabs"]);
        session.ApplySquareModifiers(3, ["Area contains 2 Altars to Goddess"]);
        session.ApplyFigurineText(1, "120% increased Quantity of Items found in adjacent Areas");

        // Figurine 1 touches square 1: its text wins there. Square 3's figurines are
        // unread, so its panel read stands.
        var square1 = session.EffectiveSquareModifiers(1);
        Assert.Contains("120% increased Quantity of Items found in adjacent Areas", square1);
        Assert.DoesNotContain("Area contains 8 additional packs of Crabs", square1);
        Assert.Contains("Area contains 2 Altars to Goddess", session.EffectiveSquareModifiers(3));

        Assert.True(session.SquareBoardKnown(1));
        Assert.True(session.SquareBoardKnown(3));
        Assert.False(session.SquareBoardKnown(2));
        Assert.DoesNotContain(2, session.SquaresWithoutFigurines);
    }

    [Fact]
    public void AFigurineReadCountsTheSquareAsRead()
    {
        var session = new VoyageSession();
        session.ApplyFigurineText(1, "120% increased Quantity of Items found in adjacent Areas");
        Assert.DoesNotContain(1, session.SquaresAwaitingModifiers);
        Assert.Contains(2, session.SquaresAwaitingModifiers);
    }

    /// <summary>A figurine carrying two mods must bind as TWO board modifiers -- the
    /// channel classifiers anchor at line start, and squares already bind per line.</summary>
    [Fact]
    public void AMultiModFigurineBindsPerLine()
    {
        var session = new VoyageSession();
        session.ApplyFigurineText(1,
            "50% reduced quantity of items found in adjacent Areas per connection\n"
            + "120% increased Quantity of Items found in adjacent Areas");

        var bound = session.BoardModifiers();
        Assert.Equal(2, bound.Count);
        Assert.All(bound, m => Assert.Single(m.AffectedCells));
    }
}
