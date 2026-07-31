using System.Text.Json;

namespace MindGoblin.Core.Voyage;

/// <summary>
/// Reads the "L:83" area-level caption printed under each chart glyph.
///
/// This is template matching, unlike <see cref="ChartPanelReader"/> which reads topology.
/// Text has no topology to exploit, and the caption is rendered in PoE's UI font (Fontin),
/// which is not installed on Windows -- a sweep of every system .ttf at every plausible
/// size missed by 134 of ~130 ink pixels on the best candidate, so the glyphs cannot be
/// re-rendered and must come from a real capture.
///
/// Two things make naive matching fail, both learned from real pixels:
///   * Digits TOUCH. "82" is one unbroken 17-pixel run with no gap to split on, while
///     "83" has a one-pixel gap. Splitting on blank columns, or halving by width, both
///     mis-segment. Matching instead walks left to right and advances by the width of
///     whichever template fits best, so touching and separated pairs decode identically.
///   * The text baseline drifts a pixel or two between cells. Every candidate is therefore
///     tried at a range of vertical offsets, not just one.
///
/// KNOWN GAP: '5' still has no template -- the training captures have yet to contain
/// one -- so a level with a 5 reads as null rather than a wrong number: a missing level
/// is recoverable, a fabricated one silently corrupts the plan. '0' and '9' were carved
/// from a 3.29.1 capture (L:80 and L:79 captions) the moment they first appeared.
/// <see cref="Learn"/> and the user template file close such gaps without a rebuild.
/// </summary>
public sealed class LevelReader
{
    public sealed record Options
    {
        /// <summary>Top of the caption strip, relative to the top of the cell.</summary>
        public int TextOffsetY { get; init; } = 22;

        /// <summary>Height of the strip. Templates are this tall.</summary>
        public int TextHeight { get; init; } = 20;

        /// <summary>Horizontal window around the cell centre that holds the caption.</summary>
        public int TextLeft { get; init; } = -40;
        public int TextRight { get; init; } = 10;

        /// <summary>
        /// Width of the fixed "L:" prefix, measured from the first ink column:
        /// L(7) gap(1) colon(3) gap(2). Digits begin after it.
        /// </summary>
        public int PrefixWidth { get; init; } = 13;

        /// <summary>Rows of slack searched above and below, for baseline drift.</summary>
        public int BaselinePad { get; init; } = 4;

        /// <summary>
        /// Worst tolerated mismatch, as wrong pixels over template ink. Correct digits
        /// score 0.0-0.1 on the training capture; the nearest wrong digit scores 0.7.
        /// The gap is wide, so this sits well clear of both.
        /// </summary>
        public double MaxError { get; init; } = 0.35;

        /// <summary>A pixel is text when it is bright and close to grey.</summary>
        public int LightMin { get; init; } = 130;
        public int LightSpread { get; init; } = 60;

        public int ReferenceWidth { get; init; } = 2560;
        public int ReferenceHeight { get; init; } = 1440;

        public Options ScaledTo(int width, int height)
        {
            var sy = height / (double)ReferenceHeight;
            var s = Math.Min(width / (double)ReferenceWidth, sy);
            return this with
            {
                TextOffsetY = (int)Math.Round(TextOffsetY * sy),
                TextHeight = (int)Math.Round(TextHeight * s),
                TextLeft = (int)Math.Round(TextLeft * s),
                TextRight = (int)Math.Round(TextRight * s),
                PrefixWidth = (int)Math.Round(PrefixWidth * s),
                ReferenceWidth = width,
                ReferenceHeight = height,
            };
        }
    }

    /// <summary>One digit's ink mask. Rows are the strip height, columns its own width.</summary>
    public sealed record DigitTemplate(char Digit, bool[,] Mask)
    {
        public int Width => Mask.GetLength(1);
        public int Height => Mask.GetLength(0);
        public int Ink { get; } = Count(Mask);

        private static int Count(bool[,] m)
        {
            var n = 0;
            foreach (var b in m) if (b) n++;
            return n;
        }

        public static DigitTemplate FromRows(char digit, IReadOnlyList<string> rows)
        {
            var h = rows.Count;
            var w = rows.Max(r => r.Length);
            var mask = new bool[h, w];
            for (var y = 0; y < h; y++)
                for (var x = 0; x < rows[y].Length; x++)
                    mask[y, x] = rows[y][x] == '#';
            return new DigitTemplate(digit, mask);
        }

        public IReadOnlyList<string> ToRows()
        {
            var rows = new string[Height];
            for (var y = 0; y < Height; y++)
            {
                var chars = new char[Width];
                for (var x = 0; x < Width; x++) chars[x] = Mask[y, x] ? '#' : '.';
                rows[y] = new string(chars);
            }
            return rows;
        }
    }

