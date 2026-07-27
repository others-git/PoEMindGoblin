namespace PoeMarketWatch.Core;

/// <summary>
/// Builds the HttpClient used for every trade API call.
///
/// The reason this exists rather than `new HttpClient()`:
///
/// HttpClientHandler defaults to <c>UseCookies = true</c>, which makes the handler manage
/// the Cookie header from its own (empty) CookieContainer -- and a Cookie header you set
/// yourself on the request is then dropped or overwritten. Every authenticated call
/// therefore went out with NO cookies and came back 401, which is indistinguishable from
/// "your session is invalid". That cost real debugging time chasing POESESSID, POETOKEN
/// and cf_clearance when the credentials were fine all along.
///
/// We set the header explicitly because the cookies come from the user's browser, not
/// from a login this app performs, so a CookieContainer buys nothing.
///
/// AutomaticDecompression is on because GGG serves gzip/brotli and a raw body would
/// otherwise fail to parse as JSON.
/// </summary>
public static class HttpFactory
{
    public static HttpClient Create(Uri? baseAddress = null)
    {
        var handler = new HttpClientHandler
        {
            // Non-negotiable: see the class remarks. Manual Cookie headers only work
            // when the handler is not managing cookies itself.
            UseCookies = false,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        };

        return new HttpClient(handler)
        {
            BaseAddress = baseAddress ?? new Uri(TradeClient.BaseUrl),
            Timeout = TimeSpan.FromSeconds(30),
        };
    }
}
