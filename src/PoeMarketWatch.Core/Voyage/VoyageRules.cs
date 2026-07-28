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
    public static List<VoyageProfile> Defaults() =>
    [
        new VoyageProfile
        {
            Name = "sulphur",
            Description = "Maximise Dead Man's Sulphur.",
            Rules =
            [
                new VoyageRule { Pattern = @"Dead Man's Sulphur:\s*\+?(\d+(?:\.\d+)?)", Weight = 5.0,
                                 Comment = "the chart's own sulphur stat, the main source" },
                new VoyageRule { Pattern = @"(\d+)%\s+increased Dead Man's Sulphur", Weight = 1.0 },
                new VoyageRule { Pattern = @"increased Dead Man's Sulphur", Weight = 5.0,
                                 Comment = "flat credit for a sulphur mod the number pattern misses; "
                                         + "requires 'increased' so it cannot double-fire on the stat line" },
            ],
        },
        new VoyageProfile
        {
            Name = "pack size",
            Description = "Maximise monsters, including adjacency modifiers.",
            BoardModifierWeight = 1.5,
            Rules =
            [
                new VoyageRule { Pattern = @"Monster Pack Size:\s*\+?(\d+)", Weight = 1.0 },
                new VoyageRule { Pattern = @"(\d+)\s+additional packs", Weight = 4.0 },
                new VoyageRule { Pattern = @"(\d+)%\s+increased Pack Size", Weight = 1.0 },
                new VoyageRule { Pattern = @"(\d+)%\s+increased Monster", Weight = 0.5 },
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
                new VoyageRule { Pattern = @"(\d+)%\s+increased Quantity", Weight = 1.0 },
                new VoyageRule { Pattern = @"(\d+)%\s+increased Rarity", Weight = 0.5 },
            ],
        },
        new VoyageProfile
        {
            Name = "safe",
            Description = "Prefer high tier, penalise dangerous monster modifiers.",
            AreaLevelWeight = 1.0,
            Rules =
            [
                new VoyageRule { Pattern = @"Monsters cannot be Taunted", Weight = -8 },
                new VoyageRule { Pattern = @"Monsters reflect", Weight = -25 },
                new VoyageRule { Pattern = @"cannot be modified to below Base Value", Weight = -5 },
                new VoyageRule { Pattern = @"Monsters Hinder on Hit", Weight = -3 },
            ],
        },
    ];

    public void Dispose()
    {
        _debounce?.Cancel();
        _watcher?.Dispose();
    }
}
