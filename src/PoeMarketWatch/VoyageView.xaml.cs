using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using PoeMarketWatch.Core.Voyage;

namespace PoeMarketWatch;

/// <summary>
/// The Voyage planner: mirror the board, read it, solve it.
///
/// A MIRROR, not an overlay. It draws its own board and chart panel on a second monitor
/// rather than painting over the game, which keeps it entirely outside the client -- no
/// injection, no hooks, nothing drawn on top of a window it does not own.
///
/// Reading happens in two passes because the game only shows half of what matters:
///   1. "Read panel" takes ONE screenshot and decodes every chart's shape, rotation and
///      area level. That is enough to plan the layout.
///   2. "Read mode" fills in the rest. Stats and the two special modifier lines exist
///      only in tooltips, so the user hovers a chart and presses Ctrl+C, and each copy
///      ticks one item off. The app watches the clipboard; it never sends the keystroke.
///
/// Pass 2 is optional. With nothing hovered the solver still returns a legal layout
/// scored on area level, so the tool is useful before the chore is done.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class VoyageView : UserControl
{
    private readonly VoyageRules _rules = new();
    private readonly ObservableCollection<FigurineRow> _figurines = new();
    private readonly ObservableCollection<string> _plan = new();
    private readonly DispatcherTimer _clipboardPoll =
        new() { Interval = TimeSpan.FromMilliseconds(250) };

    private VoyageSession _session = new();
    private VoyageSolver.Solution? _solution;
    private IReadOnlyList<VoyagePlan.Step> _steps = [];
    private Window? _popped;

    /// <summary>What the next Ctrl+C is taken to describe.</summary>
    private enum Target { Chart, Figurine }

    private Target _target = Target.Chart;
    private int _targetIndex;
    private string _lastClipboard = "";

    public VoyageView()
    {
        InitializeComponent();

        FigurineList.ItemsSource = _figurines;
        PlanList.ItemsSource = _plan;

        _rules.WriteDefaultsIfMissing();
        _rules.Changed += () => Dispatcher.Invoke(LoadProfiles);
        _rules.Error += msg => Dispatcher.Invoke(() => SetStatus($"Rule file: {msg}", bad: true));
        _rules.WatchForChanges();
        LoadProfiles();

        _clipboardPoll.Tick += (_, _) => PollClipboard();

        BuildBoard();
        BuildPanel();
        RebuildFigurines();
        RefreshProgress();

        // Popping out and docking REPARENT this control, which raises Unloaded/Loaded.
        // Stopping the poll unconditionally on Unloaded would silently kill read mode the
        // moment the user moved the window to their second monitor -- the exact workflow
        // this is for. Pause and resume instead of stop.
        Unloaded += (_, _) => _clipboardPoll.Stop();
        Loaded += (_, _) =>
        {
            if (ReadModeBtn.IsChecked == true) _clipboardPoll.Start();
        };
    }

    // ---- rule profiles -----------------------------------------------------------

    private void LoadProfiles()
    {
        var previous = (ProfileBox.SelectedItem as VoyageProfile)?.Name;
        ProfileBox.ItemsSource = _rules.Profiles;
        ProfileBox.DisplayMemberPath = nameof(VoyageProfile.Name);
        ProfileBox.SelectedItem = _rules.Profiles.FirstOrDefault(p => p.Name == previous)
                                  ?? _rules.Profiles.FirstOrDefault();

        // Edits to the rule file change the objective, so a plan computed under the old
        // rules is stale. Showing it as though it were current would be a lie.
        if (previous is not null && _solution is not null)
            SetStatus("Rules changed — solve again for a plan under the new weights.");
    }

    private VoyageProfile? Profile => ProfileBox.SelectedItem as VoyageProfile;

    private void OnProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Profile is { Description: { } d } && !string.IsNullOrWhiteSpace(d)) SetStatus(d);
    }

    private void OnCalibrate(object sender, RoutedEventArgs e)
    {
        // Opens the file rather than offering a dialog of spinners: the values are pixel
        // coordinates, and the way to get them right is to run VoyageProbe's overlay,
        // look at where the grid lands, and nudge. A GUI would not make that easier.
        try
        {
            ChartPanelReader.Options.WriteDefaultsIfMissing();
            Process.Start(new ProcessStartInfo(ChartPanelReader.Options.DefaultPath)
                { UseShellExecute = true });
            SetStatus("Calibration opened. Save, then Read panel again.");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not open the calibration file: {ex.Message}", bad: true);
        }
    }

    private void OnEditRules(object sender, RoutedEventArgs e)
    {
        try
        {
            _rules.WriteDefaultsIfMissing();
            Process.Start(new ProcessStartInfo(_rules.Path_) { UseShellExecute = true });
            SetStatus("Rule file opened. Saved edits apply without restarting.");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not open {_rules.Path_}: {ex.Message}", bad: true);
        }
    }

    // ---- pass 1: screenshot ------------------------------------------------------

    private void OnReadPanel(object sender, RoutedEventArgs e)
    {
        try
        {
            using var bmp = ScreenCapture.CapturePrimaryScreen();
            using var pixels = new BitmapPixels(bmp);
            var cells = new ChartPanelReader(
                ChartPanelReader.Options.Load(), LevelReader.LoadWithUserTemplates()).Read(pixels);

            if (cells.Count == 0)
            {
                SetStatus("No charts found. Is the Voyage screen open on the primary monitor?",
                          bad: true);
                return;
            }

            _session.ApplyPanelRead(cells);
            _solution = null;
            _plan.Clear();
            RefreshPanel();
            RefreshBoard();
            RefreshProgress();

            var unread = cells.Count(c => c.Level is null);
            SetStatus(unread == 0
                ? $"Read {cells.Count} charts."
                : $"Read {cells.Count} charts; {unread} had an unreadable level.");
        }
        catch (Exception ex)
        {
            SetStatus($"Capture failed: {ex.Message}", bad: true);
        }
    }

    // ---- pass 2: hover + clipboard -----------------------------------------------

    private void OnReadModeChanged(object sender, RoutedEventArgs e)
    {
        if (ReadModeBtn.IsChecked == true)
        {
            _lastClipboard = SafeClipboardText();     // ignore whatever is already there
            AdvanceTarget();
            _clipboardPoll.Start();
        }
        else
        {
            _clipboardPoll.Stop();
            SetStatus("Read mode off.");
        }
        RefreshPanel();
        RebuildFigurines();
    }

    /// <summary>
    /// Polling rather than a clipboard-format listener: this runs a few times a second on
    /// a string comparison, and a listener would need a window handle and message hook for
    /// no practical gain.
    /// </summary>
    private void PollClipboard()
    {
        var text = SafeClipboardText();
        if (text.Length == 0 || text == _lastClipboard) return;
        _lastClipboard = text;

        if (_target == Target.Figurine)
        {
            _session.ApplyFigurineText(_targetIndex, text);
            SetStatus($"Figurine {_targetIndex} captured.");
        }
        else if (_session.ApplyChartText(_targetIndex, text))
        {
            SetStatus($"Chart {_targetIndex} captured.");
        }
        else
        {
            SetStatus($"That copy did not look like chart {_targetIndex}.", bad: true);
            return;
        }

        _solution = null;
        AdvanceTarget();
        RefreshPanel();
        RebuildFigurines();
        RefreshProgress();
    }

    private static string SafeClipboardText()
    {
        // The clipboard is shared and can be locked by another process mid-read.
        try { return Clipboard.ContainsText() ? Clipboard.GetText().Trim() : ""; }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException) { return ""; }
    }

    /// <summary>Move to the next thing needing hover: charts first, then figurines.</summary>
    private void AdvanceTarget()
    {
        if (_session.ChartsAwaitingDetail.FirstOrDefault() is > 0 and var chart)
        {
            _target = Target.Chart;
            _targetIndex = chart;
            SetStatus($"Hover chart {chart} in game and press Ctrl+C.");
            return;
        }

        if (_session.FigurinesAwaitingDetail.FirstOrDefault() is { } figurine)
        {
            _target = Target.Figurine;
            _targetIndex = figurine.Index;
            SetStatus($"Hover the {figurine.Edge} figurine {figurine.Index} and press Ctrl+C.");
            return;
        }

        _target = Target.Chart;
        _targetIndex = 0;
        SetStatus("Everything read. Pick a profile and solve.");
    }

    private void OnFigurineSelected(object sender, SelectionChangedEventArgs e)
    {
        if (FigurineList.SelectedItem is not FigurineRow row) return;
        _target = Target.Figurine;
        _targetIndex = row.Index;
        SetStatus($"Next copy goes to figurine {row.Index} ({row.Edge}).");
    }

    // ---- solving -----------------------------------------------------------------

    private void OnSolve(object sender, RoutedEventArgs e)
    {
        if (Profile is not { } profile) { SetStatus("No rule profile loaded.", bad: true); return; }
        if (_session.Charts.Count == 0) { SetStatus("Read the panel first.", bad: true); return; }

        _solution = _session.Solve(profile, TimeSpan.FromSeconds(3));
        _steps = _session.Plan(_solution);

        _plan.Clear();
        foreach (var step in _steps)
            _plan.Add($"square {step.Square}  <-  chart {step.ChartNumber,-3} "
                      + $"{step.Chart.Shape,-8} {step.RotationText}");

        RefreshBoard();
        RefreshPanel();

        if (_solution.IsEmpty)
        {
            SolveInfo.Text = "No legal layout: the charts read cannot tile the board.";
            SetStatus("No legal layout found.", bad: true);
            return;
        }

        // Say which it got. An anytime result is usually excellent but not proven, and
        // presenting "good" as "best" would be a lie the user cannot check.
        SolveInfo.Text = $"value {_solution.Value:0.##} · "
                       + (_solution.ProvedOptimal ? "proved optimal" : "best found in budget")
                       + $" · {_solution.NodesExplored:N0} nodes in {_solution.Elapsed.TotalMilliseconds:0} ms";
        SetStatus($"Placed {_solution.Placements.Count} charts under \"{profile.Name}\".");
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        _session = new VoyageSession();
        _solution = null;
        _steps = [];
        _plan.Clear();
        SolveInfo.Text = "";
        _targetIndex = 0;
        RefreshPanel();
        RefreshBoard();
        RebuildFigurines();
        RefreshProgress();
        SetStatus("Cleared.");
    }

    // ---- second monitor ----------------------------------------------------------

    private void OnPopOut(object sender, RoutedEventArgs e)
    {
        // Already out: the button says "Dock", so dock it. Closing the window is what
        // returns the view to its tab, so this is the same path.
        if (_popped is not null) { _popped.Close(); return; }

        if (Parent is not ContentControl host) return;
        host.Content = null;

        _popped = new Window
        {
            Title = "Voyage planner",
            Content = this,
            Width = 1250,
            Height = 800,
            Background = Brushes.Black,
        };
        // Returning the view to its tab on close keeps a single instance and a single
        // session -- a second copy would silently read into different state.
        _popped.Closed += (_, _) =>
        {
            _popped!.Content = null;
            host.Content = this;
            _popped = null;
            PopOutBtn.Content = "Pop out";
        };
        PopOutBtn.Content = "Dock";
        _popped.Show();
    }

    // ---- rendering ---------------------------------------------------------------

    private Border[,] _boardCells = new Border[0, 0];
    private Border[] _panelCells = [];

    private void BuildBoard()
    {
        _boardCells = new Border[_session.Layout.Rows, _session.Layout.Cols];
        BoardGrid.Rows = _session.Layout.Rows;
        BoardGrid.Columns = _session.Layout.Cols;
        BoardGrid.Children.Clear();

        for (var r = 0; r < _session.Layout.Rows; r++)
        {
            for (var c = 0; c < _session.Layout.Cols; c++)
            {
                var cell = new Border
                {
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x32, 0x2A)),
                    BorderThickness = new Thickness(1),
                    Background = new SolidColorBrush(Color.FromRgb(0x18, 0x15, 0x12)),
                    Margin = new Thickness(1),
                };
                _boardCells[r, c] = cell;
                BoardGrid.Children.Add(cell);
            }
        }
        RefreshBoard();
    }

    private void RefreshBoard()
    {
        if (_boardCells.Length == 0) return;
        var cols = _session.Layout.Cols;

        for (var r = 0; r < _boardCells.GetLength(0); r++)
        {
            for (var c = 0; c < cols; c++)
            {
                var square = r * cols + c + 1;
                var step = _steps.FirstOrDefault(s => s.Square == square);
                _boardCells[r, c].Child = BoardSquareContent(square, step);
            }
        }
    }

    private static UIElement BoardSquareContent(int square, VoyagePlan.Step? step)
    {
        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        stack.Children.Add(new TextBlock
        {
            Text = square.ToString(),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x7E, 0x6C)),
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        if (step is null) return stack;

        stack.Children.Add(Glyph(new ChartFace(step.Chart.Shape, step.Rotation), 56,
                                 Color.FromRgb(0xC8, 0xA9, 0x6A)));
        stack.Children.Add(new TextBlock
        {
            // The whole point of the plan: read the number, find that square in the panel.
            Text = $"chart {step.ChartNumber}",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xA9, 0x6A)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 3, 0, 0),
        });
        stack.Children.Add(new TextBlock
        {
            Text = step.RotationText,
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x7E, 0x6C)),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        return stack;
    }

    private void BuildPanel()
    {
        var layout = ScreenLayout.Load();
        PanelGrid.Rows = layout.ChartPanelRows;
        PanelGrid.Columns = layout.ChartPanelCols;
        PanelGrid.Children.Clear();

        _panelCells = new Border[layout.ChartPanelRows * layout.ChartPanelCols];
        for (var i = 0; i < _panelCells.Length; i++)
        {
            var cell = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x24, 0x1E)),
                BorderThickness = new Thickness(1),
                Width = 52,
                Height = 52,
                Margin = new Thickness(1),
            };
            _panelCells[i] = cell;
            PanelGrid.Children.Add(cell);
        }
        RefreshPanel();
    }

    private void RefreshPanel()
    {
        var used = _steps.ToDictionary(s => s.ChartNumber, s => s.Square);

        for (var i = 0; i < _panelCells.Length; i++)
        {
            var index = i + 1;
            var cell = _panelCells[i];
            var chart = _session.ByPanelIndex.GetValueOrDefault(index);

            if (chart is null)
            {
                cell.Background = new SolidColorBrush(Color.FromRgb(0x14, 0x12, 0x10));
                cell.BorderBrush = new SolidColorBrush(Color.FromRgb(0x24, 0x20, 0x1C));
                cell.Child = null;
                continue;
            }

            var isPlanned = used.ContainsKey(index);
            var isTarget = ReadModeBtn.IsChecked == true
                           && _target == Target.Chart && _targetIndex == index;
            var hasDetail = !_session.ChartsAwaitingDetail.Contains(index);

            cell.Background = new SolidColorBrush(isPlanned
                ? Color.FromRgb(0x2A, 0x26, 0x18)
                : Color.FromRgb(0x1C, 0x19, 0x17));
            cell.BorderBrush = new SolidColorBrush(isTarget
                ? Color.FromRgb(0xC8, 0xA9, 0x6A)
                : isPlanned ? Color.FromRgb(0x7F, 0xA6, 0x5B) : Color.FromRgb(0x2A, 0x24, 0x1E));
            cell.BorderThickness = new Thickness(isTarget ? 2 : 1);

            var stack = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
            var header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            header.Children.Add(new TextBlock
            {
                Text = index.ToString(),
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x7E, 0x6C)),
            });
            header.Children.Add(new TextBlock
            {
                // A tick per chart is the checklist: read mode is only bearable if what
                // is left is visible at a glance.
                Text = hasDetail ? " ✓" : "",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0x7F, 0xA6, 0x5B)),
            });
            stack.Children.Add(header);
            stack.Children.Add(Glyph(FaceOf(chart), 26,
                isPlanned ? Color.FromRgb(0xC8, 0xA9, 0x6A) : Color.FromRgb(0x8A, 0x7E, 0x6C)));
            stack.Children.Add(new TextBlock
            {
                Text = chart.AreaLevel > 0 ? $"L{chart.AreaLevel}" : "L?",
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x60, 0x52)),
                HorizontalAlignment = HorizontalAlignment.Center,
            });

            cell.Child = stack;
            cell.ToolTip = Tooltip(index, chart, used.GetValueOrDefault(index));
        }

        PanelHeader.Text = _session.Charts.Count == 0
            ? "CHARTS"
            : $"CHARTS — {_session.Charts.Count} read, {_session.ChartsAwaitingDetail.Count} need hover";
    }

    private static string Tooltip(int index, Chart chart, int square)
    {
        var lines = new List<string> { $"chart {index}: {chart.Name}" };
        if (!string.IsNullOrEmpty(chart.AreaName)) lines.Add(chart.AreaName);
        if (square > 0) lines.Add($"→ square {square}");
        lines.AddRange(ChartText.ScorableLines(chart));
        return string.Join("\n", lines);
    }

    /// <summary>
    /// The rotation the panel read implied. Charts carry a shape, not a face, so the
    /// panel mirror shows the base orientation until the solver assigns one.
    /// </summary>
    private static ChartFace FaceOf(Chart chart) => new(chart.Shape, 0);

    private void RebuildFigurines()
    {
        _figurines.Clear();
        foreach (var slot in _session.Layout.Figurines)
        {
            var text = _session.Figurines.GetValueOrDefault(slot.Index);
            _figurines.Add(new FigurineRow(slot.Index, slot.Edge, text));
        }

        var left = _session.FigurinesAwaitingDetail.Count;
        FigurineHeader.Text = left == 0
            ? $"FIGURINES — all {_figurines.Count} read"
            : $"FIGURINES — {left} of {_figurines.Count} need hover";
    }

    private void RefreshProgress() =>
        Progress.Text = $"{_session.ReadProgress:P0} read";

    private void SetStatus(string text, bool bad = false)
    {
        Status.Text = text;
        Status.Foreground = new SolidColorBrush(bad
            ? Color.FromRgb(0xC4, 0x57, 0x4B)
            : Color.FromRgb(0x8A, 0x7E, 0x6C));
    }

    /// <summary>
    /// Draw a chart face: a centre pip plus an arm for each open side.
    ///
    /// Drawn rather than iconified so it matches whatever the reader measured. If the
    /// glyph on screen ever stops matching the game, the reader is wrong -- and that is
    /// exactly what the user needs to be able to see.
    /// </summary>
    private static UIElement Glyph(ChartFace face, double size, Color colour)
    {
        var canvas = new Canvas
        {
            Width = size,
            Height = size,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var brush = new SolidColorBrush(colour);
        var thickness = Math.Max(2, size / 12);
        var mid = size / 2;

        void Arm(double x1, double y1, double x2, double y2) =>
            canvas.Children.Add(new Line
            {
                X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
                Stroke = brush,
                StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            });

        if (face.IsOpen(Side.North)) Arm(mid, mid, mid, 0);
        if (face.IsOpen(Side.South)) Arm(mid, mid, mid, size);
        if (face.IsOpen(Side.West)) Arm(mid, mid, 0, mid);
        if (face.IsOpen(Side.East)) Arm(mid, mid, size, mid);

        var pip = new Ellipse
        {
            Width = thickness * 1.6,
            Height = thickness * 1.6,
            Fill = brush,
        };
        Canvas.SetLeft(pip, mid - thickness * 0.8);
        Canvas.SetTop(pip, mid - thickness * 0.8);
        canvas.Children.Add(pip);
        return canvas;
    }

    /// <summary>One figurine row in the checklist.</summary>
    public sealed class FigurineRow : INotifyPropertyChanged
    {
        public FigurineRow(int index, string edge, string? text)
        {
            Index = index;
            Edge = edge;
            Text = text ?? "not read";
            HasText = !string.IsNullOrWhiteSpace(text);
        }

        public int Index { get; }
        public string Edge { get; }
        public string Title => $"{Index}. {Edge}";
        public string Text { get; }
        public bool HasText { get; }
        public string Tick => HasText ? "✓" : "•";
        public Brush TickBrush => new SolidColorBrush(HasText
            ? Color.FromRgb(0x7F, 0xA6, 0x5B)
            : Color.FromRgb(0x6A, 0x60, 0x52));

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
