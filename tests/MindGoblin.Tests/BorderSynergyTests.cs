using MindGoblin.Core.Voyage;

namespace MindGoblin.Tests;

/// <summary>
/// Border mods must be able to STEER placement, not just decorate the score.
///
/// Scored flat per cell, a full board makes their sum a constant over every layout --
/// nine squares are occupied either way -- so a "Rare Monsters drop an additional Divine
/// Orb" square was worth the same under a rare-packed chart as under a dead one, and the
/// border modifiers decided nothing. Per-monster payouts now multiply with the tile's
/// Monster Pack Size, which is the one density stat the chart itself states.
/// </summary>
public class BorderSynergyTests
{
    /// <summary>The lines that pay per monster, in every wording the game uses.</summary>
    [Theory]
    [InlineData("Rare Monsters in adjacent Areas drop 2 additional Divine Orbs")]
    [InlineData("Rare Monsters adjacent in Areas drop 2 additional Divine Orbs")]
    [InlineData("Rare Monsters in Area drop an additional Scarab")]
    [InlineData("Magic Monsters in adjacent Areas have an additional modifier")]
    [InlineData("Monsters in adjacent Areas are at least Magic")]
    public void PerMonsterPayoutsAreRecognised(string line) =>
        Assert.True(VoyageProfile.IsPerMonsterPayout(line));

    /// <summary>
    /// Density lines ADD monsters rather than pay per monster, and flat grants have no
    /// monster in them at all -- both stay additive.
    /// </summary>
    [Theory]
    [InlineData("20% increased number of Rare Monsters in adjacent Areas")]
    [InlineData("32% increased Pack Size")]
    [InlineData("Area contains 4 additional Golden Lanterns")]
    [InlineData("Adjacent Areas contain a lost Pirate's Locker")]
    public void AdditiveLinesAreNot(string line) =>
        Assert.False(VoyageProfile.IsPerMonsterPayout(line));

    /// <summary>
    /// Observed in a run: imprisoned (essence) monsters ARE rares and DO drop the
    /// per-rare border payouts. Each is a whole rare, so an imprisoned gift carries
    /// full weight in the density model -- 4 imprisoned outranks "+30% increased".
    /// </summary>
    [Theory]
    [InlineData("Adjacent Areas contain 4 additional Imprisoned Monsters", 0.4)]
    [InlineData("Adjacent Areas contain an additional Imprisoned Monster", 0.1)]
    [InlineData("30% increased number of Rare Monsters in adjacent Areas", 0.3)]
    [InlineData("12 additional packs of Crabs", 0.12)]
    [InlineData("50% increased Pack Size", 0.0)]   // population channel, not rares
    // The community's Divine-border feeders: a rolled Strongbox pours out ~3 rares,
    // and Sea-Pillar starfish are always-rare. Both wordings (template and resolved).
    [InlineData("Adjacent Areas contain 5 additional Diviner's Strongboxes", 2.0)]
    [InlineData("Area contains an additional Strongbox", 0.4)]
    [InlineData("Adjacent Areas contain 5 additional Giant Starfish", 0.5)]
    [InlineData("Area contains an additional Giant Starfish", 0.1)]
    public void RareDensityCountsEveryRareSource(string line, double expected) =>
        Assert.Equal(expected, VoyageProfile.MonsterDensityOf(line), 3);

    /// <summary>The container-gift classifier and the quantity it multiplies with.</summary>
    [Theory]
    [InlineData("Adjacent Areas contain 3 additional Messages in a Bottle", false)]  // ground loot, sold unopened: flat
    [InlineData("Adjacent Areas contain an additional Message in a Bottle", false)]  // ground loot, sold unopened: flat
    [InlineData("Adjacent Areas contain 5 additional Diviner's Strongboxes", true)]
    [InlineData("Adjacent Areas contain an additional Cluster of Barrels", true)]
    [InlineData("Adjacent Areas contain an additional Golden Lantern", false)]   // its value IS quantity
    [InlineData("Adjacent Areas contain 5 additional Giant Starfish", false)]    // monsters, rare channel
    [InlineData("30% increased number of Rare Monsters in adjacent Areas", false)]
    public void ContainerGiftsAreKnownByName(string line, bool expected) =>
        Assert.Equal(expected, VoyageProfile.IsContainerGift(line));

    /// <summary>
    /// A drop conversion is not a container -- nothing is added, the drops the tile
    /// already makes are upgraded -- but the receiving tile's Quantity multiplies it
    /// just the same, because Quantity is what decides how many drops there are to
    /// convert. It went through neither channel and scored FLAT, inert at 40 wherever
    /// it sat, on a mod whose decks are ~2c each.
    /// </summary>
    [Theory]
    [InlineData("Basic Currency items dropped by Monsters in adjacent Areas "
                + "will instead drop as Stacked Decks", true)]
    [InlineData("Adjacent Areas contain 5 additional Diviner's Strongboxes", true)]
    [InlineData("Adjacent Areas contain 3 additional Messages in a Bottle", false)]
    [InlineData("Rare Monsters in adjacent Areas drop 2 additional Chaos Orbs", false)]
    [InlineData("60% increased explicit modifier magnitudes", false)]
    public void QuantityScaledPayoutsIncludeDropConversions(string line, bool expected) =>
        Assert.Equal(expected, VoyageProfile.ScalesWithReceiverQuantity(line));

