using System.Net.WebSockets;
using PoeMarketWatch.Core;

namespace PoeMarketWatch.Tests;

public class LiveSearchClientTests
{
    private const string Ua = "poe-market-watch/test (contact: test@example.com)";

    /// Scripted transport: each connection replays a queue of messages, then ends
    /// (null = clean close, exception instance = throw).
    private sealed class FakeTransport : IWebSocketTransport
    {
        private readonly Queue<object?> _script;
        public FakeTransport(Queue<object?> script) => _script = script;

        public WebSocketState State { get; private set; } = WebSocketState.None;
        public Uri? ConnectedTo { get; private set; }
        public string? CookieHeader { get; private set; }
        public string? UserAgent { get; private set; }

        public Task ConnectAsync(Uri uri, string cookieHeader, string userAgent, CancellationToken ct)
        {
            ConnectedTo = uri; CookieHeader = cookieHeader; UserAgent = userAgent;
            State = WebSocketState.Open;
            return Task.CompletedTask;
        }

        public async Task<string?> ReceiveAsync(CancellationToken ct)
        {
            if (_script.Count == 0)
            {
                // park forever until cancelled, like a live-but-quiet socket
                await Task.Delay(Timeout.Infinite, ct);
                return null;
            }
            var next = _script.Dequeue();
            if (next is Exception ex) throw ex;
            return (string?)next;
        }

        public Task CloseAsync(CancellationToken ct) => Task.CompletedTask;
        public void Dispose() => State = WebSocketState.Closed;
    }

    private static Func<CredentialStore.Credentials?> Creds(bool present = true) =>
        present ? () => new CredentialStore.Credentials("sess", "tok") : () => null;

    // ------------------------------------------------------------------ parsing
    [Fact]
    public void ParsesNewItemIds()
    {
        var ids = LiveSearchClient.ParseNewIds("""{"new":["abc","def"]}""");
        Assert.Equal(new[] { "abc", "def" }, ids);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"auth":false}""")]      // auth failure frame
    [InlineData("""{"new":[]}""")]           // heartbeat-ish
    [InlineData("""{"other":["x"]}""")]
    public void IgnoresNonItemFrames(string frame)
    {
        Assert.Empty(LiveSearchClient.ParseNewIds(frame));
    }

    [Fact]
    public void SkipsEmptyIdEntries()
    {
        Assert.Equal(new[] { "a" }, LiveSearchClient.ParseNewIds("""{"new":["a","",null,5]}"""));
    }

    // ------------------------------------------------------------------- uri
    [Fact]
    public void BuildsCorrectSocketUri()
    {
        var c = new LiveSearchClient("Allflame", "aL5X0Qaaue", Ua, Creds());
        Assert.Equal("wss://www.pathofexile.com/api/trade/live/Allflame/aL5X0Qaaue", c.Uri.ToString());
    }

    // --------------------------------------------------------------- delivery
    [Fact]
    public async Task DeliversItemsAndSendsCredentials()
    {
        var script = new Queue<object?>([ """{"new":["item1","item2"]}""" ]);
        FakeTransport? transport = null;
        var received = new List<string>();
        var gotItems = new TaskCompletionSource();

        var client = new LiveSearchClient("Allflame", "q1", Ua, Creds(),
            () => transport = new FakeTransport(script));
        client.ItemsFound += ids => { received.AddRange(ids); gotItems.TrySetResult(); };

        client.Start();
        await gotItems.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await client.StopAsync();

        Assert.Equal(new[] { "item1", "item2" }, received);
        Assert.Contains("POESESSID=sess", transport!.CookieHeader);
        Assert.Equal(Ua, transport.UserAgent);
    }

    [Fact]
    public async Task RaisesConnectedEvent()
    {
        var connected = new TaskCompletionSource();
        var client = new LiveSearchClient("Allflame", "q1", Ua, Creds(),
            () => new FakeTransport(new Queue<object?>()));
        client.Connected += () => connected.TrySetResult();

        client.Start();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(client.IsConnected);
        await client.StopAsync();
        Assert.False(client.IsConnected);
    }

    // -------------------------------------------------------------- reconnect
    [Fact]
    public async Task ReconnectsAfterDropAndKeepsDelivering()
    {
        // A dropped socket does not error visibly -- it just stops delivering. This is
        // the failure this whole class exists to survive.
        var first = new Queue<object?>([ """{"new":["before"]}""", null ]);   // clean close
        var second = new Queue<object?>([ """{"new":["after"]}""" ]);
        var queues = new Queue<Queue<object?>>([first, second]);

        var received = new List<string>();
        var gotAfter = new TaskCompletionSource();

        var client = new LiveSearchClient("Allflame", "q1", Ua, Creds(),
            () => new FakeTransport(queues.Count > 0 ? queues.Dequeue() : new Queue<object?>()),
            delay: (_, _) => Task.CompletedTask);   // no real backoff wait in tests
        client.ItemsFound += ids =>
        {
            received.AddRange(ids);
            if (ids.Contains("after")) gotAfter.TrySetResult();
        };

        client.Start();
        await gotAfter.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await client.StopAsync();

        Assert.Contains("before", received);
        Assert.Contains("after", received);
        Assert.True(client.ReconnectCount >= 1);
    }

    [Fact]
    public async Task SurvivesThrowingSocketAndReportsWhy()
    {
        var first = new Queue<object?>([ new IOException("connection reset") ]);
        var second = new Queue<object?>([ """{"new":["recovered"]}""" ]);
        var queues = new Queue<Queue<object?>>([first, second]);

        var reasons = new List<string>();
        var recovered = new TaskCompletionSource();

        var client = new LiveSearchClient("Allflame", "q1", Ua, Creds(),
            () => new FakeTransport(queues.Count > 0 ? queues.Dequeue() : new Queue<object?>()),
            delay: (_, _) => Task.CompletedTask);
        client.Disconnected += r => reasons.Add(r);
        client.ItemsFound += _ => recovered.TrySetResult();

        client.Start();
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await client.StopAsync();

        Assert.Contains(reasons, r => r.Contains("connection reset"));
    }

    [Fact]
    public async Task MissingCredentialsReportedRatherThanSilent()
    {
        // Silent failure here is the worst outcome: a dead search looks like a quiet one.
        var reported = new TaskCompletionSource<string>();
        var client = new LiveSearchClient("Allflame", "q1", Ua, Creds(present: false),
            () => new FakeTransport(new Queue<object?>()),
            delay: (_, _) => Task.CompletedTask);
        client.Disconnected += r => reported.TrySetResult(r);

        client.Start();
        var reason = await reported.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await client.StopAsync();

        Assert.Contains("POESESSID", reason);
    }

    [Fact]
    public async Task StopIsIdempotentAndDisposable()
    {
        var client = new LiveSearchClient("Allflame", "q1", Ua, Creds(),
            () => new FakeTransport(new Queue<object?>()));
        client.Start();
        await client.StopAsync();
        await client.StopAsync();
        await client.DisposeAsync();
        Assert.False(client.IsConnected);
    }

    [Fact]
    public void DoubleStartIsRejected()
    {
        var client = new LiveSearchClient("Allflame", "q1", Ua, Creds(),
            () => new FakeTransport(new Queue<object?>()));
        client.Start();
        Assert.Throws<InvalidOperationException>(() => client.Start());
    }
}