    private readonly Options _o;
    private readonly List<DigitTemplate> _templates;

    public LevelReader(Options? options = null, IEnumerable<DigitTemplate>? templates = null)
    {
        _o = options ?? new Options();
        _templates = (templates ?? BuiltInTemplates()).ToList();
    }

    /// <summary>Digits this reader can recognise. Anything else reads as null.</summary>
    public IReadOnlyList<char> KnownDigits => _templates.Select(t => t.Digit).Order().ToList();

    /// <summary>
    /// Read the level under a cell whose glyph is centred at (cx, cellTop).
    /// Returns null when the caption is absent or contains an untrained digit.
    /// </summary>
    public int? Read(IPixels px, int cx, int cellTop)
    {
        var o = px.Width == _o.ReferenceWidth && px.Height == _o.ReferenceHeight
            ? _o
            : _o.ScaledTo(px.Width, px.Height);

        var strip = ExtractDigits(px, o, cx, cellTop);
        if (strip is null) return null;

        var text = Decode(strip, o);
        return int.TryParse(text, out var level) ? level : null;
    }

    /// <summary>
    /// The digit pixels of the caption: the strip with the "L:" prefix cut off, padded
    /// vertically so the baseline search has room. Null when there is no caption.
    /// </summary>
    public bool[,]? ExtractDigits(IPixels px, Options o, int cx, int cellTop)
    {
        var top = cellTop + o.TextOffsetY - o.BaselinePad;
        var height = o.TextHeight + 2 * o.BaselinePad;
        var x0 = cx + o.TextLeft;
        var x1 = cx + o.TextRight;

        if (top < 0 || x0 < 0 || top + height > px.Height || x1 > px.Width) return null;

        var w = x1 - x0;
        var band = new bool[height, w];
        var firstInk = -1;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var (r, g, b) = px.At(x0 + x, top + y);
                var min = Math.Min(r, Math.Min(g, b));
                var max = Math.Max(r, Math.Max(g, b));
                if (min <= o.LightMin || max - min >= o.LightSpread) continue;
                band[y, x] = true;
                if (firstInk < 0 || x < firstInk) firstInk = x;
            }
        }

        // Anchor on the "L", not on the window edge: the caption is centred under the
        // glyph, so its absolute x shifts with the digit count.
        if (firstInk < 0) return null;
        var start = firstInk + o.PrefixWidth;
        if (start >= w) return null;

        var digits = new bool[height, w - start];
        var any = false;
        for (var y = 0; y < height; y++)
            for (var x = start; x < w; x++)
                if (band[y, x]) { digits[y, x - start] = true; any = true; }

        return any ? digits : null;
    }

    /// <summary>
    /// Walk left to right, consuming whichever template fits best at each position.
    /// Returns null the moment nothing fits -- a partial number is not a number.
    /// </summary>
    public string? Decode(bool[,] digits, Options? options = null)
    {
        var o = options ?? _o;
        var height = digits.GetLength(0);
        var width = digits.GetLength(1);
        var pad = height - o.TextHeight;
        if (pad < 0) return null;

        var text = new System.Text.StringBuilder();
        var x = 0;
        while (x < width)
        {
            if (!ColumnHasInk(digits, x)) { x++; continue; }

            DigitTemplate? best = null;
            var bestError = double.MaxValue;
            foreach (var t in _templates)
            {
                if (x + t.Width > width || t.Ink == 0) continue;
                for (var dy = 0; dy <= pad; dy++)
                {
                    var error = Mismatch(digits, t, x, dy);
                    if (error >= bestError) continue;
                    bestError = error;
                    best = t;
                }
            }

            if (best is null || bestError > o.MaxError) return null;
            text.Append(best.Digit);
            x += best.Width;
        }

        return text.Length > 0 ? text.ToString() : null;
    }

    private static bool ColumnHasInk(bool[,] m, int x)
    {
        for (var y = 0; y < m.GetLength(0); y++) if (m[y, x]) return true;
        return false;
    }

    /// <summary>Wrong pixels over template ink, so wide digits are not penalised.</summary>
    private static double Mismatch(bool[,] digits, DigitTemplate t, int x, int dy)
    {
        var wrong = 0;
        for (var y = 0; y < t.Height; y++)
            for (var tx = 0; tx < t.Width; tx++)
                if (digits[y + dy, x + tx] != t.Mask[y, tx]) wrong++;
        return wrong / (double)t.Ink;
    }

    /// <summary>
    /// Add a template carved from a capture, so an unseen digit can be taught without a
    /// rebuild. The mask must be <see cref="Options.TextHeight"/> rows tall.
    /// </summary>
    public LevelReader Learn(DigitTemplate template) =>
        new(_o, _templates.Where(t => t.Digit != template.Digit).Append(template));

    // ---- persistence -------------------------------------------------------------

    public static string DefaultPath => SettingsFolder.FileIn("level-digits.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Built-in templates, overlaid with any the user has captured. The file only needs
    /// to carry the digits it adds or corrects, so teaching the reader a '9' is a
    /// one-entry file rather than a full redefinition.
    /// </summary>
    public static LevelReader LoadWithUserTemplates(string? path = null, Options? options = null)
    {
        path ??= DefaultPath;
        var reader = new LevelReader(options);
        if (!File.Exists(path)) return reader;

        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string[]>>(
                File.ReadAllText(path), Json);
            if (loaded is null) return reader;
            foreach (var (key, rows) in loaded)
            {
                if (key.Length != 1 || !char.IsDigit(key[0]) || rows.Length == 0) continue;
                reader = reader.Learn(DigitTemplate.FromRows(key[0], rows));
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A broken template file must not stop the panel being read at all.
            return new LevelReader(options);
        }
        return reader;
    }

    public void SaveTemplates(string? path = null)
    {
        path ??= DefaultPath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var payload = _templates.OrderBy(t => t.Digit)
            .ToDictionary(t => t.Digit.ToString(), t => t.ToRows());
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(payload, Json));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Carved from a real 2560x1440 capture of the chart panel and verified digit by
    /// digit against the rendered captions of all 24 charts in it.
    /// </summary>
    public static IReadOnlyList<DigitTemplate> BuiltInTemplates() =>
        BuiltIn.Select(kv => DigitTemplate.FromRows(kv.Key, kv.Value)).ToList();

    private static readonly Dictionary<char, string[]> BuiltIn = new()
    {
        ['0'] =
        [
            "..........", "..........", "..........", "..........", "...####...",
            "..######..", ".##...###.", "##.....##.", "##.....###", "##.....###",
            "##.....###", "##.....###", "##.....##.", "###....##.", ".###..##..",
            "..#####...", "...##.....", "..........", "..........", "..........",
        ],
        ['1'] =
        [
            ".....", ".....", ".....", ".....", ".....", ".....",
            "..###", "#####", "#####", "..###", "..###", "..###",
            "..###", "..###", "..###", "..###", "..###", "..###",
            ".....", ".....",
        ],
        ['2'] =
        [
            "........", "........", "........", "........", "........", "........",
            "..####..", ".######.", ".##..###", ".....###", ".....###", ".....##.",
            "....###.", "....##..", "...##...", "..##....", ".#######", "########",
            "........", "........",
        ],
        ['3'] =
        [
            ".......", ".......", ".......", ".......", ".####..", "######.",
            "#...###", "....##.", "....##.", "..###..", "..####.", "....###",
            "....###", "....###", "....###", "...###.", "#####..", "###....",
            ".......", ".......",
        ],
        ['4'] =
        [
            "..........", "..........", "..........", "..........", "..........",
            "..........", "......##..", ".....###..", "....####..", "....####..",
            "...#####..", "..##.###..", "..##.###..", ".##..###..", ".#########",
            "##########", ".....###..", ".....###..", ".....###..", "......##..",
        ],
        ['6'] =
        [
            ".........", ".........", ".........", ".........", ".....##..",
            "...#####.", "..###...#", ".###.....", ".##......", "###..#...",
            "########.", "###...###", "###...###", "###...###", "###....##",
            ".##...###", ".###..##.", "..#####..", "....#....", ".........",
        ],
        ['7'] =
        [
            ".......", ".......", ".......", ".......", "#######", "#######",
            "..#####", "....##.", "....##.", "...##..", "...##..", "..##...",
            "..##...", "..##...", ".##....", ".##....", "##.....", "##.....",
            ".......", ".......",
        ],
        ['8'] =
        [
            ".........", ".........", "...###...", ".######..", ".##...##.",
            "###...##.", "###...##.", ".###.##..", "..####...", "..#####..",
            ".##.####.", "##...###.", "##....###", "##....##.", "###..###.",
            ".######..", "...##....", ".........", ".........", ".........",
        ],
        ['9'] =
        [
            ".........", ".........", ".........", ".........", "..####...",
            ".#######.", ".##...##.", "##....##.", "##....###", "###...###",
            "###...###", ".########", "..#######", "......##.", "......##.",
            ".....###.", "#######..", ".#####...", ".........", ".........",
        ],
    };
}