    /// <summary>
    /// The conversion square must pull the highest-quantity chart onto ITSELF. Scored
    /// flat it could not: the solver left a +200% quantity chart on the far side of the
    /// board and the figurine paid the same either way.
    /// </summary>
    [Fact]
    public void AConversionSquareTakesTheQuantityChart()
    {
        var session = Session(out _, out _);
        var quantity = 3;
        session.ApplyChartText(quantity,
            "Drowned Shelf\nAnchorfield\nItem Quantity: +200%");
        session.ApplySquareModifiers(1, [Conversion]);

        var currency = VoyageRules.Defaults().Single(p => p.Name == "currency");
        var plan = session.Plan(session.Solve(currency, TimeSpan.FromSeconds(3)));

        Assert.Equal(1, Assert.Single(plan, s => s.ChartNumber == quantity).Square);
    }

    private const string Conversion =
        "Basic Currency items dropped by Monsters in this Area "
        + "will instead drop as Stacked Decks";

    /// <summary>
    /// MONSTERS are what a monster-drop conversion converts, so pack size feeds it as
    /// surely as quantity does. On quantity alone a +150% pack tile paid a conversion
    /// exactly what a BLANK tile did, so the solver had no reason to feed the square
    /// monsters and spent the board on barrels instead -- whose loot no monster drops
    /// and which a monster-drop conversion therefore cannot touch.
    /// </summary>
    [Theory]
    [InlineData("Monster Pack Size: +150%")]
    [InlineData("Item Quantity: +150%")]
    public void AConversionIsFedByMonstersAndByQuantityAlike(string stat)
    {
        double Conversion_Worth(bool withConversion)
        {
            var session = Session(out _, out _);
            session.ApplyChartText(1, $"Feeder\nAnchorfield\n{stat}");
            if (withConversion) session.ApplySquareModifiers(1, [Conversion]);
            var currency = VoyageRules.Defaults().Single(p => p.Name == "currency");
            return session.Solve(currency, TimeSpan.FromSeconds(2)).Value;
        }

        var blank = Session(out _, out _);
        blank.ApplySquareModifiers(1, [Conversion]);
        var currencyProfile = VoyageRules.Defaults().Single(p => p.Name == "currency");
        var onBlank = blank.Solve(currencyProfile, TimeSpan.FromSeconds(2)).Value
                      - Session(out _, out _).Solve(currencyProfile, TimeSpan.FromSeconds(2)).Value;

        Assert.True(Conversion_Worth(true) - Conversion_Worth(false) > onBlank,
                    $"a +150% {stat} tile must be worth more to a conversion than a blank one");
    }

    /// <summary>
    /// A big pack gift beats a big container gift under CURRENCY, because the monsters
    /// are what drop the currency. Field-reported: 13 additional packs of Octopi scored
    /// zero against 19 Clusters of Barrels at 28.5, so the solver seated the barrels on
    /// the three-neighbour square and the packs in a corner.
    /// </summary>
    [Fact]
    public void AddedPacksOutscoreBarrelsUnderCurrency()
    {
        var currency = VoyageRules.Defaults().Single(p => p.Name == "currency");

        var packs = currency.ScoreText(["Adjacent Areas contain 13 additional packs of Octopi"]);
        var barrels = currency.ScoreText(["Adjacent Areas contain 19 additional Clusters of Barrels"]);

        Assert.True(packs > barrels,
                    $"13 packs scored {packs:0.#}, 19 barrel clusters {barrels:0.#}");
    }

    /// <summary>
    /// EVERY upright plan must weight the amplifier at 1.0, because its unit is RELATIVE.
    ///
    /// The catalog prices it 0.01 per percent of the RECEIVER's own explicit value, so
    /// weight 1.0 says "80% magnitude is worth 80% more of whatever this plan already
    /// values" -- the only coherent reading. Any other number says a plan wants its own
    /// objective amplified more, or less, than itself.
    ///
    /// Left at zero the rule compiles away entirely and the profile is blind to a
    /// multiplier on the thing it exists to maximise. Found live: sulphur scored four
    /// amplifier figurines at NOTHING on a real board, which cost it 390 points of 1965
    /// and made the search look broken -- it proved a placement-indifferent board in
    /// 3,825 nodes because, as scored, no placement mattered.
    /// </summary>
    [Fact]
    public void EveryUprightPlanWeightsTheAmplifierAtOne()
    {
        foreach (var strategy in Strategies.Shipped().Where(s => s.ChartBaseValue <= 0))
            Assert.True(Math.Abs(strategy.WeightOf(Stat.ModifierMagnitude) - 1) < 1e-9,
                        $"{strategy.Name} weights the amplifier "
                        + $"{strategy.WeightOf(Stat.ModifierMagnitude)}, not 1");
    }

    /// <summary>The consequence that matters: an amplifier beside the fattest chart the
    /// plan values must be worth a fraction OF that chart, not zero.</summary>
    [Fact]
    public void AnAmplifierIsWorthAFractionOfWhatThePlanAlreadyValues()
    {
        var sulphur = VoyageRules.Defaults().Single(p => p.Name == "sulphur");
        var fat = new Chart("c", "Reach", ChartShape.Crossing, 83, []) { Sulphur = 45 };

        Assert.True(sulphur.ExplicitValue(fat) > 0, "a chart's sulphur IS explicit value");
        Assert.Equal(0.8, sulphur.ScoreText(
            ["Adjacent Areas have 80% increased explicit modifier magnitudes"]), 6);
    }

