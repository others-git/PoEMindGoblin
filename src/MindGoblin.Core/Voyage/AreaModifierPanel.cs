using System.Text.Json;
using System.Text.RegularExpressions;

namespace MindGoblin.Core.Voyage;

/// <summary>
/// The in-game "Area Modifiers" panel, and how to make sense of what is read off it.
///
/// The figurines carved around the board cannot be copied -- the game only puts an item
/// on the clipboard, and a figurine is not an item. But the game already solves this for
/// us, and says so in its own placeholder text:
///
///     "Hover a square of the Voyage Board to see the relevant Area Modifiers"
///
/// So the modifiers are readable per SQUARE, aggregated by the game, in one fixed panel.
/// That is better than chasing figurines even if they could be copied: it is nine hovers
/// instead of twelve, it needs no mapping from figurine to square, and it yields exactly
/// what the solver wants -- what a chart placed on this square would actually get.
///
/// This class holds the panel's location and the text cleanup. The OCR itself lives in
/// the app, so everything here stays testable without a screen.
/// </summary>
public sealed class AreaModifierPanel
{
    public sealed record Options
    {
        /// <summary>
        /// Panel bounds as FRACTIONS of the screen, so a different resolution still
        /// lands on it. Measured from a 2560x1440 capture where the heading sits at
        /// x 492-668, y 387-407 and the body runs from x 363 to x 750.
        /// </summary>
        public double Left { get; init; } = 0.132;     // 338 / 2560
        public double Top { get; init; } = 0.262;      // 377 / 1440
        public double Right { get; init; } = 0.310;    // 794 / 2560
        public double Bottom { get; init; } = 0.730;   // 1051 / 1440

        /// <summary>
        /// Centre of board square 1 (top-left) in SCREEN PIXELS, and the spacing to
        /// its neighbours -- the sweep hovers each square with these so the panel
        /// shows that square's modifiers. Measured off the same 2560x1440 layout as
        /// the panel bounds; recalibrate here if the sweep hovers between squares.
        /// </summary>
        public int BoardOriginX { get; init; } = 1057;
        public int BoardOriginY { get; init; } = 534;
        public int BoardPitch { get; init; } = 193;

        /// <summary>
        /// Enlargement before recognition. The panel is dark text on light parchment,
        /// which the engine reads verbatim even at 1x -- measured on a real capture, 1x,
        /// 2x and 3x all returned the text exactly. 2x is a cheap margin for smaller
        /// modifier lines without making the bitmap large.
        /// </summary>
        public int Upscale { get; init; } = 2;

        public (int X, int Y, int Width, int Height) ToPixels(int screenWidth, int screenHeight)
        {
            var x = (int)Math.Round(Left * screenWidth);
            var y = (int)Math.Round(Top * screenHeight);
            var w = (int)Math.Round((Right - Left) * screenWidth);
            var h = (int)Math.Round((Bottom - Top) * screenHeight);
            return (x, y, Math.Max(1, w), Math.Max(1, h));
        }

        public static string DefaultPath => SettingsFolder.FileIn("area-modifier-panel.json");

        private static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        public static Options Load(string? path = null)
        {
            path ??= DefaultPath;
            if (!File.Exists(path)) return new Options();
            try
            {
                var loaded = JsonSerializer.Deserialize<Options>(File.ReadAllText(path), Json);
                // A zero-width window would read nothing and look like "no modifiers".
                return loaded is { } o && o.Right > o.Left && o.Bottom > o.Top
                    ? loaded
                    : new Options();
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                return new Options();
            }
        }

        public void Save(string? path = null)
        {
            path ??= DefaultPath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, path, overwrite: true);
        }

