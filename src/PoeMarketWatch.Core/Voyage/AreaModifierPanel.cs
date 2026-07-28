using System.Text.Json;
using System.Text.RegularExpressions;

namespace PoeMarketWatch.Core.Voyage;

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

        public static string DefaultPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PoeMarketWatch", "area-modifier-panel.json");

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

    /// <summary>Leading bullet glyphs, which OCR renders as a stray '.' or '·'.</summary>
    private static readonly Regex Bullet = new(@"^[\s.·•*\-–—]+", RegexOptions.Compiled);

    /// <summary>
    /// Turn raw OCR lines into modifier lines.
    ///
    /// Returns empty when the panel was showing its placeholder, which the caller must
    /// treat as "nothing hovered" rather than "no modifiers" -- silently recording an
    /// empty result would tick a square off the checklist without reading it.
    /// </summary>
    public static IReadOnlyList<string> CleanLines(IEnumerable<string>? lines)
    {
        if (lines is null) return [];

        var kept = new List<string>();
        var sawPlaceholder = false;

        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var line = Bullet.Replace(raw, "").Trim();
            if (line.Length == 0) continue;

            if (Heading.IsMatch(raw) || Heading.IsMatch(line)) continue;
            if (Placeholder.IsMatch(line)) { sawPlaceholder = true; continue; }

            // Single stray characters are OCR noise off panel borders, never a modifier.
            if (line.Length < 4) continue;

            kept.Add(line);
        }

        // If the placeholder is on screen, no square is hovered and NOTHING here is a
        // modifier -- including the fragments its own wrapped text leaves behind, such as
        // a lone "Modifiers" on the last line.
        return sawPlaceholder ? [] : Join(kept);
    }

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
        var last = line.Split(' ')[^1];
        return last is "additional" or "of" or "to" or "the" or "and" or "increased"
                    or "more" or "with" or "have" or "contain" or "a" or "an";
    }
}
