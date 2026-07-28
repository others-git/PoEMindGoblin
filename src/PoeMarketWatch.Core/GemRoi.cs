namespace PoeMarketWatch.Core;

/// <summary>
/// Return-on-investment for levelling vendor gems.
///
/// The trade is: buy a gem from a vendor at level 1 / quality 0 for approximately nothing,
/// put it in an offhand weapon while you play, and sell it levelled. Optionally spend
/// Gemcutter's Prisms for quality, and optionally gamble a Vaal Orb for level 21.
///
/// Only gems you can actually BUY are worth modelling. Vaal, Awakened and exceptional
/// (Empower/Enlighten/Enhance) gems are drop-only, so there is no cheap entry and any
/// "profit" computed for them is fiction -- see <see cref="GemCatalog"/>.
/// </summary>
public sealed class GemRoi
{
    /// <summary>
    /// Outcome distribution for a Vaal Orb on a skill gem.
    ///
    /// IMPORTANT: these numbers are NOT verified against a primary source. The PoE1 wiki
    /// (Anubis-gated), the Fandom mirror (402) and poedb (no gem section) were all
    /// unreachable, and search results were polluted with PoE2 mechanics, which differ.
    /// They are exposed as configuration rather than buried as constants precisely
    /// because the entire vaal EV rests on them -- correct them in one place and every
    /// number updates. Treat vaal-path results as indicative until confirmed.
    /// </summary>
    public sealed record CorruptionOdds(
        double NoChange = 0.25,
        double LevelUp = 0.25,
        double LevelDown = 0.25,
        double QualityChange = 0.25)
    {
        public bool IsNormalised => Math.Abs(Total - 1.0) < 0.0001;
        public double Total => NoChange + LevelUp + LevelDown + QualityChange;

        /// <summary>Widely-cited default. Unverified -- see the remarks on the type.</summary>
        public static CorruptionOdds Default { get; } = new();

        public const string Provenance =
            "Unverified: primary sources were unreachable (wiki gated, Fandom 402, poedb "
            + "lacks a gem section) and search results described PoE2, which differs. "
            + "Adjust in Settings if you have better numbers.";
    }

    public sealed record Costs(
        double GemcutterChaos,
        double VaalOrbChaos,
        double VendorGemChaos = 0.0);

    public enum Path
    {
        /// <summary>Buy 1/0, level to 20. No currency spent beyond the gem.</summary>
        LevelOnly,
        /// <summary>Buy 1/0, level to 20, spend 20 GCP for quality 20.</summary>
        LevelAndQuality,
        /// <summary>Buy an existing 20/20 and gamble a Vaal Orb on it.</summary>
        VaalOnly,
        /// <summary>Buy 1/0, level, quality, then gamble. The full pipeline.</summary>
        FullChain,
    }

    /// <summary>Prices for one gem, keyed by (level, quality, corrupted).</summary>
    public sealed record GemPrices(string Name, IReadOnlyDictionary<(int Level, int Quality, bool Corrupted), double> Mean)
    {
        public double? At(int level, int quality, bool corrupted) =>
            Mean.TryGetValue((level, quality, corrupted), out var v) ? v : null;
    }

    public sealed record Result(
        string Gem,
        Path Path,
        double BuyCost,
        double CurrencyCost,
        double ExpectedRevenue,
        IReadOnlyList<string> Missing)
    {
        public double TotalCost => BuyCost + CurrencyCost;
        public double Profit => ExpectedRevenue - TotalCost;

        /// <summary>Profit as a multiple of outlay. Null when nothing was spent.</summary>
        public double? Roi => TotalCost > 0 ? Profit / TotalCost : null;

        /// <summary>False when a price the maths needs was absent, so the row is not trustworthy.</summary>
        public bool IsComplete => Missing.Count == 0;

        // ---- display ----------------------------------------------------------
        // Formatted text AND the raw number behind it. A grid must sort on the number:
        // sorting the formatted text puts "99%" above "970%", because a string compare
        // hits '9' vs '7' at the second character and stops. That silently buries the
        // best rows in the middle of the table, which is worse than an obvious error.
        public string BuyText => $"{BuyCost:0.#}c";
        public string CurrencyText => CurrencyCost > 0 ? $"{CurrencyCost:0.#}c" : "-";
        public string RevenueText => $"{ExpectedRevenue:0.#}c";
        public string ProfitText => $"{Profit:+0.#;-0.#;0}c";
        public string RoiText => Roi is { } r ? r.ToString("P0") : "-";

        /// <summary>Null RoI (nothing was spent) sorts below every real ratio.</summary>
        public double RoiValue => Roi ?? double.NegativeInfinity;
    }

