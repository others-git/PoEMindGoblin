using PoeMarketWatch.Core;

namespace PoeMarketWatch.Tests;

public class GemRoiTests
{
    /// Real Spell Echo Support numbers pulled from poe.watch (Allflame), so the maths is
    /// exercised against a shape the market actually produces.
    private static GemRoi.GemPrices SpellEcho() => new("Spell Echo Support",
        new Dictionary<(int, int, bool), double>
        {
            [(1, 0, false)] = 0.49,
            [(1, 20, false)] = 10.44,
            [(20, 0, false)] = 6.66,
            [(20, 20, false)] = 31.95,
            [(20, 0, true)] = 22.06,
            [(20, 20, true)] = 28.22,
            [(20, 23, true)] = 40.73,
            [(21, 0, true)] = 14.20,
            [(21, 20, true)] = 52.33,
            [(19, 20, true)] = 12.00,
        });

    private static readonly GemRoi.Costs Costs = new(GemcutterChaos: 1.0, VaalOrbChaos: 0.5);

    [Fact]
    public void LevelOnlyIsBuyLowSellLevelled()
    {
        var r = new GemRoi().Evaluate(SpellEcho(), GemRoi.Path.LevelOnly, Costs);
        Assert.True(r.IsComplete);
        Assert.Equal(0.49, r.BuyCost, 2);
        Assert.Equal(0, r.CurrencyCost);          // no GCP spend on this path
        Assert.Equal(6.66, r.ExpectedRevenue, 2);
        Assert.Equal(6.17, r.Profit, 2);
        Assert.Equal(6.17 / 0.49, r.Roi!.Value, 2);
    }

    [Fact]
    public void QualityPathChargesTwentyGemcutters()
    {
        var r = new GemRoi().Evaluate(SpellEcho(), GemRoi.Path.LevelAndQuality, Costs);
        Assert.Equal(20.0, r.CurrencyCost, 2);    // 20 GCP at 1c
        Assert.Equal(31.95, r.ExpectedRevenue, 2);
        Assert.Equal(31.95 - 20.49, r.Profit, 2);
    }

    [Fact]
    public void GcpPriceMovesTheQualityPath()
    {
        // The whole point of the tool: quality is only worth it while GCPs are cheap.
        var cheap = new GemRoi().Evaluate(SpellEcho(), GemRoi.Path.LevelAndQuality,
            Costs with { GemcutterChaos = 0.5 });
        var dear = new GemRoi().Evaluate(SpellEcho(), GemRoi.Path.LevelAndQuality,
            Costs with { GemcutterChaos = 2.0 });
        Assert.True(cheap.Profit > dear.Profit);
        Assert.True(dear.Profit < 0); // 40c of GCP against a 31.95c sale
    }

    [Fact]
    public void VaalUsesExpectedValueAcrossAllBranches()
    {
        var r = new GemRoi().Evaluate(SpellEcho(), GemRoi.Path.VaalOnly, Costs);

        // 0.25 each of: 20/20C 28.22, 21/20C 52.33, 19/20C 12.00, 20/23C 40.73
        var expected = 0.25 * (28.22 + 52.33 + 12.00 + 40.73);
        Assert.Equal(expected, r.ExpectedRevenue, 2);

        // buys the CLEAN 20/20 and pays for the orb
        Assert.Equal(31.95, r.BuyCost, 2);
        Assert.Equal(0.5, r.CurrencyCost, 2);
    }

    [Fact]
    public void VaalPricesTheNoChangeBranchAsCorrupted()
    {
        // A corrupted 20/20 is a different market to a clean one and usually worth less.
        // Pricing "no change" off the clean listing would inflate every vaal result.
        var prices = SpellEcho();
        var r = new GemRoi().Evaluate(prices, GemRoi.Path.VaalOnly, Costs);
        var clean = prices.At(20, 20, false)!.Value;   // 31.95
        var corrupt = prices.At(20, 20, true)!.Value;  // 28.22
        Assert.True(corrupt < clean);
        Assert.Equal(0.25 * (corrupt + 52.33 + 12.00 + 40.73), r.ExpectedRevenue, 2);
    }

    [Fact]
    public void FullChainCombinesEveryCost()
    {
        var r = new GemRoi().Evaluate(SpellEcho(), GemRoi.Path.FullChain, Costs);
        Assert.Equal(0.49, r.BuyCost, 2);              // starts from the vendor gem
        Assert.Equal(20 * 1.0 + 0.5, r.CurrencyCost, 2);
        Assert.Equal(0.25 * (28.22 + 52.33 + 12.00 + 40.73), r.ExpectedRevenue, 2);
    }

