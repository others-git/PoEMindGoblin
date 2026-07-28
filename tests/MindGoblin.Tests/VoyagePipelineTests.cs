using System.Runtime.Versioning;
using MindGoblin.Core.Voyage;

namespace MindGoblin.Tests;

/// <summary>
/// Screenshot to plan, on the real capture. Every other Voyage test uses charts it
/// invented; this one starts from actual game pixels, which is the only way to catch a
/// pipeline that works perfectly on synthetic input and not at all on the thing it is for.
/// </summary>
[SupportedOSPlatform("windows")]
public class VoyagePipelineTests
{
    private static VoyageSession ReadRealPanel()
    {
        using var px = new BitmapPixels(
            Path.Combine(AppContext.BaseDirectory, "fixtures", "voyage-panel.png"));
        var session = new VoyageSession();
        session.ApplyPanelRead(new ChartPanelReader().Read(px));
        return session;
    }

    [Fact]
    public void OneScreenshotIsEnoughToPopulateTheSession()
    {
        var session = ReadRealPanel();
        Assert.Equal(24, session.Charts.Count);
        Assert.All(session.Charts, c => Assert.InRange(c.AreaLevel, 60, 90));
        // Nothing hovered yet, so everything is on the checklist.
        Assert.Equal(24, session.ChartsAwaitingDetail.Count);
    }

    [Fact]
    public void TheRealPanelYieldsALegalNineSquareLayout()
    {
        var session = ReadRealPanel();
        var solution = session.Solve(
            VoyageRules.Defaults().Single(p => p.Name == "high tier"), TimeSpan.FromSeconds(5));

        Assert.Equal(9, solution.Placements.Count);

        var board = new VoyageBoard(3, 3);
        foreach (var p in solution.Placements) board.Place(p);
        Assert.Empty(board.Validate());
    }

    [Fact]
    public void ThePlanNamesASquareAndAPanelNumberForEveryPlacement()
    {
        // "square 5 <- chart 23" is the entire deliverable. If either number is wrong or
        // missing, the plan cannot be followed no matter how good the layout is.
        var session = ReadRealPanel();
        var plan = session.Plan(session.Solve(
            VoyageRules.Defaults().Single(p => p.Name == "high tier"), TimeSpan.FromSeconds(5)));

        Assert.Equal(9, plan.Count);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9], plan.Select(s => s.Square));
        Assert.All(plan, s => Assert.InRange(s.ChartNumber, 1, 60));
        Assert.Equal(9, plan.Select(s => s.ChartNumber).Distinct().Count());
        Assert.All(plan, s => Assert.Contains(s.ChartNumber, session.ByPanelIndex.Keys));
    }

    [Fact]
    public void EachPlannedRotationIsTheOneTheSquareActuallyNeeds()
    {
        var session = ReadRealPanel();
        var solution = session.Solve(
            VoyageRules.Defaults().Single(p => p.Name == "high tier"), TimeSpan.FromSeconds(5));
        var plan = session.Plan(solution);

        // The rotation printed must be the rotation solved for -- an off-by-90 would be
        // invisible in the plan text and wrong on the board.
        foreach (var step in plan)
        {
            var placed = solution.Placements.Single(
                p => VoyagePlan.SquareNumber(p.Cell, 3) == step.Square);
            Assert.Equal(placed.Rotation, step.Rotation);
            Assert.Equal(placed.Chart.Shape, step.Chart.Shape);
        }
    }

    [Fact]
    public void HoverTextFlowsThroughToTheScoreOfARealChart()
    {
        var session = ReadRealPanel();
        var index = session.ByPanelIndex.Keys.Order().First();

        Assert.True(session.ApplyChartText(index,
            "Tempest Reach\nSeafloor Ridges\nDead Man's Sulphur: +14\n"
            + "Adjacent Modifier: Adjacent Areas contain 3 additional Strongboxes"));

        var chart = session.ByPanelIndex[index];
        Assert.Equal(14, chart.Sulphur);
        Assert.Equal("Tempest Reach", chart.Name);
        Assert.DoesNotContain(index, session.ChartsAwaitingDetail);

        // The panel read still owns the shape -- text never overrides measured pixels.
        Assert.Equal(ChartShape.Straight, chart.Shape);

        var sulphur = VoyageRules.Defaults().Single(p => p.Name == "sulphur");
        Assert.Equal(70, sulphur.ScoreChart(chart));       // 14 * 5.0
    }

    [Fact]
    public void ProfileChoiceChangesTheLayoutOfTheRealPanel()
    {
        var session = ReadRealPanel();

        var safe = session.Plan(session.Solve(
            VoyageRules.Defaults().Single(p => p.Name == "high tier"), TimeSpan.FromSeconds(3)));

        // "high tier" weights area level; a flat profile has nothing to prefer, so the two
        // must not agree by construction.
        var levels = safe.ToDictionary(s => s.Square, s => session.ByPanelIndex[s.ChartNumber].AreaLevel);
        Assert.True(levels.Values.Average() >= 80,
            $"the level-weighted profile picked an average level of {levels.Values.Average():0.#}");
    }
}
