using MindGoblin.Core.Voyage;

namespace MindGoblin.Tests;

/// <summary>
/// The banner exists because a summed score cannot say "this one is different". A Divine
/// Orb line and a Chromatic Orb line have the same shape and score within a rounding
/// error of each other, and one of them is worth a hundred times the other.
/// </summary>
public class VoyageAlertsTests
{
    private static VoyageSession SessionWith(params (int Index, string Text)[] charts)
    {
        var session = new VoyageSession();
        session.ApplyPanelRead(Enumerable.Range(1, 12).Select(i =>
            new ChartPanelReader.ReadCell(i, (i - 1) / 6, (i - 1) % 6, true, true, true, true)
            { Level = 80 }).ToList());
        foreach (var (index, text) in charts) session.ApplyChartText(index, text);
        return session;
    }

    /// <summary>
    /// The whole rule table, against the game's own vocabulary.
    ///
    /// A pattern that matches nothing the game can roll is worse than no rule at all: it
    /// looks like coverage and behaves like silence. This is the project's standing
    /// convention for rules, and it applies with more force here, because an alert that
    /// never fires is invisible rather than merely wrong -- and GGG's wording is not
    /// something to trust from memory. The Divine Orb line reads "Rare Monsters adjacent
    /// in Areas", with the words the wrong way round, alone among its eleven siblings.
    /// </summary>
    [Fact]
    public void EveryAlertPatternMatchesSomethingTheGameCanRoll()
    {
        var corpus = ChartRewards.Current.Lines.Keys
            .Concat(ChartRewards.Current.BoardLines.Keys)
            .Select(line => line.Replace("#", "12"))
            .ToList();

        var dead = VoyageAlerts.Patterns
            .Where(p => !corpus.Any(line => System.Text.RegularExpressions.Regex.IsMatch(
                line, p.Replace("#", @"\d+"),
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
            .ToList();

        Assert.True(dead.Count == 0, $"alert patterns matching nothing: {string.Join(", ", dead)}");
    }

    [Fact]
    public void DivineOrbsRaiseAJackpot()
    {
        var session = SessionWith((3,
            "Salt Barrens\nAbyssal Plain\nItem Quantity: +20%\n"
            + "Rare Monsters adjacent in Areas drop 2 additional Divine Orbs"));

        var alert = Assert.Single(VoyageAlerts.Scan(session));
        Assert.Equal(AlertKind.Jackpot, alert.Kind);
        Assert.Equal("Divine Orbs", alert.Headline);
        Assert.Equal(3, alert.ChartIndex);
    }

    /// <summary>
    /// The Area Modifiers panel resolves a template to the hovered square, so the same
    /// modifier arrives as "Rare Monsters in Area drop an additional Divine Orb" -- the
    /// wrong preposition, no digits, and singular. Anchoring on the number would miss it.
    /// </summary>
    [Fact]
    public void TheResolvedSingularWordingAlsoRaisesIt()
    {
        var session = SessionWith();
        session.ApplySquareModifiers(4, ["Rare Monsters in Area drop an additional Divine Orb"]);

        var alert = Assert.Single(VoyageAlerts.Scan(session));
        Assert.Equal("Divine Orbs", alert.Headline);
        Assert.Equal(4, alert.Square);
        Assert.Null(alert.ChartIndex);
    }

    /// <summary>"Diviner's Strongboxes" contains the letters of "Divine", and is not one.</summary>
    [Fact]
    public void DivinersStrongboxesAreNotDivineOrbs()
    {
        var session = SessionWith((5,
            "Salt Barrens\nAbyssal Plain\n"
            + "Adjacent Modifier: Adjacent Areas contain 3 additional Diviner's Strongboxes"));

        Assert.Empty(VoyageAlerts.Scan(session));
    }

    [Fact]
    public void SoulEaterIsFoundAndNamesItsChart()
    {
        var session = SessionWith((7,
            "Tempest Reach\nAnchorfield\nItem Quantity: +30%\n"
            + "Voyage Modifier: Players in all Voyage Areas have Soul Eater"));

        Assert.Equal(7, VoyageAlerts.SoulEaterChart(session));
        Assert.Contains(VoyageAlerts.Scan(session), a => a.Headline == "Soul Eater");
    }

    [Fact]
    public void NoSoulEaterChartMeansNoButton()
    {
        Assert.Null(VoyageAlerts.SoulEaterChart(SessionWith((1, "Salt Barrens\nAbyssal Plain"))));
    }

    /// <summary>
    /// Filed as a REWARD by the game's own tables, which is exactly why it needs saying:
    /// it reads like an upside and deletes most of the loot.
    /// </summary>
    [Fact]
    public void ModifiersThatReadAsRewardsAndCostYouAreFlaggedAsTraps()
    {
        var session = SessionWith((2,
            "Salt Barrens\nAbyssal Plain\nVoyage Modifier: Monsters in all Voyage Areas "
            + "cannot drop Equipment, Flasks or Tinctures"));

        var alert = Assert.Single(VoyageAlerts.Scan(session));
        Assert.Equal(AlertKind.Trap, alert.Kind);
    }

    [Fact]
    public void JackpotsSortAboveTraps()
    {
        var session = SessionWith(
            (1, "Salt Barrens\nAbyssal Plain\nVoyage Modifier: Monsters in all Voyage Areas "
                + "cannot drop Equipment, Flasks or Tinctures"),
            (2, "Tempest Reach\nAnchorfield\nVoyage Modifier: Players in all Voyage Areas "
                + "have Soul Eater"));

        var alerts = VoyageAlerts.Scan(session);
        Assert.Equal(2, alerts.Count);
        Assert.Equal(AlertKind.Jackpot, alerts[0].Kind);
        Assert.Equal(AlertKind.Trap, alerts[1].Kind);
    }

    /// <summary>A death forfeits every square not yet reached, so this is the lethal one.</summary>
    [Fact]
    public void NegativeMaximumResistancesAreFlagged()
    {
        var session = SessionWith();
        session.ApplySquareModifiers(1, ["Players have -8% to all maximum Resistances"]);

        var alert = Assert.Single(VoyageAlerts.Scan(session));
        Assert.Equal(AlertKind.Trap, alert.Kind);
        Assert.Equal("Lowered maximum resistances", alert.Headline);
    }

    [Fact]
    public void OrdinaryChartsRaiseNothing()
    {
        var session = SessionWith((1,
            "Salt Barrens\nAbyssal Plain\nItem Quantity: +42%\nGold Found: +80%\n"
            + "Adjacent Modifier: Adjacent Areas contain 4 additional Strongboxes"));

        Assert.Empty(VoyageAlerts.Scan(session));
    }

    /// <summary>
    /// One row per MODIFIER, not per source.
    ///
    /// A difficulty modifier like lowered maximum resistances sits on a quarter of a real
    /// panel. Listed one row per chart, nine identical paragraphs filled the banner and
    /// buried the two rare modifiers it existed to surface.
    /// </summary>
    [Fact]
    public void OneModifierOnManyChartsIsOneRowNamingThemAll()
    {
        var session = SessionWith(
            [.. new[] { 1, 3, 8 }.Select(i => (i,
                $"Salt Barrens\nAbyssal Plain\nPlayers have -{i}% to all maximum Resistances"))]);

        var alert = Assert.Single(VoyageAlerts.Scan(session));
        Assert.Equal([1, 3, 8], alert.Charts);
        Assert.Equal("charts 1, 3, 8", alert.Where);
    }

    [Fact]
    public void ASingleSourceStillReadsAsOne()
    {
        var session = SessionWith((6,
            "Salt Barrens\nAbyssal Plain\nPlayers have -8% to all maximum Resistances"));

        Assert.Equal("chart 6", Assert.Single(VoyageAlerts.Scan(session)).Where);
    }

    /// <summary>Charts and squares can carry the same modifier; both get named.</summary>
    [Fact]
    public void ChartsAndSquaresAreNamedSeparately()
    {
        var session = SessionWith((2,
            "Salt Barrens\nAbyssal Plain\nAdjacent Modifier: Rare Monsters adjacent in "
            + "Areas drop 2 additional Divine Orbs"));
        session.ApplySquareModifiers(5, ["Rare Monsters in Area drop an additional Divine Orb"]);

        var alert = Assert.Single(VoyageAlerts.Scan(session));
        Assert.Equal("chart 2 · square 5", alert.Where);
    }

    /// <summary>The same chart listing its implicit twice is one modifier, not two.</summary>
    [Fact]
    public void OneChartRaisesOneAlertPerHeadline()
    {
        var session = SessionWith((6,
            "Tempest Reach\nAnchorfield\n"
            + "Voyage Modifier: Players in all Voyage Areas have Soul Eater\n"
            + "Players in all Voyage Areas have Soul Eater"));

        Assert.Single(VoyageAlerts.Scan(session));
    }
}
