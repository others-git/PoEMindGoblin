using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MindGoblin.Core.Voyage;

/// <summary>
/// One scoring rule: a pattern matched against modifier text, and what a match is worth.
///
/// Patterns are regular expressions over the modifier line, so a rule can capture the
/// NUMBER in the text rather than treating every match as equal -- "8 additional packs"
/// should not score the same as "2 additional packs". Group 1, if it is numeric, is
/// multiplied by <see cref="Weight"/>; otherwise a match is worth <see cref="Weight"/>
/// flat.
/// </summary>
/// <summary>
/// What lives in a voyage area, as far as anyone knows.
///
/// ALL ESTIMATES, and deliberately named ones. No published numbers exist -- GGG ships
/// no drop tables, poedb carries no room monster data, and the community guides are
/// qualitative (checked July 2026). These are anchored to in-game observation: roughly
/// thirty packs a tileset, a third of them rare-led, an added pack rolling rare about a
/// third of the time, and the drop-sulphur figurine paying out on the order of a
/// thousand raw sulphur across a square's rares. Every constant is a knob so a real
/// measurement slots in without touching the model -- and everything DERIVED from them
/// (the per-pack rare fraction, the sulphur square's price) moves together.
/// </summary>
public static class AreaPopulation
{
    /// <summary>Monster packs in one tileset. Observed order of magnitude.</summary>
    public const double PacksPerArea = 30;

    /// <summary>Rare monsters in one tileset, before any modifiers.</summary>
    public const double RaresPerArea = 10;

    /// <summary>
    /// Chance an ADDED pack ("Adjacent Areas contain 12 additional packs of Crabs")
    /// arrives rare-led. LOW on observation: the thematic packs are predominantly
    /// normal-and-magic filler, and a rare leader is the exception. The first guess
    /// (0.3) made twelve crab packs outbid "+30% increased Rare Monsters" for a seat
    /// beside a per-rare payout square -- 3.6 implied rares against 3.0 -- which the
    /// game plainly contradicts; at 0.1 the percent roll outranks any pack gift in
    /// the tables, as it should.
    /// </summary>
    public const double RareChancePerAddedPack = 0.1;

    /// <summary>
    /// Quantity granted for the REST OF THE VOYAGE by collecting one Golden Lantern,
    /// as a fraction. Estimate: 3.29.0b says they "grant increased Quantity of items
    /// dropped by slain monsters" and publishes no number. This is what makes route
    /// ORDER care about lanterns -- a lantern square multiplies every square visited
    /// after it, so the router grabs them first when the maths says so.
    /// </summary>
    public const double QuantityPerGoldenLantern = 0.05;

    /// <summary>
    /// Rooms observed to run rare-dense, as a fraction of the base rares. One entry so
    /// far, from a single run: Brine King's Domain held an exceptional number of rares
    /// (a named-boss arena, plausibly with a guard retinue). No community or poedb data
    /// exists on rooms (re-checked 2026-07-30), so observations go straight in here --
    /// a chart opening one of these rooms is denser, which multiplies any per-rare
    /// payout square it stands on.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, double> RoomRareDensity =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            // Reordered by continued field play (2026-07-31): Sea Pillars' always-rare
            // starfish packs out-earn Brine King's Domain, the early "hard to say"
            // resolved with more runs. Pillars takes the top seat; Brine stays strong.
            ["Sea Pillars"] = 1.0,

            // "Exceptional" was the original observation and it holds -- just no longer
            // the best. Still comfortably above every other measured tileset.
            ["Brine King's Domain"] = 0.8,

            // Abyss rares throughout; below both of the above.
            ["Pelagic Abyss"] = 0.6,

            // MEASURED NEUTRAL, kept at zero so nobody re-tests them: Diving Shoals ran
            // unremarkable, and Sunken Totems adds unique mini-bosses (Utula, Ahuana --
            // Trial of the Ancestors names) which are uniques, not rares, so they feed
            // no per-rare payout; nothing notable dropped either.
            ["Diving Shoals"] = 0.0,
            ["Sunken Totems"] = 0.0,

            // Measured: no extra rares -- Anchorfield's wealth is CHESTS. Tons of
            // Sunken Loot observed, which is why the containers and quantity profiles
            // carry an "Area: Anchorfield" rule instead of this map.
            ["Anchorfield"] = 0.0,

