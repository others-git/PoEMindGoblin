using System.Text.RegularExpressions;

namespace MindGoblin.Core.Voyage;

/// <summary>
/// What a modifier is worth is one question; how much a plan cares is another. The old
/// rule files answered both at once -- 194 rules where the same mod carried unrelated
/// numbers per profile -- and every tuning session had to re-derive market facts.
///
/// This split (modelled on the community solver's mods/strategies design, then carried
/// further) separates them:
///   * <see cref="ModCatalog"/> -- every mod mapped ONCE to a stat and a normalized
///     unit value. Market facts, field measurements and hard-won lessons live here.
///   * <see cref="Strategy"/> -- a named WEIGHT PRESET over stats. Preference only.
/// A strategy compiles to the existing <see cref="VoyageProfile"/> shape, so the
/// solver, channels and board machinery are untouched.
/// </summary>
public enum Stat
{
    /// <summary>Tradeable currency in chaos-equivalents: orb drops, stacked decks,
    /// altars, lockers, support gems. Market-priced (poe.watch, 2026-07).</summary>
    Currency,

    /// <summary>Scarab drops. Their own stat: jackpot-shaped, worth chasing alone.</summary>
    Scarabs,

    /// <summary>Messages in Bottles: ~39c each, sellable unopened. The chase.</summary>
    Bottles,

    /// <summary>Strongboxes, named and generic. Dredged currency rolls them mid-voyage
    /// since 3.29.1, so their rare-fountain half also rides the rare channel.</summary>
    Strongboxes,

    /// <summary>The other openables: barrels, anchors, cages, fish, jellyfish, and the
    /// board's roaming bosses (Captainsbane, Brinerot) as loot events.</summary>
    Openables,

    /// <summary>Golden Lanterns and free lantern placement: quantity engines.</summary>
    Lanterns,

    /// <summary>Dead Man's Sulphur, in judged chaos-equivalents (~5c per point scale
    /// kept from the field-tuned era; sulphur is untradeable so this is a JUDGMENT,
    /// not a market fact). Per-rare drop = 300/rare x 10 rares (observed).</summary>
    Sulphur,

    /// <summary>Gold: found%, stat points, equipment conversion.</summary>
    Gold,

    /// <summary>Item Quantity, percent points. Multiplies most other stats; the
    /// container channel already does that mechanically at solve time.</summary>
    Quantity,

    /// <summary>Item Rarity, percent points.</summary>
    Rarity,

    /// <summary>Monster population: pack size percents and added packs.</summary>
    Packs,

    /// <summary>Rare-monster economy: counts, imprisoned (essence) monsters, starfish
    /// (one rare per pack, count maps 1:1 -- field-confirmed), possession, fracture,
    /// Pantheon, Atziri, wisps.</summary>
    Rares,

    /// <summary>Magic-monster economy: at-least-Magic upgrades and magic modifiers,
    /// priced across the whole population (8c-equiv x 30 packs for the upgrade).</summary>
    Magic,

    /// <summary>Unique-item conversions and fracturing.</summary>
    Uniques,

    /// <summary>Chart refunds ("chance to not be consumed").</summary>
    ChartRefunds,

    /// <summary>Experience gain. Nearly worthless to a farmer; dump burns it.</summary>
    Experience,

    /// <summary>Monster difficulty that kills runs. Catalog values are SEVERITIES
    /// (positive); the safe strategy weights this negative. Max-res is absent: 3.29.1
    /// disclosed it never functioned.</summary>
    Danger,

    /// <summary>"Cannot drop Equipment, Flasks or Tinctures": the loot-deleting trap
    /// that reads as a reward. Weighted negative by loot strategies, positive by dump
    /// (junk by definition is what a dump voyage burns).</summary>
    NoEquipmentDrops,

    /// <summary>Explicit-modifier magnitudes: multiplies a chart's whole rolled set.</summary>
    ModifierMagnitude,

    /// <summary>Player power (Soul Eater). No loot value; the star exists for it.</summary>
    PlayerPower,
}

/// <summary>
/// How a stat says what it is, to somebody who is not holding the catalog.
///
/// The enum names are identifiers -- terse, and several of them were frank shorthand
/// ("LootLock", "Meta", "Player") that meant nothing on a slider. What the panel shows is
/// a LABEL, and what it says on hover is a sentence, because a weight the user cannot
/// name is a weight they will not touch. Kept beside the enum so a new stat cannot ship
/// without both: <c>EveryStatHasALabelAndADescription</c> fails the build otherwise.
/// </summary>
public static class StatText
{
    public static string Label(Stat stat) => stat switch
    {
        Stat.Currency => "Currency",
        Stat.Scarabs => "Scarabs",
        Stat.Bottles => "Bottles",
        Stat.Strongboxes => "Strongboxes",
        Stat.Openables => "Other openables",
        Stat.Lanterns => "Golden Lanterns",
        Stat.Sulphur => "Sulphur",
        Stat.Gold => "Gold",
        Stat.Quantity => "Item Quantity",
        Stat.Rarity => "Item Rarity",
        Stat.Packs => "Monster packs",
        Stat.Rares => "Rare monsters",
        Stat.Magic => "Magic monsters",
        Stat.Uniques => "Uniques",
        Stat.ChartRefunds => "Chart refunds",
        Stat.Experience => "Experience",
        Stat.Danger => "Monster danger",
        Stat.NoEquipmentDrops => "No equipment drops",
        Stat.ModifierMagnitude => "Modifier magnitude",
        Stat.PlayerPower => "Player power",
        _ => stat.ToString(),
    };

