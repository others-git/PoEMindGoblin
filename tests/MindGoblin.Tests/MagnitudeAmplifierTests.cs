using MindGoblin.Core.Voyage;

namespace MindGoblin.Tests;

/// <summary>
/// "#% increased explicit modifier magnitudes" — the fourth channel, and the odd one out.
///
/// It pays nothing of its own: it multiplies the EXPLICIT modifiers of whatever chart
/// receives it, so a 60% roll beside a chart rolling +200% quantity is worth 60% of that,
/// and beside a blank chart it is worth nothing. It was scored flat, which made it both
/// counted and inert — measured: moving the figurine from square 1 to square 8 changed
/// neither the board value nor a single placement.
/// </summary>
public class MagnitudeAmplifierTests
{
    private const string Amplifier = "Adjacent Areas have 60% increased explicit modifier magnitudes";

    private static VoyageProfile Currency =>
        VoyageRules.Defaults().Single(p => p.Name == "currency");

    /// <summary>It is its own channel, and must not be mistaken for any of the other
    /// three — every classifier the scoring path consults has to agree.</summary>
    [Fact]
    public void TheAmplifierIsItsOwnChannel()
    {
        Assert.True(VoyageProfile.IsMagnitudeAmplifier(Amplifier));
        Assert.Equal(VoyageProfile.PayoutChannel.None, VoyageProfile.PayoutChannelOf(Amplifier));
        Assert.False(VoyageProfile.IsContainerGift(Amplifier));
        Assert.Equal(0, VoyageProfile.MonsterDensityOf(Amplifier));
        Assert.Equal(0, VoyageProfile.PackDensityOf(Amplifier));
        Assert.Equal(0, VoyageProfile.QuantityDensityOf(Amplifier));
    }

    [Theory]
    [InlineData("Rare Monsters in adjacent Areas drop 2 additional Divine Orbs")]
    [InlineData("Adjacent Areas contain 3 additional Messages in a Bottle")]
    [InlineData("32% increased Pack Size")]
    public void NothingElseIsAnAmplifier(string line) =>
        Assert.False(VoyageProfile.IsMagnitudeAmplifier(line));

    /// <summary>
    /// The catalog prices it as a FRACTION — 0.01 per percent — because a share is the
    /// only honest unit for a mod that pays a share. A chaos figure here would be a
    /// claim about a payout it does not have.
    /// </summary>
    [Fact]
    public void ItIsPricedAsAShareNotAValue() =>
        Assert.Equal(0.6, Currency.ScoreText([Amplifier]), 6);

    /// <summary>Explicit means explicit: the rolled affixes and the stats that aggregate
    /// them, never the chart's single IMPLICIT — which is what the game's own word for
    /// this mod excludes.</summary>
    [Fact]
    public void ExplicitValueExcludesTheImplicit()
    {
        var chart = ChartText.Parse(
            "Tempest Reach\nAnchorfield\nItem Quantity: +100%\n"
            + "Voyage Modifier: 8% increased Quantity of Items found in all Voyage Areas",
            "c")!;

        // The stat counts; the implicit does not, though both are quantity.
        Assert.Equal(Currency.ScoreText(["Item Quantity: +100%"]), Currency.ExplicitValue(chart), 6);
        Assert.NotEqual(0, Currency.ExplicitValue(chart));
    }

    /// <summary>
    /// THE POINT. A magnitude square must pull the fattest chart onto itself. Before the
    /// channel existed the buff was a per-square constant, so on a full board — every
    /// square occupied either way — it decided nothing at all.
    /// </summary>
    [Theory]
    [InlineData(1)]    // figurine 1 buffs square 1
    [InlineData(8)]    // figurine 8 buffs square 8
    public void AMagnitudeSquareTakesTheFattestChart(int figurine)
    {
        var session = new VoyageSession();
        session.ApplyPanelRead(Enumerable.Range(1, 9).Select(i =>
            new ChartPanelReader.ReadCell(i, (i - 1) / 6, (i - 1) % 6, true, true, true, true))
                .ToList());
        session.ApplyFigurineText(figurine, Amplifier);

        const int fat = 1;
        session.ApplyChartText(fat, "Fat\nAnchorfield\nItem Quantity: +200%");
        for (var i = 2; i <= 9; i++)
            session.ApplyChartText(i, $"Thin{i}\nAnchorfield\nItem Quantity: +5%");

        var plan = session.Plan(session.Solve(Currency, TimeSpan.FromSeconds(3)));
        Assert.Equal(figurine, Assert.Single(plan, s => s.ChartNumber == fat).Square);
    }

