using System.Net;
using System.Text;
using System.Text.Json;

namespace PoeMarketWatch.Core;

/// <summary>
/// Tests each layer of the trade API separately so a failure says WHICH layer broke.
///
/// This exists because the failure modes are indistinguishable from the outside: a dead
/// socket, an expired session, a stale query id and a quiet market all look identical
/// (nothing happens). Each check below isolates one cause.
///
/// Ordering matters -- later checks depend on earlier ones, so the first failure is the
/// real one and the rest are noise.
/// </summary>
public sealed class ConnectionTest
{
    public enum Result { Pass, Fail, Skipped }

    public sealed record Step(string Name, Result Result, string Detail);

    private readonly HttpClient _http;
    private readonly string _userAgent;
    private readonly Func<CredentialStore.Credentials?> _credentials;

    public ConnectionTest(string userAgent, Func<CredentialStore.Credentials?> credentials,
                          HttpClient? http = null)
    {
        _userAgent = userAgent;
        _credentials = credentials;
        _http = http ?? new HttpClient { BaseAddress = new Uri(TradeClient.BaseUrl) };
        _http.BaseAddress ??= new Uri(TradeClient.BaseUrl);
    }

    public async Task<IReadOnlyList<Step>> RunAsync(
        string league, string queryId, CancellationToken ct = default)
    {
        var steps = new List<Step>();

        // 1. Reachability + user agent. Unauthenticated, so this isolates network/UA/
        //    Cloudflare problems from credential problems.
        var reach = await CheckSearchAsync(league, ct).ConfigureAwait(false);
        steps.Add(reach);
        if (reach.Result == Result.Fail)
        {
            steps.Add(new Step("Session cookies", Result.Skipped, "skipped: the API is unreachable"));
            steps.Add(new Step("Live search socket", Result.Skipped, "skipped: the API is unreachable"));
            return steps;
        }

        // 2. Are the cookies valid? The socket 401s for the same reason a fetch would,
        //    but a fetch gives a readable status code instead of a WebSocketException.
        var creds = _credentials();
        if (creds is null)
        {
            steps.Add(new Step("Session cookies", Result.Fail, "no session stored - open Session..."));
            steps.Add(new Step("Live search socket", Result.Skipped, "skipped: no session"));
            return steps;
        }

        // 3. The socket handshake itself. Done as a raw HTTP upgrade rather than via
        //    ClientWebSocket, because that throws away the status code -- and 401 (bad
        //    cookies) vs 404 (dead query id) are completely different problems.
        var socket = await CheckSocketAsync(league, queryId, creds, ct).ConfigureAwait(false);
        steps.Insert(1, socket.cookies);
        steps.Add(socket.handshake);
        return steps;
    }

    private async Task<Step> CheckSearchAsync(string league, CancellationToken ct)
    {
        const string body = """{"query":{"status":{"option":"securable"}},"sort":{"price":"asc"}}""";
        try
        {
            using var req = new HttpRequestMessage(
                HttpMethod.Post, $"/api/trade/search/{Uri.EscapeDataString(league)}")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            req.Headers.TryAddWithoutValidation("User-Agent", _userAgent);

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                return new Step("Trade API reachable", Result.Fail,
                    "429 rate limited - wait before retrying");
            if (!resp.IsSuccessStatusCode)
                return new Step("Trade API reachable", Result.Fail,
                    $"HTTP {(int)resp.StatusCode} - is '{league}' the right league name?");

            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(text);
            var total = doc.RootElement.TryGetProperty("total", out var t) && t.TryGetInt32(out var n)
                ? n : -1;
            return new Step("Trade API reachable", Result.Pass,
                total >= 0 ? $"200 OK, league has {total} listings" : "200 OK");
        }
        catch (Exception ex)
        {
            return new Step("Trade API reachable", Result.Fail, ex.Message);
        }
    }

    private async Task<(Step cookies, Step handshake)> CheckSocketAsync(
        string league, string queryId, CredentialStore.Credentials creds, CancellationToken ct)
    {
        var uri = $"/api/trade/live/{Uri.EscapeDataString(league)}/{Uri.EscapeDataString(queryId)}";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
            req.Headers.TryAddWithoutValidation("Cookie", CredentialStore.ToCookieHeader(creds));
            req.Headers.TryAddWithoutValidation("Origin", TradeClient.BaseUrl);
            req.Headers.TryAddWithoutValidation("Connection", "Upgrade");
            req.Headers.TryAddWithoutValidation("Upgrade", "websocket");
            req.Headers.TryAddWithoutValidation("Sec-WebSocket-Version", "13");
            req.Headers.TryAddWithoutValidation("Sec-WebSocket-Key",
                Convert.ToBase64String(Guid.NewGuid().ToByteArray()));

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                                        .ConfigureAwait(false);
            var code = (int)resp.StatusCode;

            return code switch
            {
                101 => (new Step("Session cookies", Result.Pass, "accepted"),
                        new Step("Live search socket", Result.Pass, "101 Switching Protocols")),

                401 or 403 => (
                    new Step("Session cookies", Result.Fail,
                        $"HTTP {code} - POESESSID is missing, wrong or expired. Log in again and re-copy it."),
                    new Step("Live search socket", Result.Skipped, "skipped: cookies rejected")),

                // The socket attaches to an EXISTING search. Search ids do not live
                // forever, so a watch saved days ago can 404 while the same filters
                // work fine in the browser.
                404 => (new Step("Session cookies", Result.Pass, "accepted"),
                        new Step("Live search socket", Result.Fail,
                            $"HTTP 404 - query id '{queryId}' no longer exists. Re-run the search on "
                            + "the trade site and paste the new URL.")),

                429 => (new Step("Session cookies", Result.Pass, "accepted"),
                        new Step("Live search socket", Result.Fail, "429 rate limited")),

                _ => (new Step("Session cookies", Result.Pass, "accepted"),
                      new Step("Live search socket", Result.Fail,
                          $"HTTP {code} {resp.ReasonPhrase}")),
            };
        }
        catch (Exception ex)
        {
            return (new Step("Session cookies", Result.Skipped, "could not be tested"),
                    new Step("Live search socket", Result.Fail, ex.Message));
        }
    }
}