    /// <summary>
    /// An experience buff is paid PER KILL, so it is a population payout however it is
    /// worded -- "Players in adjacent Areas gain #% increased Experience" starts with
    /// Players and is still worth a percentage of nothing on an empty tile.
    /// </summary>
    [Fact]
    public void AnExperienceGiftRidesThePopulationChannel() =>
        Assert.Equal(VoyageProfile.PayoutChannel.Population,
                     VoyageProfile.PayoutChannelOf(
                         "Players in adjacent Areas gain 200% increased Experience"));

    /// <summary>
    /// The point of making it a channel at all: a levelling plan zeroes the loot stats
    /// and raises Experience, and the solver must then feed the buff the densest tile it
    /// can reach. Scored FLAT the figurine paid the same wherever it sat, so the slider
    /// could move the score and never the board.
    /// </summary>
    [Fact]
    public void ARaisedExperienceSliderSeatsTheDensestChartBesideTheBuff()
    {
        var levelling = WeightCategories.Blended(
            VoyageRules.Defaults().Single(p => p.Name == "currency"),
            new Dictionary<string, int>
            {
                ["Experience"] = WeightCategories.Max,
                ["Currency"] = 0, ["Scarabs"] = 0, ["Bottles"] = 0, ["Openables"] = 0,
                ["Quantity"] = 0, ["Rares"] = 0, ["Packs"] = 0, ["Uniques"] = 0,
                ["ModifierMagnitude"] = 0,
            },
            VoyageRules.Defaults());

        var session = Session(out _, out _);
        session.ApplyChartText(1, "Swarm\nAnchorfield\nMonster Pack Size: +150%");
        session.ApplySquareModifiers(1,
            ["Players in this Area gain 200% increased Experience"]);

        var plan = session.Plan(session.Solve(levelling, TimeSpan.FromSeconds(3)));

        Assert.Equal(1, Assert.Single(plan, s => s.ChartNumber == 1).Square);
    }

    /// <summary>Making the slider WORK must not make it fire by itself: every shipped
    /// preset farms, so all of them leave Experience at zero and none of their plans
    /// move because of this.</summary>
    [Fact]
    public void NoShippedPresetPursuesExperience()
    {
        const string xp = "Players in adjacent Areas gain 200% increased Experience";
        foreach (var strategy in Strategies.Shipped())
            Assert.True(strategy.WeightOf(Stat.Experience) <= 0,
                        $"{strategy.Name} pursues experience");
        Assert.All(VoyageRules.Defaults().Where(p => p.ChartBaseValue <= 0),
                   p => Assert.Equal(0, p.ScoreText([xp])));
    }

    /// <summary>The other half of that claim: a CONTAINER gift is not fed by monsters.
    /// Barrels are stocked when the area is built, so pack size does nothing for them,
    /// and the two channels must not be collapsed into one.</summary>
    [Fact]
    public void AContainerGiftIsNotFedByPackSize()
    {
        double Worth(string stat)
        {
            var session = Session(out _, out _);
            session.ApplyChartText(1, $"Feeder\nAnchorfield\n{stat}");
            session.ApplySquareModifiers(1, ["Area contains 5 additional Clusters of Barrels"]);
            var currency = VoyageRules.Defaults().Single(p => p.Name == "currency");
            return session.Solve(currency, TimeSpan.FromSeconds(2)).Value;
        }

        // Quantity rolls against a barrel's contents; pack size never touches them.
        Assert.True(Worth("Item Quantity: +150%") > Worth("Monster Pack Size: +150%"));
    }

    [Theory]
    [InlineData("45% increased Quantity of Items found in adjacent Areas", 0.45)]
    [InlineData("8% increased Qauntity of Items found in all Voyage Areas", 0.08)]
    [InlineData("50% increased Pack Size", 0.0)]
    public void QuantityDensityReadsBothSpellings(string line, double expected) =>
        Assert.Equal(expected, VoyageProfile.QuantityDensityOf(line), 3);

    /// <summary>
    /// 3.29.1's notes say "Fixed a typo in a Voyage modifier" WITHOUT saying which, and
    /// poedb still serves the pre-patch corpus, so the game may now emit either spelling
    /// of either line. Both must score the same through the whole chain -- an exact-match
    /// lookup that misses degrades to the pattern fallback silently, which is the worst
    /// way to be wrong about a Divine Orb.
    /// </summary>
    [Theory]
    [InlineData("Rare Monsters adjacent in Areas drop 2 additional Divine Orbs",
                "Rare Monsters in adjacent Areas drop 2 additional Divine Orbs")]
    [InlineData("20% increased Qauntity of Items found in all Voyage Areas",
                "20% increased Quantity of Items found in all Voyage Areas")]
    public void BothSpellingsOfAGGGTypoScoreAlike(string typo, string fixedUp)
    {
        foreach (var profile in VoyageRules.Defaults())
            Assert.Equal(profile.ScoreText([typo]), profile.ScoreText([fixedUp]), 6);

        Assert.Equal(VoyageProfile.PayoutChannelOf(typo), VoyageProfile.PayoutChannelOf(fixedUp));
        Assert.Equal(VoyageProfile.QuantityDensityOf(typo), VoyageProfile.QuantityDensityOf(fixedUp), 6);
        Assert.Equal(ChartRewards.IsReward(typo), ChartRewards.IsReward(fixedUp));
    }

