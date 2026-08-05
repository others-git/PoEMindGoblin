using MindGoblin.Core.Voyage;

namespace MindGoblin.Tests;

public class BoardLayoutTests
{
    [Fact]
    public void ThreeByThreeRingHasTwelveFigurines()
    {
        // Matches what is carved around the board in game.
        var layout = BoardLayout.Default();
        Assert.Equal(12, layout.Figurines.Count);
        Assert.Equal(Enumerable.Range(1, 12), layout.Figurines.Select(f => f.Index));
    }

    [Fact]
    public void FigurineCountFollowsBoardSize()
    {
        // Data, not constants: a different board must not need a rebuild.
        Assert.Equal(16, BoardLayout.Default(4, 4).Figurines.Count);
        Assert.Equal(8, BoardLayout.Default(2, 2).Figurines.Count);
    }

    [Fact]
    public void EveryFigurineTouchesABoardSquare()
    {
        var layout = BoardLayout.Default();
        Assert.All(layout.Figurines, f =>
        {
            Assert.NotEmpty(f.Adjacent);
            Assert.All(f.Adjacent, a =>
            {
                Assert.InRange(a.Row, 0, layout.Rows - 1);
                Assert.InRange(a.Col, 0, layout.Cols - 1);
            });
        });
    }

    [Fact]
    public void BindsReadTextToTheSquaresItAffects()
    {
        var layout = BoardLayout.Default();
        var read = new Dictionary<int, string>
        {
            [1] = "Adjacent Areas contain 8 additional packs of Sea Beasts",
        };

        var mods = layout.Bind(read);
        var m = Assert.Single(mods);
        Assert.Contains("Sea Beasts", m.Description);
        Assert.True(m.Affects(layout.Figurines[0].Adjacent[0].ToCell()));
    }

    [Fact]
    public void UnreadSlotsDriveTheReadPass()
    {
        // Read mode ticks these off one by one, so it has to know what is outstanding.
        var layout = BoardLayout.Default();
        var read = new Dictionary<int, string> { [1] = "something", [2] = "  " };
        var unread = layout.Unread(read);
        Assert.Equal(11, unread.Count);            // 2 is blank, so still outstanding
        Assert.DoesNotContain(unread, f => f.Index == 1);
        Assert.Contains(unread, f => f.Index == 2);
    }

    [Fact]
    public void BlankEntriesAreNotBoundAsModifiers()
    {
        var layout = BoardLayout.Default();
        Assert.Empty(layout.Bind(new Dictionary<int, string> { [1] = "   " }));
    }
}

public class ScreenLayoutTests
{
    [Fact]
    public void CoordinatesAreFractionsSoTheySurviveAResolutionChange()
    {
        // The user plays 2560x1440 windowed fullscreen, but a tool that only works at one
        // resolution works for one person.
        var rect = new ScreenLayout.Rect { X = 0.5, Y = 0.25, Width = 0.25, Height = 0.5 };

        Assert.Equal((1280, 360, 640, 720), rect.ToPixels(2560, 1440));
        Assert.Equal((960, 270, 480, 540), rect.ToPixels(1920, 1080));
    }

    [Fact]
    public void CellPixelsDivideTheAreaIntoAGrid()
    {
        var panel = new ScreenLayout.Rect { X = 0, Y = 0, Width = 1, Height = 1 };
        // 6 columns x 10 rows over a 600x1000 client
        var (x, y, w, h) = panel.CellPixels(600, 1000, rows: 10, cols: 6, row: 0, col: 0);
        Assert.Equal((0, 0, 100, 100), (x, y, w, h));

        var (x2, y2, _, _) = panel.CellPixels(600, 1000, 10, 6, row: 2, col: 3);
        Assert.Equal((300, 200), (x2, y2));
    }

    [Fact]
    public void DefaultsDescribeTheKnownSetup()
    {
        var layout = new ScreenLayout();
        Assert.Equal(2560, layout.ReferenceWidth);
        Assert.Equal(1440, layout.ReferenceHeight);
    }

    /// <summary>
    /// How many cells the panel holds is NOT here. It was, in parallel with the reader's
    /// calibration -- two files holding one fact, and the fact decides which chart a
    /// panel index refers to. Let them disagree and every chart draws on the wrong tile
    /// while the solver places the right ones. The reader owns it, because the reader is
    /// what assigns the indices.
    /// </summary>
    [Fact]
    public void ThePanelGridIsSizedByTheReaderAlone()
    {
        Assert.DoesNotContain(typeof(ScreenLayout).GetProperties(),
            p => p.Name.Contains("Rows") || p.Name.Contains("Cols"));

        var options = new ChartPanelReader.Options();
        Assert.Equal(10, options.Rows);
        Assert.Equal(6, options.Cols);
    }
}