        public static void WriteDefaultsIfMissing(string? path = null)
        {
            path ??= DefaultPath;
            if (!File.Exists(path)) new Options().Save(path);
        }
    }

    /// <summary>The panel's own heading, which is chrome rather than a modifier.</summary>
    private static readonly Regex Heading =
        new(@"^[\s.·•]*Area\s+Modifiers?[\s.:]*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The placeholder shown when no square is hovered. Recognising it matters: without
    /// it, capturing too early stores the instructions as though they were modifiers.
    /// </summary>
    private static readonly Regex Placeholder =
        new(@"Hover\s+a\s+square|Board\s+to\s+see|relevant\s+Area",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The panel's own sub-heading. It groups what follows rather than being a modifier,
    /// and was being recorded as one.
    /// </summary>
    private static readonly Regex SubHeading =
        new(@"^Modifiers?\s+from\s+\w+\s*:?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Leading bullet glyphs, which OCR renders as a stray '.' or '·'.</summary>
    private static readonly Regex Bullet = new(@"^[\s.·•*\-–—]+", RegexOptions.Compiled);

    /// <summary>What the panel was showing when it was captured.</summary>
    public enum PanelState
    {
        /// <summary>The heading was not found: wrong region, or the screen was not open.</summary>
        NotFound,

        /// <summary>"Hover a square of the Voyage Board..." -- nothing is hovered yet.</summary>
        Placeholder,

        /// <summary>A square is hovered and it genuinely has no modifiers.</summary>
        NoModifiers,

        /// <summary>A square is hovered and these are its modifiers.</summary>
        Modifiers,
    }

    public readonly record struct Reading(PanelState State, IReadOnlyList<string> Lines)
    {
        /// <summary>True once the square has actually been read, empty result included.</summary>
        public bool IsRead => State is PanelState.NoModifiers or PanelState.Modifiers;
    }

    /// <summary>
    /// Interpret a capture of the panel.
    ///
    /// The three empty-looking outcomes are NOT the same and conflating them breaks the
    /// read pass. The centre square of a 3x3 touches none of the twelve perimeter
    /// figurines, so it has no board modifiers at all -- an empty panel there is the
    /// correct answer, and treating it as "nothing hovered" left the checklist stuck on
    /// square 5 forever. Equally, an empty result because the capture region is wrong
    /// must NOT be recorded as "this square has no modifiers".
    ///
    /// The panel's heading is what tells them apart: it is chrome, always on screen while
    /// the Voyage window is open, whether or not a square is hovered.
    /// </summary>
    public static Reading Read(IEnumerable<string>? lines)
    {
        if (lines is null) return new Reading(PanelState.NotFound, []);

        var kept = new List<string>();
        var sawHeading = false;
        var sawPlaceholder = false;

        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var line = Bullet.Replace(raw, "").Trim();
            if (line.Length == 0) continue;

            if (Heading.IsMatch(raw) || Heading.IsMatch(line)) { sawHeading = true; continue; }
            if (SubHeading.IsMatch(line)) { sawHeading = true; continue; }
            if (Placeholder.IsMatch(line)) { sawPlaceholder = true; continue; }

            // Single stray characters are OCR noise off panel borders, never a modifier.
            if (line.Length < 4) continue;

            kept.Add(line);
        }

        // No heading and nothing else recognisable: the capture did not find the panel.
        if (!sawHeading && !sawPlaceholder && kept.Count == 0)
            return new Reading(PanelState.NotFound, []);

        // The placeholder means no square is hovered. Everything else on the panel then
        // belongs to it too, including the lone "Modifiers" its wrapped text leaves behind.
        if (sawPlaceholder) return new Reading(PanelState.Placeholder, []);

        var joined = Join(kept);
        return new Reading(
            joined.Count == 0 ? PanelState.NoModifiers : PanelState.Modifiers, joined);
    }

    /// <summary>The modifier lines alone, for callers that do not care why it was empty.</summary>
    public static IReadOnlyList<string> CleanLines(IEnumerable<string>? lines) =>
        Read(lines).Lines;

    /// <summary>
    /// Re-run the wrap stitching over lines that were STORED split.
    ///
    /// Sessions saved before the joiner learned a wording keep the fragments forever --
    /// a real one held "Area contains 2 additional Treasure" / "Anchors" on one square
    /// and the Golden/Lanterns split on another, each scoring zero. Joining is
    /// idempotent and its safety rule (never join on a word any modifier ends with)
    /// holds for stored data exactly as for fresh reads, so old sessions heal on load.
    /// </summary>
    public static IReadOnlyList<string> Restitch(IReadOnlyList<string> lines) => Join(lines);

    /// <summary>
    /// Rejoin modifier text that OCR split across lines.
    ///
    /// The panel wraps a modifier over two or three lines, and OCR reports each visual
    /// line separately -- "Adjacent Areas contain 8 additional" / "packs of Sea Beasts".
    /// A rule matching "(\d+) additional packs" would miss that entirely, so wrapped
    /// fragments are stitched back together before anything scores them.
    /// </summary>
    private static IReadOnlyList<string> Join(IReadOnlyList<string> lines)
    {
        var result = new List<string>();
        foreach (var line in lines)
        {
            // A continuation starts lower-case, or the previous line ended mid-phrase.
            var continues = result.Count > 0
                            && (char.IsLower(line[0]) || EndsMidPhrase(result[^1]));
            if (continues) result[^1] = result[^1] + " " + line;
            else result.Add(line);
        }
        return result;
    }

    private static bool EndsMidPhrase(string line)
    {
        var last = Trim(line.Split(' ')[^1]);
        return last.Length > 0 && MidPhraseWords.Value.Contains(last);
    }

    /// <summary>
    /// The words a wrapped modifier can dangle, DERIVED from the corpus instead of
    /// curated. A hand list failed twice over: "Rare Monsters in Area drop Dead" /
    /// "Man's Sulphur" stayed split because "Dead" was not listed, and auditing the
    /// corpus for every such gap demanded 66 more words -- two of which ("chance",
    /// "Voyage") real modifiers END with, so listing them would have glued complete
    /// modifiers together.
    ///
    /// The safe rule generates itself: a word joins iff it appears MID-LINE in some
    /// modifier and NO modifier ends with it (checked across the template wordings,
    /// the Area panel's resolved forms, and the singular forms of every last word).
    /// Ambiguous words -- Sulphur, Areas, Chance -- are excluded automatically, so a
    /// wrap after one stays split rather than risking a false merge, and a new mod
    /// table regenerates the sets with no code change.
    /// </summary>
    private static readonly Lazy<HashSet<string>> MidPhraseWords = new(() =>
    {
        var interior = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var terminal = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var template in ChartRewards.Current.Lines.Keys
                     .Concat(ChartRewards.Current.BoardLines.Keys))
            foreach (var line in Variants(template))
            {
                var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(Trim).Where(w => w.Length > 0).ToArray();
                if (words.Length == 0) continue;
                for (var i = 0; i < words.Length - 1; i++) interior.Add(words[i]);

                terminal.Add(words[^1]);
                // The game writes the singular when a roll is 1, so a line can also end
                // one 's' short of its template: "...an additional Divine Orb".
                if (words[^1].EndsWith('s')) terminal.Add(words[^1][..^1]);
            }

        // Function words carry unknown modifiers (a post-patch mod the corpus has not
        // met yet) -- still subject to the never-ends-a-line rule.
        interior.UnionWith(["additional", "of", "to", "the", "and", "increased", "more",
                            "with", "have", "contain", "contains", "a", "an", "in",
                            "per", "as", "by", "not", "be", "will", "drop", "drops"]);

        interior.ExceptWith(terminal);
        return interior;
    });

    /// <summary>
    /// The wordings a template can arrive in off the Area panel. The panel RESOLVES
    /// modifiers for the hovered square, and one of its habits is dropping the location
    /// suffix entirely -- a real session stored "32% increased Pack Size", full stop --
    /// so the suffix-stripped form must contribute its last word to the terminal set or
    /// the resolved line gets glued to whatever follows it.
    /// </summary>
    private static IEnumerable<string> Variants(string template)
    {
        var filled = template.Replace("#", "12");
        yield return filled;
        if (filled.Contains(" in adjacent Areas"))
        {
            yield return filled.Replace(" in adjacent Areas", " in Area");
            yield return filled.Replace(" in adjacent Areas", "");
        }
        if (filled.Contains(" in all Voyage Areas"))
            yield return filled.Replace(" in all Voyage Areas", "");
        if (filled.Contains("Adjacent Areas contain"))
            yield return filled.Replace("Adjacent Areas contains", "Area contains")
                               .Replace("Adjacent Areas contain", "Area contains");
    }

    private static string Trim(string word) => word.Trim(',', '.', ':', ';');
}
