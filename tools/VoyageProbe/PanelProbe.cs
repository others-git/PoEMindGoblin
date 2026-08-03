using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MindGoblin.Core.Voyage;

/// <summary>
/// Decode a real screenshot and print the plan.
///
/// The one tool that answers "is the calibration still right?" without launching the GUI:
/// it prints what the reader saw per cell, so a drift in origin or pitch shows up as
/// missing charts or nonsense shapes rather than as a quietly worse plan.
/// </summary>
[SupportedOSPlatform("windows")]
public static class PanelProbe
{
    public static int Run(string path, string profileName, string? overlayPath = null)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"no such screenshot: {path}");
            return 1;
        }

        using var pixels = new FilePixels(path);
        Console.WriteLine($"{Path.GetFileName(path)}  {pixels.Width}x{pixels.Height}");

        var reader = new ChartPanelReader(ChartPanelReader.Options.Load(),
                                          LevelReader.LoadWithUserTemplates());
        // The geometry the decode SAMPLED, which is not the calibration whenever the
        // located grid wins. Drawing the calibration instead puts the boxes on cells
        // nothing was read from, and the overlay exists to check exactly that.
        var resolved = reader.Resolve(pixels);
        var cells = reader.Read(pixels);

        if (overlayPath is not null)
        {
            DrawOverlay(path, resolved, cells, overlayPath);
            Console.WriteLine($"overlay written to {overlayPath}");
        }
        Console.WriteLine($"\n{cells.Count} charts read");
        foreach (var group in cells.GroupBy(c => c.Shape).OrderByDescending(g => g.Count()))
            Console.WriteLine($"  {group.Key,-9} x{group.Count()}");

        var unreadable = cells.Count(c => c.Level is null);
        if (unreadable > 0)
            Console.WriteLine($"  {unreadable} level(s) unreadable "
                              + $"(reader knows {string.Join("", new LevelReader().KnownDigits)})");

        Console.WriteLine("\npanel:");
        foreach (var row in cells.GroupBy(c => c.Row).OrderBy(g => g.Key))
            Console.WriteLine("  " + string.Join("  ",
                row.OrderBy(c => c.Col).Select(c => $"#{c.Index,-2} {c.Shape,-8} L{c.Level?.ToString() ?? "??"}")));

        var session = new VoyageSession();
        session.ApplyPanelRead(cells);

        var rules = VoyageRules.Defaults();
        var profile = rules.FirstOrDefault(p => p.Name == profileName) ?? rules[0];
        var solution = session.Solve(profile, TimeSpan.FromSeconds(3));

        Console.WriteLine($"\nprofile \"{profile.Name}\" -> value {solution.Value:0.##}, "
                          + $"{(solution.ProvedOptimal ? "proved optimal" : "best in budget")}, "
                          + $"{solution.NodesExplored:N0} nodes in {solution.Elapsed.TotalMilliseconds:0} ms");

        var board = new VoyageBoard(3, 3);
        foreach (var p in solution.Placements) board.Place(p);
        Console.WriteLine($"\nlayout (valid={board.IsValid()}):");
        Console.WriteLine(board.Render());

        Console.WriteLine("PLAN");
        foreach (var step in session.Plan(solution)) Console.WriteLine("  " + step);
        return 0;
    }

    /// <summary>
    /// Draw the grid the decode used back over the screenshot.
    ///
    /// This is how the coordinates were found in the first place, and it is the only
    /// honest way to check them: a drifted origin still produces a plan, just a plan
    /// built from the wrong cells. Seeing the boxes land on the glyphs is proof.
    ///
    /// <paramref name="resolved"/> is the reader's own resolved geometry, already in the
    /// capture's coordinates -- nothing here rescales, or the overlay would go back to
    /// describing a decode that did not happen.
    /// </summary>
    private static void DrawOverlay(
        string source, ChartPanelReader.Options resolved,
        IReadOnlyList<ChartPanelReader.ReadCell> cells, string destination)
    {
        using var bmp = new Bitmap(source);

        using var g = Graphics.FromImage(bmp);
        using var found = new Pen(Color.Lime, 1);
        using var empty = new Pen(Color.FromArgb(90, Color.Red), 1);
        using var font = new Font("Consolas", 9);
        using var label = new SolidBrush(Color.Yellow);

        for (var row = 0; row < resolved.Rows; row++)
        {
            for (var col = 0; col < resolved.Cols; col++)
            {
                // Rounded where the reader rounds: the overlay has to sit on the pixel
                // the decode sampled, not half a cell away from it.
                var cx = (int)Math.Round(resolved.OriginX + col * resolved.Pitch);
                var cy = (int)Math.Round(resolved.OriginY + row * resolved.Pitch)
                         + resolved.GlyphOffsetY;
                var h = resolved.GlyphHalf;
                var cell = cells.FirstOrDefault(c => c.Row == row && c.Col == col);
                g.DrawRectangle(cell is null ? empty : found, cx - h, cy - h, h * 2, h * 2);
                if (cell is not null)
                    g.DrawString($"{cell.Index} {cell.Shape} L{cell.Level?.ToString() ?? "?"}",
                                 font, label, cx - h, cy + h + 1);
            }
        }
        bmp.Save(destination, ImageFormat.Png);
    }
}

[SupportedOSPlatform("windows")]
internal sealed class FilePixels : IPixels, IDisposable
{
    private readonly Bitmap _bmp;
    private readonly int[] _argb;

    public FilePixels(string path)
    {
        _bmp = new Bitmap(path);
        Width = _bmp.Width;
        Height = _bmp.Height;
        _argb = new int[Width * Height];
        var data = _bmp.LockBits(new Rectangle(0, 0, Width, Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try { Marshal.Copy(data.Scan0, _argb, 0, _argb.Length); }
        finally { _bmp.UnlockBits(data); }
    }

    public int Width { get; }
    public int Height { get; }

    public (int R, int G, int B) At(int x, int y)
    {
        var p = _argb[y * Width + x];
        return ((p >> 16) & 0xFF, (p >> 8) & 0xFF, p & 0xFF);
    }

    public void Dispose() => _bmp.Dispose();
}
