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
        new(index, (index - 1) / cols, (index - 1) % cols, true, true, true, true);

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
    /// THE HOVER MUST BE COMPUTED FROM THE CELL, NOT THE INDEX.
    ///
    /// Every tab draws over the SAME pixels -- they are tabs, not more grid -- so the
    /// row and column come from the chart's position on the open tab. Taken from the
    /// global index instead, chart 61 divided out to row 10 of a ten-row panel and the
    /// cursor went below the inventory entirely, copying nothing or whatever sat under
    /// it. Field-reported as "the mouse moves beneath the chart inventory".
    /// </summary>
    [Theory]
    [InlineData(1, 1, 0, 0)]        // tab 1, first cell
    [InlineData(1, 60, 9, 5)]       // tab 1, last cell
    [InlineData(2, 61, 0, 0)]       // tab 2, first cell -- the same pixels as chart 1
    [InlineData(2, 120, 9, 5)]      // tab 2, last cell
    public void HoverGeometryComesFromTheCellOnTheOpenTab(
        int page, int globalIndex, int expectedRow, int expectedCol)
    {
        const int cols = 6, rows = 10;
        var local = new PanelPage(page, rows * cols).ToLocal(globalIndex);

        var row = (local - 1) / cols;
        var col = (local - 1) % cols;

        Assert.Equal(expectedRow, row);
        Assert.Equal(expectedCol, col);
        Assert.InRange(row, 0, rows - 1);       // never below the panel
    }

    /// <summary>
    /// A tab nobody has read holds no charts, so "which charts still need detail" says
    /// nothing about it -- the slurp finished tab 1, found no unread charts anywhere,
    /// and reported complete while half the inventory had never been looked at.
    /// </summary>
    [Fact]
    public void ATabNobodyHasReadCountsAsWorkLeft()
    {
        var s = new VoyageSession();
        s.ApplyPanelRead(Cells(60), new PanelPage(1, 60));
        foreach (var i in Enumerable.Range(1, 60))
            s.ApplyChartText(i, $"C{i}\nAnchorfield\nItem Quantity: +10%");

        // Tab 1 is finished, so nothing is "awaiting detail" anywhere...
        Assert.Empty(s.PagesAwaitingDetail(pages: 2, pageSize: 60));

        // ...but tab 2 has never been read, which is the thing to say.
        var work = s.PagesNeedingWork(pages: 2, pageSize: 60);
        var only = Assert.Single(work);
        Assert.Equal(2, only.Page);
        Assert.True(only.Unread, "an unread tab must be reported as needing a READ");
    }

    [Fact]
    public void AReadTabWithUnreadChartsIsNotReportedAsUnread()
    {
        var s = new VoyageSession();
        s.ApplyPanelRead(Cells(60), new PanelPage(1, 60));
        s.ApplyPanelRead(Cells(60), new PanelPage(2, 60));
        foreach (var i in Enumerable.Range(1, 60))
            s.ApplyChartText(i, $"C{i}\nAnchorfield\nItem Quantity: +10%");

        var only = Assert.Single(s.PagesNeedingWork(pages: 2, pageSize: 60));
        Assert.Equal(2, only.Page);
        Assert.False(only.Unread, "it has been read; it just needs slurping");
    }

    /// <summary>
    /// How a chart is NAMED. The plan's job is "go and fetch this one", and on a tabbed
    /// inventory a bare number cannot say where: chart 67 is not the sixty-seventh thing
    /// the user can see, it is the seventh cell of the second tab.
    /// </summary>
    [Theory]
    [InlineData(1, "1\u00b9")]        // cell 1, tab 1
    [InlineData(60, "60\u00b9")]
    [InlineData(61, "1\u00b2")]       // cell 1 again -- the same pixels, tab 2
    [InlineData(67, "7\u00b2")]
    [InlineData(120, "60\u00b2")]
    public void ATabbedPanelNamesChartsByTabAndCell(int globalIndex, string expected) =>
        Assert.Equal(expected, PanelPage.Label(globalIndex, pageSize: 60, pages: 2));

    /// <summary>A single-tab panel stays plain: nobody should read a tab number that
    /// never changes.</summary>
    [Theory]
    [InlineData(1, "1")]
    [InlineData(47, "47")]
    public void AOneTabPanelNamesChartsPlainly(int globalIndex, string expected) =>
        Assert.Equal(expected, PanelPage.Label(globalIndex, pageSize: 60, pages: 1));

    /// <summary>
    /// The solver plans over the WHOLE inventory, not the open tab. Placement is about
    /// which charts are best, and a tab is only where one is kept -- so given a better
    /// set on tab 2 the board should be built entirely from it.
    /// </summary>
    [Fact]
    public void TheSolverPlansAcrossEveryTab()
    {
        var s = new VoyageSession();
        s.ApplyPanelRead(Cells(12), new PanelPage(1, 60));
        s.ApplyPanelRead(Cells(12), new PanelPage(2, 60));
        foreach (var i in Enumerable.Range(1, 12))
            s.ApplyChartText(i, $"Dull {i}\nAnchorfield\nDead Man's Sulphur: +5");
        foreach (var i in Enumerable.Range(61, 12))
            s.ApplyChartText(i, $"Rich {i}\nAnchorfield\nDead Man's Sulphur: +90");

        var plan = s.Plan(s.Solve(
            VoyageRules.Defaults().Single(p => p.Name == "sulphur"), TimeSpan.FromSeconds(3)));

        Assert.Equal(9, plan.Count);
        Assert.All(plan, step => Assert.True(step.ChartNumber > 60,
            $"chart {step.ChartNumber} came from tab 1 when tab 2 was better"));
    }

    /// <summary>
    /// A STALE CALIBRATION FILE MUST NOT OUTVOTE A SHIPPED FACT.
    ///
    /// The tab count was being written to panel-calibration.json, and a file saved by a
    /// build from before the default became 2 pinned it at 1 -- the second tab silently
    /// vanished from an app that had been corrected days earlier, with nothing on screen
    /// to explain it. Origin and pitch are facts about the USER'S SCREEN and belong in
    /// their file; how many tabs the inventory has is a fact about the GAME, the same for
    /// everybody, and moves with the code like the mod tables do.
    /// </summary>
    [Fact]
    public void AnOldCalibrationCannotPinTheTabCount()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cal-{Guid.NewGuid():N}.json");
        try
        {
            // Exactly what the field file contained, "Pages": 1 and all.
            File.WriteAllText(path, """
                {
                  "OriginX": 1755, "OriginY": 420, "Pitch": 66.98,
                  "Rows": 10, "Cols": 6, "Pages": 1, "PageSize": 60,
                  "GlyphOffsetY": -3, "GlyphHalf": 19, "OccupiedThreshold": 150,
                  "EdgeMargin": 3, "EdgeSpan": 0.6, "OpenThreshold": 1,
                  "ReferenceWidth": 2560, "ReferenceHeight": 1440
                }
                """);

            var loaded = ChartPanelReader.Options.Load(path);

            Assert.Equal(2, loaded.Pages);          // the shipped fact wins
            Assert.Equal(1755, loaded.OriginX);     // the user's own measurements do not
            Assert.Equal(19, loaded.GlyphHalf);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>Nor may saving put it back: a round trip must not smuggle a game fact
    /// into the user's file, or the next default change is pinned all over again.</summary>
    [Fact]
    public void SavingTheCalibrationDoesNotWriteTheTabCount()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new ChartPanelReader.Options());
        Assert.DoesNotContain("Pages", json);
        Assert.DoesNotContain("PageSize", json);
        Assert.Contains("OriginX", json);
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