    /// <summary>
    /// The bottle play, end to end: a bottle is ground loot sold UNOPENED
    /// (field-confirmed), so the gift pays a fixed value into EVERY adjacent area and
    /// nothing multiplies it. Its existence is what gets maximised -- the solver must
    /// seat the bottle chart in the centre, where it touches four tiles, not chase
    /// receiver quantity, which is worth nothing to an unopened bottle.
    /// </summary>
    [Fact]
    public void ABottleGiftTakesTheCentre()
    {
        var session = Session(out _, out _);
        var gift = 11;
        session.ApplyChartText(gift,
            "Kelp Forest\nAnchorfield\nAdjacent Modifier: "
            + "Adjacent Areas contain 4 additional Messages in a Bottle");
        session.ApplyChartText(3,
            "Drowned Shelf\nAnchorfield\nItem Quantity: +120%");

        var bottles = VoyageRules.Defaults().Single(p => p.Name == "bottles");
        var plan = session.Plan(session.Solve(bottles, TimeSpan.FromSeconds(3)));

        Assert.Equal(5, Assert.Single(plan, s => s.ChartNumber == gift).Square);
    }

    /// <summary>The bottle gift wording, both numbers the game writes; strongboxes are
    /// a different gift and must not ration.</summary>
    [Theory]
    [InlineData("Adjacent Areas contain 3 additional Messages in a Bottle", true)]
    [InlineData("Adjacent Areas contain an additional Message in a Bottle", true)]
    [InlineData("Adjacent Areas contain 5 additional Diviner's Strongboxes", false)]
    [InlineData("30% increased number of Rare Monsters in adjacent Areas", false)]
    public void BottleGiftsAreKnownByName(string line, bool expected) =>
        Assert.Equal(expected, VoyageProfile.IsBottleGift(line));

    /// <summary>
    /// One bottle chart per voyage (field rule): a bottle's count is fixed by the roll,
    /// so a second bottle chart mostly re-covers areas the first already feeds -- held
    /// back it is a whole extra voyage of bottles. The BETTER roll sails, and the
    /// solve says so in its notes.
    /// </summary>
    [Fact]
    public void OnlyOneBottleChartSailsPerVoyage()
    {
        var session = Session(out _, out _);
        session.ApplyChartText(2,
            "Kelp Forest\nAnchorfield\nAdjacent Modifier: "
            + "Adjacent Areas contain 2 additional Messages in a Bottle");
        session.ApplyChartText(7,
            "Briny Quest\nAnchorfield\nAdjacent Modifier: "
            + "Adjacent Areas contain an additional Message in a Bottle");

        var bottles = VoyageRules.Defaults().Single(p => p.Name == "bottles");
        var plan = session.Plan(session.Solve(bottles, TimeSpan.FromSeconds(3)));

        Assert.Single(plan, s => s.ChartNumber == 2);       // the 2-bottle roll sails
        Assert.DoesNotContain(plan, s => s.ChartNumber == 7);
        // The rationing note by CONTENT, not by being the only one: an unhovered panel
        // also earns the "levels are unknown" note, and pinning the count made this test
        // fail for a reason that has nothing to do with bottles.
        var note = Assert.Single(session.SolveNotes, n => n.Contains("1 per voyage"));
        Assert.Contains("held back", note);
    }

    /// <summary>The bottle chart's existence is the objective: it sails even when every
    /// other chart outscores it, it sails in the CENTRE even when a fatter chart would
    /// use that square better, and both bonuses are peeled off the report.</summary>
    [Fact]
    public void TheBottleChartSailsCentredEvenWhenOutclassed()
    {
        var session = Session(out _, out _);
        foreach (var i in Enumerable.Range(1, 12).Where(i => i != 11))
            session.ApplyChartText(i,
                $"Rich {i}\nAnchorfield\nItem Quantity: +150%");
        session.ApplyChartText(11,
            "Kelp Forest\nAnchorfield\nAdjacent Modifier: "
            + "Adjacent Areas contain an additional Message in a Bottle");

        var bottles = VoyageRules.Defaults().Single(p => p.Name == "bottles");
        var solution = session.Solve(bottles, TimeSpan.FromSeconds(3));

        var bottle = solution.Placements.Single(p => p.Chart.Id.EndsWith("-11"));
        Assert.Equal(new Cell(1, 1), bottle.Cell);
        Assert.InRange(solution.Value, 0, 50_000);   // neither bonus is reported
    }

    /// <summary>Rationing is the bottle CHASE's economics; a profile that is not
    /// chasing bottles spends its charts on their merits, without notes.</summary>
    [Fact]
    public void OtherProfilesAreNotRationed()
    {
        var session = Session(out _, out _);
        session.ApplyChartText(2,
            "Kelp Forest\nAnchorfield\nAdjacent Modifier: "
            + "Adjacent Areas contain 2 additional Messages in a Bottle");
        session.ApplyChartText(7,
            "Briny Quest\nAnchorfield\nAdjacent Modifier: "
            + "Adjacent Areas contain an additional Message in a Bottle");

        var sulphur = VoyageRules.Defaults().Single(p => p.Name == "sulphur");
        session.Solve(sulphur, TimeSpan.FromSeconds(3));

        Assert.Empty(session.SolveNotes);
    }