    /// <summary>One sentence: what the stat covers, and anything about it that would
    /// surprise you. The trap stats say which way to weight them.</summary>
    public static string Describe(Stat stat) => stat switch
    {
        Stat.Currency =>
            "Tradeable orbs, stacked decks, altars and lockers, priced in chaos off poe.watch.",
        Stat.Scarabs =>
            "Scarab drops. Jackpot-shaped, so they are their own stat rather than currency.",
        Stat.Bottles =>
            "Messages in a Bottle: ~39c each and sellable UNOPENED. The chase — a hidden "
            + "voyage mod that cannot be crafted for, only revealed.",
        Stat.Strongboxes =>
            "Strongboxes, named and generic. Rolled before opening they also pour out "
            + "rares, so they feed any per-rare payout next to them.",
        Stat.Openables =>
            "The walk-up loot that is not a strongbox: barrels, treasure anchors, cages, "
            + "the Pirate's Locker, exotic fish, and the roaming bosses.",
        Stat.Lanterns =>
            "Golden Lanterns and free lantern placement. Since 3.29.0b a lantern also "
            + "grants quantity for the rest of the run, so grabbing them early pays.",
        Stat.Sulphur =>
            "Dead Man's Sulphur, which buys board rerolls and Allflame crafts. Untradeable, "
            + "so its chaos value here is a judgement rather than a market price.",
        Stat.Gold => "Gold, for the Currency Exchange and Kingsmarch.",
        Stat.Quantity =>
            "Item Quantity in percentage points. It multiplies most of the rest, and the "
            + "solver already uses it to decide which tile a container gift lands on.",
        Stat.Rarity => "Item Rarity in percentage points.",
        Stat.Packs =>
            "Monster population: pack size and added packs. More bodies, not better ones.",
        Stat.Rares =>
            "The rare-monster economy — essences, imprisoned monsters, starfish, "
            + "possession, fracture. Rares are what per-rare border payouts are paid on.",
        Stat.Magic =>
            "Magic monsters: at-least-Magic upgrades, which convert the whole population, "
            + "and extra modifiers on the ones already magic.",
        Stat.Uniques => "Unique-item conversions and fractured bases (craft stock, not vendor fodder).",
        Stat.ChartRefunds =>
            "\"Chance to not be consumed\": the chart comes back to your panel after the "
            + "voyage instead of being spent.",
        Stat.Experience =>
            "Experience gain. Nearly worthless if you are farming; the dump voyage burns it.",
        Stat.Danger =>
            "Monster difficulty that ends runs — hexproof, penetration, damage. Weight it "
            + "NEGATIVE to steer away from charts that can kill the voyage.",
        Stat.NoEquipmentDrops =>
            "\"Cannot drop Equipment, Flasks or Tinctures\". The game's own tables file "
            + "this as a REWARD and it deletes most of the loot — weight it negative "
            + "unless you are farming currency or gold, where it costs nothing.",
        Stat.ModifierMagnitude =>
            "\"Increased explicit modifier magnitudes\": an amplifier on the touched "
            + "chart's entire rolled set, rather than a payout of its own.",
        Stat.PlayerPower =>
            "Player power such as Soul Eater. It drops no loot, so it is priced at zero — "
            + "right-click a chart twice to require it instead.",
        _ => "",
    };
}

/// <summary>One catalog line: a mod wording, the stat it feeds, and its normalized
/// per-unit value. Regexes keep every lesson the old rules learned: roll-of-one "an",
/// the GGG "Qauntity" typo, template-vs-resolved wordings, scope lookaheads.</summary>
public sealed record CatalogEntry(
    string Pattern, Stat Stat, double UnitValue,
    bool ScalesWithBoard = false, string? Comment = null);

public static class ModCatalog
{
    /// <summary>Chaos values per orb, poe.watch 2026-07 ("Allflame"). Per-rare drops
    /// multiply by the ~10 rares an area holds.</summary>
    private const double R = AreaPopulation.RaresPerArea;

