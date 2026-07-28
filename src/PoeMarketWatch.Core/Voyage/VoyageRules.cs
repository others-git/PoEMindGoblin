using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PoeMarketWatch.Core.Voyage;

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
    /// Charged per square left off the main route.
    ///
    /// Edge matching permits a dead corner -- an End pointing at the border, closed
    /// elsewhere, is legal and connects to nothing. Whatever chart lands there is cut
    /// off, so it is worth something to avoid, but not at any price: a board where one
    /// stranded square buys a far better nine charts can still be the right board. Set
    /// to 0 to stop caring, or high to forbid it in practice.
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
        "PoeMarketWatch", "voyage-rules.json");

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
    /// Every pattern here is written against REAL chart text, taken from captured
    /// sessions and from poedb's mod tables for the three Voyage chart bases. Two things
    /// that came out of that and would otherwise be invisible bugs:
    ///
    ///   * GGG misspells "Quantity" as "Qauntity" -- but only in the global lines,
    ///     "8% increased Qauntity of Items found in all Voyage Areas". The adjacent and
    ///     in-area versions spell it correctly. A rule matching "Quantity" therefore
    ///     scores some rolls and silently misses others, so these match either spelling.
    ///   * The headline stats are AGGREGATES of the affix lines, so the stat is scored
    ///     and the contributing lines are dropped at parse time. Scoring both trebles a
    ///     chart carrying three sulphur affixes.
    ///
    /// Adjacency patterns matter as much as the rest: a chart's Adjacent Modifier is
    /// scored with these same rules and paid to its neighbours, which is what decides
    /// whether it belongs in the centre or a corner.
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
                                 Comment = "the chart's own sulphur total, the main source" },
                new VoyageRule { Pattern = @"(\d+)%\s+increased Dead Man's Sulphur", Weight = 2.0,
                                 Comment = "global and adjacent sulphur rolls" },
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
                new VoyageRule { Pattern = @"(\d+)%\s+increased Pack Size", Weight = 1.5,
                                 Comment = "global and adjacent pack size" },
                new VoyageRule { Pattern = @"(\d+)\s+additional packs", Weight = 3.0,
                                 Comment = "Crabs, Octopi, Sea Beasts -- all worded this way" },
                new VoyageRule { Pattern = @"(\d+)%\s+increased number of (?:Rare|Magic) Monsters",
                                 Weight = 0.75 },
                new VoyageRule { Pattern = @"(\d+)\s+additional Imprisoned Monsters", Weight = 2.0 },
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
                                 Comment = "matches GGG's 'Qauntity' typo in the global lines" },
                new VoyageRule { Pattern = @"(\d+)%\s+increased Rarity of Items", Weight = 0.75 },
            ],
        },
        new VoyageProfile
        {
            Name = "loot boxes",
            Description = "Strongboxes, barrels, treasure and unique-drop conversions.",
            BoardModifierWeight = 1.5,
            Rules =
            [
                new VoyageRule { Pattern = @"(\d+)\s+additional (?:Diviner's |Arcanist's |Operative's )?Strongboxes",
                                 Weight = 6.0 },
                new VoyageRule { Pattern = @"(\d+)\s+additional Clusters of Barrels", Weight = 1.0 },
                new VoyageRule { Pattern = @"(\d+)\s+additional Treasure", Weight = 4.0 },
                new VoyageRule { Pattern = @"(\d+)\s+additional Golden Lanterns", Weight = 3.0 },
                new VoyageRule { Pattern = @"(\d+)%\s+chance to instead drop as a Unique", Weight = 2.0 },
                new VoyageRule { Pattern = @"(\d+)\s+additional cages? of Tormented Spirits", Weight = 5.0 },
                new VoyageRule { Pattern = @"additional cage of Tormented Spirits", Weight = 5.0,
                                 Comment = "the single-cage roll is worded without a number" },
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
                new VoyageRule { Pattern = @"(\d+)% of Equipment dropped by monsters in adjacent Areas is converted to Gold",
                                 Weight = 1.0 },
            ],
        },
        new VoyageProfile
        {
            Name = "safe",
            Description = "Prefer high tier, avoid the monster mods that actually kill you.",
            AreaLevelWeight = 1.0,
            Rules =
            [
                // Weighted by what genuinely ends runs, not by what sounds bad. All of
                // these wordings are taken from real charts.
                new VoyageRule { Pattern = @"Players have -(\d+)% to all maximum Resistances", Weight = -6 },
                new VoyageRule { Pattern = @"Monsters are Hexproof", Weight = -10 },
                new VoyageRule { Pattern = @"less effect of Curses on Monsters", Weight = -6 },
                new VoyageRule { Pattern = @"Monsters cannot be Stunned", Weight = -4 },
                new VoyageRule { Pattern = @"Monsters cannot be Taunted", Weight = -6 },
                new VoyageRule { Pattern = @"Action Speed cannot be modified to below Base Value", Weight = -5 },
                new VoyageRule { Pattern = @"Monster Damage Penetrates (\d+)% Elemental Resistances", Weight = -1 },
                new VoyageRule { Pattern = @"(\d+)% more Monster Life", Weight = -0.15 },
                new VoyageRule { Pattern = @"Monsters gain (\d+)% of Maximum Life as Extra Maximum Energy Shield",
                                 Weight = -0.1 },
                new VoyageRule { Pattern = @"\+(\d+)% to Monster Critical Strike Multiplier", Weight = -0.2 },
                new VoyageRule { Pattern = @"Monsters steal Power, Frenzy and Endurance charges on Hit", Weight = -5 },
                new VoyageRule { Pattern = @"Monsters' skills Chain (\d+) additional times", Weight = -2 },
                new VoyageRule { Pattern = @"Monsters fire (\d+) additional Projectiles", Weight = -2 },
                new VoyageRule { Pattern = @"patches of Shocked Ground", Weight = -3 },
            ],
        },
    ];

    public void Dispose()
    {
        _debounce?.Cancel();
        _watcher?.Dispose();
    }
}
