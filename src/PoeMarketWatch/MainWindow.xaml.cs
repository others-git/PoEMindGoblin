using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Media;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PoeMarketWatch.Core;

namespace PoeMarketWatch;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly CredentialStore _credentials = new();
    private readonly ObservableCollection<WatchRow> _watches = new();
    private readonly ObservableCollection<MatchRow> _matches = new();
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };

    private TradeClient? _client;
    private WatchManager? _manager;
    private HotkeyService? _hotkeys;
    private int? _hotkeyId;
    private bool _running;

    public MainWindow()
    {
        InitializeComponent();
        _settings = AppSettings.Load();

        foreach (var w in _settings.Watches) _watches.Add(new WatchRow(w));
        WatchList.ItemsSource = _watches;
        MatchList.ItemsSource = _matches;

        // The gem tool is independent of the live-search machinery: no credentials, no
        // sockets, just public price data. It shares only AppSettings.
        GemTabHost.Content = new GemRoiView(_settings);

        // The Voyage planner touches neither credentials nor the network: it reads the
        // screen and the clipboard, and solves locally.
        VoyageTabHost.Content = new VoyageView();

        _tick.Tick += (_, _) => RefreshTransient();
        _tick.Start();

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _hotkeys = new HotkeyService(this);
        RegisterTravelHotkey();
        UpdateChrome();
    }

    private void RegisterTravelHotkey()
    {
        if (_hotkeyId is { } old) _hotkeys!.Unregister(old);

        // Fires while Path of Exile has focus -- that is the entire point of using a
        // system-wide hotkey rather than a WPF input binding.
        _hotkeyId = _hotkeys!.Register(Key.D, HotkeyService.Mod.Control | HotkeyService.Mod.Alt,
                                       TravelToLatest);

        HotkeyHint.Text = _hotkeyId is null
            ? "Ctrl+Alt+D is taken by another app - travel by clicking instead"
            : "Ctrl+Alt+D travels to the newest match";
    }

    // ------------------------------------------------------------------ watches
    private void OnAddWatch(object sender, RoutedEventArgs e)
    {
        var raw = UrlBox.Text?.Trim() ?? "";
        if (!TradeUrlParser.TryParse(raw, out var league, out var queryId))
        {
            AddHint.Text = "That is not a trade search URL. Copy it from the browser address bar.";
            AddHint.Foreground = new SolidColorBrush(Color.FromRgb(0xC4, 0x57, 0x4B));
            return;
        }

        var name = string.IsNullOrWhiteSpace(NameBox.Text) ? $"{league} search" : NameBox.Text.Trim();
        var watch = new Watch { Name = name, League = league, QueryId = queryId };
        _settings.Watches.Add(watch);
        _settings.DefaultLeague = league;
        _settings.Save();

        _watches.Add(new WatchRow(watch));
        UrlBox.Clear();
        NameBox.Clear();
        AddHint.Text = "Added. Press Start watching to connect.";
        AddHint.Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x7E, 0x6C));

        if (_running) _manager?.Start(new[] { watch });
    }

    private async void OnRemoveWatch(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not WatchRow row) return;
        if (_manager is not null) await _manager.StopAsync(row.Model.Id);
        _watches.Remove(row);
        _settings.Watches.RemoveAll(w => w.Id == row.Model.Id);
        _settings.Save();
    }

    // ------------------------------------------------------------------ running
    private async void OnStartStop(object sender, RoutedEventArgs e)
    {
        if (_running) { await StopAsync(); return; }

        if (_credentials.Load() is null)
        {
            Status("Session required: live search returns 401 without POESESSID.");
            OnCredentials(sender, e);
            return;
        }
        if (!_settings.Watches.Any(w => w.Enabled))
        {
            Status("No enabled watches. Paste a trade search URL first.");
            return;
        }

        _client = new TradeClient(_settings.UserAgent, credentials: () => _credentials.Load());
        _manager = new WatchManager(_client, _settings.UserAgent, () => _credentials.Load());
        _manager.Matched += OnMatched;
        _manager.Status += OnWatchStatus;
        _manager.Start(_settings.Watches);

        _running = true;
        UpdateChrome();
        Status("Watching. Matches appear the moment they are listed.");
    }

    private async Task StopAsync()
    {
        if (_manager is not null)
        {
            _manager.Matched -= OnMatched;
            _manager.Status -= OnWatchStatus;
            await _manager.DisposeAsync();
            _manager = null;
        }
        _client?.Dispose();
        _client = null;
        _running = false;
        foreach (var w in _watches) w.StatusText = "stopped";
        UpdateChrome();
        Status("Stopped.");
    }

    private void OnWatchStatus(Watch watch, string status) => Dispatcher.Invoke(() =>
    {
        foreach (var row in _watches.Where(r => r.Model.Id == watch.Id)) row.StatusText = status;
        UpdateChrome();
    });

    private void OnMatched(WatchManager.Match match) => Dispatcher.Invoke(() =>
    {
        var row = new MatchRow(match);
        _matches.Insert(0, row);
        while (_matches.Count > 200) _matches.RemoveAt(_matches.Count - 1);

        if (_settings.PlaySound && match.Watch.Notify) SystemSounds.Exclamation.Play();
        Status($"Match: {row.ItemName} ({row.PriceText})");
    });

    // ------------------------------------------------------------------- travel
    /// <summary>
    /// Bound to the hotkey and the Travel button. One press, one call -- deliberately
    /// never invoked from a match arriving, which would be unattended automation.
    /// </summary>
    private void TravelToLatest()
    {
        var target = _matches.FirstOrDefault(m => m.CanTravel);
        if (target is null) { Status("Nothing to travel to."); return; }
        _ = TravelAsync(target);
    }

    private void OnTravel(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is MatchRow row) _ = TravelAsync(row);
    }

    private async Task TravelAsync(MatchRow row)
    {
        var token = row.Match.Listing.HideoutToken?.Token;
        if (token is null || _client is null) { Status("No travel token for that listing."); return; }

        try
        {
            await _client.ActivateTokenAsync(token);
            Status($"Travelling to {row.SellerText}.");
        }
        catch (TradeApiException ex)
        {
            // Expiry is the common case: tokens live ~300s and cannot be pre-fetched.
            Status($"Travel failed: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------- chrome
    private void OnCredentials(object sender, RoutedEventArgs e)
    {
        var dialog = new CredentialWindow(_credentials, _settings) { Owner = this };
        if (dialog.ShowDialog() == true) Status("Session saved, encrypted with DPAPI.");
        UpdateChrome();
    }

    private void OnClearMatches(object sender, RoutedEventArgs e) => _matches.Clear();

    /// <summary>
    /// Tests each layer separately, because every failure mode looks identical from the
    /// outside: dead socket, expired session, stale query id and a quiet market all
    /// present as "nothing happens".
    /// </summary>
    private async void OnDiagnose(object sender, RoutedEventArgs e)
    {
        var watch = (WatchList.SelectedItem as WatchRow)?.Model
                    ?? _settings.Watches.FirstOrDefault(w => w.Enabled)
                    ?? _settings.Watches.FirstOrDefault();
        if (watch is null) { Status("Add a watch first, then Diagnose."); return; }

        DiagBtn.IsEnabled = false;
        Status($"Diagnosing '{watch.Name}'...");
        try
        {
            var test = new ConnectionTest(_settings.UserAgent, () => _credentials.Load());
            var steps = await test.RunAsync(watch.League, watch.QueryId);

            var report = string.Join("\n", steps.Select(s =>
                $"{(s.Result == ConnectionTest.Result.Pass ? "PASS" : s.Result == ConnectionTest.Result.Fail ? "FAIL" : "----")}  {s.Name}\n        {s.Detail}"));

            var failure = steps.FirstOrDefault(s => s.Result == ConnectionTest.Result.Fail);
            Status(failure is null ? "All checks passed." : $"{failure.Name}: {failure.Detail}");

            MessageBox.Show(this,
                $"Watch: {watch.Name}\nLeague: {watch.League}\nQuery id: {watch.QueryId}\n\n{report}",
                "Connection diagnosis", MessageBoxButton.OK,
                failure is null ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            Status($"Diagnosis failed: {ex.Message}");
        }
        finally
        {
            DiagBtn.IsEnabled = true;
        }
    }

    private void RefreshTransient()
    {
        foreach (var m in _matches) m.Refresh();
        if (_client is not null)
        {
            var delay = _client.Limiter.Delay();
            RateText.Text = delay > TimeSpan.Zero
                ? $"rate limited - {delay.TotalSeconds:0}s"
                : "rate limit ok";
        }
        if (_running) UpdateChrome();
    }

    private void UpdateChrome()
    {
        StartBtn.Content = _running ? "Stop" : "Start watching";
        var active = _manager?.ActiveCount ?? 0;
        var connected = _running && active > 0;
        ConnDot.Fill = new SolidColorBrush(connected
            ? Color.FromRgb(0x7F, 0xB0, 0x69)
            : Color.FromRgb(0xC4, 0x57, 0x4B));
        ConnText.Text = !_running ? "not connected"
            : connected ? $"{active} live" : "connecting";
        CredsBtn.Content = _credentials.Exists ? "Session ok" : "Session...";
    }

    private void Status(string text) => StatusText.Text = text;

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        _tick.Stop();
        _hotkeys?.Dispose();
        if (_running) await StopAsync();
        _settings.Save();
    }
}

// ----------------------------------------------------------------------- rows
public sealed class WatchRow : INotifyPropertyChanged
{
    public WatchRow(Watch model) => Model = model;
    public Watch Model { get; }

    public string Name => Model.Name;

    public bool Enabled
    {
        get => Model.Enabled;
        set { Model.Enabled = value; OnChanged(); }
    }

    private string _status = "idle";
    public string StatusText
    {
        get => _status;
        set { _status = value; OnChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public sealed class MatchRow : INotifyPropertyChanged
{
    public MatchRow(WatchManager.Match match) => Match = match;
    public WatchManager.Match Match { get; }

    public string ItemName => Match.Listing.ItemName ?? "(unnamed item)";
    public string PriceText => Match.Listing.PriceText ?? "no price";
    public string FeeText => Match.Listing.GoldFee is { } f ? $"{f:N0} gold fee" : "";
    public string WatchName => Match.Watch.Name;
    public string SellerText =>
        (Match.Listing.AccountName ?? "unknown") + (Match.Listing.SellerOnline ? "" : " (offline)");

    /// <summary>Tokens live ~300s; past that the listing must be refetched.</summary>
    public double? TokenSecondsLeft => Match.Listing.HideoutToken is { } t
        ? TokenScanner.SecondsUntilExpiry(t.Token, DateTimeOffset.UtcNow)
        : null;

    public bool CanTravel => Match.Listing.HideoutToken is not null && (TokenSecondsLeft ?? -1) > 0;

    public string TokenText => Match.Listing.HideoutToken is null
        ? "no token"
        : TokenSecondsLeft is { } s and > 0 ? $"{s:0}s" : "expired";

    public string AgeText
    {
        get
        {
            var age = DateTimeOffset.UtcNow - Match.At;
            return age.TotalSeconds < 60 ? $"{age.TotalSeconds:0}s ago" : $"{age.TotalMinutes:0}m ago";
        }
    }

    public void Refresh()
    {
        OnChanged(nameof(AgeText));
        OnChanged(nameof(TokenText));
        OnChanged(nameof(CanTravel));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
