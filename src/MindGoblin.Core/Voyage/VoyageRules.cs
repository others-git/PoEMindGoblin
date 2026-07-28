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
public sealed class VoyageRule
{
    public string Pattern { get; set; } = "";
    public double Weight { get; set; } = 1;
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

        // A captured number scales the weight, so "8 additional packs" beats "2".
        if (m.Groups.Count > 1 && double.TryParse(m.Groups[1].Value, out var n))
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
    /// </summary>
    public double ScoreChart(Chart chart) =>
        ScoreText(chart.OwnLines()) + chart.AreaLevel * AreaLevelWeight;

    /// <summary>What a chart's Adjacent Modifier is worth to ONE neighbour.</summary>
    public double ScoreAdjacent(Chart chart) =>
        string.IsNullOrEmpty(chart.AdjacentModifier) ? 0 : ScoreText([chart.AdjacentModifier!]);

    /// <summary>Builds the (chart, cell) scorer the solver needs from this profile.</summary>
    public Func<Chart, Cell, double> Scorer(IReadOnlyList<BoardModifier> boardModifiers)
    {
        var byCell = new Dictionary<Cell, double>();
        foreach (var m in boardModifiers)
        {
            var value = ScoreText([m.Description]) * BoardModifierWeight;
            foreach (var cell in m.AffectedCells)
                byCell[cell] = byCell.GetValueOrDefault(cell) + value;
        }

        return (chart, cell) => ScoreChart(chart) + byCell.GetValueOrDefault(cell);
    }
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

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        SettingsFolder.Name, "voyage-rules.json");

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

    private static bool SameRules(VoyageProfile a, VoyageProfile b) =>
        a.Rules.Count == b.Rules.Count
        && a.Rules.Zip(b.Rules).All(pair => pair.First.Pattern == pair.Second.Pattern
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
    [
        new VoyageProfile
        {
            Name = "sulphur",
            Description = "Maximise Dead Man's Sulphur.",
            Rules =
            [
                new VoyageRule { Pattern = @"Dead Man's Sulphur:\s*\+?(\d+)", Weight = 5.0,
                                 Comment = "the chart's own total, its largest source" },
                new VoyageRule { Pattern = @"(\d+)%\s+increased Dead Man's Sulphur", Weight = 2.0,
                                 Comment = "the global and adjacent rolls" },
                new VoyageRule { Pattern = @"drop Dead Man's Sulphur", Weight = 30,
                                 Comment = "board modifier: rares drop it directly" },
            ],
        },

        new VoyageProfile
        {
            Name = "quantity",
            Description = "Maximise item quantity and rarity.",
            Rules =
            [
                new VoyageRule { Pattern = @"Item Quantity:\s*\+?(\d+)", Weight = 1.0 },
                new VoyageRule { Pattern = @"Item Rarity:\s*\+?(\d+)", Weight = 0.5 },
                new VoyageRule { Pattern = @"(\d+)%\s+increased Q(?:uantity|auntity) of Items", Weight = 1.5,
                                 Comment = "GGG spells it 'Qauntity' in the global lines only" },
                new VoyageRule { Pattern = @"(\d+)%\s+increased Rarity of Items", Weight = 0.75 },
                new VoyageRule { Pattern = @"Flasks found.*chance to have (\d+)% Quality", Weight = 0.4,
                                 Comment = "a quality flask is a better item, so it belongs here" },
                new VoyageRule { Pattern = @"cannot drop Equipment, Flasks or Tinctures", Weight = -60,
                                 Comment = "a global roll that deletes most of the loot" },
                // Board modifiers, off the figurines rather than the charts.
                new VoyageRule { Pattern = @"(\d+)%\s+increased explicit modifier magnitudes", Weight = 0.5,
                                 Comment = "rolls every other modifier higher" },
                new VoyageRule { Pattern = @"(\d+)% [Cc]hance (?:for Chart|to not be consumed)", Weight = 0.8,
                                 Comment = "a chart you keep is a voyage you did not pay for" },
                new VoyageRule { Pattern = @"Charts have (\d+)% chance to not be consumed", Weight = 0.8 },
                new VoyageRule { Pattern = @"(\d+)% more Rarity of Items found", Weight = 1.0 },
                new VoyageRule { Pattern = @"(\d+)% reduced quantity of items found", Weight = -1.5 },
                new VoyageRule { Pattern = @"gain (\d+)% increased Experience", Weight = 0.1 },
            ],
        },

        new VoyageProfile
        {
            Name = "pack size",
            Description = "Maximise monsters, including what neighbours receive.",
            BoardModifierWeight = 1.5,
            Rules =
            [
                new VoyageRule { Pattern = @"Monster Pack Size:\s*\+?(\d+)", Weight = 1.0 },
                new VoyageRule { Pattern = @"(\d+)%\s+increased Pack Size", Weight = 1.5 },
                new VoyageRule { Pattern = @"(\d+)\s+additional packs of", Weight = 3.0,
                                 Comment = "Crabs, Octopi, Sea Beasts, the Drowned" },
                new VoyageRule { Pattern = @"Magic Monsters.*have an additional modifier", Weight = 8,
                                 Comment = "board modifier" },
                new VoyageRule { Pattern = @"(\d+)% increased number of Rare monsters.*per connection",
                                 Weight = 0.4, Comment = "board modifier: scales with connections" },
                new VoyageRule { Pattern = @"(\d+)%\s+increased number of (?:Rare|Magic) Monsters", Weight = 0.75 },
                new VoyageRule { Pattern = @"(\d+)\s+additional Imprisoned Monsters", Weight = 4.0 },
                new VoyageRule { Pattern = @"are at least Magic", Weight = 15 },
                new VoyageRule { Pattern = @"have Soul Eater", Weight = 10,
                                 Comment = "worth having where the packs are" },
            ],
        },

        new VoyageProfile
        {
            Name = "strongbox",
            Description = "Arcanist's, Diviner's and Operative's boxes, and the quantity that fills them.",
            BoardModifierWeight = 1.5,
            Rules =
            [
                // The three named types are the ones worth planning around. Note the
                // patterns are mutually exclusive: "additional Strongboxes" cannot match
                // "additional Diviner's Strongboxes", because the type sits between the
                // two words, so nothing is scored twice.
                new VoyageRule { Pattern = @"(\d+)\s+additional Arcanist's Strongboxes", Weight = 25,
                                 Comment = "currency" },
                new VoyageRule { Pattern = @"(\d+)\s+additional Diviner's Strongboxes", Weight = 25,
                                 Comment = "divination cards" },
                new VoyageRule { Pattern = @"(\d+)\s+additional Operative's Strongboxes", Weight = 20 },
                new VoyageRule { Pattern = @"(\d+)\s+additional Strongboxes", Weight = 8,
                                 Comment = "the plain roll: random types" },
                // Box contents roll against area quantity and rarity, so the tile's own
                // stats decide what the boxes are actually worth.
                new VoyageRule { Pattern = @"Item Quantity:\s*\+?(\d+)", Weight = 0.5 },
                new VoyageRule { Pattern = @"Item Rarity:\s*\+?(\d+)", Weight = 0.3 },
                new VoyageRule { Pattern = @"(\d+)%\s+increased Q(?:uantity|auntity) of Items", Weight = 0.75 },
                new VoyageRule { Pattern = @"cannot drop Equipment, Flasks or Tinctures", Weight = -30 },
            ],
        },

        new VoyageProfile
        {
            Name = "containers",
            Description = "Barrels, lanterns, spirits, Sunken Loot and the rest of the openables.",
            BoardModifierWeight = 1.5,
            Rules =
            [
                new VoyageRule { Pattern = @"(\d+)\s+additional Clusters of Barrels", Weight = 1.5 },
                new VoyageRule { Pattern = @"(\d+)\s+additional Golden Lanterns", Weight = 6 },
                new VoyageRule { Pattern = @"(\d+)\s+additional cages of Tormented Spirits", Weight = 8 },
                new VoyageRule { Pattern = @"an additional cage of Tormented Spirits", Weight = 8,
                                 Comment = "the game writes the singular when the roll is 1" },
                new VoyageRule { Pattern = @"(\d+)\s+additional Messages in Bottles", Weight = 5 },
                new VoyageRule { Pattern = @"(\d+)\s+additional Giant Starfish", Weight = 4 },
                new VoyageRule { Pattern = @"(\d+)\s+additional Treasure", Weight = 5,
                                 Comment = "Treasure Anchors" },
                new VoyageRule { Pattern = @"highly prized and exotic Fish", Weight = 8 },
                new VoyageRule { Pattern = @"contain Friendly Jellyfish", Weight = 5 },
                new VoyageRule { Pattern = @"contains? Filthscrabble", Weight = 6,
                                 Comment = "board modifier; the table says 'contain'" },

                // TILESET. A chart states which area it opens, and the tilesets are not
                // equal: Anchorfield is thick with Sunken Loot chests, which no chart
                // modifier accounts for. There is no published list of tilesets or their
                // contents -- poedb documents the chart bases and nothing about the areas
                // -- so this is an OBSERVED preference, not a measured one, and it is the
                // first thing to retune. The Voyage tab lists the tilesets you hold, so
                // adding a rule for another is a copy and a number.
                new VoyageRule { Pattern = @"contain a Brinerot raiding party", Weight = 12,
                                 Comment = "board modifier" },
                new VoyageRule { Pattern = @"contain Captainsbane", Weight = 10,
                                 Comment = "board modifier" },
                new VoyageRule { Pattern = @"Placing Lanterns does not reduce your Lantern count",
                                 Weight = 15, Comment = "board modifier: free lanterns" },
                new VoyageRule { Pattern = @"Area: Anchorfield", Weight = 25,
                                 Comment = "observed: dense Sunken Loot chests" },
                new VoyageRule { Pattern = @"Item Quantity:\s*\+?(\d+)", Weight = 0.3 },
            ],
        },

        new VoyageProfile
        {
            Name = "rare monsters",
            Description = "Essences, possession, fractures and everything that rides a rare.",
            BoardModifierWeight = 1.5,
            Rules =
            [
                new VoyageRule { Pattern = @"imprisoned by Essences", Weight = 40 },
                new VoyageRule { Pattern = @"(\d+)% chance for Rare Monsters.*to be Possessed", Weight = 0.3 },
                new VoyageRule { Pattern = @"Rare Monsters.*(\d+)% chance to Fracture on death", Weight = 0.5 },
                new VoyageRule { Pattern = @"will have a Pantheon Modifier", Weight = 15 },
                new VoyageRule { Pattern = @"(\d+)%\s+increased number of Rare Monsters", Weight = 1.0 },
                new VoyageRule { Pattern = @"(\d+)%\s+increased number of Magic Monsters", Weight = 0.4 },
                new VoyageRule { Pattern = @"Empowered by (\d+) Wildwood Wisps", Weight = 0.005,
                                 Comment = "the roll is in thousands" },
                new VoyageRule { Pattern = @"Atziri's Influence", Weight = 20 },
                // Two wordings for the same thing: the mod table has the template ("in
                // adjacent Areas drop # additional Chaos Orbs") while the Area Modifiers
                // panel shows it resolved for the square you hovered ("in Area drop an
                // additional Chaos Orb").
                new VoyageRule { Pattern = @"Rare Monsters.*drop (\d+) additional", Weight = 8,
                                 Comment = "board modifier: extra currency per rare" },
                new VoyageRule { Pattern = @"Rare Monsters.*drop an additional", Weight = 8 },

                // Rares spawn out of packs, so more packs is more rares -- and every
                // payout above is per rare. This profile ignored pack size entirely,
                // which meant it would take a chart with a Pantheon modifier and few
                // monsters over one with the same modifier and half again as many.
                new VoyageRule { Pattern = @"Monster Pack Size:\s*\+?(\d+)", Weight = 0.3 },
                new VoyageRule { Pattern = @"(\d+)%\s+increased Pack Size", Weight = 0.5 },
                new VoyageRule { Pattern = @"(\d+)\s+additional packs of", Weight = 1.5 },
                new VoyageRule { Pattern = @"(\d+)\s+additional Imprisoned Monsters", Weight = 2.0,
                                 Comment = "an imprisoned monster is a rare" },
            ],
        },

        new VoyageProfile
        {
            Name = "uniques",
            Description = "Jewellery rerolled into uniques, and fractured drops.",
            BoardModifierWeight = 1.5,
            Rules =
            [
                new VoyageRule { Pattern = @"(\d+)% chance to instead drop as a Unique", Weight = 3.0 },
                new VoyageRule { Pattern = @"Items dropped.*(\d+)% chance to be Fractured", Weight = 8.0 },
                new VoyageRule { Pattern = @"Rare Monsters.*(\d+)% chance to Fracture on death", Weight = 0.4 },
                new VoyageRule { Pattern = @"Item Rarity:\s*\+?(\d+)", Weight = 0.4 },
                new VoyageRule { Pattern = @"(\d+)%\s+increased Rarity of Items", Weight = 0.75 },
                new VoyageRule { Pattern = @"cannot drop Equipment, Flasks or Tinctures", Weight = -60,
                                 Comment = "jewellery included, so this guts the profile" },
            ],
        },

        new VoyageProfile
        {
            Name = "currency",
            Description = "Orbs off rare monsters, Stacked Decks and scarabs. Board modifiers.",
            BoardModifierWeight = 1.5,
            Rules =
            [
                // These are figurine modifiers, so they decide which SQUARE is worth
                // standing a chart on rather than which chart to pick. Weighted by rough
                // trade value, which is the part most worth arguing with.
                new VoyageRule { Pattern = @"drop (\d+) additional Divine Orbs", Weight = 120 },
                new VoyageRule { Pattern = @"drop (\d+) additional Exalted Orbs", Weight = 60 },
                new VoyageRule { Pattern = @"drop (\d+) additional Ancient Orbs", Weight = 12 },
                new VoyageRule { Pattern = @"drop (\d+) additional Orbs of Annulment", Weight = 12 },
                new VoyageRule { Pattern = @"drop (\d+) additional Vaal Orbs", Weight = 6 },
                new VoyageRule { Pattern = @"drop (\d+) additional Regal Orbs", Weight = 5 },
                new VoyageRule { Pattern = @"drop (\d+) additional Gemcutter's Prisms", Weight = 5 },
                new VoyageRule { Pattern = @"drop (\d+) additional Blessed Orbs", Weight = 4 },
                new VoyageRule { Pattern = @"drop (\d+) additional Chaos Orbs", Weight = 4 },
                new VoyageRule { Pattern = @"drop (\d+) additional Orbs of Regret", Weight = 2 },
                new VoyageRule { Pattern = @"drop (\d+) additional Chromatic Orbs", Weight = 1 },
                new VoyageRule { Pattern = @"drop (\d+) additional Scarabs", Weight = 15 },
                new VoyageRule { Pattern = @"(\d+)% more Currency found", Weight = 2.0 },
                new VoyageRule { Pattern = @"(\d+)% more Scarabs found", Weight = 1.5 },
                new VoyageRule { Pattern = @"instead drop as Stacked Decks", Weight = 40 },
                new VoyageRule { Pattern = @"chance to drop a Support Gem", Weight = 0.2 },
                new VoyageRule { Pattern = @"a lost Pirate's Locker", Weight = 25 },
                new VoyageRule { Pattern = @"(\d+) Altars to the Goddess", Weight = 12 },
                // Rares are what carry all of the above, so more of them is more of it.
                new VoyageRule { Pattern = @"(\d+)%\s+increased number of Rare [Mm]onsters", Weight = 0.5 },
                // The one that pushes the other way, and unusually it scales with how
                // connected the board is -- the only modifier seen so far that does.
                new VoyageRule { Pattern = @"(\d+)% reduced quantity of items found.*per connection",
                                 Weight = -2.0 },
            ],
        },

        new VoyageProfile
        {
            Name = "gold",
            Description = "Maximise gold.",
            Rules =
            [
                new VoyageRule { Pattern = @"Gold Found:\s*\+?(\d+)", Weight = 1.0 },
                new VoyageRule { Pattern = @"(\d+)%\s+increased Gold found", Weight = 1.0 },
                new VoyageRule { Pattern = @"(\d+)% of Equipment dropped by monsters.*converted to Gold",
                                 Weight = 1.0 },
                new VoyageRule { Pattern = @"(\d+)%\s+increased Pack Size", Weight = 0.5,
                                 Comment = "gold comes off monsters" },
                new VoyageRule { Pattern = @"(\d+)\s+additional packs of", Weight = 1.0 },
            ],
        },

        new VoyageProfile
        {
            Name = "magic monsters",
            Description = "Every monster at least Magic, and as many of them as possible.",
            BoardModifierWeight = 1.5,
            Rules =
            [
                // "are at least Magic" upgrades the whole area, so it is worth far more
                // than a percentage bump to how many Magic monsters spawn.
                new VoyageRule { Pattern = @"are at least Magic", Weight = 60 },
                new VoyageRule { Pattern = @"(\d+)%\s+increased number of Magic Monsters", Weight = 1.0 },
                new VoyageRule { Pattern = @"Magic Monsters.*have an additional modifier", Weight = 30,
                                 Comment = "board modifier: better rolls on every magic pack" },
                // More monsters of any kind means more of them get upgraded.
                new VoyageRule { Pattern = @"Monster Pack Size:\s*\+?(\d+)", Weight = 0.5 },
                new VoyageRule { Pattern = @"(\d+)%\s+increased Pack Size", Weight = 0.75 },
                new VoyageRule { Pattern = @"(\d+)\s+additional packs of", Weight = 1.5 },
                // No penalty on extra rares. "increased number of Rare Monsters" ADDS
                // rares; it does not convert magic ones into them, so it costs this
                // objective nothing and pricing it as a loss would skew boards away from
                // charts that are simply good.
                new VoyageRule { Pattern = @"Item Quantity:\s*\+?(\d+)", Weight = 0.3 },
            ],
        },

        new VoyageProfile
        {
            Name = "high tier",
            Description = "Highest area levels, avoiding the monster mods that most often "
                          + "end a run. The only profile not about loot.",
            AreaLevelWeight = 1.0,
            Rules =
            [
                // Weighted by what genuinely kills, not by what sounds bad. Every wording
                // is present in the generated table.
                new VoyageRule { Pattern = @"Players have -?(\d+)% to all maximum Resistances", Weight = -3,
                                 Comment = "the roll is negative; match the digits either way" },
                new VoyageRule { Pattern = @"Monsters are Hexproof", Weight = -10 },
                new VoyageRule { Pattern = @"less effect of Curses on Monsters", Weight = -6 },
                new VoyageRule { Pattern = @"Monsters cannot be Stunned", Weight = -4 },
                new VoyageRule { Pattern = @"Monsters cannot be Taunted", Weight = -6 },
                new VoyageRule { Pattern = @"Speed cannot be modified to below Base Value", Weight = -5 },
                new VoyageRule { Pattern = @"Monster Damage Penetrates (\d+)% Elemental Resistances", Weight = -1 },
                new VoyageRule { Pattern = @"(\d+)% more Monster Life", Weight = -0.15 },
                new VoyageRule { Pattern = @"Monsters gain (\d+)% of Maximum Life as Extra Maximum Energy Shield",
                                 Weight = -0.1 },
                new VoyageRule { Pattern = @"(\d+)% to Monster Critical Strike Multiplier", Weight = -0.2 },
                new VoyageRule { Pattern = @"Monsters steal Power, Frenzy and Endurance charges on Hit", Weight = -5 },
                new VoyageRule { Pattern = @"Monsters' skills Chain (\d+) additional times", Weight = -2 },
                new VoyageRule { Pattern = @"Monsters fire (\d+) additional Projectiles", Weight = -2 },
                new VoyageRule { Pattern = @"Area has patches of", Weight = -3 },
                new VoyageRule { Pattern = @"(\d+)% increased Monster Damage", Weight = -0.2 },
            ],
        },
    ];

    public void Dispose()
    {
        _debounce?.Cancel();
        _watcher?.Dispose();
    }
}
