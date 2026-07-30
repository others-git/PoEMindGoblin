using MindGoblin.Core.Voyage;

namespace MindGoblin.Tests;

/// <summary>
/// The right-click cycle: none -> excluded -> required -> none. The rules say what a
/// chart is WORTH; the marks are the user overruling them in either direction -- an X
/// for a chart being saved or sold, a star for a chart no profile can price.
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

    private static VoyageProfile Sulphur =>
        VoyageRules.Defaults().Single(p => p.Name == "sulphur");

    [Fact]
    public void AnExcludedChartIsNeverPlanned()
    {
        var session = Session();
        // The best chart in the panel, by a mile -- and vetoed.
        session.ApplyChartText(5, "Reach\nAnchorfield\nDead Man's Sulphur: +90");
        Assert.Equal(ChartMark.Excluded, session.CycleMark(5));

        var plan = session.Plan(session.Solve(Sulphur, TimeSpan.FromSeconds(3)));
        Assert.Equal(9, plan.Count);
        Assert.DoesNotContain(plan, s => s.ChartNumber == 5);
    }

    /// <summary>One cycle on one gesture because it is ONE question -- what may the
    /// planner do with this chart -- and the three answers are mutually exclusive.
    /// Excluded-and-required must stay unrepresentable.</summary>
    [Fact]
    public void TheCycleRunsExcludedRequiredNone()
    {
        var session = Session();

        Assert.Equal(ChartMark.Excluded, session.CycleMark(5));
        Assert.True(session.IsExcluded(5));
        Assert.False(session.IsRequired(5));

        Assert.Equal(ChartMark.Required, session.CycleMark(5));
        Assert.False(session.IsExcluded(5));
        Assert.True(session.IsRequired(5));

        Assert.Equal(ChartMark.None, session.CycleMark(5));
        Assert.Empty(session.Excluded);
        Assert.Empty(session.Required);
    }

    [Fact]
    public void BothMarksSurviveARestart()
    {
        var session = Session();
        session.CycleMark(3);                       // excluded
        session.CycleMark(8); session.CycleMark(8); // required

        var restored = VoyageSession.FromState(session.ToState());
        Assert.True(restored.IsExcluded(3));
        Assert.True(restored.IsRequired(8));
        Assert.False(restored.IsExcluded(5));
    }

    /// <summary>A hand-edited session file can claim a chart is both. The X wins:
    /// resurrecting a chart the user vetoed costs a voyage; dropping a star costs
    /// a re-click.</summary>
    [Fact]
    public void ACorruptFileCannotMakeAChartBothMarks()
    {
        var state = Session().ToState();
        state.Excluded = [4];
        state.Required = [4];

        var restored = VoyageSession.FromState(state);
        Assert.True(restored.IsExcluded(4));
        Assert.False(restored.IsRequired(4));
    }

    [Fact]
    public void SpendingAVoyageDoesNotLeaveStaleMarks()
    {
        var session = Session();
        session.CycleMark(2);                       // excluded
        session.CycleMark(6); session.CycleMark(6); // required
        // Neither can be spent while marked in practice, but a stale mark on a
        // vanished chart must not linger either way.
        session.CompleteVoyage([2, 6]);
        Assert.Empty(session.Excluded);
        Assert.Empty(session.Required);
    }
}
