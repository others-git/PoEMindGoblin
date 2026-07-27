using PoeMarketWatch.Core;

namespace PoeMarketWatch.Tests;

public class RateLimiterTests
{
    /// Exactly what the live API returned for POST /api/trade/search/{league}
    private static readonly Dictionary<string, string> LiveHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["x-rate-limit-policy"] = "trade-search-request-limit",
        ["x-rate-limit-rules"] = "Ip",
        ["x-rate-limit-ip"] = "5:10:60,15:60:300,30:300:1800,600:21600:3600",
        ["x-rate-limit-ip-state"] = "1:10:0,1:60:0,1:300:0,11:21600:0",
    };

    /// Fake clock so tests never actually sleep.
    private sealed class Clock
    {
        public TimeSpan Now = TimeSpan.FromSeconds(1000);
        public TimeSpan Get() => Now;
        public Task DelayAsync(TimeSpan d) { Now += d; return Task.CompletedTask; }
        public void Advance(double seconds) => Now += TimeSpan.FromSeconds(seconds);
    }

    private static (RateLimiter, Clock) Make()
    {
        var c = new Clock();
        return (new RateLimiter(c.Get, c.DelayAsync), c);
    }

    private static Func<string, string?> Headers(Dictionary<string, string> d) =>
        k => d.TryGetValue(k, out var v) ? v : null;

    private static Func<string, string?> Headers(params (string, string)[] kv)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in kv) d[k] = v;
        return Headers(d);
    }

    [Fact]
    public void ParsesRuleHeader()
    {
        var r = RateLimiter.ParseRules("5:10:60,15:60:300,30:300:1800,600:21600:3600");
        Assert.Equal(4, r.Count);
        Assert.Equal(new RateLimiter.Rule(5, 10, 60), r[0]);
        Assert.Equal(new RateLimiter.Rule(600, 21600, 3600), r[3]);
    }

    [Fact]
    public void ParseToleratesJunk()
    {
        Assert.Empty(RateLimiter.ParseRules(""));
        Assert.Empty(RateLimiter.ParseRules(null));
        Assert.Equal(new[] { new RateLimiter.Rule(5, 10, 60) },
                     RateLimiter.ParseRules("5:10:60,garbage,x:y:z"));
    }

    [Fact]
    public void LearnsFromLiveHeaders()
    {
        var (rl, _) = Make();
        rl.Update(Headers(LiveHeaders));
        Assert.Contains("ip", rl.PolicyNames);
        Assert.Equal(TimeSpan.Zero, rl.Delay()); // state penalties are all 0
    }

    [Fact]
    public void IgnoresHeadersWithoutRules()
    {
        var (rl, _) = Make();
        rl.Update(Headers(("x-rate-limit-policy", "x")));
        Assert.Empty(rl.PolicyNames);
    }

    [Fact]
    public async Task GatesOneSlotBelowTheCap()
    {
        // 5:10 = 5 per 10s. We stop at 4 to leave headroom for the browser
        // sharing this IP, so the 5th must wait for the window to roll.
        var (rl, c) = Make();
        rl.Update(Headers(("x-rate-limit-rules", "Ip"),
                          ("x-rate-limit-ip", "5:10:60"),
                          ("x-rate-limit-ip-state", "0:10:0")));
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(TimeSpan.Zero, rl.Delay());
            Assert.True(await rl.AcquireAsync());
        }
        Assert.True(rl.Delay() > TimeSpan.Zero);
        c.Advance(10.1);
        Assert.Equal(TimeSpan.Zero, rl.Delay());
    }

    [Fact]
    public void ServerStateBeatsLocalCount()
    {
        // Our tally can miss requests made by the browser on the same IP.
        var (rl, _) = Make();
        rl.Update(Headers(("x-rate-limit-rules", "Ip"),
                          ("x-rate-limit-ip", "5:10:60"),
                          ("x-rate-limit-ip-state", "4:10:0")));
        Assert.True(rl.Delay() > TimeSpan.Zero);
    }

    [Fact]
    public void HonoursPenaltyInStateHeader()
    {
        var (rl, c) = Make();
        rl.Update(Headers(("x-rate-limit-rules", "Ip"),
                          ("x-rate-limit-ip", "5:10:60"),
                          ("x-rate-limit-ip-state", "5:10:60")));
        Assert.True(rl.Delay() >= TimeSpan.FromSeconds(59));
        c.Advance(61);
        Assert.Equal(TimeSpan.Zero, rl.Delay());
    }

    [Fact]
    public void StaleServerStateAgesOut()
    {
        // Regression: a busy -state reading must not gate us forever. A reported
        // count is only meaningful for the length of its own window.
        var (rl, c) = Make();
        rl.Update(Headers(("x-rate-limit-rules", "Ip"),
                          ("x-rate-limit-ip", "5:10:60"),
                          ("x-rate-limit-ip-state", "5:10:0")));
        Assert.True(rl.Delay() > TimeSpan.Zero);
        c.Advance(10.1);
        Assert.Equal(TimeSpan.Zero, rl.Delay());
    }

    [Fact]
    public void AppliesRetryAfter()
    {
        var (rl, c) = Make();
        rl.Update(Headers(LiveHeaders));
        rl.Penalise(TimeSpan.FromSeconds(120));
        Assert.True(rl.Delay() >= TimeSpan.FromSeconds(119));
        c.Advance(121);
        Assert.Equal(TimeSpan.Zero, rl.Delay());
    }

    [Fact]
    public void RetryAfterWorksWithNoKnownPolicy()
    {
        var (rl, _) = Make();
        rl.Penalise(TimeSpan.FromSeconds(30));
        Assert.True(rl.Delay() >= TimeSpan.FromSeconds(29));
    }

    [Fact]
    public void NullRetryAfterIsNoOp()
    {
        var (rl, _) = Make();
        rl.Penalise(null);
        Assert.Equal(TimeSpan.Zero, rl.Delay());
    }

    [Fact]
    public async Task TightestRuleWins()
    {
        var (rl, _) = Make();
        rl.Update(Headers(("x-rate-limit-rules", "Ip"),
                          ("x-rate-limit-ip", "5:10:60,6:3600:600"),
                          ("x-rate-limit-ip-state", "0:10:0,0:3600:0")));
        for (var i = 0; i < 5; i++) await rl.AcquireAsync();
        Assert.True(rl.Delay() > TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task AcquireRespectsTimeout()
    {
        var (rl, _) = Make();
        rl.Update(Headers(("x-rate-limit-rules", "Ip"),
                          ("x-rate-limit-ip", "1:3600:60"),
                          ("x-rate-limit-ip-state", "1:3600:0")));
        Assert.False(await rl.AcquireAsync(TimeSpan.FromSeconds(0.5)));
    }

    [Fact]
    public void TracksMultipleNamedRuleSets()
    {
        var (rl, _) = Make();
        rl.Update(Headers(("x-rate-limit-rules", "Ip,Account"),
                          ("x-rate-limit-ip", "10:10:60"),
                          ("x-rate-limit-ip-state", "0:10:0"),
                          ("x-rate-limit-account", "2:10:60"),
                          ("x-rate-limit-account-state", "2:10:0")));
        Assert.Equal(2, rl.PolicyNames.Count);
        Assert.True(rl.Delay() > TimeSpan.Zero); // the stricter one gates
    }
}