    public static readonly IReadOnlyList<CatalogEntry> Entries =
    [
        // ---- per-rare currency drops (rare channel at solve time) ------------------
        new(@"drop (?:(\d+)|an) additional Divine Orbs?", Stat.Currency, 162 * R,
            Comment: "the jackpot; 162c each, x10 rares"),
        new(@"drop (?:(\d+)|an) additional Orbs? of Annulment", Stat.Currency, 8.7 * R),
        new(@"drop (?:(\d+)|an) additional Ancient Orbs?", Stat.Currency, 4.8 * R,
            Comment: "3.29.1 made these Allflame-craftable; re-fetch prices when settled"),
        new(@"drop (?:(\d+)|an) additional Chromatic Orbs?", Stat.Currency, 3 * R,
            Comment: "reworked this league; higher than history suggests"),
        new(@"drop (?:(\d+)|an) additional Gemcutter's Prisms?", Stat.Currency, 2 * R),
        new(@"drop (?:(\d+)|an) additional Exalted Orbs?", Stat.Currency, 1.2 * R),
        new(@"drop (?:(\d+)|an) additional Orbs? of Regret", Stat.Currency, 1.1 * R),
        new(@"drop (?:(\d+)|an) additional Chaos Orbs?", Stat.Currency, 1 * R),
        new(@"drop (?:(\d+)|an) additional Vaal Orbs?", Stat.Currency, 1 * R),
        new(@"drop (?:(\d+)|an) additional Blessed Orbs?", Stat.Currency, 0.9 * R),
        new(@"drop (?:(\d+)|an) additional Regal Orbs?", Stat.Currency, 0.4 * R),
        new(@"drop (?:(\d+)|an) additional Scarabs?", Stat.Scarabs, 10 * R,
            Comment: "avg scarab ~10c with jackpot potential (user-tuned upward twice)"),
        new(@"Rare Monsters.*have (\d+)% chance to drop a Support Gem", Stat.Currency, 0.2),

        // ---- sulphur ----------------------------------------------------------------
        new(@"drop Dead Man's Sulphur", Stat.Sulphur,
            AreaPopulation.RaresPerArea * AreaPopulation.SulphurPerRareDrop,
            Comment: "EACH rare drops 250-350 sulphur (best-measured constant in this "
                     + "model): rares x 300. The square IS the farm."),
        new(@"contains? Filthscrabble", Stat.Sulphur, AreaPopulation.SulphurPerFilthscrabble,
            Comment: "community-sourced ~4,000-sulphur boss"),
        new(@"Dead Man's Sulphur:\s*\+?(\d+)", Stat.Sulphur, 1,
            Comment: "the chart's own total, its largest source"),
        new(@"(\d+)%\s+increased Dead Man's Sulphur(?!\s+found in all Voyage Areas)",
            Stat.Sulphur, 0.4,
            Comment: "adjacent/in-area/panel wordings; the global scales below"),
        new(@"(\d+)%\s+increased Dead Man's Sulphur found in all Voyage Areas",
            Stat.Sulphur, 0.2, ScalesWithBoard: true,
            Comment: "multiplies the WHOLE board's sulphur: scored as that fraction"),

        // ---- bottles, boxes, containers, lanterns ----------------------------------
        new(@"(?:(\d+)|an)\s+additional Messages? in (?:a )?Bottles?", Stat.Bottles, 39,
            Comment: "researched 2026-07-31: ~39c each, sellable UNOPENED; a hidden-"
                     + "until-charted voyage mod, ilvl 68+, max +2 -- cannot be crafted "
                     + "for, only revealed. Container channel seats it beside quantity."),
        new(@"(?:(\d+)|an)\s+additional Arcanist's Strongbox(?:es)?", Stat.Strongboxes, 25),
        new(@"(?:(\d+)|an)\s+additional Diviner's Strongbox(?:es)?", Stat.Strongboxes, 25),
        new(@"(?:(\d+)|an)\s+additional Operative's Strongbox(?:es)?", Stat.Strongboxes, 20,
            Comment: "community: the speedrun centre pick, divines of scarabs when rolled"),
        new(@"(?:(\d+)|an)\s+additional Strongbox(?:es)?", Stat.Strongboxes, 8,
            Comment: "the generic roll; named boxes have their own lines above"),
        new(@"(?:(\d+)|an)\s+additional Clusters? of Barrels", Stat.Openables, 1.5),
        new(@"(?:(\d+)|an)\s+additional Treasure", Stat.Openables, 5,
            Comment: "Treasure Anchors"),
        new(@"(?:(\d+)|an)\s+additional cages? of Tormented Spirits", Stat.Openables, 8),
        new(@"highly prized and exotic Fish", Stat.Openables, 8),
        new(@"contain Friendly Jellyfish", Stat.Openables, 5),
        new(@"a lost Pirate's Locker", Stat.Openables, 25),
        new(@"contain a Brinerot raiding party", Stat.Openables, 12,
            Comment: "board modifier"),
        new(@"contain Captainsbane", Stat.Openables, 10,
            Comment: "board modifier"),
        new(@"(?:(\d+)|an) Altars? to the Goddess", Stat.Currency, 12,
            Comment: "3.29.1 blessings convert common currency to rarer, party-wide"),
        // A CONVERSION, so the tile it lands on decides what it is worth: the value is
        // (decks converted) x (deck price - the basic currency it replaced), and the
        // count of basic drops is what Item Quantity multiplies. 40 is ~20 converted
        // drops at a deck's ~2c, priced for a BASELINE tile -- the receiver's quantity
        // scales it from there, which is why it must not be scored flat.
        new(@"instead drop as Stacked Decks", Stat.Currency, 40,
            Comment: "conversion: ~2c per deck, count scales with the receiver's Quantity"),
        new(@"(?:(\d+)|an)\s+additional Golden Lanterns?", Stat.Lanterns, 12,
            Comment: "3.29.0b made these grant increased Quantity too; 3.29.1 buffs "
                     + "hired mercenaries from them as well"),
        new(@"Placing Lanterns does not reduce your Lantern count", Stat.Lanterns, 15,
            Comment: "board modifier: free lanterns"),

        // ---- loot power -------------------------------------------------------------
        new(@"Item Quantity:\s*\+?(\d+)", Stat.Quantity, 1),
        new(@"(\d+)%\s+increased Quantity of Items(?!\s+found in all Voyage Areas)",
            Stat.Quantity, 1.5,
            Comment: "adjacent, in-area and panel-resolved wordings"),
        new(@"(\d+)%\s+increased Q(?:uantity|auntity) of Items found in all Voyage Areas",
            Stat.Quantity, 1, ScalesWithBoard: true,
            Comment: "GGG spells it 'Qauntity' in the globals; multiplies the board"),
        new(@"Item Rarity:\s*\+?(\d+)", Stat.Rarity, 1),
        new(@"(\d+)%\s+increased Rarity of Items(?!\s+found in all Voyage Areas)",
            Stat.Rarity, 1.5),
        new(@"(\d+)%\s+increased Rarity of Items found in all Voyage Areas",
            Stat.Rarity, 1, ScalesWithBoard: true),
        new(@"(\d+)% more Rarity of Items found", Stat.Rarity, 3,
            Comment: "MORE, not increased: the stronger multiplier"),
        new(@"(\d+)% more Currency found", Stat.Currency, 2),
        new(@"(\d+)% more Scarabs found", Stat.Scarabs, 1.5),
        // A FRACTION, not chaos: 0.01 per percent, so a 60% roll scores 0.6 and the
        // solver multiplies that by the RECEIVING chart's explicit-modifier value. This
        // mod pays nothing of its own -- it is a share of whatever it lands beside -- so
        // the only honest unit is the share. Priced flat (it was 1.5/percent) it scored
        // the same 90 points beside a blank chart as beside the best chart on the board,
        // and being per-square it then steered no placement at all.
        new(@"(\d+)%\s+increased explicit modifier magnitudes", Stat.ModifierMagnitude, 0.01,
            Comment: "a FRACTION of the receiving chart's explicit mods, not a flat value"),
        new(@"Flasks found.*chance to have (\d+)% Quality", Stat.Quantity, 0.3),

        // ---- population and the rare economy ---------------------------------------
        new(@"Monster Pack Size:\s*\+?(\d+)", Stat.Packs, 1),
        new(@"(\d+)%\s+increased Pack Size(?! in all Voyage Areas)", Stat.Packs, 1.5),
        new(@"(\d+)%\s+increased Pack Size in all Voyage Areas", Stat.Packs, 1,
            ScalesWithBoard: true),
        new(@"(?:(\d+)|an)\s+additional packs? of", Stat.Packs, 3,
            Comment: "each pack ~10% chance to arrive rare-led (field-corrected from "
                     + "30%); the rare channel counts that separately"),
        new(@"(\d+)% increased number of Rare monsters.*per connection", Stat.Packs, 0.4),
        new(@"(\d+)%\s+increased number of Rare Monsters(?!.*all Voyage Areas)(?!.*per connection)",
            Stat.Rares, 1,
            Comment: "the per-connection variant is its own entry under Packs"),
        new(@"(\d+)%\s+increased number of Rare Monsters in all Voyage Areas",
            Stat.Rares, 1, ScalesWithBoard: true,
            Comment: "rare count IS the rare economy: a global multiplier on it"),
        new(@"(\d+)%\s+increased number of Magic Monsters(?!.*all Voyage Areas)",
            Stat.Magic, 1),
        new(@"(\d+)%\s+increased number of Magic Monsters in all Voyage Areas",
            Stat.Magic, 0.7, ScalesWithBoard: true),
        new(@"(?:(\d+)|an)\s+additional Imprisoned Monsters?", Stat.Rares, 2,
            Comment: "OBSERVED: imprisoned (essence) monsters ARE rares and DO drop "
                     + "the per-rare border payouts"),
        new(@"(?:(\d+)|an)\s+additional Giant Starfish", Stat.Rares, 2,
            Comment: "field-confirmed: one rare per starfish pack, count maps 1:1"),
        new(@"imprisoned by Essences", Stat.Rares, 40,
            Comment: "every natural rare becomes an Essence monster, voyage-wide"),
        new(@"(\d+)% chance for Rare Monsters.*to be Possessed", Stat.Rares, 0.3),
        new(@"Rare Monsters.*(\d+)% chance to Fracture on death", Stat.Rares, 0.5),
        new(@"will have a Pantheon Modifier", Stat.Rares, 15),
        new(@"Atziri's Influence", Stat.Rares, 20,
            Comment: "a named modifier, the only one of its kind in the tables"),
        new(@"Empowered by (\d+) Wildwood Wisps", Stat.Rares, 0.005,
            Comment: "the roll is in thousands"),
        new(@"are at least Magic", Stat.Magic, 8 * AreaPopulation.PacksPerArea,
            Comment: "converts the WHOLE population; priced per pack x 30, and the "
                     + "population channel multiplies by the receiving tile's packs"),
        new(@"Magic Monsters.*have an additional modifier", Stat.Magic, 30),

        // ---- uniques, gold, misc ----------------------------------------------------
        new(@"(\d+)% chance to instead drop as a Unique", Stat.Uniques, 3),
        new(@"Items dropped.*(\d+)% chance to be Fractured", Stat.Uniques, 8,
            Comment: "fractured bases are craft stock, not vendor fodder"),
        new(@"Gold Found:\s*\+?(\d+)", Stat.Gold, 1),
        new(@"(\d+)%\s+increased Gold found", Stat.Gold, 1),
        new(@"(\d+)% of Equipment dropped by monsters.*converted to Gold", Stat.Gold, 1),
        new(@"(\d+)% chance (?:for Charts? )?to not be consumed", Stat.ChartRefunds, 3,
            Comment: "a refunded adjacent chart is ~its own value x the chance"),
        new(@"gain (\d+)% increased Experience", Stat.Experience, 0.1),
        new(@"have Soul Eater", Stat.PlayerPower, 10,
            Comment: "player power, not loot; the required-star exists for it"),
        new(@"cannot drop Equipment, Flasks or Tinctures", Stat.NoEquipmentDrops, 1,
            Comment: "filed as a reward by the game's own tables, deletes most loot"),

        // ---- danger (severities; the safe strategy weights these negative) ----------
        // Max-res is deliberately ABSENT: 3.29.1 disclosed it never functioned.
        new(@"Monsters are Hexproof", Stat.Danger, 10),
        new(@"less effect of Curses on Monsters", Stat.Danger, 6),
        new(@"Monsters cannot be Taunted", Stat.Danger, 6),
        new(@"Monsters cannot be Stunned", Stat.Danger, 4),
        new(@"Speed cannot be modified to below Base Value", Stat.Danger, 5),
        new(@"Monsters steal Power, Frenzy and Endurance charges on Hit", Stat.Danger, 5),
        new(@"Monsters fire (\d+) additional Projectiles", Stat.Danger, 2),
        new(@"Monsters' skills Chain (\d+) additional times", Stat.Danger, 2),
        new(@"Area has patches of", Stat.Danger, 3),
        new(@"Monster Damage Penetrates (\d+)% Elemental Resistances", Stat.Danger, 1),
        new(@"(\d+)% more Monster Life", Stat.Danger, 0.15),
        new(@"(\d+)% increased Monster Damage", Stat.Danger, 0.2),
        new(@"(\d+)% to Monster Critical Strike Multiplier", Stat.Danger, 0.2),
        new(@"Monsters deal (\d+)% extra Physical Damage as", Stat.Danger, 0.15),
        new(@"Monsters gain (\d+)% of Maximum Life as Extra Maximum Energy Shield",
            Stat.Danger, 0.1),
        new(@"Monsters gain (\d+)% of their Physical Damage as Extra Chaos Damage",
            Stat.Danger, 0.15),
        new(@"Monsters apply Grasping Vines", Stat.Danger, 3,
            Comment: "3.29.1's replacement for the disabled max-res roll, same upsides"),
        new(@"(\d+)% reduced quantity of items found.*per connection", Stat.Quantity, -2,
            Comment: "worse the better the board is joined -- the thing the planner "
                     + "maximises; negative quantity, not danger"),
    ];
}

