using PoeMarketWatch.Core;

namespace PoeMarketWatch.Tests;

public class TradeUrlParserTests
{
    [Theory]
    // full url, exactly as copied from the browser
    [InlineData("https://www.pathofexile.com/trade/search/Allflame/aL5X0Qaaue", "Allflame", "aL5X0Qaaue")]
    // no scheme
    [InlineData("www.pathofexile.com/trade/search/Allflame/aL5X0Qaaue", "Allflame", "aL5X0Qaaue")]
    // trailing query string (live search links carry one)
    [InlineData("https://www.pathofexile.com/trade/search/Allflame/aL5X0Qaaue?q=%7B%7D", "Allflame", "aL5X0Qaaue")]
    // fragment
    [InlineData("https://www.pathofexile.com/trade/search/Allflame/aL5X0Qaaue#top", "Allflame", "aL5X0Qaaue")]
    // bare shorthand
    [InlineData("Allflame/aL5X0Qaaue", "Allflame", "aL5X0Qaaue")]
    // league with a space
    [InlineData("https://www.pathofexile.com/trade/search/Hardcore%20Allflame/xyz", "Hardcore Allflame", "xyz")]
    // trailing slash
    [InlineData("https://www.pathofexile.com/trade/search/Allflame/aL5X0Qaaue/", "Allflame", "aL5X0Qaaue")]
    public void ParsesTradeUrls(string input, string league, string queryId)
    {
        Assert.True(TradeUrlParser.TryParse(input, out var l, out var q));
        Assert.Equal(league, l);
        Assert.Equal(queryId, q);
    }

    [Fact]
    public void ParsesPoe2UrlWithRealmSegment()
    {
        Assert.True(TradeUrlParser.TryParse(
            "https://www.pathofexile.com/trade2/search/poe2/Standard/abc123", out var l, out var q));
        Assert.Equal("Standard", l);
        Assert.Equal("abc123", q);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nonsense")]
    [InlineData("https://www.pathofexile.com/")]
    public void RejectsGarbage(string? input)
    {
        Assert.False(TradeUrlParser.TryParse(input, out _, out _));
    }

    [Fact]
    public void RoundTripsThroughBuild()
    {
        var url = TradeUrlParser.Build("Allflame", "aL5X0Qaaue");
        Assert.True(TradeUrlParser.TryParse(url, out var l, out var q));
        Assert.Equal("Allflame", l);
        Assert.Equal("aL5X0Qaaue", q);
    }
}

public class AppSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pmw-set-" + Guid.NewGuid().ToString("N"));
    private string File_ => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void RoundTripsWatches()
    {
        var s = new AppSettings { DefaultLeague = "Allflame", TravelHotkey = "Ctrl+D" };
        s.Watches.Add(new Watch { Name = "Wands", League = "Allflame", QueryId = "abc" });
        s.Save(File_);

        var loaded = AppSettings.Load(File_);
        Assert.Equal("Allflame", loaded.DefaultLeague);
        Assert.Equal("Ctrl+D", loaded.TravelHotkey);
        var w = Assert.Single(loaded.Watches);
        Assert.Equal("Wands", w.Name);
        Assert.Equal("abc", w.QueryId);
        Assert.True(w.Enabled);
    }

    [Fact]
    public void MissingFileGivesDefaults()
    {
        var s = AppSettings.Load(Path.Combine(_dir, "nope.json"));
        Assert.Empty(s.Watches);
        Assert.False(string.IsNullOrWhiteSpace(s.UserAgent));
    }

    [Fact]
    public void CorruptFileDoesNotPreventStartup()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(File_, "{ this is not json");
        var s = AppSettings.Load(File_);
        Assert.NotNull(s);
        Assert.Empty(s.Watches);
    }

    [Fact]
    public void NeverContainsCredentials()
    {
        // Secrets belong in the DPAPI store; the plain JSON must stay safe to share.
        var s = new AppSettings { DefaultLeague = "Allflame" };
        s.Watches.Add(new Watch { Name = "x", League = "Allflame", QueryId = "q" });
        s.Save(File_);
        var text = File.ReadAllText(File_);
        Assert.DoesNotContain("POESESSID", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("POETOKEN", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WatchExposesItsTradeUrl()
    {
        var w = new Watch { League = "Allflame", QueryId = "aL5X0Qaaue" };
        Assert.Equal("https://www.pathofexile.com/trade/search/Allflame/aL5X0Qaaue", w.TradeUrl);
    }
}
