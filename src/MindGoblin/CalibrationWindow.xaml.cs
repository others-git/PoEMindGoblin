using System.IO;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using MindGoblin.Core;
using MindGoblin.Core.Voyage;

namespace MindGoblin;

/// <summary>
/// Nudge the chart-panel calibration over the last Identify capture, with the decode
/// running live. The old Calibrate button opened a folder of JSON, which answered the
/// question "where are the numbers" while leaving "are they right" to imagination.
/// Here the grid is drawn over the very pixels the reader consumes: when every box
/// sits on a glyph and the count says 60, the calibration is right by inspection.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class CalibrationWindow : Window
{
    private readonly string _capturePath;
    private ChartPanelReader.Options _options;
    private System.Drawing.Bitmap? _bitmap;

    public static string CapturePath => SettingsFolder.FileIn("last-identify.png");

    public CalibrationWindow()
    {
        InitializeComponent();
        _capturePath = CapturePath;
        _options = ChartPanelReader.Options.Load();

        Loaded += (_, _) =>
        {
            _bitmap = new System.Drawing.Bitmap(_capturePath);
            // OnLoad, or BitmapImage keeps the FILE handle open for as long as the
            // image is shown -- and the next Identify Charts would then fail to
            // overwrite last-identify.png, silently freezing this view in the past.
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.UriSource = new Uri(_capturePath);
            image.EndInit();
            Shot.Source = image;
            Redraw();
        };
        Unloaded += (_, _) => _bitmap?.Dispose();
        PreviewKeyDown += OnKey;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 5 : 1;
        var handled = true;
        _options = e.Key switch
        {
            Key.Left => _options with { OriginX = _options.OriginX - step },
            Key.Right => _options with { OriginX = _options.OriginX + step },
            Key.Up => _options with { OriginY = _options.OriginY - step },
            Key.Down => _options with { OriginY = _options.OriginY + step },
            _ => Handle(out handled),
        };
        if (handled) { e.Handled = true; Redraw(); }
        ChartPanelReader.Options Handle(out bool h) { h = false; return _options; }
    }

    private void OnNudge(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        var delta = int.Parse(tag[1..].Replace("+", ""));
        _options = tag[0] switch
        {
            'x' => _options with { OriginX = _options.OriginX + delta },
            'y' => _options with { OriginY = _options.OriginY + delta },
            _ => _options with { Pitch = Math.Max(10, _options.Pitch + delta) },
        };
        Redraw();
    }

    /// <summary>Redecode with the current numbers and redraw every glyph box.</summary>
    private void Redraw()
    {
        OriginLabel.Text = $"{_options.OriginX},{_options.OriginY}";
        PitchLabel.Text = _options.Pitch.ToString();
        if (_bitmap is null) return;

        // No clone: BitmapPixels borrows, so the window keeps its capture across redraws.
        var pixels = new BitmapPixels(_bitmap);
        var cells = new ChartPanelReader(_options, LevelReader.LoadWithUserTemplates())
            .Read(pixels);
        var occupied = cells.ToDictionary(c => (c.Row, c.Col), c => c);
        DecodeLabel.Text = $"{cells.Count} charts detected · "
            + string.Join("  ", cells.GroupBy(c => c.Shape).OrderByDescending(g => g.Count())
                .Select(g => $"{g.Key}×{g.Count()}"));

        Overlay.Children.Clear();
        var found = new SolidColorBrush(Color.FromRgb(0x86, 0xA8, 0x6A));
        var empty = new SolidColorBrush(Color.FromArgb(0x70, 0x6B, 0x5F, 0x4E));
        for (var row = 0; row < _options.Rows; row++)
            for (var col = 0; col < _options.Cols; col++)
            {
                var cx = _options.OriginX + col * _options.Pitch;
                var cy = _options.OriginY + row * _options.Pitch + _options.GlyphOffsetY;
                var hit = occupied.TryGetValue((row, col), out var cell);
                var box = new Rectangle
                {
                    Width = _options.GlyphHalf * 2,
                    Height = _options.GlyphHalf * 2,
                    Stroke = hit ? found : empty,
                    StrokeThickness = 2,
                };
                Canvas.SetLeft(box, cx - _options.GlyphHalf);
                Canvas.SetTop(box, cy - _options.GlyphHalf);
                Overlay.Children.Add(box);
                if (hit)
                {
                    var tag = new TextBlock
                    {
                        Text = cell!.Shape?.ToString()[..2] ?? "??",
                        Foreground = found,
                        FontSize = 11,
                        FontFamily = new FontFamily("Consolas"),
                    };
                    Canvas.SetLeft(tag, cx - _options.GlyphHalf);
                    Canvas.SetTop(tag, cy + _options.GlyphHalf + 1);
                    Overlay.Children.Add(tag);
                }
            }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _options.Save();
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            ChartPanelReader.Options.WriteDefaultsIfMissing();
            AreaModifierPanel.Options.WriteDefaultsIfMissing();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                System.IO.Path.GetDirectoryName(ChartPanelReader.Options.DefaultPath)!)
                { UseShellExecute = true });
        }
        catch (Exception) { /* explorer failing to open is not worth a crash */ }
    }
}