    /// <summary>The receipt view agrees: a bottle gift hands every neighbour the same
    /// value, whatever that neighbour's quantity. Before the ground-loot correction the
    /// same board showed the +120% tile receiving 2.2x its poorer neighbours.</summary>
    [Fact]
    public void ABottleGiftPaysEveryNeighbourTheSame()
    {
        var session = Session(out _, out _);
        var gift = 11;
        session.ApplyChartText(gift,
            "Kelp Forest\nAnchorfield\nAdjacent Modifier: "
            + "Adjacent Areas contain 4 additional Messages in a Bottle");
        session.ApplyChartText(3,
            "Drowned Shelf\nAnchorfield\nItem Quantity: +120%");

        var bottles = VoyageRules.Defaults().Single(p => p.Name == "bottles");
        var solution = session.Solve(bottles, TimeSpan.FromSeconds(3));
        var giftSquare = session.Plan(solution).Single(s => s.ChartNumber == gift).Square;

        var received = Enumerable.Range(1, 9)
            .SelectMany(sq => session.ReceivedOnSquare(bottles, solution, sq))
            .Where(r => r.FromSquare == giftSquare && r.Modifier.Contains("Bottle"))
            .Select(r => r.Value)
            .ToList();
        Assert.True(received.Count >= 2);
        Assert.All(received, v => Assert.Equal(received[0], v, 6));
    }

    /// <summary>
    /// The game never leaves a square empty by choice: a pool of nine-plus charts must
    /// fill the board even when every chart SCORES negative -- the currency profile's
    /// penalties made the solver answer with holes.
    /// </summary>
    [Fact]
    public void NegativeChartsStillFillTheBoard()
    {
        var session = new VoyageSession();
        session.ApplyPanelRead(Enumerable.Range(1, 12).Select(i =>
            new ChartPanelReader.ReadCell(i, (i - 1) / 6, (i - 1) % 6, true, true, true, true))
                .ToList());
        foreach (var i in Enumerable.Range(1, 12))
            session.ApplyChartText(i, "Reach\nAnchorfield\nVoyage Modifier: Monsters in "
                                      + "all Voyage Areas cannot drop Equipment, Flasks or Tinctures");

        var bottles = VoyageRules.Defaults().Single(p => p.Name == "bottles");
        var solution = session.Solve(bottles, TimeSpan.FromSeconds(3));
        Assert.Equal(9, solution.Placements.Count);
    }

    /// <summary>
    /// Rares drive currency THROUGH THE REAL MODS: the pool (poedb) has no self-scope
    /// rare adders, so the interaction runs on adjacency -- a "+30% increased Rare
    /// Monsters in adjacent Areas" gift chart must seat itself beside the Divine
    /// square under the currency profile.
    /// </summary>
    [Fact]
    public void ARareGiftChartFeedsTheDivineSquare()
    {
        var session = Session(out _, out _);
        var gift = 6;
        session.ApplyChartText(gift,
            "Deep Trench\nAnchorfield\nAdjacent Modifier: "
            + "30% increased number of Rare Monsters in adjacent Areas");
        session.ApplySquareModifiers(1,
            ["Rare Monsters in Area drop an additional Divine Orb"]);

        var currency = VoyageRules.Defaults().Single(p => p.Name == "currency");
        var plan = session.Plan(session.Solve(currency, TimeSpan.FromSeconds(3)));

        Assert.Contains(plan.Single(s => s.ChartNumber == gift).Square, new[] { 2, 4 });
    }

    /// <summary>
    /// The conflation guard, asked for from the field: a GLOBAL rare line lifts every
    /// tile equally, so it must not fake own-tile density and steal the Divine square
    /// from a genuinely dense chart.
    /// </summary>
    [Fact]
    public void AGlobalRareLineDoesNotStealThePayoutSquare()
    {
        var session = Session(out var packChart, out _);   // packChart: +50% pack size
        var global = 6;
        session.ApplyChartText(global,
            "Deep Trench\nAnchorfield\nVoyage Modifier: "
            + "25% increased number of Rare Monsters in all Voyage Areas");
        session.ApplySquareModifiers(1,
            ["Rare Monsters in Area drop an additional Divine Orb"]);

        var currency = VoyageRules.Defaults().Single(p => p.Name == "currency");
        var plan = session.Plan(session.Solve(currency, TimeSpan.FromSeconds(3)));

        Assert.Equal(1, plan.Single(s => s.ChartNumber == packChart).Square);
    }

