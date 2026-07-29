using MindGoblin.Core.Voyage;

namespace MindGoblin.Tests;

/// <summary>
/// The figurines cannot be copied -- the game only puts items on the clipboard, and a
/// carving is not an item -- so their modifiers are read off the "Area Modifiers" panel
/// instead, per square, via OCR. OCR output is noisy and wrapped, and this is the layer
/// that has to cope with that.
/// </summary>
public class AreaModifierPanelTests
{
    /// <summary>
    /// The wrap that broke a real session: "Rare Monsters in Area drop Dead" wraps onto
    /// "Man's Sulphur", the continuation starts UPPERCASE, and "Dead" was not a
    /// recognised dangling word -- so square 6 stored a fragment no rule could score,
    /// the payout square was worth zero, and the solver "failed to connect the dots"
    /// with a rare-adding neighbour when there were no dots to connect.
    /// </summary>
    [Theory]
    [InlineData("Rare Monsters in Area drop Dead", "Man's Sulphur",
                "Rare Monsters in Area drop Dead Man's Sulphur")]
    [InlineData("Adjacent Areas contain 4 additional Golden", "Lanterns",
                "Adjacent Areas contain 4 additional Golden Lanterns")]
    [InlineData("Adjacent Areas contain 2 additional cages of Tormented", "Spirits",
                "Adjacent Areas contain 2 additional cages of Tormented Spirits")]
    public void UppercaseContinuationsAreStitched(string first, string second, string joined)
    {
        var reading = AreaModifierPanel.Read(["Area Modifiers", first, second]);
        Assert.Equal([joined], reading.Lines);
    }

    /// <summary>
    /// EVERY wrap of EVERY corpus line must survive: split each line at each word
    /// boundary and the reader must stitch it back -- except where the boundary word is
    /// AMBIGUOUS (some modifier ends with it), where staying split is the safe choice.
    /// This is the exhaustive version of the "drop Dead / Man's Sulphur" bug hunt: the
    /// hand-audit found 66 distinct gap words, which is not a list to maintain by hand.
    /// </summary>
    [Fact]
    public void EveryPossibleWrapOfEveryModifierIsStitched()
    {
        var lines = ChartRewards.Current.Lines.Keys
            .Concat(ChartRewards.Current.BoardLines.Keys)
            .Select(l => l.Replace("#", "12"))
            .ToList();
        // Mirrors the reader's variant expansion: resolved panel wordings can DROP the
        // location suffix, so their last words are terminal too.
        var terminal = lines
            .SelectMany(l => new[]
            {
                l,
                l.Replace(" in adjacent Areas", ""),
                l.Replace(" in all Voyage Areas", ""),
            })
            .SelectMany(l => { var w = l.Split(' ')[^1].Trim(',', '.');
                               return w.EndsWith('s') ? new[] { w, w[..^1] } : [w]; })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var failures = new List<string>();
        foreach (var line in lines)
        {
            var words = line.Split(' ');
            for (var cut = 1; cut < words.Length; cut++)
            {
                var first = string.Join(' ', words[..cut]);
                var second = string.Join(' ', words[cut..]);
                if (second.Length < 4 || first.Length < 4) continue; // OCR-noise filter
                var boundary = words[cut - 1].Trim(',', '.');
                var ambiguous = terminal.Contains(boundary);

                var got = AreaModifierPanel.Read(["Area Modifiers", first, second]).Lines;
                if (!ambiguous && (got.Count != 1 || got[0] != line))
                    failures.Add($"'{first}' | '{second}' -> {got.Count} lines");
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(12)));
    }

