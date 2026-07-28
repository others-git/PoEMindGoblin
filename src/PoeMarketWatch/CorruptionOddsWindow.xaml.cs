using System.Windows;
using System.Windows.Media;
using PoeMarketWatch.Core;

namespace PoeMarketWatch;

/// <summary>
/// Edits the Vaal Orb outcome distribution.
///
/// This dialog exists because the odds could not be verified against any primary source
/// (see GemRoi.CorruptionOdds). Rather than hard-code an unverified constant into an
/// expected-value calculation and present the output as fact, the assumption is surfaced,
/// labelled, and made correctable in one place.
/// </summary>
public partial class CorruptionOddsWindow : Window
{
    private readonly AppSettings _settings;

    public CorruptionOddsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        NoChangeBox.Text = settings.VaalNoChange.ToString("0.###");
        LevelUpBox.Text = settings.VaalLevelUp.ToString("0.###");
        LevelDownBox.Text = settings.VaalLevelDown.ToString("0.###");
        QualityBox.Text = settings.VaalQualityChange.ToString("0.###");
        Validate();
    }

    private void OnChanged(object sender, RoutedEventArgs e) => Validate();

    /// <summary>Live feedback: a distribution that does not sum to 1 is not saveable.</summary>
    private bool Validate()
    {
        if (!TryRead(out var odds))
        {
            SumText.Text = "Every field must be a number of 0 or more.";
            SumText.Foreground = new SolidColorBrush(Color.FromRgb(0xC4, 0x57, 0x4B));
            SaveBtn.IsEnabled = false;
            return false;
        }

        var ok = odds.IsNormalised;
        SumText.Text = ok
            ? $"Sums to {odds.Total:0.###} — good."
            : $"Sums to {odds.Total:0.###} — must be 1.0.";
        SumText.Foreground = new SolidColorBrush(ok
            ? Color.FromRgb(0x7F, 0xB0, 0x69)
            : Color.FromRgb(0xC4, 0x57, 0x4B));
        SaveBtn.IsEnabled = ok;
        return ok;
    }

    private bool TryRead(out GemRoi.CorruptionOdds odds)
    {
        odds = GemRoi.CorruptionOdds.Default;
        if (!Num(NoChangeBox.Text, out var a) || !Num(LevelUpBox.Text, out var b)
            || !Num(LevelDownBox.Text, out var c) || !Num(QualityBox.Text, out var d))
            return false;
        odds = new GemRoi.CorruptionOdds(a, b, c, d);
        return true;

        static bool Num(string s, out double v) =>
            double.TryParse(s, out v) && v >= 0;
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        var d = GemRoi.CorruptionOdds.Default;
        NoChangeBox.Text = d.NoChange.ToString("0.###");
        LevelUpBox.Text = d.LevelUp.ToString("0.###");
        LevelDownBox.Text = d.LevelDown.ToString("0.###");
        QualityBox.Text = d.QualityChange.ToString("0.###");
        Validate();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!Validate() || !TryRead(out var odds)) return;
        _settings.VaalNoChange = odds.NoChange;
        _settings.VaalLevelUp = odds.LevelUp;
        _settings.VaalLevelDown = odds.LevelDown;
        _settings.VaalQualityChange = odds.QualityChange;
        _settings.Save();
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