    /// <summary>
    /// And it is worth what it amplifies: the same board is worth strictly more with the
    /// buff on the fat chart's square than with the fat chart elsewhere. A flat model
    /// scored both identically — that equality was the bug.
    /// </summary>
    [Fact]
    public void TheBoardIsWorthMoreWhenTheAmplifierLandsOnValue()
    {
        double Solve(bool fatChartExists)
        {
            var session = new VoyageSession();
            session.ApplyPanelRead(Enumerable.Range(1, 9).Select(i =>
                new ChartPanelReader.ReadCell(i, (i - 1) / 6, (i - 1) % 6, true, true, true, true))
                    .ToList());
            session.ApplyFigurineText(1, Amplifier);
            session.ApplyChartText(1, fatChartExists
                ? "Fat\nAnchorfield\nItem Quantity: +200%"
                : "Blank\nAnchorfield\nItem Quantity: +0%");
            for (var i = 2; i <= 9; i++)
                session.ApplyChartText(i, $"Thin{i}\nAnchorfield\nItem Quantity: +0%");
            return session.Solve(Currency, TimeSpan.FromSeconds(3)).Value;
        }

        // Beside a board of blanks the amplifier pays nothing at all -- the defining
        // property, and the one a flat score could never express.
        var blank = Solve(false);
        var withFat = Solve(true);
        var fatAlone = Currency.ScoreText(["Item Quantity: +200%"]);

        Assert.Equal(0, blank, 6);
        Assert.True(withFat > fatAlone,
            $"the amplifier added nothing: {withFat} vs the chart's own {fatAlone}");
    }

    /// <summary>
    /// The chart-carried wording rides the same channel. It used to become a flat
    /// AdjacentValue: reach counted, so it drifted to the centre, but it was blind to
    /// WHICH neighbours it amplified.
    /// </summary>
    [Fact]
    public void AChartCarriedAmplifierSeeksValuableNeighbours()
    {
        var session = new VoyageSession();
        session.ApplyPanelRead(Enumerable.Range(1, 9).Select(i =>
            new ChartPanelReader.ReadCell(i, (i - 1) / 6, (i - 1) % 6, true, true, true, true))
                .ToList());

        // Square 5 is the centre of a 3x3: four neighbours, and no figurine reaches it.
        session.ApplyChartText(1, $"Amp\nAnchorfield\n{Amplifier}");
        session.ApplyChartText(2, "Fat\nAnchorfield\nItem Quantity: +200%");
        for (var i = 3; i <= 9; i++)
            session.ApplyChartText(i, $"Thin{i}\nAnchorfield\nItem Quantity: +0%");

        var plan = session.Plan(session.Solve(Currency, TimeSpan.FromSeconds(3)));
        var ampSquare = Assert.Single(plan, s => s.ChartNumber == 1).Square;
        var fatSquare = Assert.Single(plan, s => s.ChartNumber == 2).Square;

        // Adjacent, not merely both on the board: the whole value of the pairing is that
        // they touch.
        var session2 = new VoyageSession();
        var adjacent = Math.Abs(session2.CellOf(ampSquare).Row - session2.CellOf(fatSquare).Row)
                       + Math.Abs(session2.CellOf(ampSquare).Col - session2.CellOf(fatSquare).Col);
        Assert.Equal(1, adjacent);
    }

    /// <summary>
    /// The synergy knob reaches the amplifier, and reaches it EXACTLY ONCE.
    ///
    /// It is the switch for "model tile interactions or do not", so at 0 an amplifier —
    /// which is nothing but a tile interaction — must be worth nothing. The solver was
    /// applying it zero times on the board side while Route applied it once, so the plan
    /// and the route it printed disagreed at any synergy but the shipped 1.0.
    /// </summary>
    [Fact]
    public void SynergyScalesTheAmplifierOnceEverywhere()
    {
        VoyageSolver.Solution Solve(double synergy)
        {
            var profile = VoyageRules.Defaults().Single(p => p.Name == "currency");
            profile.MonsterPayoutSynergy = synergy;

            var session = new VoyageSession();
            session.ApplyPanelRead(Enumerable.Range(1, 9).Select(i =>
                new ChartPanelReader.ReadCell(i, (i - 1) / 6, (i - 1) % 6, true, true, true, true))
                    .ToList());
            session.ApplyFigurineText(1, Amplifier);
            session.ApplyChartText(1, "Fat\nAnchorfield\nItem Quantity: +200%");
            for (var i = 2; i <= 9; i++)
                session.ApplyChartText(i, $"Thin{i}\nAnchorfield\nItem Quantity: +0%");
            return session.Solve(profile, TimeSpan.FromSeconds(3));
        }

        var off = Solve(0).Value;
        var on = Solve(1).Value;

        // At zero the amplifier is the only thing that could have paid, so the board is
        // worth just the fat chart.
        Assert.Equal(Currency.ScoreText(["Item Quantity: +200%"]), off, 6);
        Assert.True(on > off, $"synergy did not reach the amplifier: {on} vs {off}");
    }

    /// <summary>
    /// Dump must still treat an amplifier as a KEEPER. Its catalog value is a share of
    /// the chart it lands beside, and dump's charts are junk by construction, so the
    /// share of them is a rounding error — the same reason dump prices global multipliers
    /// with flat negatives rather than per-percent ones.
    /// </summary>
    [Fact]
    public void DumpStillRefusesToBurnAnAmplifier() =>
        Assert.True(VoyageRules.Defaults().Single(p => p.Name == "dump")
                        .ScoreText([Amplifier]) < 0);
}
