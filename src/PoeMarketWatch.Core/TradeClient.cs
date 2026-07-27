using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace PoeMarketWatch.Core;

/// <summary>
/// Client for pathofexile.com's (undocumented) trade API.
///
/// GGG does not publish a spec for these endpoints -- when asked about third-party
/// access they said only that "the internal APIs currently used by the trade website
/// will remain available without authentication for now". That is an acknowledgement,
/// not a guarantee: shapes can change without notice, so every response is parsed
/// defensively and anything unexpected surfaces as <see cref="TradeApiException"/>
/// rather than being swallowed.
///
/// Auth split, measured against the live API:
///   POST /api/trade/search/{league}     -> 200 unauthenticated
///   GET  /api/trade/fetch/{ids}?query=  -> 200 unauthenticated, but NO tokens
///   POST /api/trade/whisper             -> 401 without cookies
///   wss  /api/trade/live/{league}/{id}  -> 401 without cookies
/// </summary>
public sealed class TradeClient : IDisposable
{
    public const string BaseUrl = "https://www.pathofexile.com";

    /// The fetch endpoint accepts at most 10 item ids per call.
    public const int FetchBatchSize = 10;

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly RateLimiter _limiter;

    public TradeClient(
        string userAgent,
        HttpClient? http = null,
        RateLimiter? limiter = null,
        Func<CredentialStore.Credentials?>? credentials = null)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
            throw new ArgumentException(
                "GGG asks third-party clients to identify themselves; a descriptive " +
                "User-Agent with contact details is not optional.", nameof(userAgent));

        _ownsHttp = http is null;
        _http = http ?? HttpFactory.Create();
        _http.BaseAddress ??= new Uri(BaseUrl);
        if (!_http.DefaultRequestHeaders.UserAgent.TryParseAdd(userAgent))
            _http.DefaultRequestHeaders.Add("User-Agent", userAgent);

