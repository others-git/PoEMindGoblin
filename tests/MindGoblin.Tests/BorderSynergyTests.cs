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
    [InlineData("Adjacent Areas contain 3 additional Messages in a Bottle", true)]
    [InlineData("Adjacent Areas contain an additional Message in a Bottle", true)]
    [InlineData("Adjacent Areas contain 5 additional Diviner's Strongboxes", true)]
    [InlineData("Adjacent Areas contain an additional Cluster of Barrels", true)]
    [InlineData("Adjacent Areas contain an additional Golden Lantern", false)]   // its value IS quantity
    [InlineData("Adjacent Areas contain 5 additional Giant Starfish", false)]    // monsters, rare channel
    [InlineData("30% increased number of Rare Monsters in adjacent Areas", false)]
    public void ContainerGiftsAreKnownByName(string line, bool expected) =>
        Assert.Equal(expected, VoyageProfile.IsContainerGift(line));

    [Theory]
    [InlineData("45% increased Quantity of Items found in adjacent Areas", 0.45)]
    [InlineData("8% increased Qauntity of Items found in all Voyage Areas", 0.08)]
    [InlineData("50% increased Pack Size", 0.0)]
    public void QuantityDensityReadsBothSpellings(string line, double expected) =>
        Assert.Equal(expected, VoyageProfile.QuantityDensityOf(line), 3);

    /// <summary>
    /// The bottle play, end to end: a bottle gift pays into its NEIGHBOURS, multiplied
    /// by their quantity -- so the solver must seat the bottle chart beside the
    /// highest-quantity tile in the panel, not wherever its flat value lands.
    /// </summary>
    [Fact]
    public void ABottleGiftSitsBesideTheQuantityTile()
    {
        var session = Session(out _, out _);
        var gift = 11;
        var quantity = 3;
        session.ApplyChartText(gift,
            "Kelp Forest\nAnchorfield\nAdjacent Modifier: "
            + "Adjacent Areas contain 4 additional Messages in a Bottle");
        session.ApplyChartText(quantity,
            "Drowned Shelf\nAnchorfield\nItem Quantity: +120%");

        var bottles = VoyageRules.Defaults().Single(p => p.Name == "bottles");
        var plan = session.Plan(session.Solve(bottles, TimeSpan.FromSeconds(3)));

        var giftSquare = Assert.Single(plan, s => s.ChartNumber == gift).Square;
        var qtySquare = Assert.Single(plan, s => s.ChartNumber == quantity).Square;
        var neighbours = qtySquare switch
        {
            1 => new[] { 2, 4 }, 2 => new[] { 1, 3, 5 }, 3 => new[] { 2, 6 },
            4 => new[] { 1, 5, 7 }, 5 => new[] { 2, 4, 6, 8 }, 6 => new[] { 3, 5, 9 },
            7 => new[] { 4, 8 }, 8 => new[] { 5, 7, 9 }, _ => new[] { 6, 8 },
        };
        Assert.Contains(giftSquare, neighbours);
    }

    /// <summary>A container BOARD modifier is a per-quantity payout: its square must
    /// pull the highest-quantity chart onto itself, the Milky speedrun placement.</summary>
    [Fact]
    public void ABottleSquareTakesTheQuantityChart()
    {
        var session = Session(out _, out _);
        var quantity = 3;
        session.ApplyChartText(quantity,
            "Drowned Shelf\nAnchorfield\nItem Quantity: +120%");
        session.ApplySquareModifiers(1, ["Area contains 3 additional Messages in a Bottle"]);

        var bottles = VoyageRules.Defaults().Single(p => p.Name == "bottles");
        var plan = session.Plan(session.Solve(bottles, TimeSpan.FromSeconds(3)));

        Assert.Equal(1, Assert.Single(plan, s => s.ChartNumber == quantity).Square);
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
            new ChartPanelReader.ReadCell(i, (i - 1) / 6, (i - 1) % 6, true, true, true, true)
            { Level = 80 }).ToList());
        foreach (var i in Enumerable.Range(1, 12))
            session.ApplyChartText(i, "Reach\nAnchorfield\nVoyage Modifier: Monsters in "
                                      + "all Voyage Areas cannot drop Equipment, Flasks or Tinctures");

        var bottles = VoyageRules.Defaults().Single(p => p.Name == "bottles");
        var solution = session.Solve(bottles, TimeSpan.FromSeconds(3));
        Assert.Equal(9, solution.Placements.Count);
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
            new ChartPanelReader.ReadCell(i, (i - 1) / 6, (i - 1) % 6, true, true, true, true)
            { Level = 80 }).ToList());
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
            new ChartPanelReader.ReadCell(i, (i - 1) / 6, (i - 1) % 6, true, true, true, true)
            { Level = 80 }).ToList());

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
