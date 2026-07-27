using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace PoeMarketWatch.Core;

/// <summary>Abstraction over ClientWebSocket so reconnect logic is testable offline.</summary>
public interface IWebSocketTransport : IDisposable
{
    WebSocketState State { get; }
    Task ConnectAsync(Uri uri, string cookieHeader, string userAgent, CancellationToken ct);
    Task<string?> ReceiveAsync(CancellationToken ct);
    Task CloseAsync(CancellationToken ct);
}

/// <summary>
/// One live search: a socket that pushes item ids the moment they are listed.
///
/// This is the whole point of the app. Polling cannot compete -- the search endpoint
/// allows only 600 requests per 6 hours per IP, roughly one every 36 seconds across all
/// your searches, whereas the socket pushes within milliseconds of indexing.
///
/// The socket requires authentication (401 without cookies) even though search does not.
/// Note also the trade site caps you at ~20 simultaneous live connections.
///
/// Reconnect matters more than it looks: a dropped socket does not error, it simply
/// stops delivering, and a live search that silently died looks identical to a quiet
/// market. Hence the explicit state machine and <see cref="Disconnected"/> signal.
/// </summary>
public sealed class LiveSearchClient : IAsyncDisposable
{
    private readonly string _league;
    private readonly string _queryId;
    private readonly string _userAgent;
    private readonly Func<CredentialStore.Credentials?> _credentials;
    private readonly Func<IWebSocketTransport> _transportFactory;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    private CancellationTokenSource? _cts;
    private Task? _pump;

    public LiveSearchClient(
        string league,
        string queryId,
        string userAgent,
        Func<CredentialStore.Credentials?> credentials,
        Func<IWebSocketTransport>? transportFactory = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _league = league;
        _queryId = queryId;
        _userAgent = userAgent;
        _credentials = credentials;
        _transportFactory = transportFactory ?? (() => new ClientWebSocketTransport());
        _delay = delay ?? Task.Delay;
    }

    public string QueryId => _queryId;
    public string League => _league;
    public bool IsConnected { get; private set; }
    public int ReconnectCount { get; private set; }
    public DateTimeOffset? LastMessageAt { get; private set; }

    /// <summary>Item ids that just appeared. Fetch them to get details + tokens.</summary>
    public event Action<IReadOnlyList<string>>? ItemsFound;
    public event Action? Connected;
    public event Action<string>? Disconnected;

    public Uri Uri => new($"wss://www.pathofexile.com/api/trade/live/{Uri.EscapeDataString(_league)}/{Uri.EscapeDataString(_queryId)}");

    public void Start()
    {
        if (_pump is not null) throw new InvalidOperationException("already started");
        _cts = new CancellationTokenSource();
        _pump = Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_pump is not null)
        {
            try { await _pump.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
        }
        _pump = null;
        IsConnected = false;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        // Backoff must be bounded but never give up: the socket dying is the normal
        // case (network blips, GGG restarts), and a search that quietly stops is worse
        // than one that reconnects noisily.
        var backoff = TimeSpan.FromSeconds(1);
        var maxBackoff = TimeSpan.FromMinutes(2);

        while (!ct.IsCancellationRequested)
        {
            IWebSocketTransport? socket = null;
            try
            {
                var creds = _credentials()
                    ?? throw new TradeAuthException(
                        "live search needs POESESSID -- the socket returns 401 without it");

                socket = _transportFactory();
                await socket.ConnectAsync(Uri, CredentialStore.ToCookieHeader(creds), _userAgent, ct)
                            .ConfigureAwait(false);

                IsConnected = true;
                backoff = TimeSpan.FromSeconds(1); // reset only after a real connection
                Connected?.Invoke();

                while (!ct.IsCancellationRequested)
                {
                    var message = await socket.ReceiveAsync(ct).ConfigureAwait(false);
                    if (message is null) break; // clean close
                    LastMessageAt = DateTimeOffset.UtcNow;
                    var ids = ParseNewIds(message);
                    if (ids.Count > 0) ItemsFound?.Invoke(ids);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Disconnected?.Invoke(ex.Message);
            }
            finally
            {
                IsConnected = false;
                socket?.Dispose();
            }

            if (ct.IsCancellationRequested) break;
            ReconnectCount++;
            try { await _delay(backoff, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            backoff = backoff * 2 > maxBackoff ? maxBackoff : backoff * 2;
        }
        IsConnected = false;
    }

    /// <summary>
    /// Live messages look like <c>{"new":["hash1","hash2"]}</c>. Auth failures arrive as
    /// <c>{"auth":false}</c>, and there are heartbeat frames we simply ignore.
    /// </summary>
    public static IReadOnlyList<string> ParseNewIds(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return Array.Empty<string>();
        try
        {
            using var doc = JsonDocument.Parse(message);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return Array.Empty<string>();
            if (!doc.RootElement.TryGetProperty("new", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();
            var ids = new List<string>();
            foreach (var e in arr.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String && e.GetString() is { } s && s.Length > 0)
                    ids.Add(s);
            return ids;
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts?.Dispose();
    }
}

internal sealed class ClientWebSocketTransport : IWebSocketTransport
{
    private readonly ClientWebSocket _socket = new();
    private readonly byte[] _buffer = new byte[64 * 1024];

    public WebSocketState State => _socket.State;

    public async Task ConnectAsync(Uri uri, string cookieHeader, string userAgent, CancellationToken ct)
    {
        // Same trap as HttpClient: if the socket has its own cookie container it can
        // clobber a manually set Cookie header. Leave Cookies null and set the header.
        _socket.Options.Cookies = null;
        _socket.Options.SetRequestHeader("Cookie", cookieHeader);
        _socket.Options.SetRequestHeader("User-Agent", userAgent);
        _socket.Options.SetRequestHeader("Origin", TradeClient.BaseUrl);
        await _socket.ConnectAsync(uri, ct).ConfigureAwait(false);
    }

    public async Task<string?> ReceiveAsync(CancellationToken ct)
    {
        var builder = new StringBuilder();
        while (true)
        {
            var result = await _socket.ReceiveAsync(new ArraySegment<byte>(_buffer), ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            builder.Append(Encoding.UTF8.GetString(_buffer, 0, result.Count));
            if (result.EndOfMessage) return builder.ToString();
        }
    }

    public async Task CloseAsync(CancellationToken ct)
    {
        if (_socket.State == WebSocketState.Open)
            await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct).ConfigureAwait(false);
    }

    public void Dispose() => _socket.Dispose();
}