            // Measured: a LOOT room, not a rare room -- piles of gold and treasure
            // clams (the clams only seem to drop gold). The gold profile carries its
            // Area rule.
            ["Clam-infested Shelf"] = 0.0,

            // SIGHTED, not settled -- zeros for RARES with open questions attached.
            // Hazardous Depths: a Rotmother-themed loot box, possibly unique-item
            // related. Kishara's Rest: possibly a boss fight. Neither showed extra
            // rares, BUT both charts trade expensive on the player market -- aggregated
            // community knowledge that there is real value here even if one run could
            // not name it. The market signal lives in Area rules (quantity, and uniques
            // for Depths), not in this map; promote here the moment a run shows rares.
            ["Hazardous Depths"] = 0.0,

            // 3.29.1 added this chart variant; unmeasured -- field-log a number when run.
            ["Eldritch Depths"] = 0.0,
            ["Kishara's Rest"] = 0.0,
        };

    /// <summary>The room bonus for a chart, wherever the room name landed in the parse
    /// (the hover text carries it as a bare line, so it is usually in Modifiers).</summary>
    public static double RoomRareBonus(Chart chart)
    {
        if (chart.AreaName is { } area && RoomRareDensity.TryGetValue(area.Trim(), out var a))
            return a;
        foreach (var line in chart.Modifiers)
            if (RoomRareDensity.TryGetValue(line.Trim(), out var b)) return b;
        return 0;
    }

    /// <summary>Raw sulphur one rare drops under the drop-sulphur figurine.
    /// OBSERVED in game at 250-350 per rare across several sightings -- the
    /// best-sourced constant in this model, where the rest are still estimates.</summary>
    public const double SulphurPerRareDrop = 300;

    /// <summary>
    /// What the Filthscrabble boss pays, in raw sulphur. Community-sourced
    /// (Milkybk_'s strategy video, encoded by the one-more-map solver as "a
    /// ~4,000-sulphur boss") -- unverified by us, so trim it when a run says
    /// otherwise.
    /// </summary>
    public const double SulphurPerFilthscrabble = 4000;

    /// <summary>
    /// Rares one additional Strongbox yields when rolled before opening.
    /// Community numbers (Milky/Zac): "Stream of Monsters" +4 and "of Rarity"
    /// +3 make a box worth ~7 rares. Priced below the ceiling because not every
    /// box rolls both -- raised from 3 in 3.29.1, which let DREDGED currency
    /// roll boxes mid-voyage, so the "did I bring currency" discount shrank.
    /// </summary>
    public const double RaresPerRolledStrongbox = 4;
}

public sealed class VoyageRule
{
    public string Pattern { get; set; } = "";
    public double Weight { get; set; } = 1;

    /// <summary>
    /// This rule's captured number is a PERCENTAGE OF THE WHOLE BOARD, not a flat roll.
    ///
    /// "20% increased Dead Man's Sulphur found in all Voyage Areas" multiplies the
    /// sulphur of every area in the voyage, so what it is worth is 20% of the board's
    /// total -- on a board carrying ~850 flat sulphur that is ~170, not the token 40 a
    /// flat weight gave it. Scored flat, the best sulphur chart in a real panel ranked
    /// seventeenth and was left behind. The session estimates the board total from the
    /// top boardsize chart scores and multiplies; with Weight 1 the captured number is
    /// an exact fraction of that estimate.
    /// </summary>
    public bool ScalesWithBoard { get; set; }

    public string? Comment { get; set; }

    [JsonIgnore]
    private Regex? _compiled;