/// <summary>
/// A named weight preset over stats -- preference, nothing else -- plus the solver
/// knobs a plan needs. Compiles into the old profile shape so nothing downstream moves.
/// </summary>
public sealed class Strategy
{
    public string Name { get; set; } = "unnamed";
    public string? Description { get; set; }

    /// <summary>Multiplier per stat. Absent = 0 = that economy is invisible here.</summary>
    public Dictionary<Stat, double> Weights { get; set; } = new();

    public double BoardModifierWeight { get; set; } = 1.0;
    public double AreaLevelWeight { get; set; }
    public double ChartBaseValue { get; set; }
    public double MonsterPayoutSynergy { get; set; } = 1.0;
    public double StrandedSquarePenalty { get; set; } = 40;
    public int? MaxCharts { get; set; }

    /// <summary>
    /// Strategy-specific knowledge no stat can carry: tileset preferences and other
    /// field lessons that belong to one plan alone. Appended verbatim on compile.
    /// </summary>
    public List<VoyageRule> ExtraRules { get; set; } = new();

    /// <summary>
    /// Dump switches this off: its board estimate is dominated by ChartBaseValue and
    /// would price global percentage mods arbitrarily. With it off, board-scaling
    /// catalog entries are SKIPPED entirely -- the strategy must say what a global
    /// is worth flat, in ExtraRules, or it is worth nothing.
    /// </summary>
    public bool UseBoardScaling { get; set; } = true;

