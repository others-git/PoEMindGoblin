using System.Net;
using System.Text;
using PoeMarketWatch.Core;

namespace PoeMarketWatch.Tests;

public class ConnectionTestTests
{
    private const string Ua = "poe-market-watch/test (contact: test@example.com)";

    private sealed class FakeHandler : HttpMessageHandler
    {
        public Queue<HttpResponseMessage> Responses { get; } = new();
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Responses.Count > 0
                ? Responses.Dequeue()
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (ConnectionTest, FakeHandler) Make(bool withCreds = true)
    {
        var handler = new FakeHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri(TradeClient.BaseUrl) };
        return (new ConnectionTest(Ua,
            withCreds ? () => new CredentialStore.Credentials("sess", "tok") : () => null,
            http), handler);
    }

    private static ConnectionTest.Step Find(IReadOnlyList<ConnectionTest.Step> steps, string name)
        => steps.First(s => s.Name == name);

    [Fact]
    public async Task AllGreenWhenSocketUpgrades()
    {
        var (test, handler) = Make();
        handler.Responses.Enqueue(Json("""{"id":"x","total":42,"result":[]}"""));
        handler.Responses.Enqueue(new HttpResponseMessage((HttpStatusCode)101));

        var steps = await test.RunAsync("Allflame", "q1");

        Assert.All(steps, s => Assert.Equal(ConnectionTest.Result.Pass, s.Result));
        Assert.Contains("42 listings", Find(steps, "Trade API reachable").Detail);
    }

    [Fact]
    public async Task UnreachableApiSkipsTheRest()
    {
        // The first failure is the real one; the rest would be noise.
        var (test, handler) = Make();
        handler.Responses.Enqueue(Json("nope", HttpStatusCode.ServiceUnavailable));

        var steps = await test.RunAsync("Allflame", "q1");

        Assert.Equal(ConnectionTest.Result.Fail, Find(steps, "Trade API reachable").Result);
        Assert.Equal(ConnectionTest.Result.Skipped, Find(steps, "Live search socket").Result);
    }

    [Fact]
    public async Task WrongLeagueIsCalledOut()
    {
        var (test, handler) = Make();
        handler.Responses.Enqueue(Json("nope", HttpStatusCode.NotFound));
        var steps = await test.RunAsync("Nonsense", "q1");
        Assert.Contains("right league name", Find(steps, "Trade API reachable").Detail);
    }

    [Fact]
    public async Task MissingSessionIsReportedBeforeTouchingTheSocket()
    {
        var (test, handler) = Make(withCreds: false);
        handler.Responses.Enqueue(Json("""{"id":"x","result":[]}"""));

        var steps = await test.RunAsync("Allflame", "q1");

        Assert.Equal(ConnectionTest.Result.Fail, Find(steps, "Session cookies").Result);
        Assert.Equal(ConnectionTest.Result.Skipped, Find(steps, "Live search socket").Result);
        Assert.Single(handler.Requests); // never attempted the upgrade
    }

    [Fact]
    public async Task ExpiredCookiesBlameCookiesNotTheSocket()
    {
        var (test, handler) = Make();
        handler.Responses.Enqueue(Json("""{"id":"x","result":[]}"""));
        handler.Responses.Enqueue(new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var steps = await test.RunAsync("Allflame", "q1");

        var cookies = Find(steps, "Session cookies");
        Assert.Equal(ConnectionTest.Result.Fail, cookies.Result);
        Assert.Contains("expired", cookies.Detail);
        Assert.Equal(ConnectionTest.Result.Skipped, Find(steps, "Live search socket").Result);
    }

    [Fact]
    public async Task DeadQueryIdBlamesTheQueryNotTheCookies()
    {
        // The failure that looks exactly like "quiet market": the search id expired,
        // so the socket 404s while the same filters still work in the browser.
        var (test, handler) = Make();
        handler.Responses.Enqueue(Json("""{"id":"x","result":[]}"""));
        handler.Responses.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound));

        var steps = await test.RunAsync("Allflame", "staleId");

        Assert.Equal(ConnectionTest.Result.Pass, Find(steps, "Session cookies").Result);
        var socket = Find(steps, "Live search socket");
        Assert.Equal(ConnectionTest.Result.Fail, socket.Result);
        Assert.Contains("staleId", socket.Detail);
        Assert.Contains("no longer exists", socket.Detail);
    }

    [Fact]
    public async Task RateLimitIsDistinguishedFromAuthFailure()
    {
        var (test, handler) = Make();
        handler.Responses.Enqueue(Json("""{"id":"x","result":[]}"""));
        handler.Responses.Enqueue(new HttpResponseMessage(HttpStatusCode.TooManyRequests));

        var steps = await test.RunAsync("Allflame", "q1");
        Assert.Contains("429", Find(steps, "Live search socket").Detail);
    }

    [Fact]
    public async Task SocketRequestCarriesUpgradeAndCookies()
    {
        var (test, handler) = Make();
        handler.Responses.Enqueue(Json("""{"id":"x","result":[]}"""));
        handler.Responses.Enqueue(new HttpResponseMessage((HttpStatusCode)101));

        await test.RunAsync("Allflame", "q1");

        var upgrade = handler.Requests[1];
        Assert.Contains("/api/trade/live/Allflame/q1", upgrade.RequestUri!.ToString());
        Assert.True(upgrade.Headers.Contains("Cookie"));
        Assert.True(upgrade.Headers.Contains("Upgrade"));
        Assert.True(upgrade.Headers.Contains("Sec-WebSocket-Key"));
    }

    [Fact]
    public async Task SearchProbeIsUnauthenticated()
    {
        // Isolating network/UA problems from credential problems requires this call to
        // carry no cookies at all.
        var (test, handler) = Make();
        handler.Responses.Enqueue(Json("""{"id":"x","result":[]}"""));
        handler.Responses.Enqueue(new HttpResponseMessage((HttpStatusCode)101));

        await test.RunAsync("Allflame", "q1");

        Assert.False(handler.Requests[0].Headers.Contains("Cookie"));
    }
}