    [JsonIgnore]
    public Regex Compiled => _compiled ??=
        new Regex(Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Score this rule against one line of modifier text. Zero when it misses.</summary>
    public double Score(string text)
    {
        if (string.IsNullOrWhiteSpace(Pattern)) return 0;
        var m = Compiled.Match(text);
        if (!m.Success) return 0;

        // A captured number scales the weight, so "8 additional packs" beats "2". A
        // pattern may offer the number as ONE branch of an alternation -- the shipped
        // count rules read "(?:(\d+)|an)", because the game writes the singular when a
        // roll is 1 ("an additional cage") -- and then an unfilled group falls through
        // to the flat weight, which prices the singular exactly as a roll of 1.
        if (m.Groups.Count > 1 && double.TryParse(m.Groups[1].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var n))
            return n * Weight;
        return Weight;
    }
}

/// <summary>
/// A named optimisation target -- "sulphur", "pack size", "quantity".
///
/// There is deliberately no single "best" board. What a good layout looks like depends
/// entirely on what you are farming, so the objective is a profile you pick rather than
/// something baked into the solver.
/// </summary>
public sealed class VoyageProfile
{
    public string Name { get; set; } = "unnamed";
    public string? Description { get; set; }
    public List<VoyageRule> Rules { get; set; } = new();

    /// <summary>
    /// Multiplier applied to a board modifier's score.
    ///
    /// Board modifiers buff ADJACENT squares, so their value is realised through whatever
    /// chart sits next to them. Kept separate from chart scoring so you can tune how much
    /// the adjacency is worth without rewriting every rule.
    /// </summary>
    public double BoardModifierWeight { get; set; } = 1.0;

    /// <summary>Weight applied to a chart's area level, for when higher tier is the goal.</summary>
    public double AreaLevelWeight { get; set; }

    /// <summary>
    /// A flat value for spending a chart at all, added to every chart's score.
    ///
    /// Exists for INVERTED objectives. The "dump" profile weights everything valuable
    /// NEGATIVELY so the least valuable charts win -- but a solver maximising a sum of
    /// negatives would rather leave squares empty than fill them with junk, and the whole
    /// point of a dump voyage is to spend nine charts. A base large enough to keep every
    /// chart net positive makes the fullest board always worth more, and the junk still
    /// sorts to the top within it.
    /// </summary>
    public double ChartBaseValue { get; set; }

    /// <summary>
    /// How strongly per-monster border payouts scale with the tile they land on.
    ///
    /// A border modifier like "Rare Monsters in adjacent Areas drop # additional Divine
    /// Orbs" pays PER MONSTER, so its real value multiplies with how many monsters the
    /// tile has -- and the tile's Monster Pack Size is a stat the chart states. Scored
    /// flat, the payout square is worth the same +120 under a rare-packed chart as under
    /// a dead one, and since a full board occupies every square either way, the border
    /// mods then decide NOTHING about placement: their sum is a constant over any full
    /// layout. This factor is what lets a payout square pull the monster-dense chart
    /// onto itself: the payout is multiplied by (1 + synergy x PackSize/100).
    ///
    /// 1.0 says "trust the tile's pack size at face value"; 0 switches the interaction
    /// off and returns to flat scoring. What it deliberately does NOT model: neighbours'
    /// adjacent monster mods boosting the payout tile (a three-way interaction the
    /// pairwise solver cannot see), and global monster multipliers (which lift every
    /// square equally and so cannot steer placement).
    /// </summary>
    public double MonsterPayoutSynergy { get; set; } = 1.0;

    /// <summary>
    /// Is this border modifier a per-monster payout? Matches the lines that START with a
    /// monster noun -- "Rare Monsters ... drop/have ...", "Magic Monsters ... have an
    /// additional modifier", "Monsters ... are at least Magic" -- and deliberately not
    /// the density lines ("#% increased number of Rare Monsters"), which ADD monsters
    /// rather than pay per monster and stay additive.
    /// </summary>
    public static bool IsPerMonsterPayout(string description) =>
        PayoutChannelOf(description) != PayoutChannel.None;

    /// <summary>
    /// Which population a per-monster payout scales with. One channel was wrong in both
    /// directions: rare-dense rooms (Brine, Pelagic) were multiplied into at-least-Magic
    /// tiles where rares gain nothing -- they are already above magic -- while pack
    /// gifts, which the upgrade converts wholesale, counted at a token rate.
    /// </summary>
    public enum PayoutChannel
    {
        /// <summary>Not a per-monster payout at all.</summary>
        None,

        /// <summary>Pays per RARE -- the sulphur and orb drops. Scales with rare density.</summary>
        Rares,

        /// <summary>Pays across the general population -- "at least Magic" upgrades,
        /// magic-modifier buffs. Scales with pack density.</summary>
        Population,
    }

