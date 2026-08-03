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

    /// <summary>
    /// Progress must agree with the checklist, because they answer the same question.
    /// The slurp reads figurines, and counting only PANEL reads left a completed pass
    /// with an empty checklist and a bar stuck below full, forever.
    /// </summary>
    [Fact]
    public void ProgressReachesOneOnFigurineReadsAlone()
    {
        var session = new VoyageSession();
        foreach (var slot in session.Layout.Figurines)
            session.ApplyFigurineText(slot.Index, "Adjacent Areas contain 8 additional packs");

        Assert.Empty(session.SquaresAwaitingModifiers);
        Assert.Equal(1.0, session.ReadProgress, 6);
    }

    /// <summary>Progress is exactly the complement of the checklist, part way through
    /// too -- one figurine read is one of the eight readable squares done.</summary>
    [Fact]
    public void ProgressTracksTheChecklistPartWayThrough()
    {
        var session = new VoyageSession();
        session.ApplyFigurineText(1, "Adjacent Areas contain 8 additional packs");

        Assert.Equal(7, session.SquaresAwaitingModifiers.Count);
        Assert.Equal(1 / 8.0, session.ReadProgress, 6);
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

    /// <summary>
    /// The fourth meaning of an empty-looking panel, and the one that used to hide: the
    /// capture landed somewhere else entirely and OCR found text there anyway. Without
    /// the heading, unrelated text is not evidence the panel was captured -- and calling
    /// it Modifiers marks the square READ, so its real modifiers are never collected and
    /// nothing ever asks for them again.
    /// </summary>
    [Fact]
    public void TextThatIsNotAModifierMeansTheCaptureMissedThePanel()
    {
        var reading = AreaModifierPanel.Read([
            "Sell items to this vendor",
            "Waypoint discovered nearby",
        ]);
        Assert.Equal(AreaModifierPanel.PanelState.NotFound, reading.State);
        Assert.False(reading.IsRead);
        Assert.Empty(reading.Lines);
    }

    /// <summary>One modifier is enough: the junk beside it does not veto the read.</summary>
    [Fact]
    public void OneModifierLineCarriesTheReadWithoutTheHeading()
    {
        var reading = AreaModifierPanel.Read([
            "Sell items to this vendor",
            "Adjacent Areas contain 4 additional Golden Lanterns",
        ]);
        Assert.Equal(AreaModifierPanel.PanelState.Modifiers, reading.State);
        Assert.Contains("Adjacent Areas contain 4 additional Golden Lanterns", reading.Lines);
    }

    /// <summary>
    /// The evidence test must not become a second corpus to maintain: EVERY board
    /// modifier the game can show has to look like one, alone and unheaded. A digit is
    /// the usual tell, but the digit-less lines ("Adjacent Areas contain Captainsbane")
    /// are exactly the ones a shape rule loses, and losing one means a real read
    /// silently downgraded to "capture missed".
    /// </summary>
    [Fact]
    public void EveryBoardModifierIsRecognisedWithoutTheHeading()
    {
        var missed = ChartRewards.Current.BoardLines.Keys
            .Select(l => l.Replace("#", "12"))
            .Where(l => AreaModifierPanel.Read([l]).State != AreaModifierPanel.PanelState.Modifiers)
            .ToList();
        Assert.True(missed.Count == 0, string.Join("\n", missed.Take(12)));
    }

    /// <summary>
    /// The three documented meanings stay three. The heading is what separates them, and
    /// the new evidence rule applies only where there is no heading to go on.
    /// </summary>
    [Fact]
    public void TheEmptyLookingPanelsStayDistinct()
    {
        Assert.Equal(AreaModifierPanel.PanelState.Placeholder,
            AreaModifierPanel.Read([". Area Modifiers", "Hover a square of the Voyage"]).State);
        Assert.Equal(AreaModifierPanel.PanelState.NoModifiers,
            AreaModifierPanel.Read([". Area Modifiers"]).State);
        Assert.Equal(AreaModifierPanel.PanelState.NotFound,
            AreaModifierPanel.Read(["Sell items to this vendor"]).State);
    }

    /// <summary>The heading proves the panel was found, so what sits under it is the
    /// panel's own text -- the evidence rule has no say there.</summary>
    [Fact]
    public void TheHeadingStillVouchesForWhateverFollowsIt()
    {
        var reading = AreaModifierPanel.Read([". Area Modifiers", "Sell items to this vendor"]);
        Assert.Equal(AreaModifierPanel.PanelState.Modifiers, reading.State);
        Assert.Equal(["Sell items to this vendor"], reading.Lines);
    }
}

/// <summary>
/// Repair must decline a TIE. The twelve "Rare Monsters ... drop # additional &lt;orb&gt;
/// Orbs" board lines are token-identical apart from the orb word, so OCR damage to
/// exactly that word leaves every one of them on the same overlap score -- and taking
/// the first turned a damaged Chromatic figurine into a Divine one, scored as one, and
/// fired the GRAIL alert on it. A guess that confident is worse than no repair.
/// </summary>
public class CanonicalizeTieTests
{
    /// <summary>
    /// The distinguishing word is the damaged one, so nothing distinguishes the
    /// candidates: pass the line through untouched. A damaged Divine declines too --
    /// losing a repair is the price, and it is the cheap side of this trade.
    /// </summary>
    [Theory]
    [InlineData("Rare Monsters in adjacent Areas drop 2 additional Chramatic Orbs")]
    [InlineData("Rare Monsters in adjacent Areas drop 2 additional Exaited Orbs")]
    [InlineData("Rare Monsters in adjacent Areas drop 2 additional Chaas Orbs")]
    [InlineData("Rare Monsters in adjacent Areas drop 2 additional Divlne Orbs")]
    [InlineData("Rare Monsters in adjacent Areas drop 2 additional Orbs of Annulmnt")]
    public void ADamagedOrbWordIsNotRepairedByGuessing(string line) =>
        Assert.Equal(line, AreaModifierPanel.Canonicalize(line));

    /// <summary>
    /// The bug, end to end and verbatim: this line used to come back as the Divine
    /// template, which alerts as GRAIL. No orb line may ever be invented from one whose
    /// own orb word is unreadable.
    /// </summary>
    [Fact]
    public void ADamagedChromaticNeverBecomesADivine()
    {
        const string damaged = "Rare Monsters in adjacent Areas drop 2 additional Chramatic Orbs";
        Assert.DoesNotContain("Divine", AreaModifierPanel.Canonicalize(damaged));
    }

    /// <summary>
    /// The other half: damage to a SHARED word leaves the orb word intact, one template
    /// wins outright, and the repair still happens. Declining a tie must not turn into
    /// declining everything.
    /// </summary>
    [Theory]
    [InlineData("Rare Monsters in adjacent Areås drop 2 additional Chromatic Orbs",
                "Rare Monsters in adjacent Areas drop 2 additional Chromatic Orbs")]
    [InlineData("Rare Monsters in adjacent Areas drop 2 additional Chromatic Orbs l:",
                "Rare Monsters in adjacent Areas drop 2 additional Chromatic Orbs")]
    [InlineData("Rare Mons+ers in adjacent Areas drop 1 additional Exalted Orbs",
                "Rare Monsters in adjacent Areas drop 1 additional Exalted Orbs")]
    public void DamageToASharedWordStillRepairs(string mangled, string expected) =>
        Assert.Equal(expected, AreaModifierPanel.Canonicalize(mangled));
}