        _limiter = limiter ?? new RateLimiter();
        _credentials = credentials ?? (() => null);
    }

    private readonly Func<CredentialStore.Credentials?> _credentials;

    public RateLimiter Limiter => _limiter;

    /// <summary>Set once the first authenticated fetch reveals where GGG puts the token.</summary>
    public string? DiscoveredTokenPath { get; private set; }

    // ------------------------------------------------------------------ search
    public sealed record SearchResult(string QueryId, IReadOnlyList<string> ItemIds, int Total);

    /// <param name="query">The full query object, i.e. {"query":{...},"sort":{...}}.</param>
    public async Task<SearchResult> SearchAsync(string league, JsonElement query, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/trade/search/{Uri.EscapeDataString(league)}")
        {
            Content = new StringContent(query.GetRawText(), Encoding.UTF8, "application/json"),
        };
        using var doc = await SendJsonAsync(req, authenticated: false, ct).ConfigureAwait(false);
        var root = doc.RootElement;

        if (!root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String)
            throw new TradeApiException("search response had no 'id' -- the API shape may have changed");

        var ids = new List<string>();
        if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Array)
            foreach (var item in result.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s)
                    ids.Add(s);

        var total = root.TryGetProperty("total", out var t) && t.TryGetInt32(out var n) ? n : ids.Count;
        return new SearchResult(id.GetString()!, ids, total);
    }

    // ------------------------------------------------------------------- fetch
    public sealed record Listing(
        string ItemId,
        string? ItemName,
        string? AccountName,
        string? Whisper,
        long? GoldFee,
        string? PriceText,
        bool SellerOnline,
        TokenScanner.ActionToken? HideoutToken,
        JsonElement Raw);

    /// <summary>
    /// Fetch listing detail. Authenticated calls also carry the short-lived action
    /// tokens; unauthenticated ones do not, so travel will be unavailable.
    /// </summary>
    public async Task<IReadOnlyList<Listing>> FetchAsync(
        IEnumerable<string> itemIds, string queryId, CancellationToken ct = default)
    {
        var all = new List<Listing>();
        foreach (var batch in itemIds.Chunk(FetchBatchSize))
        {
            var path = $"/api/trade/fetch/{string.Join(',', batch)}?query={Uri.EscapeDataString(queryId)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, path);
            using var doc = await SendJsonAsync(req, authenticated: true, ct).ConfigureAwait(false);

            if (!doc.RootElement.TryGetProperty("result", out var result)
                || result.ValueKind != JsonValueKind.Array)
                throw new TradeApiException("fetch response had no 'result' array");

            foreach (var entry in result.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                all.Add(ParseListing(entry.Clone()));
            }
        }
        return all;
    }

    private Listing ParseListing(JsonElement entry)
    {
        var listing = entry.TryGetProperty("listing", out var l) ? l : default;
        var account = listing.ValueKind == JsonValueKind.Object
                      && listing.TryGetProperty("account", out var a) ? a : default;

        // Token field name is not documented and community models are stale, so find
        // it by shape rather than by name. See TokenScanner.
        TokenScanner.ActionToken? hideout = null;
        foreach (var tok in TokenScanner.Scan(entry))
        {
            if (!tok.IsHideout) continue;
            hideout = tok;
            DiscoveredTokenPath ??= tok.FoundAtPath;
            break;
        }

        return new Listing(
            ItemId: Str(entry, "id") ?? "",
            ItemName: entry.TryGetProperty("item", out var it) ? Str(it, "name") ?? Str(it, "typeLine") : null,
            AccountName: Str(account, "name"),
            Whisper: Str(listing, "whisper"),
            GoldFee: listing.ValueKind == JsonValueKind.Object
                     && listing.TryGetProperty("fee", out var f) && f.TryGetInt64(out var fee) ? fee : null,
            PriceText: PriceOf(listing),
            SellerOnline: account.ValueKind == JsonValueKind.Object
                          && account.TryGetProperty("online", out var on)
                          && on.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined),
            HideoutToken: hideout,
            Raw: entry);
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? PriceOf(JsonElement listing)
    {
        if (listing.ValueKind != JsonValueKind.Object) return null;
        if (!listing.TryGetProperty("price", out var p) || p.ValueKind != JsonValueKind.Object) return null;
        var amount = p.TryGetProperty("amount", out var am) && am.TryGetDouble(out var d) ? d : (double?)null;
        var currency = Str(p, "currency");
        return amount is null || currency is null ? null : $"{amount:0.##} {currency}";
    }

    // ------------------------------------------------------------------ travel
    /// <summary>
    /// Trigger "Travel to Hideout" (or a whisper) for a listing.
    ///
    /// Travel and whisper are the SAME endpoint, distinguished by the 'tok' claim in
    /// the server-signed token. Tokens live ~300s and cannot be forged, only relayed.
    ///
    /// This must only ever be called in direct response to a user action -- one
    /// keypress, one call. Firing it automatically on a live-search match is
    /// unattended automation and is not something this client should be wired to do.
    /// </summary>
    public async Task ActivateTokenAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("empty token", nameof(token));

        var ttl = TokenScanner.SecondsUntilExpiry(token, DateTimeOffset.UtcNow);
        if (ttl is < 0)
            throw new TradeApiException(
                $"token expired {-ttl:0}s ago -- refetch the listing (tokens live ~300s)");

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/trade/whisper")
        {
            Content = JsonContent.Create(new { token }),
        };
        using var _ = await SendJsonAsync(req, authenticated: true, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------- plumbing
    private async Task<JsonDocument> SendJsonAsync(
        HttpRequestMessage req, bool authenticated, CancellationToken ct)
    {
        if (authenticated)
        {
            var creds = _credentials()
                ?? throw new TradeAuthException(
                    "this endpoint needs POESESSID -- there is no OAuth scope for trade");
            req.Headers.Add("Cookie", CredentialStore.ToCookieHeader(creds));
            // The endpoint is CSRF-guarded; these are not optional.
            req.Headers.Referrer = new Uri($"{BaseUrl}/trade/search");
            req.Headers.Add("X-Requested-With", "XMLHttpRequest");
            if (!req.Headers.Contains("Origin")) req.Headers.Add("Origin", BaseUrl);
        }

        await _limiter.AcquireAsync(ct: ct).ConfigureAwait(false);
        var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        try
        {
            _limiter.Update(resp.Headers);

            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retry = resp.Headers.RetryAfter?.Delta
                            ?? (resp.Headers.RetryAfter?.Date is { } d ? d - DateTimeOffset.UtcNow : null);
                _limiter.Penalise(retry ?? TimeSpan.FromSeconds(60));
                throw new TradeRateLimitException(retry ?? TimeSpan.FromSeconds(60));
            }

            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new TradeAuthException(
                    $"{(int)resp.StatusCode} from {req.RequestUri} -- session cookies missing or expired");

            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new TradeApiException($"{(int)resp.StatusCode} from {req.RequestUri}: {Trim(body)}");

            try
            {
                return JsonDocument.Parse(body);
            }
            catch (JsonException ex)
            {
                throw new TradeApiException($"non-JSON response from {req.RequestUri}: {Trim(body)}", ex);
            }
        }
        finally
        {
            resp.Dispose();
        }
    }

    private static string Trim(string s) => s.Length <= 200 ? s : s[..200] + "…";

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}

public class TradeApiException : Exception
{
    public TradeApiException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class TradeAuthException : TradeApiException
{
    public TradeAuthException(string message) : base(message) { }
}

public sealed class TradeRateLimitException : TradeApiException
{
    public TimeSpan RetryAfter { get; }
    public TradeRateLimitException(TimeSpan retryAfter)
        : base($"rate limited; retry in {retryAfter.TotalSeconds:0}s") => RetryAfter = retryAfter;
}
