namespace PoeMarketWatch.Core;

/// <summary>
/// Client-side rate limiting for the pathofexile.com trade API.
///
/// GGG publishes their limits on every response and expects clients to obey them.
/// Ignoring this gets your IP throttled and then banned, so this is the gate every
/// request goes through -- not an afterthought.
///
/// Header shape (observed live on POST /api/trade/search/&lt;league&gt;):
/// <code>
///   x-rate-limit-policy   : trade-search-request-limit
///   x-rate-limit-rules    : Ip
///   x-rate-limit-ip       : 5:10:60,15:60:300,30:300:1800,600:21600:3600
///   x-rate-limit-ip-state : 1:10:0,1:60:0,1:300:0,11:21600:0
/// </code>
/// <c>x-rate-limit-rules</c> names the active rules; each name has its own pair of
/// headers (<c>x-rate-limit-{name}</c> / <c>x-rate-limit-{name}-state</c>), lowercased.
/// Each entry is <c>hits:periodSeconds:penaltySeconds</c> -- so <c>5:10:60</c> means at
/// most 5 requests per 10s, and exceeding it locks you out for 60s.
/// </summary>
public sealed class RateLimiter
{
    public readonly record struct Rule(int Hits, int Period, int Penalty);

    private sealed class Policy
    {
        public List<Rule> Rules { get; set; } = new();

        /// <summary>
        /// period -&gt; (server-reported hits, when we observed it).
        /// The timestamp matters: a count is only meaningful for the length of its
        /// own window. Without ageing it out, one busy reading gates us forever.
        /// </summary>
        public Dictionary<int, (int Hits, TimeSpan Seen)> ServerState { get; } = new();

        public TimeSpan BlockedUntil { get; set; } = TimeSpan.Zero;
        public List<TimeSpan> History { get; } = new();
    }

    private readonly Func<TimeSpan> _clock;
    private readonly Func<TimeSpan, Task> _delayAsync;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Policy> _policies = new(StringComparer.OrdinalIgnoreCase);

    public RateLimiter(Func<TimeSpan>? clock = null, Func<TimeSpan, Task>? delayAsync = null)
    {
        _clock = clock ?? (() => TimeSpan.FromTicks(Environment.TickCount64 * TimeSpan.TicksPerMillisecond));
        _delayAsync = delayAsync ?? (d => Task.Delay(d));
    }

    public IReadOnlyCollection<string> PolicyNames
    {
        get { lock (_gate) return _policies.Keys.ToArray(); }
    }

    /// <summary>Parse <c>5:10:60,15:60:300</c>. Tolerates blanks and junk entries.</summary>
    public static List<Rule> ParseRules(string? header)
    {
        var result = new List<Rule>();
        if (string.IsNullOrWhiteSpace(header)) return result;
        foreach (var raw in header.Split(','))
        {
            var part = raw.Trim();
            if (part.Length == 0) continue;
            var bits = part.Split(':');
            if (bits.Length != 3) continue;
            if (int.TryParse(bits[0], out var hits)
                && int.TryParse(bits[1], out var period)
                && int.TryParse(bits[2], out var penalty))
            {
                result.Add(new Rule(hits, period, penalty));
            }
        }
        return result;
    }

    /// <summary>Learn limits from a response. Header lookup must be case-insensitive.</summary>
    public void Update(Func<string, string?> header)
    {
        var rulesHeader = header("x-rate-limit-rules");
        if (string.IsNullOrWhiteSpace(rulesHeader)) return;

        var now = _clock();
        lock (_gate)
        {
            foreach (var rawName in rulesHeader.Split(','))
            {
                var name = rawName.Trim();
                if (name.Length == 0) continue;
                var key = name.ToLowerInvariant();

                var rules = ParseRules(header($"x-rate-limit-{key}"));
                var state = ParseRules(header($"x-rate-limit-{key}-state"));

                if (!_policies.TryGetValue(key, out var policy))
                    _policies[key] = policy = new Policy();

                if (rules.Count > 0) policy.Rules = rules;

                foreach (var st in state)
                {
                    // state entry is hits:period:activePenalty
                    policy.ServerState[st.Period] = (st.Hits, now);
                    if (st.Penalty > 0)
                    {
                        var until = now + TimeSpan.FromSeconds(st.Penalty);
                        if (until > policy.BlockedUntil) policy.BlockedUntil = until;
                    }
                }
            }
        }
    }