    /// <summary>
    /// One bottle chart per voyage (field rule): a bottle's count is fixed by the roll
    /// and nothing multiplies it, so a second bottle chart in the same voyage mostly
    /// re-covers areas the first already feeds -- held back, it is a whole extra
    /// voyage of bottles. Lives on the SHIPPED strategy, not the serialized profile:
    /// a rule file written by an older build cannot switch mechanics off, and a tuned
    /// profile keeps its weights without losing the rationing.
    /// </summary>
    public bool SeatsOneBottleChart { get; set; }

    /// <summary>The catalog priced by this strategy's weights: the old rule shape.</summary>
    public VoyageProfile Compile() => new()
    {
        Name = Name,
        Description = Description,
        BoardModifierWeight = BoardModifierWeight,
        AreaLevelWeight = AreaLevelWeight,
        ChartBaseValue = ChartBaseValue,
        MonsterPayoutSynergy = MonsterPayoutSynergy,
        StrandedSquarePenalty = StrandedSquarePenalty,
        MaxCharts = MaxCharts,
        Rules =
        [
            .. ModCatalog.Entries
                .Where(e => UseBoardScaling || !e.ScalesWithBoard)
                .Where(e => Math.Abs(WeightOf(e.Stat) * e.UnitValue) > 1e-9)
                .Select(e => new VoyageRule
                {
                    Pattern = e.Pattern,
                    Weight = WeightOf(e.Stat) * e.UnitValue,
                    ScalesWithBoard = e.ScalesWithBoard,
                    Comment = e.Comment,
                    Category = e.Stat.ToString(),
                }),
            // Stamped as Extras structurally rather than by convention: what makes a
            // rule "the strategy's own" is where it came from, not its wording, and
            // several ExtraRules deliberately reuse a catalog wording at a different
            // weight. Left to a pattern lookup they filed themselves under that stat.
            .. ExtraRules.Select(r => new VoyageRule
            {
                Pattern = r.Pattern,
                Weight = r.Weight,
                ScalesWithBoard = r.ScalesWithBoard,
                Comment = r.Comment,
                Category = WeightCategories.Other,
            }),
        ],
    };

