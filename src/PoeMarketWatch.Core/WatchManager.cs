using System.Collections.Concurrent;

namespace PoeMarketWatch.Core;

/// <summary>
/// Runs every enabled <see cref="Watch"/> and turns socket pushes into enriched listings.
///
/// Flow: live socket pushes item ids -> fetch details (authenticated, so the response
/// carries the short-lived travel token) -> raise <see cref="Matched"/>.
///
/// Nothing here ever travels or whispers. The manager's job ends at "here is a match";
/// acting on it requires a keypress, which lives in the UI layer. That separation is the
/// ToS boundary made structural rather than a comment.
/// </summary>
public sealed class WatchManager : IAsyncDisposable
{
    private readonly TradeClient _client;
    private readonly Func<CredentialStore.Credentials?> _credentials;
    private readonly string _userAgent;
    private readonly Func<Watch, LiveSearchClient>? _clientFactory;
    private readonly ConcurrentDictionary<string, LiveSearchClient> _live = new();

    public WatchManager(
        TradeClient client,
        string userAgent,
        Func<CredentialStore.Credentials?> credentials,
        Func<Watch, LiveSearchClient>? clientFactory = null)
    {
        _client = client;
        _userAgent = userAgent;
        _credentials = credentials;
        _clientFactory = clientFactory;
    }

    public sealed record Match(Watch Watch, TradeClient.Listing Listing, DateTimeOffset At);

    public event Action<Match>? Matched;
    public event Action<Watch, string>? Status;

    /// <summary>The trade site caps simultaneous live connections; exceeding it drops sockets.</summary>
    public const int MaxConcurrentWatches = 20;

    public int ActiveCount => _live.Count(kv => kv.Value.IsConnected);

    public void Start(IEnumerable<Watch> watches)
    {
        var enabled = watches.Where(w => w.Enabled).ToList();
        if (enabled.Count > MaxConcurrentWatches)
        {
            Status?.Invoke(enabled[0],
                $"{enabled.Count} watches enabled but the trade site allows about " +
                $"{MaxConcurrentWatches} live connections; the excess will keep dropping");
        }

        foreach (var watch in enabled)
        {
            if (_live.ContainsKey(watch.Id)) continue;
            if (string.IsNullOrWhiteSpace(watch.QueryId) || string.IsNullOrWhiteSpace(watch.League))
            {
                Status?.Invoke(watch, "skipped: no league/query id");
                continue;
            }

            var live = _clientFactory?.Invoke(watch)
                       ?? new LiveSearchClient(watch.League, watch.QueryId, _userAgent, _credentials);

            live.ItemsFound += ids => _ = OnItemsAsync(watch, ids);
            live.Connected += () => Status?.Invoke(watch, "connected");
            live.Disconnected += reason => Status?.Invoke(watch, $"disconnected: {reason}");

            _live[watch.Id] = live;
            live.Start();
        }
    }

    private async Task OnItemsAsync(Watch watch, IReadOnlyList<string> ids)
    {
        try
        {
            var listings = await _client.FetchAsync(ids, watch.QueryId).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            foreach (var listing in listings)
                Matched?.Invoke(new Match(watch, listing, now));
        }
        catch (TradeRateLimitException ex)
        {
            Status?.Invoke(watch, $"rate limited, backing off {ex.RetryAfter.TotalSeconds:0}s");
        }
        catch (Exception ex)
        {
            // A fetch failure must not kill the socket -- the next push should still work.
            Status?.Invoke(watch, $"fetch failed: {ex.Message}");
        }
    }

    public async Task StopAsync(string watchId)
    {
        if (_live.TryRemove(watchId, out var live)) await live.DisposeAsync().ConfigureAwait(false);
    }

    public async Task StopAllAsync()
    {
        foreach (var id in _live.Keys.ToList()) await StopAsync(id).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await StopAllAsync().ConfigureAwait(false);
}