    /// <summary>
    /// The other direction, exhaustively: NO pair of complete modifiers may ever be
    /// merged into one, whatever order they arrive in.
    /// </summary>
    [Fact]
    public void NoTwoCompleteModifiersAreEverMerged()
    {
        var lines = ChartRewards.Current.Lines.Keys
            .Concat(ChartRewards.Current.BoardLines.Keys)
            .Select(l => l.Replace("#", "12"))
            .Where(l => !char.IsLower(l[0]))       // lowercase starts are joined by design
            .ToList();

        var failures = new List<string>();
        foreach (var a in lines)
            foreach (var b in lines)
            {
                if (a == b) continue;
                var got = AreaModifierPanel.Read(["Area Modifiers", a, b]).Lines;
                if (got.Count != 2) failures.Add($"'{a}' swallowed '{b}'");
            }
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(12)));
    }

    /// <summary>Two complete modifiers must never be glued into one.</summary>
    [Fact]
    public void CompleteModifiersStaySeparate()
    {
        var reading = AreaModifierPanel.Read([
            "Area Modifiers",
            "32% increased Pack Size",
            "Area contains 4 additional Golden Lanterns",
        ]);
        Assert.Equal(2, reading.Lines.Count);
    }

    [Fact]
    public void DropsThePanelHeading()
    {
        // OCR reports the heading with a leading bullet glyph: ". Area Modifiers".
        var lines = AreaModifierPanel.CleanLines([
            ". Area Modifiers",
            "Adjacent Areas contain 8 additional packs of Sea Beasts",
        ]);
        Assert.Equal(["Adjacent Areas contain 8 additional packs of Sea Beasts"], lines);
    }

    [Fact]
    public void ThePlaceholderIsNotAModifier()
    {
        // Verbatim from a real capture. Capturing before hovering must not record the
        // instructions as though they were the square's modifiers.
        var lines = AreaModifierPanel.CleanLines([
            ". Area Modifiers",
            "Hover a square of the Voyage",
            "Board to see the relevant Area",
            "Modifiers",
        ]);
        Assert.Empty(lines);
    }

    [Fact]
    public void AnEmptyReadIsEmptyRatherThanNoise()
    {
        Assert.Empty(AreaModifierPanel.CleanLines(null));
        Assert.Empty(AreaModifierPanel.CleanLines([]));
        Assert.Empty(AreaModifierPanel.CleanLines(["", "   ", "."]));
    }

    [Fact]
    public void RejoinsAModifierWrappedAcrossLines()
    {
        // The panel wraps; OCR reports each visual line. A rule matching
        // "(\d+) additional packs" would miss this entirely if left split.
        var lines = AreaModifierPanel.CleanLines([
            "Adjacent Areas contain 8 additional",
            "packs of Sea Beasts",
        ]);
        Assert.Equal(["Adjacent Areas contain 8 additional packs of Sea Beasts"], lines);
    }

    [Fact]
    public void RejoinsOnALowercaseContinuation()
    {
        var lines = AreaModifierPanel.CleanLines([
            "Areas contain 2 additional Strongboxes and",
            "increased Quantity of Items found",
        ]);
        Assert.Single(lines);
        Assert.Contains("Strongboxes and increased Quantity", lines[0]);
    }

    [Fact]
    public void KeepsSeparateModifiersSeparate()
    {
        // Two modifiers, each starting a new sentence, must not be stitched into one --
        // that would let a single rule match across two unrelated effects.
        var lines = AreaModifierPanel.CleanLines([
            "Adjacent Areas contain 8 additional packs of Sea Beasts",
            "Areas have 20% increased Monster Pack Size",
        ]);
        Assert.Equal(2, lines.Count);
    }

    [Fact]
    public void StripsBulletGlyphs()
    {
        var lines = AreaModifierPanel.CleanLines(["• Areas have 20% increased Quantity"]);
        Assert.Equal(["Areas have 20% increased Quantity"], lines);
    }

    [Fact]
    public void DiscardsStrayCharactersFromPanelBorders()
    {
        var lines = AreaModifierPanel.CleanLines(["l", "//", "Areas contain 3 Strongboxes"]);
        Assert.Equal(["Areas contain 3 Strongboxes"], lines);
    }

    [Fact]
    public void TheDefaultRegionMatchesWhereThePanelWasMeasured()
    {
        // Measured on a real 2560x1440 capture: the heading sits at x 492-668, y 387-407
        // and the body text runs out to x 750.
        var (x, y, w, h) = new AreaModifierPanel.Options().ToPixels(2560, 1440);
        Assert.True(x < 492 && x + w > 750, $"panel x {x}..{x + w} misses the text");
        Assert.True(y < 387 && y + h > 520, $"panel y {y}..{y + h} misses the text");
    }

    [Fact]
    public void TheRegionIsFractionalSoItSurvivesAResolutionChange()
    {
        var o = new AreaModifierPanel.Options();
        var (x1080, _, w1080, _) = o.ToPixels(1920, 1080);
        var (x1440, _, w1440, _) = o.ToPixels(2560, 1440);
        // Within a pixel: each side rounds independently at each resolution.
        Assert.True(Math.Abs(x1440 * 0.75 - x1080) <= 1, $"{x1080} vs {x1440 * 0.75}");
        Assert.True(Math.Abs(w1440 * 0.75 - w1080) <= 1, $"{w1080} vs {w1440 * 0.75}");
    }

    [Theory]
    [InlineData(@"{ ""Left"": 0.9, ""Right"": 0.1 }")]     // inverted, would read nothing
    [InlineData(@"{ ""Top"": 0.9, ""Bottom"": 0.2 }")]
    [InlineData("{ not json")]
    public void NonsenseConfigFallsBackToTheMeasuredDefault(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"panel-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, content);
            Assert.Equal(new AreaModifierPanel.Options().Left,
                         AreaModifierPanel.Options.Load(path).Left);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void EditsRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"panel-{Guid.NewGuid():N}.json");
        try
        {
            new AreaModifierPanel.Options { Left = 0.2, Upscale = 5 }.Save(path);
            var loaded = AreaModifierPanel.Options.Load(path);
            Assert.Equal(0.2, loaded.Left);
            Assert.Equal(5, loaded.Upscale);
        }
        finally { File.Delete(path); }
    }
}

