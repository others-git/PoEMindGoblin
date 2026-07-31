using MindGoblin.Core.Voyage;

namespace MindGoblin.Tests;

public class LevelReaderTests
{
    [Fact]
    public void ExposesTheDigitsItWasTrainedOn()
    {
        // '5' has never appeared in a training capture. Stating that explicitly matters:
        // callers need to know a null level can mean "untrained digit", not just "no text".
        Assert.Equal(['0', '1', '2', '3', '4', '6', '7', '8', '9'], new LevelReader().KnownDigits);
    }

    [Fact]
    public void EveryBuiltInTemplateIsTheStripHeight()
    {
        // A short template would silently match at the wrong baseline offset.
        var height = new LevelReader.Options().TextHeight;
        Assert.All(LevelReader.BuiltInTemplates(), t => Assert.Equal(height, t.Height));
    }

    [Fact]
    public void TemplateRowsRoundTrip()
    {
        foreach (var t in LevelReader.BuiltInTemplates())
        {
            var again = LevelReader.DigitTemplate.FromRows(t.Digit, t.ToRows());
            Assert.Equal(t.ToRows(), again.ToRows());
            Assert.Equal(t.Ink, again.Ink);
        }
    }

    /// <summary>Lay templates out on a strip the way the game does, to test decoding.</summary>
    private static bool[,] Compose(string text, int gap = 1, int dy = 0)
    {
        var o = new LevelReader.Options();
        var templates = LevelReader.BuiltInTemplates().ToDictionary(t => t.Digit);
        var chosen = text.Select(c => templates[c]).ToList();
        var width = chosen.Sum(t => t.Width) + gap * (chosen.Count - 1);
        var strip = new bool[o.TextHeight + 2 * o.BaselinePad, width];

        var x = 0;
        foreach (var t in chosen)
        {
            for (var y = 0; y < t.Height; y++)
                for (var tx = 0; tx < t.Width; tx++)
                    if (t.Mask[y, tx]) strip[y + dy, x + tx] = true;
            x += t.Width + gap;
        }
        return strip;
    }

    [Theory]
    [InlineData("83")] [InlineData("71")] [InlineData("68")] [InlineData("124")]
    public void DecodesSeparatedDigits(string text) =>
        Assert.Equal(text, new LevelReader().Decode(Compose(text), new LevelReader.Options()));

    [Theory]
    [InlineData("82")] [InlineData("74")] [InlineData("1234")]
    public void DecodesTouchingDigits(string text)
    {
        // The real font kerns digits together with no gap at all -- "82" is one unbroken
        // run of ink. Splitting on blank columns would read it as a single glyph.
        Assert.Equal(text, new LevelReader().Decode(Compose(text, gap: 0), new LevelReader.Options()));
    }

    [Theory]
    [InlineData(0)] [InlineData(2)] [InlineData(4)]
    public void ToleratesBaselineDrift(int dy)
    {
        // Captions do not all sit on exactly the same row; the reader searches vertically.
        Assert.Equal("78", new LevelReader().Decode(Compose("78", dy: dy), new LevelReader.Options()));
    }

    [Fact]
    public void RefusesAnUntrainedDigitInsteadOfGuessing()
    {
        // A '9' drawn as a plain block matches nothing well. Returning null keeps a bad
        // level out of the plan; returning the closest digit would put a wrong one in.
        var o = new LevelReader.Options();
        var strip = new bool[o.TextHeight + 2 * o.BaselinePad, 9];
        for (var y = 4; y < 16; y++)
            for (var x = 0; x < 9; x++) strip[y, x] = true;
        Assert.Null(new LevelReader().Decode(strip, o));
    }

    [Fact]
    public void LearningAddsADigitWithoutDisturbingTheRest()
    {
        var five = LevelReader.DigitTemplate.FromRows('5', new LevelReader.Options() is var o
            ? Enumerable.Range(0, o.TextHeight)
                        .Select(y => y is >= 4 and < 16 ? "#########" : ".........")
                        .ToArray()
            : []);

        var taught = new LevelReader().Learn(five);
        Assert.Contains('5', taught.KnownDigits);
        Assert.Equal(10, taught.KnownDigits.Count);
        // The built-in reader must be untouched -- Learn returns a new reader.
        Assert.DoesNotContain('5', new LevelReader().KnownDigits);
    }

    [Fact]
    public void LearningReplacesRatherThanDuplicatesADigit()
    {
        var eight = LevelReader.BuiltInTemplates().Single(t => t.Digit == '8');
        var taught = new LevelReader().Learn(eight);
        Assert.Equal(9, taught.KnownDigits.Count);
    }

    [Fact]
    public void UserTemplatesOverlayTheBuiltInsAndSurviveARoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"level-digits-{Guid.NewGuid():N}.json");
        try
        {
            var o = new LevelReader.Options();
            var rows = Enumerable.Range(0, o.TextHeight)
                                 .Select(y => y is >= 4 and < 16 ? "#####" : ".....")
                                 .ToArray();
            new LevelReader().Learn(LevelReader.DigitTemplate.FromRows('5', rows))
                             .SaveTemplates(path);

            var loaded = LevelReader.LoadWithUserTemplates(path);
            Assert.Contains('5', loaded.KnownDigits);
            Assert.Contains('8', loaded.KnownDigits);   // built-ins still present
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ABrokenTemplateFileFallsBackToTheBuiltIns()
    {
        // Reading the panel at all matters more than a hand-edited template file.
        var path = Path.Combine(Path.GetTempPath(), $"level-digits-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ not json");
            Assert.Equal(new LevelReader().KnownDigits,
                         LevelReader.LoadWithUserTemplates(path).KnownDigits);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void MissingTemplateFileIsNotAnError()
    {
        var path = Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.json");
        Assert.Equal(new LevelReader().KnownDigits,
                     LevelReader.LoadWithUserTemplates(path).KnownDigits);
    }

    [Fact]
    public void CalibrationRescalesToAnotherResolution()
    {
        var scaled = new LevelReader.Options().ScaledTo(1920, 1080);
        Assert.Equal(16, scaled.TextOffsetY);      // 22 * 0.75 = 16.5, rounds to even
        Assert.Equal(15, scaled.TextHeight);       // 20 * 0.75
        Assert.Equal(10, scaled.PrefixWidth);      // 13 * 0.75
        Assert.Equal(1920, scaled.ReferenceWidth);
    }
}
