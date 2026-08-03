using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using MindGoblin.Core;

namespace MindGoblin;

/// <summary>
/// Gem levelling RoI.
///
/// Prices come from poe.watch rather than poe.ninja, which is Cloudflare-gated for
/// non-browser clients. Both sides of the trade use the MEAN price: using min-to-buy and
/// mean-to-sell would flatter every row, and the point of the tool is to be trusted.
///
/// Only vendor-buyable gems appear. Vaal, Awakened, exceptional and transfigured gems are
/// drop-only, so a "profit" computed from a level-1 entry price they do not have is
/// fiction -- see GemCatalog.
/// </summary>
public partial class GemRoiView : UserControl
{
    private readonly AppSettings _settings;
    private readonly ObservableCollection<Row> _rows = new();
    private GemCatalog? _catalog;
    private IReadOnlyDictionary<string, GemRoi.GemPrices>? _ladders;
    private bool _loading;

    /// <summary>
    /// Identifies the app to poe.watch. A courtesy, and the thing they would use to ask
    /// us to stop if we ever misbehaved, so it names the project rather than pretending
    /// to be a browser.
    /// </summary>
    private const string UserAgent = "MindGoblin/0.1 (Path of Exile toolkit)";

    public GemRoiView() : this(new AppSettings()) { }

    public GemRoiView(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        Grid.ItemsSource = _rows;

        LeagueBox.Text = string.IsNullOrWhiteSpace(settings.DefaultLeague)
            ? "Allflame" : settings.DefaultLeague;
        GcpBox.Text = settings.GemcutterChaos.ToString("0.##");
        VaalBox.Text = settings.VaalOrbChaos.ToString("0.##");

        _catalog = GemCatalog.LoadDefault();
        if (_catalog is null)
            Status("gem-index.json is missing, so vendor filtering is unavailable.");
    }

    // ------------------------------------------------------------------ refresh
    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var league = LeagueBox.Text.Trim();
        if (league.Length == 0) { Status("Enter a league."); return; }

