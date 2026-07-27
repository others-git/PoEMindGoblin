using System.Net;
using System.Text;
using System.Text.Json;
using PoeMarketWatch.Core;

namespace PoeMarketWatch.Tests;

public class TradeClientTests
{
    private const string Ua = "poe-market-watch/test (contact: test@example.com)";

    private const string HideoutJwt =
        "eyJ0eXAiOiJKV1QiLCJhbGciOiJIUzI1NiJ9.eyJ0b2siOiJoaWRlb3V0IiwiZXhwIjo0MTAyNDQ0ODAwfQ.sig";
    private const string ExpiredJwt =
        "eyJ0eXAiOiJKV1QiLCJhbGciOiJIUzI1NiJ9.eyJ0b2siOiJoaWRlb3V0IiwiZXhwIjoxMDAwMDAwMDAwfQ.sig";

    /// Records requests and replays canned responses.
    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<string?> Bodies { get; } = new();
        public Queue<HttpResponseMessage> Responses { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? null
                : await request.Content.ReadAsStringAsync(cancellationToken));
            return Responses.Count > 0
                ? Responses.Dequeue()
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (TradeClient, FakeHandler) Make(bool withCreds = true)
    {
        var handler = new FakeHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri(TradeClient.BaseUrl) };
        var client = new TradeClient(Ua, http,
            credentials: withCreds
                ? () => new CredentialStore.Credentials("sess", "tok")
                : () => null);
        return (client, handler);
    }

    private static JsonElement Query(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void RequiresDescriptiveUserAgent()
    {
        Assert.Throws<ArgumentException>(() => new TradeClient(""));
        Assert.Throws<ArgumentException>(() => new TradeClient("   "));
    }

    [Fact]
    public async Task SearchParsesIdAndResults()
    {
        var (client, handler) = Make();
        handler.Responses.Enqueue(Json("""
            {"id":"D6OaqrogC5","complexity":7,"total":3,"result":["aaa","bbb","ccc"]}
            """));

        var res = await client.SearchAsync("Allflame", Query("""{"query":{}}"""));

        Assert.Equal("D6OaqrogC5", res.QueryId);
        Assert.Equal(3, res.Total);
        Assert.Equal(new[] { "aaa", "bbb", "ccc" }, res.ItemIds);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Contains("/api/trade/search/Allflame", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task SearchDoesNotSendCookies()
    {
        // Search works unauthenticated; sending the session needlessly widens exposure.
        var (client, handler) = Make();
        handler.Responses.Enqueue(Json("""{"id":"x","result":[]}"""));
        await client.SearchAsync("Allflame", Query("""{"query":{}}"""));
        Assert.False(handler.Requests[0].Headers.Contains("Cookie"));
    }

    [Fact]
    public async Task SearchRejectsUnexpectedShape()
    {
        var (client, handler) = Make();
        handler.Responses.Enqueue(Json("""{"unexpected":true}"""));
        var ex = await Assert.ThrowsAsync<TradeApiException>(
            () => client.SearchAsync("Allflame", Query("""{"query":{}}""")));
        Assert.Contains("shape may have changed", ex.Message);
    }

    [Fact]
    public async Task FetchParsesRealUnauthenticatedResponse()
    {
        var (client, handler) = Make();
        var fixture = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "fixtures", "fetch-unauthenticated.json"));
        handler.Responses.Enqueue(Json(fixture));

        var listings = await client.FetchAsync(new[] { "abc" }, "q1");

        var l = Assert.Single(listings);
        Assert.Equal(1045, l.GoldFee);            // async Market listing
        Assert.False(l.SellerOnline);             // account.online was null
        Assert.NotNull(l.Whisper);
        Assert.Null(l.HideoutToken);              // no token without a session
        Assert.Null(client.DiscoveredTokenPath);
    }

    [Fact]
    public async Task FetchFindsHideoutTokenUnderAnyFieldName()
    {
        var (client, handler) = Make();
        handler.Responses.Enqueue(Json(
            "{\"result\":[{\"id\":\"abc\",\"item\":{\"name\":\"Mystic Refractor\"}," +
            "\"listing\":{\"method\":\"psapi\",\"fee\":7900," +
            "\"account\":{\"name\":\"Sklogwk#7167\",\"online\":{\"league\":\"Allflame\"}}," +
            "\"some_future_name\":\"" + HideoutJwt + "\"," +
            "\"price\":{\"amount\":80,\"currency\":\"chaos\"}}}]}"));

        var l = (await client.FetchAsync(new[] { "abc" }, "q1")).Single();

        Assert.NotNull(l.HideoutToken);
        Assert.True(l.HideoutToken!.IsHideout);
        Assert.Equal("Mystic Refractor", l.ItemName);
        Assert.Equal("80 chaos", l.PriceText);
        Assert.Equal(7900, l.GoldFee);
        Assert.True(l.SellerOnline);
        // the real field name is reported so it self-documents
        Assert.Contains("some_future_name", client.DiscoveredTokenPath!);
    }

    [Fact]
    public async Task FetchBatchesAtTenIds()
    {
        var (client, handler) = Make();
        for (var i = 0; i < 3; i++) handler.Responses.Enqueue(Json("""{"result":[]}"""));

        await client.FetchAsync(Enumerable.Range(0, 25).Select(i => $"id{i}"), "q1");

        Assert.Equal(3, handler.Requests.Count); // 10 + 10 + 5
        Assert.Equal(10, handler.Requests[0].RequestUri!.ToString()
            .Split("/api/trade/fetch/")[1].Split('?')[0].Split(',').Length);
    }

    [Fact]
    public async Task FetchSendsAuthAndCsrfHeaders()
    {
        var (client, handler) = Make();
        handler.Responses.Enqueue(Json("""{"result":[]}"""));
        await client.FetchAsync(new[] { "a" }, "q1");

        var req = handler.Requests[0];
        Assert.True(req.Headers.Contains("Cookie"));
        Assert.Contains("POESESSID=sess", req.Headers.GetValues("Cookie").First());
        Assert.True(req.Headers.Contains("X-Requested-With"));
        Assert.NotNull(req.Headers.Referrer);
    }

    [Fact]
    public async Task AuthenticatedCallWithoutCredentialsFailsClearly()
    {
        var (client, _) = Make(withCreds: false);
        var ex = await Assert.ThrowsAsync<TradeAuthException>(
            () => client.FetchAsync(new[] { "a" }, "q1"));
        Assert.Contains("no OAuth scope for trade", ex.Message);
    }

    [Fact]
    public async Task ActivatePostsTokenToWhisperEndpoint()
    {
        // Travel and whisper are the same endpoint; the token's 'tok' claim decides.
        var (client, handler) = Make();
        handler.Responses.Enqueue(Json("""{"success":true}"""));

        await client.ActivateTokenAsync(HideoutJwt);

        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.EndsWith("/api/trade/whisper", handler.Requests[0].RequestUri!.ToString());
        Assert.Contains(HideoutJwt, handler.Bodies[0]!);
    }

    [Fact]
    public async Task ActivateRefusesExpiredTokenWithoutCallingTheApi()
    {
        var (client, handler) = Make();
        var ex = await Assert.ThrowsAsync<TradeApiException>(() => client.ActivateTokenAsync(ExpiredJwt));
        Assert.Contains("expired", ex.Message);
        Assert.Empty(handler.Requests); // never hit the network
    }

    [Fact]
    public async Task RateLimitHeadersAreLearned()
    {
        var (client, handler) = Make();
        var resp = Json("""{"id":"x","result":[]}""");
        resp.Headers.Add("x-rate-limit-rules", "Ip");
        resp.Headers.Add("x-rate-limit-ip", "5:10:60");
        resp.Headers.Add("x-rate-limit-ip-state", "1:10:0");
        handler.Responses.Enqueue(resp);

        await client.SearchAsync("Allflame", Query("""{"query":{}}"""));

        Assert.Contains("ip", client.Limiter.PolicyNames);
    }

    [Fact]
    public async Task TooManyRequestsBecomesTypedExceptionAndPenalty()
    {
        var (client, handler) = Make();
        var resp = Json("""{"error":"rate limited"}""", HttpStatusCode.TooManyRequests);
        resp.Headers.Add("Retry-After", "45");
        handler.Responses.Enqueue(resp);

        var ex = await Assert.ThrowsAsync<TradeRateLimitException>(
            () => client.SearchAsync("Allflame", Query("""{"query":{}}""")));
        Assert.Equal(45, ex.RetryAfter.TotalSeconds, 1);
        Assert.True(client.Limiter.Delay() > TimeSpan.Zero); // now self-gated
    }

    [Fact]
    public async Task NonJsonResponseIsReportedNotSwallowed()
    {
        // Undocumented API: a Cloudflare HTML page must fail loudly, not parse as empty.
        var (client, handler) = Make();
        handler.Responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<!DOCTYPE html><html>...", Encoding.UTF8, "text/html"),
        });
        var ex = await Assert.ThrowsAsync<TradeApiException>(
            () => client.SearchAsync("Allflame", Query("""{"query":{}}""")));
        Assert.Contains("non-JSON", ex.Message);
    }
}
