using System.Text.Json;

namespace MindGoblin.Core.Voyage;

/// <summary>
/// Where the board's figurines sit and which squares each one touches.
///
/// The Voyage board is ringed with carved figures -- 12 on a 3x3 board. Every one of them
/// carries an ADJACENCY modifier ("Adjacent Areas contain 8 additional packs of Sea
/// Beasts"); none is global, so a figurine only ever affects the squares it touches.
/// They are fixed to the board, unlike a chart's own Adjacent Modifier which travels with
/// the chart. Both exist and both feed the same objective.
///
/// Deliberately data, not constants: board size, figurine count and which cells each
/// touches are all league-mechanic details that GGG can change, and a hardcoded 3x3 with
/// 12 positions would need a rebuild the day it does.
/// </summary>
public sealed class BoardLayout
{
    public int Rows { get; set; } = 3;
    public int Cols { get; set; } = 3;

    /// <summary>Figurine slots, in the order they should be read off screen.</summary>
    public List<FigurineSlot> Figurines { get; set; } = new();

    public sealed class FigurineSlot
    {
        /// <summary>1-based, matching the order a read pass ticks them off.</summary>
        public int Index { get; set; }

        /// <summary>Which board edge it sits against, for describing it to the user.</summary>
        public string Edge { get; set; } = "";

        /// <summary>Board squares this figurine's modifier applies to.</summary>
        public List<CellRef> Adjacent { get; set; } = new();
    }

    public sealed class CellRef
    {
        public int Row { get; set; }
        public int Col { get; set; }
        public Cell ToCell() => new(Row, Col);
    }

    /// <summary>
    /// The standard ring: one figurine per board square edge, walking the perimeter
    /// clockwise from the top-left. A 3x3 board therefore has 4 x 3 = 12, which matches
    /// what is carved around the board in game.
    /// </summary>
    public static BoardLayout Default(int rows = 3, int cols = 3)
    {
        var layout = new BoardLayout { Rows = rows, Cols = cols };
        var index = 1;

        void Add(string edge, int r, int c) =>
            layout.Figurines.Add(new FigurineSlot
            {
                Index = index++,
                Edge = edge,
                Adjacent = [new CellRef { Row = r, Col = c }],
            });

        for (var c = 0; c < cols; c++) Add("top", 0, c);
        for (var r = 0; r < rows; r++) Add("right", r, cols - 1);
        for (var c = cols - 1; c >= 0; c--) Add("bottom", rows - 1, c);
        for (var r = rows - 1; r >= 0; r--) Add("left", r, 0);

        return layout;
    }

    /// <summary>Pair each figurine slot with the modifier text read from the board.</summary>
    public IReadOnlyList<BoardModifier> Bind(IReadOnlyDictionary<int, string> descriptions)
    {
        var result = new List<BoardModifier>();
        foreach (var slot in Figurines)
        {
            if (!descriptions.TryGetValue(slot.Index, out var text) || string.IsNullOrWhiteSpace(text))
                continue;
            var cells = slot.Adjacent.Select(a => a.ToCell()).ToList();
            // One modifier PER LINE, matching how squares bind: a figurine can carry
            // several mods, and the channel classifiers anchor at line start -- bound
            // as one blob, every line after the first would silently lose its channel.
            foreach (var line in text.Split('\n',
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                result.Add(new BoardModifier(line, cells));
        }
        return result;
    }

    /// <summary>Slots with no modifier read yet, so a read pass can show what is left.</summary>
    public IReadOnlyList<FigurineSlot> Unread(IReadOnlyDictionary<int, string> descriptions) =>
        Figurines.Where(f => !descriptions.ContainsKey(f.Index)
                             || string.IsNullOrWhiteSpace(descriptions[f.Index])).ToList();

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string DefaultPath => SettingsFolder.FileIn("board-layout.json");

    public static BoardLayout Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return Default();
        try
        {
            var loaded = JsonSerializer.Deserialize<BoardLayout>(File.ReadAllText(path), Options);
            return loaded is { Figurines.Count: > 0 } ? loaded : Default();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return Default();
        }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>
/// Where things are on screen.
///
/// Kept as data because it must not be static: the user plays Windowed Fullscreen at
/// 2560x1440, but resolution, UI scale and window mode all move these rectangles, and a
/// tool that only works at one resolution is a tool that works for one person.
/// Coordinates are FRACTIONS of the client area, so they survive a resolution change; the
/// pixel maths happens at capture time.
/// </summary>
public sealed class ScreenLayout
{
    public string Name { get; set; } = "2560x1440 windowed fullscreen";
    public int ReferenceWidth { get; set; } = 2560;
    public int ReferenceHeight { get; set; } = 1440;

    /// <summary>The 3x3 voyage board.</summary>
    public Rect Board { get; set; } = new();

    /// <summary>The 6x10 chart panel on the right.</summary>
    public Rect ChartPanel { get; set; } = new();

    public int ChartPanelRows { get; set; } = 10;
    public int ChartPanelCols { get; set; } = 6;

    public sealed class Rect
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }

        /// <summary>Convert fractional coordinates to pixels for a given client size.</summary>
        public (int X, int Y, int W, int H) ToPixels(int clientWidth, int clientHeight) =>
            ((int)Math.Round(X * clientWidth), (int)Math.Round(Y * clientHeight),
             (int)Math.Round(Width * clientWidth), (int)Math.Round(Height * clientHeight));

        /// <summary>The rectangle of one cell in a grid laid over this area.</summary>
        public (int X, int Y, int W, int H) CellPixels(
            int clientWidth, int clientHeight, int rows, int cols, int row, int col)
        {
            var (x, y, w, h) = ToPixels(clientWidth, clientHeight);
            var cw = w / (double)cols;
            var ch = h / (double)rows;
            return ((int)Math.Round(x + col * cw), (int)Math.Round(y + row * ch),
                    (int)Math.Round(cw), (int)Math.Round(ch));
        }
    }

    public static string DefaultPath => SettingsFolder.FileIn("screen-layout.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static ScreenLayout Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return new ScreenLayout();
        try
        {
            return JsonSerializer.Deserialize<ScreenLayout>(File.ReadAllText(path), Options)
                   ?? new ScreenLayout();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new ScreenLayout();
        }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
        File.Move(tmp, path, overwrite: true);
    }
}