        _loading = true;
        RefreshBtn.IsEnabled = false;
        Status($"Fetching {league} prices from poe.watch...");
        try
        {
            using var client = new PoeWatchClient(UserAgent);
            var gems = await client.GetAsync("gem", league);
            var currency = await client.GetAsync("currency", league);

            var gcp = PoeWatchClient.CurrencyChaos(currency, "Gemcutter's Prism");
            var vaal = PoeWatchClient.CurrencyChaos(currency, "Vaal Orb");
            if (gcp is { } g) GcpBox.Text = g.ToString("0.##");
            if (vaal is { } v) VaalBox.Text = v.ToString("0.##");
            PriceNote.Text = "currency prices from poe.watch; edit to override";

            _ladders = PoeWatchClient.ToLadders(gems, MinVolume());

            Status($"{gems.Count} gem rows, {_ladders.Count} gems with usable prices.");
            // Recalculate persists the league and the two fetched prices along with it.
            Recalculate();
        }
        catch (Exception ex)
        {
            Status($"Fetch failed: {ex.Message}");
        }
        finally
        {
            _loading = false;
            RefreshBtn.IsEnabled = true;
        }
    }

    private void OnRecalculate(object sender, RoutedEventArgs e) => Recalculate();
    private void OnFilterChanged(object sender, RoutedEventArgs e) => Recalculate();

    private void Recalculate()
    {
        _rows.Clear();

        // Unparsable text keeps the stored price rather than replacing it with a
        // placeholder: a box mid-edit is not an instruction to forget the override.
        var costs = new GemRoi.Costs(ParseDouble(GcpBox.Text, _settings.GemcutterChaos),
                                     ParseDouble(VaalBox.Text, _settings.VaalOrbChaos));
        if (_ladders is null)
        {
            Status("No price data yet - press Refresh prices.");
            Persist(costs);
            return;
        }

        var roi = new GemRoi(_settings.Corruption());
        var paths = SelectedPaths();

        var skippedNonVendor = 0;
        var incomplete = 0;
        var results = new List<Row>();

        foreach (var (name, prices) in _ladders)
        {
            // Unknown gems are excluded too: the PoB-derived catalogue lags the live
            // game, and assuming an unknown gem is buyable invents an entry price.
            if (_catalog is not null && !_catalog.IsVendorBuyable(name)) { skippedNonVendor++; continue; }

            var maxLevel = _catalog?.Find(name)?.MaxLevel ?? 20;
            foreach (var r in roi.EvaluateAll(prices, costs, paths, maxLevel))
            {
                if (!r.IsComplete) { incomplete++; continue; }
                results.Add(new Row(r));
            }
        }

        foreach (var row in results.OrderByDescending(r => r.Result.Profit))
            _rows.Add(row);

        var profitable = results.Count(r => r.Result.Profit > 0);
        Status($"{_rows.Count} rows ({profitable} profitable) · {skippedNonVendor} non-vendor gems "
             + $"skipped · {incomplete} rows dropped for missing prices");
        Persist(costs);
    }

    private GemRoi.Path[] SelectedPaths()
    {
        // The toggles are strategy, not display filters: they change what you DO, and
        // therefore which paths are even on the table.
        var noQuality = IgnoreQuality.IsChecked == true;
        var noCorrupt = IgnoreCorruption.IsChecked == true;

        return (noQuality, noCorrupt) switch
        {
            (true, true) => [GemRoi.Path.LevelOnly],
            (true, false) => [GemRoi.Path.LevelOnly, GemRoi.Path.VaalOnly],
            (false, true) => [GemRoi.Path.LevelOnly, GemRoi.Path.LevelAndQuality],
            (false, false) => Enum.GetValues<GemRoi.Path>(),
        };
    }

    private void OnOdds(object sender, RoutedEventArgs e)
    {
        var dialog = new CorruptionOddsWindow(_settings) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true) Recalculate();
    }

    /// <summary>
    /// Remember what the calculation was actually run with.
    ///
    /// The two currency prices are OVERRIDES -- the fetch fills them in and the user
    /// corrects them -- so a correction that does not survive a restart silently re-costs
    /// every row at the shipped default. They are stored with the league, which was
    /// already persisted, and from the same values the rows were computed from.
    ///
    /// Runs last, because a write that fails has to be able to say so without the row
    /// count writing over it.
    /// </summary>
    private void Persist(GemRoi.Costs costs)
    {
        var league = LeagueBox.Text.Trim();
        if (league.Length > 0) _settings.DefaultLeague = league;
        _settings.GemcutterChaos = costs.GemcutterChaos;
        _settings.VaalOrbChaos = costs.VaalOrbChaos;

        try
        {
            _settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Every filter toggle recalculates, so this runs often: a locked settings
            // file must cost the override, not the app.
            Status($"Settings not saved: {ex.Message}");
        }
    }

    private int MinVolume() => int.TryParse(MinVolumeBox.Text, out var n) && n >= 0 ? n : 0;

    private static double ParseDouble(string text, double fallback) =>
        double.TryParse(text, out var d) && d >= 0 ? d : fallback;

    private void Status(string text) => StatusText.Text = text;

    // --------------------------------------------------------------------- row
    public sealed class Row
    {
        public Row(GemRoi.Result result) => Result = result;
        public GemRoi.Result Result { get; }

        public string Gem => Result.Gem;

        public string PathName => Result.Path switch
        {
            GemRoi.Path.LevelOnly => "level → 20/0",
            GemRoi.Path.LevelAndQuality => "level + GCP → 20/20",
            GemRoi.Path.VaalOnly => "vaal 20/20 → 21/20",
            GemRoi.Path.FullChain => "full chain → 21/20",
            _ => Result.Path.ToString(),
        };

        // Text for display; the *Value properties are what the grid sorts on. See the
        // remarks on GemRoi.Result -- sorting formatted text puts "99%" above "970%".
        public string BuyText => Result.BuyText;
        public string CurrencyText => Result.CurrencyText;
        public string RevenueText => Result.RevenueText;
        public string ProfitText => Result.ProfitText;
        public string RoiText => Result.RoiText;

        public double BuyValue => Result.BuyCost;
        public double CurrencyValue => Result.CurrencyCost;
        public double RevenueValue => Result.ExpectedRevenue;
        public double ProfitValue => Result.Profit;
        public double RoiValue => Result.RoiValue;

        /// <summary>Flags the rows whose numbers rest on an unverified assumption.</summary>
        public string Note => Result.Path is GemRoi.Path.VaalOnly or GemRoi.Path.FullChain
            ? "EV — unverified vaal odds"
            : "";
    }
}