/// <summary>
/// Per-square modifiers in the session. The game totals the figurine effects for whatever
/// square you hover, so a square that has been read supersedes anything worked out from
/// the figurines around it.
/// </summary>
public class SquareModifierTests
{
    [Fact]
    public void ASquareReadingBecomesABoardModifierForThatCellOnly()
    {
        var session = new VoyageSession();
        session.ApplySquareModifiers(5, ["Adjacent Areas contain 8 additional packs"]);

        var modifier = Assert.Single(session.BoardModifiers());
        Assert.Equal([new Cell(1, 1)], modifier.AffectedCells);   // square 5 of a 3x3
    }

    [Fact]
    public void SquareNumbersMapRowMajorFromOne()
    {
        var session = new VoyageSession();
        Assert.Equal(new Cell(0, 0), session.CellOf(1));
        Assert.Equal(new Cell(1, 1), session.CellOf(5));
        Assert.Equal(new Cell(2, 2), session.CellOf(9));
    }

    [Fact]
    public void ASquareReadingSupersedesTheFigurinesTouchingIt()
    {
        // The panel is the game's own total for that square. Counting the figurine as
        // well would pay twice for the same effect.
        var session = new VoyageSession();
        session.ApplyFigurineText(1, "Adjacent Areas contain 8 additional packs");  // -> square 1
        session.ApplySquareModifiers(1, ["Adjacent Areas contain 8 additional packs"]);

        var affectingSquareOne = session.BoardModifiers()
            .Count(m => m.AffectedCells.Contains(new Cell(0, 0)));
        Assert.Equal(1, affectingSquareOne);
    }

    [Fact]
    public void FigurinesStillCoverSquaresThatHaveNotBeenRead()
    {
        var session = new VoyageSession();
        session.ApplyFigurineText(2, "Adjacent Areas contain 8 additional packs");  // -> square 2
        session.ApplySquareModifiers(1, ["something else entirely"]);

        Assert.Contains(session.BoardModifiers(),
                        m => m.AffectedCells.Contains(new Cell(0, 1)));
    }

    [Fact]
    public void ClearingASquarePutsItBackOnTheChecklist()
    {
        var session = new VoyageSession();
        session.ApplySquareModifiers(4, ["a modifier"]);
        Assert.DoesNotContain(4, session.SquaresAwaitingModifiers);

        session.ClearSquareModifiers(4);
        Assert.Contains(4, session.SquaresAwaitingModifiers);
    }

    [Fact]
    public void ASquareWithNoModifiersCountsAsRead()
    {
        // The centre of a 3x3 touches none of the twelve perimeter figurines, so an empty
        // result there is the truth about it. Treating it as unread left the read pass
        // stuck on square 5 forever.
        var session = new VoyageSession();
        session.ApplySquareModifiers(5, []);

        Assert.DoesNotContain(5, session.SquaresAwaitingModifiers);
        Assert.Empty(session.SquareModifiers[5]);
        Assert.Empty(session.BoardModifiers());
    }

    [Fact]
    public void NullUnreadsASquareWhereEmptyDoesNot()
    {
        // Square 1, not 5: the centre is excluded from the checklist entirely, so it
        // could never demonstrate the difference.
        var session = new VoyageSession();
        session.ApplySquareModifiers(1, []);
        Assert.DoesNotContain(1, session.SquaresAwaitingModifiers);

        session.ApplySquareModifiers(1, null);
        Assert.Contains(1, session.SquaresAwaitingModifiers);
    }

