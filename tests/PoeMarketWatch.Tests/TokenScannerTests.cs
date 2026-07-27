using System.Text.Json;
using PoeMarketWatch.Core;

namespace PoeMarketWatch.Tests;

public class TokenScannerTests
{
    /// A real "Travel to Hideout" token captured from the trade site (long expired).
    /// tok=hideout, iss=<search id>, sub=<item hash>, 300s TTL.
    private const string RealHideoutJwt =
        "eyJ0eXAiOiJKV1QiLCJhbGciOiJIUzI1NiJ9." +
        "eyJqdGkiOiJmNDExMTE0MGUxYTA2MWM4MWE2YmEyNjMzYzYxNGMzMyIsImlzcyI6ImFMNVgwUWFhdWUiL" +
        "CJhdWQiOiI2MmE2YzQ0Ni1mOGQzLTQyZTMtYjQ2NC02OGE3NjBlOTQ5MjkiLCJ0b2siOiJoaWRlb3V0Iiw" +
        "ic3ViIjoiNjIzMDY4MzZmNDA5YTlkZTA3YjdlOGY0MzAwMTdlOGY2MTdmNTQzYjg1ODY1OGQyNzk5ZmQ0N" +
        "WI0MzdhNDYxMCIsImRhdCI6ImY0NDAyMWE2NDIwNGM4MzNkMjFmYzUyYjQ0M2Y4NTYyIiwiaWF0IjoxNzg" +
        "1MTcwMjc4LCJleHAiOjE3ODUxNzA1Nzh9." +
        "OBoQaVr4OU2aqlahVpbwoTlgHZh-ustL9k6BeuQqh4o";

    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    [Fact]
    public void ReadsRealHideoutToken()
    {
        Assert.True(TokenScanner.TryReadJwt(RealHideoutJwt, out var kind));
        Assert.Equal("hideout", kind);
    }

    [Fact]
    public void FindsTokenRegardlessOfFieldName()
    {
        // The whole point: we do not know what GGG calls this field, and community
        // models disagree/are stale. Any of these must work.
        foreach (var field in new[] { "hideout_token", "whisper_token", "token", "travelToken" })
        {
            var json = "{\"result\":[{\"id\":\"abc\",\"listing\":{\"method\":\"psapi\",\""
                       + field + "\":\"" + RealHideoutJwt + "\"}}]}";
            var found = TokenScanner.Scan(json);
            Assert.Single(found);
            Assert.Equal("hideout", found[0].Kind);
            Assert.True(found[0].IsHideout);
            Assert.Contains(field, found[0].FoundAtPath, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ReportsPathSoTheFieldSelfDocuments()
    {
        var json = "{\"result\":[{\"listing\":{\"nested\":{\"deep\":\""
                   + RealHideoutJwt + "\"}}}]}";
        var found = TokenScanner.Scan(json);
        Assert.Equal("$.result[0].listing.nested.deep", found[0].FoundAtPath);
    }

    [Fact]
    public void RealUnauthenticatedResponseHasNoToken()
    {
        // Captured live from GET /api/trade/fetch/... with no cookies. Confirms the
        // token is session-gated, which is why the app needs POESESSID at all.
        var json = File.ReadAllText(FixturePath("fetch-unauthenticated.json"));
        Assert.Empty(TokenScanner.Scan(json));

        using var doc = JsonDocument.Parse(json);
        var listing = doc.RootElement.GetProperty("result")[0].GetProperty("listing");
        // ...but it does carry the async-trade gold fee, proving these are Market listings.
        Assert.True(listing.TryGetProperty("fee", out _));
    }

    [Fact]
    public void IgnoresProseAndNonJwtStrings()
    {
        var json = """
        {"listing":{"whisper":"@Someone Hi, I would like to buy your Wrath Finger Sapphire Ring",
                    "method":"psapi","stash":{"name":"6"},"price":{"currency":"alch"}}}
        """;
        Assert.Empty(TokenScanner.Scan(json));
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("a.b.c")]
    [InlineData("not.a.jwt-at-all-but-long-enough-to-pass-the-length-check-xxxxx")]
    [InlineData("eyJ0eXAiOiJKV1QifQ.!!!notbase64!!!.sig")]
    public void RejectsMalformed(string value)
    {
        Assert.False(TokenScanner.TryReadJwt(value, out _));
    }

    [Fact]
    public void ComputesExpiry()
    {
        // exp = 1785170578
        var issued = DateTimeOffset.FromUnixTimeSeconds(1785170278);
        var ttl = TokenScanner.SecondsUntilExpiry(RealHideoutJwt, issued);
        Assert.NotNull(ttl);
        Assert.Equal(300, ttl!.Value, 1);

        var later = DateTimeOffset.FromUnixTimeSeconds(1785170578 + 60);
        Assert.True(TokenScanner.SecondsUntilExpiry(RealHideoutJwt, later)!.Value < 0);
    }

    [Fact]
    public void ExpiryIsNullWhenAbsent()
    {
        // {"tok":"hideout"} with no exp
        const string noExp = "eyJ0eXAiOiJKV1QiLCJhbGciOiJIUzI1NiJ9.eyJ0b2siOiJoaWRlb3V0In0.sig";
        Assert.True(TokenScanner.TryReadJwt(noExp, out var kind));
        Assert.Equal("hideout", kind);
        Assert.Null(TokenScanner.SecondsUntilExpiry(noExp, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void DistinguishesWhisperFromHideout()
    {
        // same shape, tok=whisper  ->  {"tok":"whisper","exp":1785170578}
        const string whisperJwt =
            "eyJ0eXAiOiJKV1QiLCJhbGciOiJIUzI1NiJ9.eyJ0b2siOiJ3aGlzcGVyIiwiZXhwIjoxNzg1MTcwNTc4fQ.sig";
        Assert.True(TokenScanner.TryReadJwt(whisperJwt, out var kind));
        Assert.Equal("whisper", kind);
        var found = TokenScanner.Scan("{\"a\":\"" + whisperJwt + "\"}");
        Assert.False(found[0].IsHideout);
    }

    [Fact]
    public void TokenToStringDoesNotDumpTheToken()
    {
        var t = new TokenScanner.ActionToken(RealHideoutJwt, "hideout", "$.x");
        Assert.DoesNotContain(RealHideoutJwt, t.ToString(), StringComparison.Ordinal);
    }
}
