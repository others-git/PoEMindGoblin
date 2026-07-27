using System.Reflection;
using PoeMarketWatch.Core;

namespace PoeMarketWatch.Tests;

public class HttpFactoryTests
{
    /// <summary>
    /// Regression for a bug that cost real debugging time: with the default
    /// HttpClientHandler, UseCookies is true, the handler owns the Cookie header from its
    /// own empty container, and a manually set Cookie header is dropped. Every
    /// authenticated call then went out with no cookies and returned 401 -- identical to
    /// "your session is invalid", so the hunt went to POESESSID/POETOKEN/cf_clearance
    /// while the credentials were fine.
    /// </summary>
    [Fact]
    public void HandlerMustNotManageCookies()
    {
        using var client = HttpFactory.Create();

        var handler = GetHandler(client);
        Assert.NotNull(handler);
        Assert.False(handler!.UseCookies,
            "UseCookies must be false or manually set Cookie headers are silently dropped");
    }

    [Fact]
    public void DecompressionIsEnabled()
    {
        // GGG serves gzip/brotli; without this the body never parses as JSON.
        using var client = HttpFactory.Create();
        var handler = GetHandler(client);
        Assert.NotEqual(System.Net.DecompressionMethods.None, handler!.AutomaticDecompression);
    }

    [Fact]
    public void DefaultsToTheTradeHost()
    {
        using var client = HttpFactory.Create();
        Assert.Equal(new Uri(TradeClient.BaseUrl), client.BaseAddress);
    }

    [Fact]
    public void HasATimeoutSoAHungSocketCannotStallAWatch()
    {
        using var client = HttpFactory.Create();
        Assert.True(client.Timeout > TimeSpan.Zero);
        Assert.True(client.Timeout < TimeSpan.FromMinutes(2));
    }

    private static HttpClientHandler? GetHandler(HttpClient client)
    {
        // The handler is private state on HttpMessageInvoker.
        var field = typeof(HttpMessageInvoker).GetField(
            "_handler", BindingFlags.NonPublic | BindingFlags.Instance);
        return field?.GetValue(client) as HttpClientHandler;
    }
}