    [Fact]
    public void SkewedOddsChangeTheExpectedValue()
    {
        // Guards the reason odds are configurable rather than constant.
        var pessimistic = new GemRoi(new GemRoi.CorruptionOdds(
            NoChange: 0.4, LevelUp: 0.1, LevelDown: 0.4, QualityChange: 0.1));
        var optimistic = new GemRoi(new GemRoi.CorruptionOdds(
            NoChange: 0.1, LevelUp: 0.6, LevelDown: 0.1, QualityChange: 0.2));

        var lo = pessimistic.Evaluate(SpellEcho(), GemRoi.Path.VaalOnly, Costs);
        var hi = optimistic.Evaluate(SpellEcho(), GemRoi.Path.VaalOnly, Costs);
        Assert.True(hi.ExpectedRevenue > lo.ExpectedRevenue);
    }

    [Fact]
    public void OddsMustSumToOne()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new GemRoi(new GemRoi.CorruptionOdds(0.5, 0.5, 0.5, 0.5)));
        Assert.Contains("sum to 1.0", ex.Message);
    }

    [Fact]
    public void DefaultOddsCarryTheirProvenance()
    {
        // These numbers could not be verified; the app must not present them as fact.
        Assert.True(GemRoi.CorruptionOdds.Default.IsNormalised);
        Assert.Contains("Unverified", GemRoi.CorruptionOdds.Provenance);
    }

    [Fact]
    public void DeLevelledBranchFallsBackToTheCheapestCorruptedPrice()
    {
        // Nobody lists a corrupted 19/20 -- it is near worthless. Requiring that price
        // made EVERY vaal row incomplete, so the entire path silently produced nothing.
        var noNineteen = new GemRoi.GemPrices("No 19",
            new Dictionary<(int, int, bool), double>
            {
                [(1, 0, false)] = 0.5,
                [(20, 20, false)] = 30.0,
                [(20, 20, true)] = 28.0,
                [(21, 20, true)] = 52.0,
                [(20, 23, true)] = 40.0,
                [(20, 0, true)] = 9.0,   // cheapest corrupted -> the fallback
            });

        var r = new GemRoi().Evaluate(noNineteen, GemRoi.Path.VaalOnly, Costs);

        Assert.True(r.IsComplete);   // the row survives
        Assert.Equal(0.25 * (28.0 + 52.0 + 9.0 + 40.0), r.ExpectedRevenue, 2);
    }

    [Fact]
    public void VaalIsIncompleteWhenTheGemHasNoCorruptedMarketAtAll()
    {
        var cleanOnly = new GemRoi.GemPrices("Clean only",
            new Dictionary<(int, int, bool), double>
            {
                [(1, 0, false)] = 0.5,
                [(20, 20, false)] = 30.0,
            });
        var r = new GemRoi().Evaluate(cleanOnly, GemRoi.Path.VaalOnly, Costs);
        Assert.False(r.IsComplete);
        Assert.Contains(r.Missing, m => m.Contains("no fallback"));
    }

    [Fact]
    public void MissingPricesAreReportedNotSilentlyZero()
    {
        // A thin market must produce an incomplete row, not a fake bargain.
        var sparse = new GemRoi.GemPrices("Obscure Gem",
            new Dictionary<(int, int, bool), double> { [(1, 0, false)] = 0.5 });

        var r = new GemRoi().Evaluate(sparse, GemRoi.Path.LevelAndQuality, Costs);
        Assert.False(r.IsComplete);
        Assert.Contains("20/20", r.Missing);
    }

    [Fact]
    public void EvaluateAllCoversEveryRequestedPath()
    {
        var all = new GemRoi().EvaluateAll(SpellEcho(), Costs, Enum.GetValues<GemRoi.Path>());
        Assert.Equal(4, all.Count);
        Assert.All(all, r => Assert.True(r.IsComplete));
    }

    [Fact]
    public void SortingMustUseNumbersNotFormattedText()
    {
        // The bug this guards: a grid sorting the display string ranks "99%" above
        // "970%", because the compare hits '9' vs '7' at the second character. That
        // silently buries the best rows mid-table, which is worse than an obvious error.
        static GemRoi.Result R(double buy, double revenue) =>
            new("g", GemRoi.Path.LevelOnly, buy, 0, revenue, Array.Empty<string>());

        var nearlyDouble = R(buy: 1.0, revenue: 1.99);   //  99% RoI
        var tenfold = R(buy: 1.0, revenue: 10.7);        // 970% RoI

        // the formatted text really is misordered -- this is the trap, not a strawman
        Assert.Equal("99%", nearlyDouble.RoiText);
        Assert.Equal("970%", tenfold.RoiText);
        Assert.True(string.CompareOrdinal(nearlyDouble.RoiText, tenfold.RoiText) > 0);

        // ...while the numeric value orders correctly
        Assert.True(tenfold.RoiValue > nearlyDouble.RoiValue);

        var sorted = new[] { nearlyDouble, tenfold }.OrderByDescending(r => r.RoiValue).ToList();
        Assert.Equal(tenfold, sorted[0]);
    }

    [Fact]
    public void NullRoiSortsBelowEveryRealRatio()
    {
        var free = new GemRoi.Result("g", GemRoi.Path.LevelOnly, 0, 0, 5, Array.Empty<string>());
        var loss = new GemRoi.Result("g", GemRoi.Path.LevelOnly, 100, 0, 1, Array.Empty<string>());
        Assert.Null(free.Roi);
        Assert.Equal("-", free.RoiText);
        Assert.True(loss.RoiValue > free.RoiValue);
    }

    [Fact]
    public void CurrencyTextShowsADashRatherThanZero()
    {
        var r = new GemRoi().Evaluate(SpellEcho(), GemRoi.Path.LevelOnly, Costs);
        Assert.Equal("-", r.CurrencyText);
        Assert.Equal(0, r.CurrencyCost);
    }

    [Fact]
    public void ProfitTextCarriesItsSign()
    {
        var gain = new GemRoi.Result("g", GemRoi.Path.LevelOnly, 1, 0, 5, Array.Empty<string>());
        var loss = new GemRoi.Result("g", GemRoi.Path.LevelOnly, 5, 0, 1, Array.Empty<string>());
        Assert.StartsWith("+", gain.ProfitText);
        Assert.StartsWith("-", loss.ProfitText);
    }

    [Fact]
    public void RoiIsNullWhenNothingWasSpent()
    {
        var free = new GemRoi.GemPrices("Free",
            new Dictionary<(int, int, bool), double>
            {
                [(1, 0, false)] = 0.0,
                [(20, 0, false)] = 5.0,
            });
        var r = new GemRoi().Evaluate(free, GemRoi.Path.LevelOnly, new GemRoi.Costs(1, 1));
        Assert.Null(r.Roi);
        Assert.Equal(5.0, r.Profit, 2);
    }
}

