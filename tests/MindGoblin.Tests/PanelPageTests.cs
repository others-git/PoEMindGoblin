using MindGoblin.Core.Voyage;

namespace MindGoblin.Tests;

/// <summary>
/// The chart inventory is TABBED, and a screenshot only ever shows the open tab. That
/// makes a panel index meaningless on its own -- "chart 7" is a different chart on tab 1
/// and tab 2 -- so indices run straight through: tab 1 owns 1..Size, tab 2 the next Size.
/// </summary>
public class PanelPageTests
{
    private static ChartPanelReader.ReadCell Cell(int index, int cols = 6) =>
        new(index, (index - 1) / cols, (index - 1) % cols, true, true, true, true) { Level = 80 };

    private static IReadOnlyList<ChartPanelReader.ReadCell> Cells(int count) =>
        Enumerable.Range(1, count).Select(i => Cell(i)).ToList();

    [Fact]
    public void TabsOwnConsecutiveBlocksOfIndices()
    {
        var one = new PanelPage(1, 60);
        var two = new PanelPage(2, 60);

        Assert.Equal(1, one.First);
        Assert.Equal(60, one.Last);
        Assert.Equal(61, two.First);
        Assert.Equal(120, two.Last);

        // A cell the reader called 7 is chart 7 on tab 1 and chart 67 on tab 2.
        Assert.Equal(7, one.ToGlobal(7));
        Assert.Equal(67, two.ToGlobal(7));
        Assert.Equal(7, two.ToLocal(67));

        Assert.True(one.Contains(60));
        Assert.False(one.Contains(61));
        Assert.True(two.Contains(61));
    }

    /// <summary>A single-tab panel behaves exactly as it did before tabs existed, which
    /// is what keeps every stored session and every caller valid.</summary>
    [Fact]
    public void AllIsTheUntabbedBehaviour()
    {
        Assert.Equal(9, PanelPage.All.ToGlobal(9));
        Assert.Equal(9, PanelPage.All.ToLocal(9));
        Assert.True(PanelPage.All.Contains(1));
        Assert.True(PanelPage.All.Contains(9999));
    }

    /// <summary>
    /// THE BUG TABS WOULD HAVE CAUSED. A read is evidence about the tab that was open
    /// and says nothing about any other, so reconciling the whole session against it
    /// would strike every chart on every other tab and then delete them -- opening the
    /// planner on tab 1 would quietly consume tab 2.
    /// </summary>
    [Fact]
    public void ReadingOneTabNeverConsumesAnother()
    {
        var s = new VoyageSession();
        var one = new PanelPage(1, 60);
        var two = new PanelPage(2, 60);

        s.ApplyPanelRead(Cells(60), one);
        s.ApplyPanelRead(Cells(60), two);
        Assert.Equal(120, s.Charts.Count);

        s.ApplyChartText(67, "Kelp Forest\nAnchorfield\nItem Quantity: +42%");

        // Re-read tab 1 as many times as it takes to drop a chart, twice over.
        for (var i = 0; i < 4; i++) s.ApplyPanelRead(Cells(60), one);

        Assert.Equal(120, s.Charts.Count);
        Assert.Equal("Kelp Forest", s.ByPanelIndex[67].Name);
    }

    /// <summary>Scoping must not cost the reconciliation it was built on: a chart that
    /// really is gone from the OPEN tab still goes, on the same two strikes.</summary>
    [Fact]
    public void AChartGoneFromTheOpenTabIsStillDropped()
    {
        var s = new VoyageSession();
        var two = new PanelPage(2, 60);
        s.ApplyPanelRead(Cells(60), two);
        Assert.Equal(60, s.Charts.Count);

        s.ApplyPanelRead(Cells(59), two);
        Assert.Equal(60, s.Charts.Count);      // one strike, still there
        s.ApplyPanelRead(Cells(59), two);
        Assert.Equal(59, s.Charts.Count);      // struck twice, gone
        Assert.False(s.ByPanelIndex.ContainsKey(120));
    }

    [Fact]
    public void ChartsOnPageReportsOnlyThatTab()
    {
        var s = new VoyageSession();
        s.ApplyPanelRead(Cells(60), new PanelPage(1, 60));
        s.ApplyPanelRead(Cells(3), new PanelPage(2, 60));

        Assert.Equal(60, s.ChartsOnPage(new PanelPage(1, 60)).Count);
        Assert.Equal([61, 62, 63], s.ChartsOnPage(new PanelPage(2, 60)));
    }

    /// <summary>
    /// What the slurp needs to hand off between tabs: it can only hover what is on
    /// screen, so when the open tab is done it has to name the next one that still has
    /// work rather than going quiet with half the inventory unread.
    /// </summary>
    [Fact]
    public void PagesAwaitingDetailNamesTheTabsWithWorkLeft()
    {
        var s = new VoyageSession();
        s.ApplyPanelRead(Cells(60), new PanelPage(1, 60));
        s.ApplyPanelRead(Cells(60), new PanelPage(2, 60));

        // Finish tab 1 entirely; tab 2 is untouched.
        foreach (var i in Enumerable.Range(1, 60))
            s.ApplyChartText(i, $"C{i}\nAnchorfield\nItem Quantity: +10%");

        Assert.Equal([2], s.PagesAwaitingDetail(pages: 2, pageSize: 60));

        // A single-tab panel has no handoff to describe.
        Assert.Empty(s.PagesAwaitingDetail(pages: 1, pageSize: 60));
    }

    /// <summary>
    /// The default matches the GAME, which has two tabs. It shipped defaulted to one as
    /// a "prepare for later" flag, so the app disagreed with the screen out of the box
    /// and asked the user to go and fix it -- the same mistake as assuming a resolution
    /// instead of detecting one.
    /// </summary>
    [Fact]
    public void ThePanelDefaultsToTheTabsTheGameHas()
    {
        var options = new ChartPanelReader.Options();
        Assert.Equal(2, options.Pages);
        Assert.Equal(60, options.PageSize);
        Assert.Equal(1, options.Page(1).First);
        Assert.Equal(60, options.Page(1).Last);
        Assert.Equal(61, options.Page(2).First);
        Assert.Equal(120, options.Page(2).Last);
    }
}