    private const int GcpPerTwentyQuality = 20;

    private readonly CorruptionOdds _odds;

    public GemRoi(CorruptionOdds? odds = null)
    {
        _odds = odds ?? CorruptionOdds.Default;
        if (!_odds.IsNormalised)
            throw new ArgumentException(
                $"corruption odds must sum to 1.0, got {_odds.Total:0.###}", nameof(odds));
    }

    public Result Evaluate(GemPrices prices, Path path, Costs costs, int maxLevel = 20)
    {
        var missing = new List<string>();

        double Need(int level, int quality, bool corrupted, string label)
        {
            var p = prices.At(level, quality, corrupted);
            if (p is null) missing.Add(label);
            return p ?? 0;
        }

        return path switch
        {
            Path.LevelOnly => Build(
                buy: Need(1, 0, false, "1/0"),
                currency: 0,
                revenue: Need(maxLevel, 0, false, $"{maxLevel}/0")),

            Path.LevelAndQuality => Build(
                buy: Need(1, 0, false, "1/0"),
                currency: costs.GemcutterChaos * GcpPerTwentyQuality,
                revenue: Need(maxLevel, 20, false, $"{maxLevel}/20")),

            Path.VaalOnly => Build(
                buy: Need(maxLevel, 20, false, $"{maxLevel}/20"),
                currency: costs.VaalOrbChaos,
                revenue: VaalExpectedValue(prices, maxLevel, missing)),

            Path.FullChain => Build(
                buy: Need(1, 0, false, "1/0"),
                currency: costs.GemcutterChaos * GcpPerTwentyQuality + costs.VaalOrbChaos,
                revenue: VaalExpectedValue(prices, maxLevel, missing)),

            _ => throw new ArgumentOutOfRangeException(nameof(path)),
        };

        Result Build(double buy, double currency, double revenue) =>
            new(prices.Name, path, buy + costs.VendorGemChaos, currency, revenue, missing);
    }

    /// <summary>
    /// Expected sale value of vaaling a level-20 quality-20 gem.
    ///
    /// Every branch lands on a CORRUPTED gem, which is a different market to the clean
    /// one -- a corrupted 20/20 is generally worth less than a clean 20/20, so pricing
    /// the "no change" branch off the clean listing would silently inflate every result.
    /// </summary>
    private double VaalExpectedValue(GemPrices prices, int maxLevel, List<string> missing)
    {
        // Required branches: without these the EV is meaningless.
        double Branch(int level, int quality, string label)
        {
            var p = prices.At(level, quality, true);
            if (p is null) missing.Add($"{label} (corrupted)");
            return p ?? 0;
        }

        var noChange = Branch(maxLevel, 20, $"{maxLevel}/20");
        var levelUp = Branch(maxLevel + 1, 20, $"{maxLevel + 1}/20");
        // The quality branch lands somewhere in a range; q23 is the tradeable landmark
        // and the one with real listing volume, so it stands in for the branch.
        var qualityChange = Branch(maxLevel, 23, $"{maxLevel}/23");

        // A de-levelled gem is the one outcome nobody bothers listing -- a corrupted
        // 19/20 has almost no market, so demanding a price for it made EVERY vaal row
        // incomplete and the whole path silently produced nothing. Fall back to the
        // cheapest corrupted state this gem has, which is the right shape for a
        // near-worthless result, and only give up if the gem has no corrupted market
        // at all.
        var levelDown = prices.At(maxLevel - 1, 20, true) ?? CheapestCorrupted(prices);
        if (levelDown is null) missing.Add($"{maxLevel - 1}/20 (corrupted, no fallback)");

        return _odds.NoChange * noChange
             + _odds.LevelUp * levelUp
             + _odds.LevelDown * (levelDown ?? 0)
             + _odds.QualityChange * qualityChange;
    }

    private static double? CheapestCorrupted(GemPrices prices)
    {
        double? cheapest = null;
        foreach (var ((_, _, corrupted), price) in prices.Mean)
        {
            if (!corrupted) continue;
            if (cheapest is null || price < cheapest) cheapest = price;
        }
        return cheapest;
    }

    public IReadOnlyList<Result> EvaluateAll(
        GemPrices prices, Costs costs, IEnumerable<Path> paths, int maxLevel = 20) =>
        paths.Select(p => Evaluate(prices, p, costs, maxLevel)).ToList();
}