    public double WeightOf(Stat stat) => Weights.GetValueOrDefault(stat);
}

/// <summary>
/// The shipped strategies: the twelve old profiles re-expressed as weight presets over
/// the normalized catalog. Where a profile encoded a market fact, that fact moved into
/// the catalog and the weight became 1; where it encoded preference, the preference is
/// the weight. Tileset lessons ride in ExtraRules verbatim.
/// </summary>
public static class Strategies
{
    private static VoyageRule Extra(string pattern, double weight, string comment) =>
        new() { Pattern = pattern, Weight = weight, Comment = comment };

    /// <summary>Keyed by profile NAME because the serialized profile cannot carry it:
    /// mechanics must survive a rule file written before the flag existed.</summary>
    public static bool SeatsOneBottleChart(string profileName) =>
        Shipped().FirstOrDefault(s =>
            s.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase))
        ?.SeatsOneBottleChart ?? false;

    public static List<Strategy> Shipped() =>
    [
        new()
        {
            Name = "sulphur",
            Description = "Maximise Dead Man's Sulphur.",
            Weights = { [Stat.Sulphur] = 5 },
        },
        new()
        {
            Name = "bottles",
            Description = "Chase Messages in a Bottle: seat the gift where it touches the most tiles.",
            SeatsOneBottleChart = true,
            BoardModifierWeight = 1.5,
            AreaLevelWeight = 0.3,   // bottles only exist on 68+ charts; break ties upward
            Weights =
            {
                [Stat.Bottles] = 1.5, [Stat.Strongboxes] = 0.6, [Stat.Openables] = 0.8,
                [Stat.Lanterns] = 0.7, [Stat.Quantity] = 1, [Stat.Rarity] = 0.3,
                [Stat.ChartRefunds] = 1, [Stat.NoEquipmentDrops] = -40,
            },
            ExtraRules =
            {
                Extra(@"Area: Anchorfield", 15, "Sunken Loot density compounds the stack"),
            },
        },
        new()
        {
            Name = "currency",
            Description = "Maximise raw currency: orb payouts, scarabs, decks, altars.",
            BoardModifierWeight = 1.5,
            Weights =
            {
                [Stat.Currency] = 1, [Stat.Scarabs] = 1, [Stat.Bottles] = 1,
                [Stat.Openables] = 1, [Stat.ModifierMagnitude] = 1, [Stat.Quantity] = 1,
                [Stat.Rares] = 0.5, [Stat.NoEquipmentDrops] = -20,
            },
        },
        new()
        {
            Name = "scarabs",
            Description = "Chase Scarabs: per-rare drops beside dense tiles, and Operative's boxes.",
            // Both scarab lines in the corpus are ADJACENT-scoped and both exist as
            // figurine mods, so where things sit decides this plan more than most --
            // "#% more Scarabs found in adjacent Areas" and "Rare Monsters in adjacent
            // Areas drop # additional Scarabs", chart and border alike.
            BoardModifierWeight = 1.5,
            // 3.29.0b gates Operative's, Diviner's and Arcanist's to spawn by default
            // above area level 67, and an Operative's box is half this plan.
            AreaLevelWeight = 0.5,
            Weights =
            {
                [Stat.Scarabs] = 1,
                // The scarab drop pays PER RARE, so rares are the delivery vehicle. The
                // channel machinery already multiplies the payout by the receiving
                // tile's rare density; this is what makes a rare-ADDING mod worth
                // taking for its own sake.
                [Stat.Rares] = 0.5,
                // Strongboxes score NOTHING as boxes here, and that is deliberate rather
                // than an omission: a box's generic loot is not scarabs. What every box
                // is worth to this plan is that it is a rare fountain -- MonsterDensityOf
                // already counts one as ~4 rares -- and that arrives through the channel
                // machinery whatever the rule weight, so a box beside a per-rare scarab
                // square feeds it for free. Only Operative's holds scarabs of its own,
                // and that is a rule, not a stat. See ExtraRules.
                //
                // Scarabs out of a box ride the quantity channel like any container gift.
                [Stat.Quantity] = 0.3,
                [Stat.Packs] = 0.2,
                // Cheap here, and that is informative: scarabs are not equipment, so the
                // loot-lock costs a scarab run far less than it costs a currency one.
                [Stat.NoEquipmentDrops] = -10,
            },
            ExtraRules =
            {
                // An Operative's Strongbox CONTAINS scarabs. The catalog can only file a
                // mod under ONE stat, and files this one under Strongboxes at 20 -- below
                // Arcanist's and Diviner's at 25, which is backwards for anybody farming
                // scarabs, and flatly at odds with the note the catalog carries on it:
                // "the speedrun centre pick, divines of scarabs when rolled".
                //
                // So this REPLACES the catalog's opinion for this plan rather than adding
                // to it, which is why Strongboxes is weighted at nothing above: no line
                // may be scored by two rules of one profile (RulesReviewTests), and a
                // premium stacked on top of the box rule would pay this line twice.
                //
                // JUDGED, not measured, and named as such: the catalog's ~10c scarab at
                // roughly four or five a box. Trim it the moment a run gives a real count.
                Extra(@"(?:(\d+)|an)\s+additional Operative's Strongbox(?:es)?", 50,
                      "the scarab box: ~4-5 scarabs at the catalog's ~10c anchor. Its "
                      + "generic box loot is invisible here on purpose. Unmeasured."),
            },
        },
        new()
        {
            Name = "rare monsters",
            Description = "Essences, possession, fractures and everything that rides a rare.",
            BoardModifierWeight = 1.5,
            Weights =
            {
                [Stat.Rares] = 1, [Stat.Currency] = 0.2, [Stat.Scarabs] = 1.5,
                [Stat.Packs] = 0.4, [Stat.Strongboxes] = 0.4,
            },
        },
        new()
        {
            Name = "strongbox",
            Description = "Crack every box: Diviner's and Operative's first.",
            BoardModifierWeight = 1.5,
            AreaLevelWeight = 0.5,
            Weights =
            {
                [Stat.Strongboxes] = 1, [Stat.Quantity] = 0.5, [Stat.Rarity] = 0.2,
                [Stat.NoEquipmentDrops] = -30,
            },
        },
        new()
        {
            Name = "containers",
            Description = "Barrels, lanterns, spirits, Sunken Loot and the rest of the openables.",
            BoardModifierWeight = 1.5,
            Weights =
            {
                [Stat.Openables] = 1, [Stat.Lanterns] = 1, [Stat.Bottles] = 0.3,
                [Stat.Quantity] = 0.3,
            },
            ExtraRules =
            {
                Extra(@"(?:(\d+)|an)\s+additional Giant Starfish", 4,
                      "loot-bearing rares count as openables here"),
                Extra(@"Area: Anchorfield", 25, "observed: dense Sunken Loot chests"),
            },
        },
        new()
        {
            Name = "uniques",
            Description = "Unique conversions, fractured bases and rarity.",
            BoardModifierWeight = 1.5,
            Weights =
            {
                [Stat.Uniques] = 1, [Stat.Rarity] = 0.5, [Stat.NoEquipmentDrops] = -60,
            },
            ExtraRules =
            {
                Extra(@"Rare Monsters.*(\d+)% chance to Fracture on death", 0.4,
                      "fractured RARES feed the same hunt"),
                Extra(@"Area: (?:Hazardous Depths|Kishara's Rest)", 15,
                      "market-backed: both charts trade expensive; rumours are "
                      + "unique-shaped (Rotmother loot box, a boss at Kishara's Rest)"),
            },
        },
        new()
        {
            Name = "gold",
            Description = "Maximise Gold for the Currency Exchange and Kingsmarch.",
            Weights = { [Stat.Gold] = 1, [Stat.Packs] = 0.4 },
            ExtraRules =
            {
                Extra(@"Area: Clam-infested Shelf", 20,
                      "measured: a LOOT room of gold piles and treasure clams"),
            },
        },
        new()
        {
            Name = "pack size",
            Description = "Maximise monsters, including what neighbours receive.",
            BoardModifierWeight = 1.5,
            // Rares at 0.75: any monster count is population here (imprisoned rides
            // the same stat at catalog value -- the old hand-tuned 4 became 1.5, a
            // documented drift the factorization buys simplicity with).
            Weights =
            {
                [Stat.Packs] = 1, [Stat.Rares] = 0.75, [Stat.Magic] = 0.05,
                [Stat.PlayerPower] = 1,
            },
        },
        new()
        {
            Name = "magic monsters",
            Description = "At-least-Magic upgrades and the magic-monster economy.",
            BoardModifierWeight = 1.5,
            Weights =
            {
                [Stat.Magic] = 1, [Stat.Packs] = 0.5, [Stat.Quantity] = 0.3,
            },
        },
        new()
        {
            Name = "high tier",
            Description = "Highest area level the build survives: danger counts against.",
            AreaLevelWeight = 1.0,
            Weights = { [Stat.Danger] = -1 },
        },
        new()
        {
            Name = "dump",
            Description = "Burn the least valuable charts to clear panel space.",
            // Everything valuable scores NEGATIVE so the junk sorts to the top; the
            // base keeps every chart net positive so the board still fills, and board
            // scaling is off because dump's estimate is dominated by the base.
            ChartBaseValue = 900,
            AreaLevelWeight = -1,
            BoardModifierWeight = 0,
            MonsterPayoutSynergy = 0,
            UseBoardScaling = false,
            Weights =
            {
                [Stat.Currency] = -0.05, [Stat.Scarabs] = -0.8, [Stat.Bottles] = -0.3,
                [Stat.Strongboxes] = -0.8, [Stat.Openables] = -0.7, [Stat.Lanterns] = -0.7,
                [Stat.Sulphur] = -2.5, [Stat.Gold] = -0.3, [Stat.Quantity] = -1,
                [Stat.Rarity] = -0.3, [Stat.Packs] = -0.5, [Stat.Rares] = -1,
                [Stat.Magic] = -0.05, [Stat.Uniques] = -1, [Stat.ChartRefunds] = -1,
                [Stat.Experience] = -1, [Stat.PlayerPower] = -1,
                [Stat.NoEquipmentDrops] = 15,   // the one positive: junk by definition
            },
            // GLOBAL multipliers get strong flat negatives rather than ScalesWithBoard:
            // a board-wide multiplier is a valuable chart, and valuable charts are
            // kept, not burned. A casual per-percent negative is not a deterrent.
            ExtraRules =
            {
                Extra(@"(\d+)%\s+increased Q(?:uantity|auntity) of Items found in all Voyage Areas",
                      -8, "global loot multiplier: emphatically a keeper"),
                Extra(@"(\d+)%\s+increased Dead Man's Sulphur found in all Voyage Areas",
                      -8, "global sulphur: a keeper"),
                Extra(@"(\d+)%\s+increased Rarity of Items found in all Voyage Areas",
                      -4, "global rarity: a keeper"),
                Extra(@"(\d+)%\s+increased Pack Size in all Voyage Areas",
                      -5, "global pack size: a keeper"),
                Extra(@"(\d+)%\s+increased number of (?:Rare|Magic) Monsters in all Voyage Areas",
                      -4, "global monster counts: keepers"),
                // An amplifier is a keeper for the same reason a global is, and it needs
                // the same treatment: its catalog value is a SHARE of the chart it lands
                // beside, and dump's charts are junk by construction, so the share of
                // them is a rounding error. A flat negative is the deterrent that works.
                Extra(@"(\d+)%\s+increased explicit modifier magnitudes",
                      -3, "an amplifier is a keeper, not fuel"),
            },
        },
    ];
}
