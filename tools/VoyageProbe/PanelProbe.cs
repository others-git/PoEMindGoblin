using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PoeMarketWatch.Core.Voyage;

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
    public static int Run(string path, string profileName)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"no such screenshot: {path}");
            return 1;
        }

        using var pixels = new FilePixels(path);
        Console.WriteLine($"{Path.GetFileName(path)}  {pixels.Width}x{pixels.Height}");

        var cells = new ChartPanelReader(levels: LevelReader.LoadWithUserTemplates()).Read(pixels);
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