    public static PayoutChannel PayoutChannelOf(string description) =>
        PerRare.IsMatch(description) ? PayoutChannel.Rares
        : PerPopulation.IsMatch(description) ? PayoutChannel.Population
        : PayoutChannel.None;

    private static readonly Regex PerRare =
        new(@"^\s*Rare\s+Monsters\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    // Anchored to the two payout WORDINGS, not just the "Monsters" prefix: half the
    // difficulty table starts with "Monsters" too ("Monsters cannot be Stunned"), and a
    // classifier whose word three scoring paths trust must not call a curse a payout.
    private static readonly Regex PerPopulation =
        new(@"^\s*(?:Magic\s+)?Monsters\b.*(?:at least Magic|additional modifier)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// How much RARE density a modifier line adds, as a fraction of a tile's rares.
    /// Per-rare payouts multiply with this: increased-rare percent rolls convert
    /// directly, imprisoned (essence) monsters are whole rares, and added packs convert
    /// through their rare chance. Pack-size and magic-count lines belong to
    /// <see cref="PackDensityOf"/>, the population channel.
    /// </summary>
    public static double MonsterDensityOf(string line)
    {
        var density = 0.0;
        var count = RareCountPct.Match(line);
        if (count.Success) density += double.Parse(count.Groups[1].Value) / 100;
        var packs = AdditionalPacks.Match(line);
        if (packs.Success)
        {
            // n added packs, each with a chance to arrive rare-led, as a fraction of
            // the tileset's base rares.
            var n = packs.Groups[1].Success ? double.Parse(packs.Groups[1].Value) : 1;
            density += n * AreaPopulation.RareChancePerAddedPack / AreaPopulation.RaresPerArea;
        }

        var boxes = AdditionalStrongboxes.Match(line);
        if (boxes.Success)
        {
            // A box is rares-on-demand: rolled before opening it pours monsters out,
            // and every one of them collects the square's per-rare payouts. This is
            // the community's Divine-border feeder -- "+5 Strongboxes" beside a
            // Divine square is worth more than any percent roll.
            var n = boxes.Groups[1].Success ? double.Parse(boxes.Groups[1].Value) : 1;
            density += n * AreaPopulation.RaresPerRolledStrongbox / AreaPopulation.RaresPerArea;
        }

        var starfish = AdditionalStarfish.Match(line);
        if (starfish.Success)
        {
            // Field-confirmed mechanism (2026-07-31): each starfish PACK carries
            // exactly one rare, and the mod's count is the pack count -- so the
            // number maps to rares one for one.
            var n = starfish.Groups[1].Success ? double.Parse(starfish.Groups[1].Value) : 1;
            density += n / AreaPopulation.RaresPerArea;
        }

        var imprisoned = ImprisonedMonsters.Match(line);
        if (imprisoned.Success)
        {
            // OBSERVED in a run: imprisoned (essence) monsters ARE rares, and they DO
            // drop the per-rare border payouts -- sulphur, orbs, all of it. Each one is
            // a whole rare, so an imprisoned gift outranks a percent roll of the same
            // headline size: 4 imprisoned = 0.4 of the base rares, against 0.3 from
            // "+30% increased".
            var n = imprisoned.Groups[1].Success ? double.Parse(imprisoned.Groups[1].Value) : 1;
            density += n / AreaPopulation.RaresPerArea;
        }
        return density;
    }

    /// <summary>
    /// How much POPULATION density a line adds, as a fraction of a tile's packs. This
    /// is what "at least Magic" and the other whole-population payouts multiply with:
    /// pack size directly, and every added pack as one of the tileset's packs -- added
    /// packs get upgraded wholesale, which is what made ignoring them wrong.
    /// </summary>
    public static double PackDensityOf(string line)
    {
        var density = 0.0;
        var pack = PackSizePct.Match(line);
        if (pack.Success) density += double.Parse(pack.Groups[1].Value) / 100;
        var packs = AdditionalPacks.Match(line);
        if (packs.Success)
        {
            var n = packs.Groups[1].Success ? double.Parse(packs.Groups[1].Value) : 1;
            density += n / AreaPopulation.PacksPerArea;
        }
        return density;
    }

    /// <summary>
    /// A TILE's rare density from what the chart itself states: pack size (rares spawn
    /// out of packs) and the tileset's measured room bonus. NOTHING ELSE, validated
    /// against the poedb mod pool: every rare-adding chart line is scoped "in adjacent
    /// Areas" or "in all Voyage Areas" -- self-scope rare adders do not exist. An
    /// earlier version summed the chart's own lines here, which could only ever catch
    /// GLOBAL lines and so credited a board-wide buff to the carrier's own tile --
    /// exactly the conflation this comment exists to prevent repeating. Adjacent
    /// gifts pay their neighbours through the channel machinery; global lines lift
    /// every tile equally and must steer placement not at all.
    /// </summary>
    public static double ChartRareDensity(Chart chart) =>
        chart.MonsterPackSize / 100 + AreaPopulation.RoomRareBonus(chart);

    /// <summary>The population twin. Same corpus rule: pack size is the only
    /// self-scope population statement a chart makes.</summary>
    public static double ChartPackDensity(Chart chart) =>
        chart.MonsterPackSize / 100;

    private static readonly Regex PackSizePct =
        new(@"(\d+)%\s+increased Pack Size", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex RareCountPct =
        new(@"(\d+)%\s+increased number of Rare Monsters",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AdditionalPacks =
        new(@"(?:(\d+)|an)\s+additional packs? of",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ImprisonedMonsters =
        new(@"(?:(\d+)|an)\s+additional Imprisoned Monsters?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    /// <summary>
    /// Adjacent/board lines that put OPENABLES into an area -- bottles, boxes, barrels,
    /// cages, treasures. Their loot multiplies with the receiving tile's quantity, so
    /// their value rides the quantity channel rather than scoring flat. Lanterns are
    /// deliberately absent (their value IS quantity, not loot to multiply) and starfish
    /// are monsters, priced on the rare channel.
    /// </summary>
    public static bool IsContainerGift(string line) => ContainerGift.IsMatch(line);

    private static readonly Regex ContainerGift =
        new(@"additional (?:\w+'s )?(?:Strongbox(?:es)?|Messages? in (?:a )?Bottles?|"
            + @"Clusters? of Barrels|cages? of Tormented Spirits|Treasure)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>How much QUANTITY a line grants, as a fraction -- what container
    /// payouts multiply with. Both spellings: GGG writes 'Qauntity' in the globals.</summary>
    public static double QuantityDensityOf(string line)
    {
        var m = QuantityPct.Match(line);
        return m.Success ? double.Parse(m.Groups[1].Value) / 100 : 0;
    }

    private static readonly Regex QuantityPct =
        new(@"(\d+)%\s+increased Qu?au?ntity of Items",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex AdditionalStrongboxes =
        new(@"(?:(\d+)|an)\s+additional (?:\w+'s )?Strongbox(?:es)?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AdditionalStarfish =
        new(@"(?:(\d+)|an)\s+additional Giant Starfish",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// How many charts to spend, at most. Null means fill the board.
    ///
    /// The game says "place UP TO nine Charts", and "your very first Voyage will require
    /// only four" -- so a plan for fewer is a real thing to ask for, not a degenerate one.
    /// </summary>
    public int? MaxCharts { get; set; }

    /// <summary>
    /// Charged per square left off the main route.
    ///
    /// Charged ON TOP of forfeiting the chart itself. A square cut off from the route is
    /// never visited, so the solver already voids whatever is placed there; this is the
    /// additional cost of having burned the slot at all. Set to 0 to charge only the
    /// forfeit, or high to forbid stranding outright.
    /// </summary>
    public double StrandedSquarePenalty { get; set; } = 40;

    public double ScoreText(IEnumerable<string> lines) =>
        lines.Sum(line => Rules.Sum(r => r.Score(line)));

    /// <summary>
    /// A chart's own value: its stats, its global Voyage Modifier and its monster mods,
    /// plus area level. The Adjacent Modifier is excluded -- see <see cref="Chart.OwnLines"/>.
    /// Without a board estimate, board-scaling rules score at face value.
    /// </summary>
    public double ScoreChart(Chart chart) => ScoreChart(chart, null);

    /// <summary>
    /// The same, with board-scaling rules worth their percentage OF the estimate: a
    /// global "N% increased X in all Voyage Areas" line multiplies everything the board
    /// produces, so its value is N% of the board's total rather than a flat N.
    /// </summary>
    public double ScoreChart(Chart chart, double? boardEstimate)
    {
        var total = chart.AreaLevel * AreaLevelWeight + ChartBaseValue;
        foreach (var line in chart.OwnLines())
            foreach (var rule in Rules)
            {
                var value = rule.Score(line);
                if (value == 0) continue;
                if (rule.ScalesWithBoard && boardEstimate is { } estimate)
                    value = value * estimate / 100;
                total += value;
            }
        return total;
    }

    /// <summary>What a chart's Adjacent Modifier is worth to ONE neighbour.</summary>
    public double ScoreAdjacent(Chart chart) =>
        string.IsNullOrEmpty(chart.AdjacentModifier) ? 0 : ScoreText([chart.AdjacentModifier!]);

}

/// <summary>
/// The rule file, watched on disk so edits apply without restarting the app.
///
/// Tuning an objective is inherently iterative -- you try a weight, look at the plan,
/// change it. Requiring a restart for every tweak would make the tool unusable for the
/// thing it exists to do.
/// </summary>
public sealed class VoyageRules : IDisposable
{
    private readonly string _path;
    private FileSystemWatcher? _watcher;
    private readonly Lock _gate = new();
    private List<VoyageProfile> _profiles = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public VoyageRules(string? path = null)
    {
        _path = path ?? DefaultPath;
        Reload();
    }

    public static string DefaultPath => SettingsFolder.FileIn("voyage-rules.json");

    public string Path_ => _path;

    /// <summary>Raised after a successful reload, so a view can refresh itself.</summary>
    public event Action? Changed;

    /// <summary>Raised when a reload fails, so bad JSON is visible instead of silent.</summary>
    public event Action<string>? Error;

    public IReadOnlyList<VoyageProfile> Profiles
    {
        get { lock (_gate) return _profiles.ToList(); }
    }

    public VoyageProfile? Find(string name) =>
        Profiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public void Reload()
    {
        try
        {
            if (!File.Exists(_path))
            {
                lock (_gate) _profiles = Defaults();
                return;
            }
            var text = File.ReadAllText(_path);
            var loaded = JsonSerializer.Deserialize<List<VoyageProfile>>(text, Options);
            lock (_gate) _profiles = loaded is { Count: > 0 } ? loaded : Defaults();
            Changed?.Invoke();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // Keep serving the last good profiles: a half-saved file mid-edit must not
            // blank the tool, but the user has to know the edit did not take.
            Error?.Invoke($"{System.IO.Path.GetFileName(_path)}: {ex.Message}");
        }
    }

    public void Save()
    {
        var dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        List<VoyageProfile> snapshot;
        lock (_gate) snapshot = _profiles.ToList();
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, Options));
        File.Move(tmp, _path, overwrite: true);
    }

    /// <summary>Write the built-in profiles to disk as a starting point to edit.</summary>
    public void WriteDefaultsIfMissing()
    {
        if (File.Exists(_path)) return;
        lock (_gate) _profiles = Defaults();
        Save();
    }

    /// <summary>Shipped profiles the file does not have, and ones whose rules have moved on.</summary>
    public sealed record DefaultsStatus(
        IReadOnlyList<string> Missing, IReadOnlyList<string> Outdated)
    {
        public bool AnythingToDo => Missing.Count > 0 || Outdated.Count > 0;
    }

    /// <summary>
    /// Compare what is on disk with what ships.
    ///
    /// The file is written once and then never touched again, which is right for
    /// something the user edits -- but it meant no shipped profile ever reached anyone
    /// who had already run the app. A whole set of new objectives, and fixes to rules
    /// that matched nothing, sat in the binary and never appeared.
    /// </summary>
    public DefaultsStatus CompareWithDefaults()
    {
        var shipped = Defaults();
        var onDisk = Profiles.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

        var missing = shipped.Where(p => !onDisk.ContainsKey(p.Name)).Select(p => p.Name).ToList();
        var outdated = shipped
            .Where(p => onDisk.TryGetValue(p.Name, out var mine) && !SameRules(mine, p))
            .Select(p => p.Name)
            .ToList();

        return new DefaultsStatus(missing, outdated);
    }

    // The profile-level knobs count as much as the rules: when strongbox gained
    // AreaLevelWeight, a comparison of rules alone told every existing file it was
    // current while it silently scored area level at zero.
    private static bool SameRules(VoyageProfile a, VoyageProfile b) =>
        Math.Abs(a.BoardModifierWeight - b.BoardModifierWeight) < 1e-9
        && Math.Abs(a.AreaLevelWeight - b.AreaLevelWeight) < 1e-9
        && Math.Abs(a.MonsterPayoutSynergy - b.MonsterPayoutSynergy) < 1e-9
        && Math.Abs(a.ChartBaseValue - b.ChartBaseValue) < 1e-9
        && Math.Abs(a.StrandedSquarePenalty - b.StrandedSquarePenalty) < 1e-9
        && a.MaxCharts == b.MaxCharts
        && a.Rules.Count == b.Rules.Count
        && a.Rules.Zip(b.Rules).All(pair => pair.First.Pattern == pair.Second.Pattern
                                            && pair.First.ScalesWithBoard == pair.Second.ScalesWithBoard
                                            && Math.Abs(pair.First.Weight - pair.Second.Weight) < 1e-9);

    /// <summary>
    /// Add shipped profiles the file does not have, leaving everything else alone.
    ///
    /// Additive on purpose: a new objective should just turn up, but a profile the user
    /// has tuned must not be overwritten because the shipped weights moved.
    /// Use <see cref="RestoreDefaults"/> to take the shipped version of everything.
    /// </summary>
    public IReadOnlyList<string> AddMissingDefaults()
    {
        var status = CompareWithDefaults();
        if (status.Missing.Count == 0) return [];

        var shipped = Defaults().ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        lock (_gate)
            foreach (var name in status.Missing)
                _profiles.Add(shipped[name]);

        Save();
        Changed?.Invoke();
        return status.Missing;
    }

    /// <summary>Replace everything with the shipped profiles, discarding local edits.</summary>
    public void RestoreDefaults()
    {
        lock (_gate) _profiles = Defaults();
        Save();
        Changed?.Invoke();
    }

    /// <summary>Watch the file so edits made in any editor apply immediately.</summary>
    public void WatchForChanges()
    {
        var dir = System.IO.Path.GetDirectoryName(_path);
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);

        _watcher = new FileSystemWatcher(dir, System.IO.Path.GetFileName(_path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        // Editors often write twice (truncate then write); a short settle avoids
        // reading a half-written file and reporting a bogus parse error.
        _watcher.Changed += (_, _) => DebouncedReload();
        _watcher.Created += (_, _) => DebouncedReload();
        _watcher.Renamed += (_, _) => DebouncedReload();
    }

    private CancellationTokenSource? _debounce;

    private void DebouncedReload()
    {
        _debounce?.Cancel();
        _debounce = new CancellationTokenSource();
        var token = _debounce.Token;
        Task.Delay(200, token).ContinueWith(t =>
        {
            if (!t.IsCanceled) Reload();
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Starting profiles. Patterns capture the number in the text so magnitude counts.
    /// These are examples to edit, not an authority on what is worth farming.
    /// </summary>
    /// <summary>
    /// The built-in profiles.
    ///
    /// Every pattern is checked against the generated mod table -- see
    /// ProfileCoverageTests, which fails if a shipped profile contains a rule that matches
    /// nothing the game can roll, or if a reward line exists that no profile scores. That
    /// turns "does this rule work?" from a guess into a build failure, which is the whole
    /// reason for pulling the table in the first place.
    ///
    /// Weights are a starting point, not a claim about the economy. They are the thing
    /// most worth editing, which is why the file is hot-reloaded.
    /// </summary>
    public static List<VoyageProfile> Defaults() =>
        // The shipped strategies (weight presets over the normalized ModCatalog),
        // compiled into the rule shape everything downstream consumes. The 700 lines
        // of hand-entangled per-profile rules that used to live here are factored
        // into ModCatalog (objective values, one entry per mod) and Strategies
        // (preference, one weight per stat) -- see VoyageStrategy.cs.
        Strategies.Shipped().Select(s => s.Compile()).ToList();

    public void Dispose()
    {
        _debounce?.Cancel();
        _watcher?.Dispose();
    }
}
