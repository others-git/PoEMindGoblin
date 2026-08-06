using MindGoblin.Core.Voyage;

namespace MindGoblin.Tests;

/// <summary>
/// The star is how a person overrules the score in a chart's favour. Soul Eater is the
/// canonical case: voyage-wide player power, priced at zero by every profile, so the
/// planner drops the chart -- right arithmetic, wrong answer. Required is a value bonus
/// rather than a solver constraint: the search still chooses WHERE the chart goes, only
/// whether it goes stops being negotiable -- which quietly subsumes the old "cheapest
/// square" pin, since the layout that loses least is the optimum.
/// </summary>
public class RequiredChartTests
{
    /// <summary>Twelve Crossings: eleven worth taking, chart 4 worth nothing.</summary>
    private static VoyageSession Session()
    {
        var session = new VoyageSession();
        session.ApplyPanelRead(Enumerable.Range(1, 12).Select(i =>
            new ChartPanelReader.ReadCell(i, (i - 1) / 6, (i - 1) % 6, true, true, true, true))
                .ToList());
        foreach (var i in Enumerable.Range(1, 12).Where(i => i != 4))
            session.ApplyChartText(i, "Reach\nAnchorfield\nDead Man's Sulphur: +50");
        session.ApplyChartText(4,
            "Reach\nAnchorfield\nVoyage Modifier: Players in all Voyage Areas have Soul Eater");
        return session;
    }

    private static VoyageProfile Sulphur =>
        VoyageRules.Defaults().Single(p => p.Name == "sulphur");

    private static void Require(VoyageSession session, int index)
    {
        session.CycleMark(index);
        Assert.Equal(ChartMark.Required, session.CycleMark(index));
    }

    [Fact]
    public void AWorthlessChartIsDroppedUntilRequired()
    {
        var session = Session();
        var without = session.Plan(session.Solve(Sulphur, TimeSpan.FromSeconds(3)));
        Assert.DoesNotContain(without, s => s.ChartNumber == 4);

        Require(session, 4);
        var with_ = session.Plan(session.Solve(Sulphur, TimeSpan.FromSeconds(3)));
        Assert.Equal(9, with_.Count);
        Assert.Contains(with_, s => s.ChartNumber == 4);
    }

    [Fact]
    public void SeveralChartsCanBeRequiredAtOnce()
    {
        var session = Session();
        Require(session, 4);
        Require(session, 7);
        Require(session, 11);

        var plan = session.Plan(session.Solve(Sulphur, TimeSpan.FromSeconds(3)));
        Assert.Equal(9, plan.Count);
        foreach (var index in new[] { 4, 7, 11 })
            Assert.Contains(plan, s => s.ChartNumber == index);
    }

    /// <summary>The bonus buys inclusion, not points: the reported score must be what
    /// the board actually pays, not ten million per star.</summary>
    [Fact]
    public void TheReportedScoreDoesNotIncludeTheBonus()
    {
        var session = Session();
        var plain = session.Solve(Sulphur, TimeSpan.FromSeconds(3));

        // Requiring a chart the plan already took changes nothing -- score included.
        var taken = session.Plan(plain)[0].ChartNumber;
        Require(session, taken);
        var required = session.Solve(Sulphur, TimeSpan.FromSeconds(3));

        Assert.Contains(session.Plan(required), s => s.ChartNumber == taken);
        Assert.Equal(plain.Value, required.Value, 3);
    }

    /// <summary>
    /// A board capped below nine placements must still spend one of the few on the
    /// star rather than quietly dropping it. Corners rather than Crossings: a partial
    /// board has to form a CLOSED cluster, and four Corners tile a 2x2 block while
    /// four Crossings tile nothing.
    /// </summary>
    [Fact]
    public void ACappedBoardStillSpendsAPlacementOnTheStar()
    {
        var session = new VoyageSession();
        session.ApplyPanelRead(Enumerable.Range(1, 12).Select(i =>
            new ChartPanelReader.ReadCell(i, (i - 1) / 6, (i - 1) % 6, true, true, false, false))
                .ToList());
        foreach (var i in Enumerable.Range(1, 12).Where(i => i != 4))
            session.ApplyChartText(i, "Reach\nAnchorfield\nDead Man's Sulphur: +50");
        session.ApplyChartText(4,
            "Reach\nAnchorfield\nVoyage Modifier: Players in all Voyage Areas have Soul Eater");
        Require(session, 4);

        var capped = Sulphur;
        capped.MaxCharts = 4;
        var plan = session.Plan(session.Solve(capped, TimeSpan.FromSeconds(3)));

        Assert.Equal(4, plan.Count);
        Assert.Contains(plan, s => s.ChartNumber == 4);
    }
}