    public void Update(System.Net.Http.Headers.HttpResponseHeaders headers) =>
        Update(name => headers.TryGetValues(name, out var v) ? string.Join(",", v) : null);

    /// <summary>Apply a 429's Retry-After to every known policy.</summary>
    public void Penalise(TimeSpan? retryAfter)
    {
        if (retryAfter is not { } wait || wait <= TimeSpan.Zero) return;
        var now = _clock();
        lock (_gate)
        {
            if (_policies.Count == 0) _policies["_global"] = new Policy();
            foreach (var policy in _policies.Values)
            {
                var until = now + wait;
                if (until > policy.BlockedUntil) policy.BlockedUntil = until;
            }
        }
    }

    /// <summary>Seconds to wait before a request would be safe. Zero = go now.</summary>
    public TimeSpan Delay()
    {
        var now = _clock();
        lock (_gate)
        {
            var worst = TimeSpan.Zero;
            foreach (var policy in _policies.Values)
            {
                var d = DelayFor(policy, now);
                if (d > worst) worst = d;
            }
            return worst;
        }
    }

    private static TimeSpan DelayFor(Policy policy, TimeSpan now)
    {
        if (now < policy.BlockedUntil) return policy.BlockedUntil - now;
        if (policy.Rules.Count == 0) return TimeSpan.Zero;

        // drop history older than the longest window we care about
        var horizon = now - TimeSpan.FromSeconds(policy.Rules.Max(r => r.Period));
        policy.History.RemoveAll(t => t < horizon);

        var worst = TimeSpan.Zero;
        foreach (var rule in policy.Rules)
        {
            if (rule.Hits <= 0) continue;
            var windowStart = now - TimeSpan.FromSeconds(rule.Period);
            var local = policy.History.Count(t => t >= windowStart);

            var reported = 0;
            if (policy.ServerState.TryGetValue(rule.Period, out var st)
                && (now - st.Seen) < TimeSpan.FromSeconds(rule.Period))
            {
                reported = st.Hits; // older than its own window would be meaningless
            }

            var used = Math.Max(local, reported);

            // Stay one slot below the cap: this bucket is shared with anything else
            // on the same IP (notably your browser on the trade site), so sitting
            // exactly at the edge invites a 429 triggered by someone else's request.
            if (used < rule.Hits - 1) continue;

            var inWindow = policy.History.Where(t => t >= windowStart).ToList();
            var wait = inWindow.Count > 0
                ? inWindow.Min() + TimeSpan.FromSeconds(rule.Period) - now
                : TimeSpan.FromSeconds(rule.Period); // server says full, we have no history
            if (wait > worst) worst = wait;
        }
        return worst > TimeSpan.Zero ? worst : TimeSpan.Zero;
    }

    /// <summary>Block until a request is safe. False if <paramref name="timeout"/> elapsed first.</summary>
    public async Task<bool> AcquireAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        TimeSpan? deadline = timeout is { } t ? _clock() + t : null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var wait = Delay();
            if (wait <= TimeSpan.Zero)
            {
                lock (_gate)
                {
                    var now = _clock();
                    foreach (var policy in _policies.Values) policy.History.Add(now);
                }
                return true;
            }
            if (deadline is { } d && _clock() + wait > d) return false;
            await _delayAsync(wait < TimeSpan.FromSeconds(1) ? wait : TimeSpan.FromSeconds(1))
                .ConfigureAwait(false);
        }
    }
}