    /// <summary>
    /// An adjacent gift buffs the NEIGHBOURS and never the tile it stands on: the
    /// chart's own value must be identical with and without the gift line, under
    /// every profile. Asked directly from the field ("do gives-adjacent tiles buff
    /// the tile they're on disproportionally?") -- the answer must stay no.
    /// </summary>
    [Fact]
    public void AnAdjacentGiftAddsNothingToItsOwnTile()
    {
        var bare = new Chart("a", "a", ChartShape.Crossing, 80, [])
        { MonsterPackSize = 36, ItemQuantity = 55, Sulphur = 30 };
        var gifted = bare with
        { AdjacentModifier = "Adjacent Areas contains 7 additional Giant Starfish" };

        foreach (var profile in VoyageRules.Defaults())
            Assert.Equal(profile.ScoreChart(bare), profile.ScoreChart(gifted));
    }

    /// <summary>The tile-side view of adjacency: a square beside a payout gift lists
    /// what arrives and from where, channel-scaled like the solver prices it.</summary>
    [Fact]
    public void ASquareReportsWhatItGainsFromNeighbours()
    {
        var session = Session(out _, out _);
        var gift = 11;
        session.ApplyChartText(gift,
            "Kelp Forest\nAnchorfield\nAdjacent Modifier: "
            + "30% increased number of Rare Monsters in adjacent Areas");

        // The sulphur square seats the gift chart beside square 1 deterministically
        // (the same arrangement ARareGiftChartSitsBesideTheSulphurSquare proves).
        session.ApplySquareModifiers(1, ["Rare Monsters in Area drop Dead Man's Sulphur"]);

        var sulphur = VoyageRules.Defaults().Single(p => p.Name == "sulphur");
        var solution = session.Solve(sulphur, TimeSpan.FromSeconds(3));
        var giftSquare = session.Plan(solution).Single(s => s.ChartNumber == gift).Square;

        var received = session.ReceivedOnSquare(sulphur, solution, 1);
        Assert.Contains(received, r => r.FromSquare == giftSquare
                                       && r.Modifier.Contains("Rare Monsters"));
    }

    /// <summary>
    /// Straight from a live board that solved suspiciously fast: a FIGURINE carrying
    /// the per-rare sulphur payout must dominate the sulphur solve exactly like a
    /// square read of the same line would.
    /// </summary>
    [Fact]
    public void AFigurineSulphurPayoutDominatesTheSolve()
    {
        var session = Session(out _, out _);
        session.ApplyFigurineText(10, "Rare Monsters in adjacent Areas drop Dead Man's Sulphur");

        var sulphur = VoyageRules.Defaults().Single(p => p.Name == "sulphur");
        var solution = session.Solve(sulphur, TimeSpan.FromSeconds(3));
        Assert.True(solution.Value > 10_000,
                    $"the sulphur square is worth ~15k and the solve saw {solution.Value:0.#}");

        // ...and identically after a save/load round-trip, which is how the app and
        // probe actually run every solve.
        var restored = VoyageSession.FromState(session.ToState());
        var again = restored.Solve(sulphur, TimeSpan.FromSeconds(3));
        Assert.True(again.Value > 10_000,
                    $"restore lost the figurine payout: {again.Value:0.#}");
    }

    /// <summary>Filthscrabble is a ~4,000-sulphur boss (community-sourced): the
    /// sulphur profile must price his square above even the per-rare sulphur square,
    /// and the dump profile must refuse to burn the chart.</summary>
    [Fact]
    public void FilthscrabbleIsASulphurJackpot()
    {
        var sulphur = VoyageRules.Defaults().Single(p => p.Name == "sulphur");
        var boss = sulphur.ScoreText(["Area contains Filthscrabble"]);
        var perRare = sulphur.ScoreText(["Rare Monsters in Area drop Dead Man's Sulphur"]);
        Assert.True(boss > perRare, $"boss {boss} should outrank the per-rare square {perRare}");

        var dump = VoyageRules.Defaults().Single(p => p.Name == "dump");
        Assert.True(dump.ScoreText(["Area contains Filthscrabble"]) < -5000,
                    "dump must treat the Filthscrabble chart as a keeper");
    }

    /// <summary>The population channel: pack size and added packs, at full weight --
    /// an at-least-Magic upgrade converts added packs wholesale.</summary>
    [Theory]
    [InlineData("50% increased Pack Size", 0.5)]
    [InlineData("12 additional packs of Crabs", 0.4)]
    [InlineData("30% increased number of Rare Monsters in adjacent Areas", 0.0)]
    public void PackDensityCountsThePopulation(string line, double expected) =>
        Assert.Equal(expected, VoyageProfile.PackDensityOf(line), 3);

    /// <summary>
    /// The channel split, end to end: a rare-dense room (Brine) gains NOTHING from an
    /// at-least-Magic tile -- its rares are already above magic -- so the +50% pack
    /// chart wins that seat even against the best rare room in the game.
    /// </summary>
    [Fact]
    public void ARareRoomDoesNotWinTheUpgradedTile()
    {
        var session = Session(out var packChart, out _);
        session.ApplyChartText(7, "Deep Plunge\nCoral Reef Chart\nBrine King's Domain");
        session.ApplySquareModifiers(1, ["Monsters in Area are at least Magic"]);

        var magic = VoyageRules.Defaults().Single(p => p.Name == "magic monsters");
        var plan = session.Plan(session.Solve(magic, TimeSpan.FromSeconds(3)));

        Assert.Equal(packChart, Assert.Single(plan, s => s.Square == 1).ChartNumber);
    }

