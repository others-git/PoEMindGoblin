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
///
/// The LAST resort of three. Auto-detection reads the game's own window and covers
/// almost everything; the resolution picker covers a launcher detection cannot find;
/// this grid is for when the panel itself has moved.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class CalibrationWindow : Window
{
    private readonly string _capturePath;
    private ChartPanelReader.Options _options;
    private System.Drawing.Bitmap? _bitmap;
    private bool _loadingPresets;

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

            // Work in the CAPTURE's coordinate space, which is the game's. The reader
            // rescales its own copy to whatever it is decoding, so calibrating in the
            // stored reference space meant a nudge of 1 moved the drawn box by 1 and
            // the actual decode by the scale factor -- the two never agreeing.
            // SEEDED FROM THE CAPTURE, not from the stored numbers.
            //
            // It opened on the calibration rescaled to the screen, which is exactly the
            // thing that has drifted -- so the grid started visibly off the tiles and
            // the first job was always to undo that by hand. Locate finds the grid in
            // the pixels; the stored calibration is the fallback for when it cannot.
            _options = ChartPanelReader.Locate(new BitmapPixels(_bitmap), _options)
                       ?? _options.ForScreen(_bitmap.Width, _bitmap.Height);

            LoadPresets();
            // The window itself takes the keys. Every button is Focusable="False", so
            // focus stays here and the arrows cannot be stolen by focus navigation.
            Focus();
            Keyboard.Focus(this);
            Redraw();
        };
        Unloaded += (_, _) => _bitmap?.Dispose();

        // handledEventsToo: nothing downstream gets to swallow an arrow key before the
        // window has had it.
        AddHandler(PreviewKeyDownEvent, new KeyEventHandler(OnKey), handledEventsToo: true);
    }

    // ---- resolution ---------------------------------------------------------------

    private sealed record Choice(string Label, GameResolution Value);

    private void LoadPresets()
    {
        _loadingPresets = true;
        var current = GameResolution.Load();
        var detected = GameWindow.ClientBounds();

        var choices = new List<Choice>
        {
            new(detected is { } d ? $"Auto — detected {d.Width}×{d.Height}"
                                  : "Auto — game not running",
                new GameResolution()),
        };
        choices.AddRange(ScreenPreset.Common.Select(
            p => new Choice(p.Label, new GameResolution
            {
                Auto = false, Width = p.Width, Height = p.Height,
            })));

        // A pin for a resolution the shortlist does not carry must still show as itself
        // rather than silently reading as Auto.
        if (current.IsPinned && ScreenPreset.Match(current.Width, current.Height) is null)
            choices.Add(new Choice($"{current.Width} × {current.Height}", current));

        ResolutionBox.ItemsSource = choices;
        ResolutionBox.DisplayMemberPath = nameof(Choice.Label);
        ResolutionBox.SelectedItem = current.IsPinned
            ? choices.FirstOrDefault(c => c.Value.IsPinned
                                          && c.Value.Width == current.Width
                                          && c.Value.Height == current.Height)
              ?? choices[0]
            : choices[0];

        var capture = _bitmap is null ? "" : $"capture is {_bitmap.Width}×{_bitmap.Height}";
        ResolutionNote.Text = detected is null && !current.IsPinned
            ? $"Falling back to the primary screen · {capture}"
            : capture;
        _loadingPresets = false;
    }

    private void OnResolutionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingPresets) return;
        if (ResolutionBox.SelectedItem is not Choice choice) return;
        try
        {
            choice.Value.Save();
            AdviceLabel.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Advise($"Could not save the resolution: {ex.Message}");
        }
    }

    // ---- nudging ------------------------------------------------------------------

    /// <summary>
    /// One nudge, as data rather than as three copies of the same arithmetic. Pitch is
    /// floored: a zero pitch stacks every cell on the origin and reads as "no charts".
    /// </summary>
    private static ChartPanelReader.Options Nudge(
        ChartPanelReader.Options o, char axis, int delta) => axis switch
    {
        'x' => o with { OriginX = o.OriginX + delta },
        'y' => o with { OriginY = o.OriginY + delta },
        _ => o with { Pitch = Math.Max(10, o.Pitch + delta) },
    };

    private void OnKey(object sender, KeyEventArgs e)
    {
        var step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 5 : 1;
        var (axis, delta) = e.Key switch
        {
            Key.Left => ('x', -step),
            Key.Right => ('x', step),
            Key.Up => ('y', -step),
            Key.Down => ('y', step),
            Key.OemMinus or Key.Subtract => ('p', -step),
            Key.OemPlus or Key.Add => ('p', step),
            _ => ('\0', 0),
        };
        if (axis == '\0') return;

        _options = Nudge(_options, axis, delta);
        e.Handled = true;
        Redraw();
    }

    private void OnNudge(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || tag.Length < 2) return;
        if (!int.TryParse(tag[1..], System.Globalization.NumberStyles.AllowLeadingSign,
                          System.Globalization.CultureInfo.InvariantCulture, out var delta))
            return;
        _options = Nudge(_options, tag[0], delta);
        Redraw();
    }

    /// <summary>Back to the grid found in the capture, which is where it opened.</summary>
    private void OnReset(object sender, RoutedEventArgs e)
    {
        var shipped = new ChartPanelReader.Options();
        _options = _bitmap is null ? shipped
            : ChartPanelReader.Locate(new BitmapPixels(_bitmap), shipped)
              ?? shipped.ForScreen(_bitmap.Width, _bitmap.Height);
        Redraw();
    }

    // ---- decode + overlay ----------------------------------------------------------

    private void Advise(string message)
    {
        AdviceLabel.Text = message;
        AdviceLabel.Visibility = Visibility.Visible;
    }

    /// <summary>Redecode with the current numbers and redraw every glyph box.</summary>
    private void Redraw()
    {
        OriginLabel.Text = $"{_options.OriginX},{_options.OriginY}";
        PitchLabel.Text = _options.Pitch.ToString("0.##");
        if (_bitmap is null) return;

        // No clone: BitmapPixels borrows, so the window keeps its capture across redraws.
        var pixels = new BitmapPixels(_bitmap);
        var cells = new ChartPanelReader(_options, LevelReader.LoadWithUserTemplates())
            .Read(pixels);
        var occupied = cells.ToDictionary(c => (c.Row, c.Col), c => c);
        var slots = _options.Rows * _options.Cols;

        DecodeLabel.Text = cells.Count == 0
            ? $"No charts detected in {slots} cells"
            : $"{cells.Count} of {slots} cells hold a chart · "
              + string.Join("  ", cells.GroupBy(c => c.Shape).OrderByDescending(g => g.Count())
                  .Select(g => $"{g.Key}×{g.Count()}"))
              + $" · {cells.Count(c => c.Level is null)} unreadable levels";

        // An almost-empty panel decodes to almost nothing whether the calibration is
        // perfect or hopeless, and reads as "it didn't notice my charts" either way.
        // Say which it is, because the picture cannot.
        AdviceLabel.Visibility = Visibility.Collapsed;
        if (cells.Count == 0)
            Advise("Nothing decoded. If the panel really is empty this says nothing about "
                   + "the calibration — re-run Identify Charts with a full panel. If it is "
                   + "not empty, nudge the origin until the boxes sit on the glyphs.");
        else if (cells.Count < 8)
            Advise($"Only {cells.Count} charts in this capture — enough to check the boxes "
                   + "land on them, but too few to trust the grid across the whole panel. "
                   + "Re-run Identify Charts with a fuller panel to be sure.");

        Overlay.Children.Clear();
        var found = new SolidColorBrush(Color.FromRgb(0x86, 0xA8, 0x6A));
        var empty = new SolidColorBrush(Color.FromArgb(0x70, 0x6B, 0x5F, 0x4E));
        for (var row = 0; row < _options.Rows; row++)
            for (var col = 0; col < _options.Cols; col++)
            {
                // Rounded exactly where the reader rounds, so the box the user nudges
                // sits on the pixel the decode actually samples.
                var cx = (int)Math.Round(_options.OriginX + col * _options.Pitch);
                var cy = (int)Math.Round(_options.OriginY + row * _options.Pitch)
                         + _options.GlyphOffsetY;
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
        // Saved against the capture's own size, so ForScreen is the identity next time
        // the game runs at this resolution and a nudge of 1 stays a nudge of 1.
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