    [Fact]
    public void NoPerimeterFigurineTouchesTheCentreSquare()
    {
        // The premise of the above: on the standard ring every figurine sits against one
        // EDGE square, so the centre can never receive a board modifier from one.
        var layout = BoardLayout.Default();
        var centre = new Cell(1, 1);
        Assert.DoesNotContain(layout.Figurines,
            f => f.Adjacent.Any(a => a.ToCell() == centre));
    }

    [Fact]
    public void OnlySquaresAFigurineCanReachAreOnTheChecklist()
    {
        // Eight, not nine: the centre of a 3x3 touches no figurine, so asking the user
        // to hover it is asking them to confirm what the layout already determines.
        var session = new VoyageSession();
        Assert.Equal([1, 2, 3, 4, 6, 7, 8, 9], session.SquaresAwaitingModifiers);
        Assert.Equal([5], session.SquaresWithoutFigurines);
    }

    [Fact]
    public void ProgressReachesOneWithoutTheCentreSquare()
    {
        // If the excluded square still counted, the read could never finish.
        var session = new VoyageSession();
        foreach (var sq in session.SquaresAwaitingModifiers.ToList())
            session.ApplySquareModifiers(sq, ["a modifier"]);
        Assert.Equal(1.0, session.ReadProgress, 6);
    }

    [Fact]
    public void SquareModifiersReachTheSolverAndPullAChartOntoThatSquare()
    {
        // The point of reading them at all: a modifier on one square should pull the
        // chart that benefits from it onto that square.
        var session = new VoyageSession();
        session.ApplyPanelRead(Enumerable.Range(1, 9).Select(i =>
            new ChartPanelReader.ReadCell(i, (i - 1) / 6, (i - 1) % 6, true, true, true, true)
            { Level = 80 }).ToList());
        session.ApplySquareModifiers(5, ["Adjacent Areas contain 8 additional packs"]);

        var profile = new VoyageProfile
        {
            Name = "packs",
            Rules = [new VoyageRule { Pattern = @"(\d+) additional packs", Weight = 10 }],
        };
        var solution = session.Solve(profile, TimeSpan.FromSeconds(1));

        Assert.Equal(80, solution.Value);   // 8 * 10, applied once, on square 5
    }
}

/// <summary>
/// Which of the three empty-looking panels was captured. Conflating them either sticks
/// the read pass on a square that legitimately has nothing, or records a failed capture
/// as though the square had been read.
/// </summary>
public class PanelStateTests
{
    [Fact]
    public void ThePlaceholderMeansNothingIsHoveredYet()
    {
        var reading = AreaModifierPanel.Read([
            ". Area Modifiers",
            "Hover a square of the Voyage",
            "Board to see the relevant Area",
            "Modifiers",
        ]);
        Assert.Equal(AreaModifierPanel.PanelState.Placeholder, reading.State);
        Assert.False(reading.IsRead);
    }

    [Fact]
    public void HeadingWithNothingUnderItMeansTheSquareHasNoModifiers()
    {
        // This is square 5 of a 3x3: hovered, and genuinely empty.
        var reading = AreaModifierPanel.Read([". Area Modifiers"]);
        Assert.Equal(AreaModifierPanel.PanelState.NoModifiers, reading.State);
        Assert.True(reading.IsRead);
        Assert.Empty(reading.Lines);
    }

    [Fact]
    public void NoHeadingAtAllMeansTheCaptureMissedThePanel()
    {
        // A wrong region reads as empty too, and must NOT be recorded as "no modifiers".
        Assert.Equal(AreaModifierPanel.PanelState.NotFound, AreaModifierPanel.Read([]).State);
        Assert.Equal(AreaModifierPanel.PanelState.NotFound, AreaModifierPanel.Read(null).State);
        Assert.False(AreaModifierPanel.Read(["", "  ", "x"]).IsRead);
    }

    [Fact]
    public void ModifiersUnderTheHeadingAreRead()
    {
        var reading = AreaModifierPanel.Read([
            ". Area Modifiers",
            "Adjacent Areas contain 8 additional packs of Sea Beasts",
        ]);
        Assert.Equal(AreaModifierPanel.PanelState.Modifiers, reading.State);
        Assert.True(reading.IsRead);
        Assert.Single(reading.Lines);
    }

    [Fact]
    public void ModifiersAreStillReadWhenOcrMissesTheHeading()
    {
        // The heading distinguishes the EMPTY cases. Actual modifier text is proof enough
        // on its own that the panel was found.
        var reading = AreaModifierPanel.Read(["Areas contain 8 additional packs of Sea Beasts"]);
        Assert.Equal(AreaModifierPanel.PanelState.Modifiers, reading.State);
    }
}