    /// <summary>A packs-gift chart belongs BESIDE the upgrade tile: its packs are
    /// converted wholesale, which the rare channel priced at a token.</summary>
    [Fact]
    public void APackGiftChartSitsBesideTheUpgradedTile()
    {
        var session = Session(out _, out _);
        var gift = 11;
        session.ApplyChartText(gift,
            "Kelp Forest\nAnchorfield\nAdjacent Modifier: "
            + "Adjacent Areas contain 12 additional packs of Crabs");
        session.ApplySquareModifiers(1, ["Monsters in Area are at least Magic"]);

        var magic = VoyageRules.Defaults().Single(p => p.Name == "magic monsters");
        var plan = session.Plan(session.Solve(magic, TimeSpan.FromSeconds(3)));

        // Square 1 is the top-left corner; its neighbours are squares 2 and 4.
        Assert.Contains(Assert.Single(plan, s => s.ChartNumber == gift).Square,
                        new[] { 2, 4 });
    }

    /// <summary>
    /// A rare-dense ROOM makes its chart the right tenant for a payout square. Brine
    /// King's Domain was observed running an exceptional number of rares, so the chart
    /// opening it carries +0.5 rare density -- more than a +42% pack headline -- and
    /// the Divine square picks it over the packed chart.
    /// </summary>
    /// <summary>
    /// "At least Magic" converts the whole tile, so the tile wants the biggest
    /// population that can stand on it: under the magic-monsters profile, the upgraded
    /// square takes the +50% pack chart over a plain one.
    /// </summary>
    [Fact]
    public void TheUpgradedTileTakesTheBiggestPacks()
    {
        var session = Session(out var packChart, out _);
        session.ApplySquareModifiers(1, ["Monsters in Area are at least Magic"]);

        var magic = VoyageRules.Defaults().Single(p => p.Name == "magic monsters");
        var plan = session.Plan(session.Solve(magic, TimeSpan.FromSeconds(3)));

        Assert.Equal(packChart, Assert.Single(plan, s => s.Square == 1).ChartNumber);
    }

    /// <summary>Field observations, one line each: rooms measured dense, and rooms
    /// measured neutral so they are not re-tested.</summary>
    [Theory]
    [InlineData("Sea Pillars", 1.0)]
    [InlineData("Brine King's Domain", 0.8)]
    [InlineData("Pelagic Abyss", 0.6)]
    [InlineData("Clam-infested Shelf", 0.0)]
    [InlineData("Diving Shoals", 0.0)]
    [InlineData("Sunken Totems", 0.0)]
    [InlineData("Hazardous Depths", 0.0)] // sighted: Rotmother loot box, open question
    [InlineData("Kishara's Rest", 0.0)]   // sighted: possible boss fight, open question
    public void RoomObservationsAreOnRecord(string room, double expected)
    {
        var chart = new Chart("c", "c", ChartShape.Crossing, 80, [room]);
        Assert.Equal(expected, AreaPopulation.RoomRareBonus(chart), 3);
    }

    [Fact]
    public void ARareDenseRoomWinsThePayoutSquare()
    {
        // A fresh session with no pack charts: every competitor is plain, so the room
        // bonus alone decides the payout square. At 1.0 the room outranks any pack
        // headline in the tables too -- doubled rares beat a half-again pack.
        var session = new VoyageSession();
        session.ApplyPanelRead(Enumerable.Range(1, 12).Select(i =>
            new ChartPanelReader.ReadCell(i, (i - 1) / 6, (i - 1) % 6, true, true, true, true))
                .ToList());
        var brine = 7;
        session.ApplyChartText(brine, "Deep Plunge\nCoral Reef Chart\nBrine King's Domain");
        session.ApplySquareModifiers(1, ["Rare Monsters in Area drop an additional Divine Orb"]);

        var plan = session.Plan(session.Solve(Currency, TimeSpan.FromSeconds(3)));
        Assert.Equal(brine, Assert.Single(plan, s => s.Square == 1).ChartNumber);
    }