public class GemCatalogTests
{
    private static GemCatalog Catalog() => new(new[]
    {
        new GemCatalog.Gem("Spell Echo Support", "Spell Echo", true, 20, true, ""),
        new GemCatalog.Gem("Vaal Spark", "Vaal Spark", false, 20, false, "vaal"),
        new GemCatalog.Gem("Empower Support", "Empower", true, 3, false, "exceptional"),
        new GemCatalog.Gem("Awakened Spell Echo Support", "Awakened Spell Echo", true, 5, false, "awakened"),
        new GemCatalog.Gem("Fireball", "Fireball", false, 20, true, ""),
    });

    [Theory]
    [InlineData("Spell Echo Support", true)]
    [InlineData("Fireball", true)]
    [InlineData("Vaal Spark", false)]          // drop/corrupt only
    [InlineData("Empower Support", false)]     // exceptional, drop only
    [InlineData("Awakened Spell Echo Support", false)]
    public void KnowsWhatCanBeBought(string name, bool vendor)
    {
        Assert.Equal(vendor, Catalog().IsVendorBuyable(name));
    }

    [Fact]
    public void JoinsAcrossTheSupportSuffixMismatch()
    {
        // PoB says "Spell Echo", poe.watch says "Spell Echo Support".
        var c = Catalog();
        Assert.NotNull(c.Find("Spell Echo"));
        Assert.NotNull(c.Find("Spell Echo Support"));
        Assert.Equal(c.Find("Spell Echo")!.Name, c.Find("Spell Echo Support")!.Name);
    }

    [Fact]
    public void UnknownGemsAreNotAssumedBuyable()
    {
        // The PoB clone lags the live game -- 63 live gem names were absent from it.
        // Defaulting unknowns to buyable would invent a cheap entry price.
        var c = Catalog();
        Assert.Null(c.Find("Chain Hook of Angling"));
        Assert.False(c.IsVendorBuyable("Chain Hook of Angling"));
    }

    [Fact]
    public void VendorGemsExcludeEveryDropOnlyCategory()
    {
        var vendor = Catalog().VendorGems.Select(g => g.Name).ToList();
        Assert.Equal(2, vendor.Count);
        Assert.DoesNotContain(vendor, n => n.StartsWith("Vaal ") || n.StartsWith("Awakened "));
    }

    [Fact]
    public void ShippedCatalogueLoadsAndLooksSane()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "assets", "gem-index.json");
        Assert.True(File.Exists(path), $"asset missing at {path}");

        var c = GemCatalog.Load(path);
        Assert.True(c.Count > 700, $"only {c.Count} gems");
        Assert.True(c.IsVendorBuyable("Spell Echo Support"));
        Assert.False(c.IsVendorBuyable("Vaal Spark"));
        Assert.False(c.IsVendorBuyable("Empower Support"));
        // exceptional gems cap well below 20; a hard-coded 20 would be wrong for them
        Assert.Equal(3, c.Find("Empower Support")!.MaxLevel);

        // Transfigured gems come from the Wildwood, not a vendor. Before this was
        // handled they dominated the profit table with fictional entry prices.
        Assert.False(c.IsVendorBuyable("Bladefall of Trarthus"));
        Assert.Equal("transfigured", c.Find("Bladefall of Trarthus")!.Why);

        // ...but a name match on " of " would have wrongly excluded these.
        Assert.True(c.IsVendorBuyable("Purity of Elements"));
        Assert.True(c.IsVendorBuyable("Rain of Arrows"));
        Assert.True(c.IsVendorBuyable("Herald of Thunder"));
    }
}
