using MindGoblin.Core.Voyage;

namespace MindGoblin.Tests;

/// <summary>
/// The X is the user's veto: the rules say what a chart is worth, the X says "not this
/// one anyway" -- saved for a friend, a distrusted read, a chart being sold.
/// </summary>
public class ExclusionTests
{
    private static VoyageSession Session()
    {
        var session = new VoyageSession();
        session.ApplyPanelRead(Enumerable.Range(1, 12).Select(i =>
            new ChartPanelReader.ReadCell(i, (i - 1) / 6, (i - 1) % 6, true, true, true, true)
            { Level = 80 }).ToList());
        return session;
    }

    private static VoyageProfile Quantity =>
        VoyageRules.Defaults().Single(p => p.Name == "quantity");

    [Fact]
    public void AnExcludedChartIsNeverPlanned()
    {
        var session = Session();
        // The best chart in the panel, by a mile -- and vetoed.
        session.ApplyChartText(5, "Reach\nAnchorfield\nItem Quantity: +110%");
        Assert.True(session.ToggleExcluded(5));

        var plan = session.Plan(session.Solve(Quantity, TimeSpan.FromSeconds(3)));
        Assert.Equal(9, plan.Count);
        Assert.DoesNotContain(plan, s => s.ChartNumber == 5);
    }

    [Fact]
    public void TheSecondToggleLiftsTheVeto()
    {
        var session = Session();
        session.ToggleExcluded(5);
        Assert.False(session.ToggleExcluded(5));
        Assert.Empty(session.Excluded);
    }

    [Fact]
    public void TheVetoSurvivesARestart()
    {
        var session = Session();
        session.ToggleExcluded(3);
        session.ToggleExcluded(8);

        var restored = VoyageSession.FromState(session.ToState());
        Assert.True(restored.IsExcluded(3));
        Assert.True(restored.IsExcluded(8));
        Assert.False(restored.IsExcluded(5));
    }

    [Fact]
    public void SpendingAVoyageDoesNotLeaveStaleVetoes()
    {
        var session = Session();
        session.ToggleExcluded(2);
        // Chart 2 cannot be spent while excluded in practice, but a stale X on a
        // vanished chart must not linger either way.
        session.CompleteVoyage([2]);
        Assert.Empty(session.Excluded);
    }
}