    /// <summary>
    /// The classifier's word is load-bearing in three scoring paths, so it must not
    /// call a curse a payout: every DIFFICULTY line the game can roll classifies as
    /// None, even though half of them start with "Monsters".
    /// </summary>
    [Fact]
    public void NoDifficultyLineClassifiesAsAPayout()
    {
        var offenders = ChartRewards.Current.Lines
            .Where(kv => kv.Value.Equals("difficulty", StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key.Replace("#", "12"))
            .Where(l => VoyageProfile.PayoutChannelOf(l) != VoyageProfile.PayoutChannel.None)
            .ToList();
        Assert.True(offenders.Count == 0, string.Join(" | ", offenders));
    }

    private static VoyageSession Session(out int packChart, out int plainChart)
    {
        var session = new VoyageSession();
        session.ApplyPanelRead(Enumerable.Range(1, 12).Select(i =>
            new ChartPanelReader.ReadCell(i, (i - 1) / 6, (i - 1) % 6, true, true, true, true))
                .ToList());

        // Two charts distinguishable only by pack size, which currency does not score --
        // so their own values tie and only the synergy can separate them.
        packChart = 4;
        plainChart = 9;
        session.ApplyChartText(packChart,
            "Tempest Reach\nAnchorfield\nMonster Pack Size: +50%");
        session.ApplyChartText(plainChart,
            "Salt Barrens\nAnchorfield\nGold Found: +80%");
        return session;
    }

    private static VoyageProfile Currency =>
        VoyageRules.Defaults().Single(p => p.Name == "currency");

    /// <summary>
    /// The payout square pulls the monster-dense chart onto itself: +50% pack size on a
    /// per-rare Divine square is worth half again the payout, and no other square pays
    /// the pack chart anything extra.
    /// </summary>
    [Fact]
    public void ThePayoutSquarePullsThePackedChart()
    {
        var session = Session(out var packChart, out _);
        session.ApplySquareModifiers(1, ["Rare Monsters in Area drop an additional Divine Orb"]);

        var solution = session.Solve(Currency, TimeSpan.FromSeconds(3));
        var plan = session.Plan(solution);

        Assert.Equal(packChart, Assert.Single(plan, s => s.Square == 1).ChartNumber);
    }

    /// <summary>
    /// The chart-x-chart pairing: a per-monster payout ADJACENT modifier is worth more
    /// beside a dense neighbour, so the Chaos-per-rare chart seeks out the packed tile.
    /// Under currency the two charts' own values tie; only the pairing separates them.
    /// </summary>
    [Fact]
    public void APayoutAdjacentChartSeeksTheDenseNeighbour()
    {
        var session = Session(out var packChart, out _);
        var payoutChart = 7;
        session.ApplyChartText(payoutChart,
            "Coral Shelf\nAnchorfield\nAdjacent Modifier: "
            + "Rare Monsters in adjacent Areas drop 2 additional Chaos Orbs");

        var solution = session.Solve(Currency, TimeSpan.FromSeconds(3));
        var plan = session.Plan(solution);

        var payoutAt = Assert.Single(plan, s => s.ChartNumber == payoutChart);
        var packAt = Assert.Single(plan, s => s.ChartNumber == packChart);
        var a = session.CellOf(payoutAt.Square);
        var b = session.CellOf(packAt.Square);
        Assert.Equal(1, Math.Abs(a.Row - b.Row) + Math.Abs(a.Col - b.Col));
    }

    /// <summary>
    /// The neighbour-density pairing: a chart whose adjacent modifier ADDS monsters is
    /// worth more beside a per-monster payout square -- the packs it grants multiply the
    /// payout next door. Currency has no rule for "additional packs", so the gift chart's
    /// own value is zero and ONLY this interaction can place it deliberately.
    /// </summary>
    [Fact]
    public void AMonsterGiftChartSeeksThePayoutSquare()
    {
        var session = Session(out _, out _);
        var giftChart = 11;
        session.ApplyChartText(giftChart,
            "Kelp Forest\nAnchorfield\nAdjacent Modifier: "
            + "Adjacent Areas contain 4 additional packs of Sea Beasts");
        session.ApplySquareModifiers(1, ["Rare Monsters in Area drop an additional Divine Orb"]);

        var solution = session.Solve(Currency, TimeSpan.FromSeconds(3));
        var plan = session.Plan(solution);

        // Square 1 is the top-left corner; its neighbours are squares 2 and 4.
        var giftAt = Assert.Single(plan, s => s.ChartNumber == giftChart);
        Assert.Contains(giftAt.Square, new[] { 2, 4 });
    }

    /// <summary>
    /// The reported miss, end to end: a "rares drop sulphur" square and a chart whose
    /// adjacent modifier ADDS rares. The rares the chart grants each pay sulphur on the
    /// square next door, so the solver must put them side by side -- under sulphur rules
    /// the gift chart is otherwise worthless, and only the interaction places it.
    /// </summary>
    [Fact]
    public void ARareGiftChartSitsBesideTheSulphurSquare()
    {
        var session = Session(out _, out _);
        var gift = 11;
        session.ApplyChartText(gift,
            "Kelp Forest\nAnchorfield\nAdjacent Modifier: "
            + "30% increased number of Rare Monsters in adjacent Areas");
        session.ApplySquareModifiers(1, ["Rare Monsters in Area drop Dead Man's Sulphur"]);

        var sulphur = VoyageRules.Defaults().Single(p => p.Name == "sulphur");
        var plan = session.Plan(session.Solve(sulphur, TimeSpan.FromSeconds(3)));

        // Square 1 is the top-left corner; its neighbours are squares 2 and 4.
        Assert.Contains(Assert.Single(plan, s => s.ChartNumber == gift).Square,
                        new[] { 2, 4 });
    }

    /// <summary>
    /// Zero switches the interaction off: the solve still works, and the score drops by
    /// exactly the synergy's contribution -- payout x weight x packsize/100.
    /// </summary>
    [Fact]
    public void SynergyZeroReturnsToFlatScoring()
    {
        var session = Session(out _, out _);
        session.ApplySquareModifiers(1, ["Rare Monsters in Area drop an additional Divine Orb"]);

        var with = session.Solve(Currency, TimeSpan.FromSeconds(3));

        var off = Currency;
        off.MonsterPayoutSynergy = 0;
        var without = session.Solve(off, TimeSpan.FromSeconds(3));

        // 1620 (a Divine = 162 chaos on poe.watch, per rare x ~10 rares) x 1.5
        // (BoardModifierWeight) x 0.5 (pack size fraction).
        Assert.Equal(1215, with.Value - without.Value, 3);
    }
}
