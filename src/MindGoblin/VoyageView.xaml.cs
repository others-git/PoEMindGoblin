using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MindGoblin.Core.Voyage;

namespace MindGoblin;

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
public partial class VoyageView : UserControl, IDisposable
{
    private readonly VoyageRules _rules = new();
    private readonly ObservableCollection<ModifierRow> _modifiers = new();
    private readonly ObservableCollection<SummaryRow> _summary = new();
    private readonly ObservableCollection<AlertRow> _alerts = new();

    /// <summary>
    /// Whether to force the Soul Eater chart onto the board.
    ///
    /// Off by default, because forcing a chart the rules score at zero costs a square
    /// that something profitable would otherwise have. It is a judgement about how much
    /// player power is worth to you, which is not a thing the planner can weigh.
    /// </summary>
    private bool _useSoulEater;

    /// <summary>Whether the alert banner is dropped down over the board.</summary>
    private bool _alertsOpen;
    private readonly DispatcherTimer _clipboardPoll =
        new() { Interval = TimeSpan.FromMilliseconds(250) };

    private VoyageSession _session = new();
    private string? _restoredProfile;
    private IReadOnlyList<string> _added = [];
    private IReadOnlyList<string> _outdated = [];

    /// <summary>
    /// Off for offscreen rendering, so inspecting the layout cannot overwrite a real
    /// session with sample data. Saving is a side effect the render has no business having.
    /// </summary>
    private bool _persistenceEnabled = true;
    private HotkeyService? _hotkeys;
    private int? _captureHotkey;
    private bool _capturing;
    private VoyageSolver.Solution? _solution;
    private IReadOnlyList<VoyagePlan.Step> _steps = [];
    /// <summary>What the next capture is taken to describe.</summary>
    private enum Target { Chart, Figurine, Square }

    private Target _target = Target.Chart;
    private int _targetIndex;
    private string _lastClipboard = "";

    /// <summary>Items deliberately passed over, so the checklist cannot stall on one of them.</summary>
    private readonly HashSet<(Target, int)> _skipped = new();

    public VoyageView()
    {
        InitializeComponent();

        ModifierList.ItemsSource = _modifiers;
        SummaryList.ItemsSource = _summary;
        AlertList.ItemsSource = _alerts;

        // Reading a board is slow -- a screenshot, nine hovers, then a hover per chart.
        // Throwing that away on exit would make the tool worse than doing it by hand.
        var (restored, state) = VoyageSession.Restore();
        _session = restored;
        _restoredProfile = state?.Profile;
        _useSoulEater = state?.UseSoulEater ?? false;

        _rules.WriteDefaultsIfMissing();

        // A profile added since the rule file was first written would otherwise never
        // appear -- the file is created once and then left alone, which is right for
        // something you edit and wrong for shipping new objectives.
        _added = _rules.AddMissingDefaults();
        _outdated = _rules.CompareWithDefaults().Outdated;

        _rules.Changed += () => Dispatcher.Invoke(LoadProfiles);
        _rules.Error += msg => Dispatcher.Invoke(() => SetStatus($"Rule file: {msg}", bad: true));
        _rules.WatchForChanges();
        LoadProfiles();

        _clipboardPoll.Tick += (_, _) => PollClipboard();

        BuildBoard();
        BuildPanel();
        RefreshPanel();
        RebuildModifiers();
        RefreshProgress();
        ReportRestored();
        ReportProfileChanges();

        // Popping out and docking REPARENT this control, which raises Unloaded/Loaded.
        // Stopping the poll unconditionally on Unloaded would silently kill read mode the
        // moment the user moved the window to their second monitor -- the exact workflow
        // this is for. Pause and resume instead of stop.
        Unloaded += (_, _) => _clipboardPoll.Stop();
        Loaded += (_, _) =>
        {
            if (ReadModeBtn.IsChecked == true) _clipboardPoll.Start();
            RegisterCaptureHotkey();
        };
    }

    /// <summary>
    /// Release the things that outlive a window.
    ///
    /// VoyageRules holds a FileSystemWatcher and HotkeyService holds a system-wide key
    /// registration -- neither goes away with the control. It matters most for the
    /// offscreen renderer, which builds a view per invocation and would otherwise leave a
    /// watcher behind each time.
    /// </summary>
    public void Dispose()
    {
        _clipboardPoll.Stop();
        if (_captureHotkey is { } id) _hotkeys?.Unregister(id);
        _hotkeys?.Dispose();
        _solving?.Cancel();
        _rules.Dispose();
        GC.SuppressFinalize(this);
    }

    // ---- persistence -------------------------------------------------------------

    /// <summary>
    /// Write the session out.
    ///
    /// Called after every capture rather than on exit: the app can be killed, and a save
    /// that only happens on a clean shutdown is a save that fails exactly when it matters.
    /// A failure here must never interrupt reading, so it degrades to a status line.
    /// </summary>
    private void Persist()
    {
        if (!_persistenceEnabled) return;
        try
        {
            _session.Save(profile: Profile?.Name, useSoulEater: _useSoulEater);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Could not save the session: {ex.Message}", bad: true);
        }
    }

    /// <summary>
    /// Say what changed in the shipped profiles, because otherwise it is invisible: the
    /// dropdown simply lacks an objective and nothing explains why.
    /// </summary>
    private void ReportProfileChanges()
    {
        var parts = new List<string>();
        if (_added.Count > 0)
            parts.Add($"Added {_added.Count} new profile{(_added.Count == 1 ? "" : "s")}: "
                      + string.Join(", ", _added));
        if (_outdated.Count > 0)
            parts.Add($"{_outdated.Count} profile{(_outdated.Count == 1 ? "" : "s")} differ from "
                      + $"the shipped rules ({string.Join(", ", _outdated)}) — "
                      + "Restore rules to take the current ones.");

        if (parts.Count > 0) SetStatus(string.Join("  ", parts));
    }

    private void OnRestoreRules(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            "Replace every rule profile with the shipped ones?\n\n"
            + "Any weights you have edited will be lost. The read you have captured is "
            + "not affected.",
            "Restore rules", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;

        _rules.RestoreDefaults();
        _added = [];
        _outdated = [];
        _solution = null;
        SetStatus("Rules restored to the shipped defaults. Solve again.");
    }

    private void ReportRestored()
    {
        if (_session.Charts.Count == 0 && _session.SquareModifiers.Count == 0) return;

        var parts = new List<string>();
        if (_session.Charts.Count > 0) parts.Add($"{_session.Charts.Count} charts");
        if (_session.SquareModifiers.Count > 0)
            parts.Add($"{_session.SquareModifiers.Count} squares");
        SetStatus($"Restored {string.Join(" and ", parts)} from your last session. "
                  + "Read panel again if the charts have changed.");
    }

    // ---- rule profiles -----------------------------------------------------------

    private void LoadProfiles()
    {
        var previous = (ProfileBox.SelectedItem as VoyageProfile)?.Name;
        ProfileBox.ItemsSource = _rules.Profiles;
        ProfileBox.SelectedItem =
            _rules.Profiles.FirstOrDefault(p => p.Name == (previous ?? _restoredProfile))
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
        if (IsLoaded) Persist();          // so the app reopens on the same objective
    }

    private void OnCalibrate(object sender, RoutedEventArgs e)
    {
        // Opens the file rather than offering a dialog of spinners: the values are pixel
        // coordinates, and the way to get them right is to run VoyageProbe's overlay,
        // look at where the grid lands, and nudge. A GUI would not make that easier.
        // Two things are calibrated now -- where the chart panel is, and where the Area
        // Modifiers panel is -- so this opens the folder rather than one of them.
        try
        {
            ChartPanelReader.Options.WriteDefaultsIfMissing();
            AreaModifierPanel.Options.WriteDefaultsIfMissing();
            var folder = System.IO.Path.GetDirectoryName(ChartPanelReader.Options.DefaultPath)!;
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            SetStatus("Calibration files opened. Save, then read again.");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not open the calibration folder: {ex.Message}", bad: true);
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
            _summary.Clear();
            Persist();
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

    // ---- pass 2a: read the Area Modifiers panel ----------------------------------

    /// <summary>
    /// Bind the capture key.
    ///
    /// A SYSTEM-WIDE hotkey, because the whole point is to press it while Path of Exile
    /// has focus and the square is hovered. Alt-tabbing to click a button would drop the
    /// hover and the panel would be empty by the time anything was captured.
    /// </summary>
    private void RegisterCaptureHotkey()
    {
        if (_captureHotkey is not null) return;
        if (Window.GetWindow(this) is not { } window) return;

        _hotkeys ??= new HotkeyService(window);
        _captureHotkey = _hotkeys.Register(
            System.Windows.Input.Key.C,
            HotkeyService.Mod.Control | HotkeyService.Mod.Alt,
            () => Dispatcher.Invoke(async () => await CaptureAreaModifiersAsync()));

        CaptureHint.Text = _captureHotkey is null
            ? "Ctrl+Alt+C is taken by another app"
            : ScreenOcr.IsAvailable
                ? "Hover a square in game, press Ctrl+Alt+C"
                : "No OCR language pack — type modifiers below instead";
    }

    /// <summary>
    /// Screenshot the Area Modifiers panel and record what it says about the target square.
    ///
    /// The game aggregates the figurine effects per square and shows them here, which is
    /// why this reads squares rather than figurines: the figurines are carvings, not
    /// items, so the game will not copy them, but it will total them up for you.
    /// </summary>
    private async Task CaptureAreaModifiersAsync()
    {
        if (_capturing) return;                     // a held key must not queue captures
        if (_target != Target.Square || _targetIndex == 0)
        {
            SetStatus("Select a board square first, then hover it in game.", bad: true);
            return;
        }
        if (!ScreenOcr.IsAvailable)
        {
            SetStatus("Windows has no OCR language pack installed. Type the text below.",
                      bad: true);
            return;
        }

        _capturing = true;
        var square = _targetIndex;
        try
        {
            var options = AreaModifierPanel.Options.Load();
            var screen = ScreenCapture.PrimaryScreenBounds();
            var (x, y, w, h) = options.ToPixels(screen.Width, screen.Height);
            var raw = await ScreenOcr.ReadRegionAsync(
                new System.Drawing.Rectangle(x, y, w, h), options.Upscale);

            var reading = AreaModifierPanel.Read(raw);
            if (!reading.IsRead)
            {
                // An empty panel has three meanings and only one of them is an error the
                // user can fix by hovering. Saying which is the difference between a
                // useful message and a wrong one.
                SetStatus(reading.State == AreaModifierPanel.PanelState.Placeholder
                    ? $"Hover square {square} in game before pressing Ctrl+Alt+C."
                    : "Could not find the Area Modifiers panel — open the Voyage screen, "
                      + "or adjust the region under Calibrate.",
                    bad: true);
                return;
            }

            _session.ApplySquareModifiers(square, reading.Lines);
            _solution = null;
            Persist();
            SetStatus(reading.Lines.Count == 0
                ? $"Square {square}: no board modifiers."
                : $"Square {square}: read {reading.Lines.Count} "
                  + (reading.Lines.Count == 1 ? "modifier." : "modifiers."));
            AdvanceTarget();
            RefreshBoard();
            RefreshPanel();
            RebuildModifiers();
            RefreshProgress();
        }
        catch (Exception ex)
        {
            SetStatus($"Could not read the panel: {ex.Message}", bad: true);
        }
        finally
        {
            _capturing = false;
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
        RebuildModifiers();
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
        Capture(text, "copied");
    }

    /// <summary>
    /// Apply captured text to whatever is currently targeted.
    ///
    /// Shared by the clipboard watcher and the manual box on purpose: the game only
    /// supports Ctrl+C on things it treats as items, so a figurine may not be copyable at
    /// all. Both routes must land in exactly the same place, or the checklist would track
    /// one of them and not the other.
    /// </summary>
    private void Capture(string text, string how)
    {
        if (_targetIndex == 0)
        {
            SetStatus("Nothing selected — click a chart or a figurine first.", bad: true);
            return;
        }

        if (_target == Target.Square)
        {
            var lines = text.Replace("\r\n", "\n").Split('\n')
                            .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            _session.ApplySquareModifiers(_targetIndex, lines);
            SetStatus(lines.Count == 0
                ? $"Square {_targetIndex}: marked as having no modifiers."
                : $"Square {_targetIndex} {how}.");
        }
        else if (_target == Target.Figurine)
        {
            _session.ApplyFigurineText(_targetIndex, text);
            SetStatus($"Figurine {_targetIndex} {how}.");
        }
        else if (_session.ApplyChartText(_targetIndex, text))
        {
            SetStatus($"Chart {_targetIndex} {how}.");
        }
        else
        {
            SetStatus($"That text did not look like chart {_targetIndex}.", bad: true);
            return;
        }

        _solution = null;
        Persist();
        DetailBox.Clear();
        AdvanceTarget();
        RefreshBoard();
        RefreshPanel();
        RebuildModifiers();
        RefreshProgress();
    }

    private void OnApplyDetail(object sender, RoutedEventArgs e)
    {
        var text = DetailBox.Text.Trim();

        // An empty box is meaningful for a square -- it records "this one has no
        // modifiers", which is the truth about the centre of the board.
        if (text.Length == 0 && _target != Target.Square)
        {
            SetStatus("Nothing to apply.", bad: true);
            return;
        }
        Capture(text, "entered");
    }

    private void OnDetailKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // Ctrl+Enter rather than Enter: the box is multi-line, and tooltip text is too.
        if (e.Key != System.Windows.Input.Key.Enter) return;
        if ((System.Windows.Input.Keyboard.Modifiers
             & System.Windows.Input.ModifierKeys.Control) == 0) return;
        e.Handled = true;
        OnApplyDetail(sender, e);
    }

    /// <summary>Leave this one unread and move on, so one stubborn tooltip cannot stall the pass.</summary>
    private void OnSkipTarget(object sender, RoutedEventArgs e)
    {
        if (_targetIndex == 0) return;
        _skipped.Add((_target, _targetIndex));
        DetailBox.Clear();
        AdvanceTarget();
        RefreshBoard();
        RefreshPanel();
        RebuildModifiers();
    }

    private static string SafeClipboardText()
    {
        // The clipboard is shared and can be locked by another process mid-read.
        try { return Clipboard.ContainsText() ? Clipboard.GetText().Trim() : ""; }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException) { return ""; }
    }

    /// <summary>
    /// Move to the next thing needing detail: board squares first, then charts.
    ///
    /// Squares lead because they are the cheap half -- nine hovers and a hotkey, no
    /// clipboard involved -- and because a chart's worth depends on which square it lands
    /// on, so the board modifiers are what make the chart readings mean anything.
    /// </summary>
    private void AdvanceTarget()
    {
        var square = _session.SquaresAwaitingModifiers
            .FirstOrDefault(i => !_skipped.Contains((Target.Square, i)));
        if (square > 0)
        {
            SetTarget(Target.Square, square,
                      $"Hover square {square} in game, then press Ctrl+Alt+C.");
            return;
        }

        var chart = _session.ChartsAwaitingDetail
            .FirstOrDefault(i => !_skipped.Contains((Target.Chart, i)));
        if (chart > 0)
        {
            SetTarget(Target.Chart, chart,
                      $"Hover chart {chart} in game, Ctrl+C — or paste its text below.");
            return;
        }

        SetTarget(Target.Chart, 0, _skipped.Count == 0
            ? "Everything read. Pick a profile and solve."
            : $"Done, {_skipped.Count} skipped. Click one to come back to it.");
    }

    private void SetTarget(Target target, int index, string? status = null)
    {
        _target = target;
        _targetIndex = index;
        _skipped.Remove((target, index));

        TargetLabel.Text = index == 0 ? "Nothing selected" : target switch
        {
            Target.Chart => $"Chart {index}",
            Target.Figurine => $"Figurine {index}",
            _ => $"Square {index}",
        };

        if (index != 0)
        {
            // Show what is already captured, so the box doubles as an editor -- which is
            // how an OCR misread gets corrected.
            DetailBox.Text = target switch
            {
                Target.Figurine => _session.Figurines.GetValueOrDefault(index, ""),
                Target.Square => string.Join("\n",
                    _session.SquareModifiers.GetValueOrDefault(index) ?? []),
                _ => "",
            };
        }
        if (status is not null) SetStatus(status);
    }

    private void OnModifierSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ModifierList.SelectedItem is not ModifierRow row) return;
        if (row.Kind == ModifierRow.Sort.Tileset)
        {
            SetStatus($"{row.Title}: {row.Where}. Add a rule matching \"Area: {row.Title}\" "
                      + "to prefer it.");
            return;
        }
        SetTarget(row.Kind switch
                  {
                      ModifierRow.Sort.Square => Target.Square,
                      ModifierRow.Sort.Figurine => Target.Figurine,
                      _ => Target.Chart,          // VoyageWide points at its chart
                  },
                  row.Index,
                  $"{row.Title} selected — copy or type its text below.");
        RefreshBoard();
        RefreshPanel();
    }

    // ---- solving -----------------------------------------------------------------

    /// <summary>In flight, so a second Solve cancels and restarts instead of stacking.</summary>
    private CancellationTokenSource? _solving;

    private void OnSolve(object sender, RoutedEventArgs e) => _ = SolveAsync();

    /// <summary>
    /// Solve OFF the UI thread.
    ///
    /// The search deliberately burns one core for up to three seconds -- that is its
    /// budget, not a bug -- but doing it on the dispatcher froze the whole window with
    /// no sign anything was happening. It now runs on a worker against a SNAPSHOT of
    /// the session (ToState/FromState), so a clipboard capture landing mid-solve
    /// mutates the live session, never the one being searched. One solve at a time:
    /// clicking again cancels the running one, and the solver's token check unwinds it
    /// within a few thousand nodes.
    /// </summary>
    private async Task<bool> SolveAsync()
    {
        if (Profile is not { } profile) { SetStatus("No rule profile loaded.", bad: true); return false; }
        if (_session.Charts.Count == 0)
        {
            // The figurine ring reflects what has been READ, not what has been planned,
            // so it still has to be redrawn on the path where there is nothing to solve.
            RefreshBoard();
            SetStatus("Read the panel first.", bad: true);
            return false;
        }

        _solving?.Cancel();
        var cts = _solving = new CancellationTokenSource();
        SolveBusy.Visibility = Visibility.Visible;
        SetStatus("Solving \u2014 up to 3 s\u2026");

        var pin = _useSoulEater ? VoyageAlerts.SoulEaterChart(_session) : null;
        var snapshot = VoyageSession.FromState(_session.ToState());
        VoyageSolver.Solution solution;
        try
        {
            solution = await Task.Run(
                () => snapshot.Solve(profile, TimeSpan.FromSeconds(3), cts.Token, pin),
                CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            return false;   // superseded by a newer solve, which owns the indicator now
        }
        finally
        {
            if (_solving == cts) { SolveBusy.Visibility = Visibility.Collapsed; _solving = null; }
        }
        if (cts.IsCancellationRequested) return false;

        ApplySolution(solution, profile);
        return true;
    }

    /// <summary>The synchronous path, for the offscreen --render harness only: an async
    /// solve would return at the first await and the render would capture an unsolved
    /// board.</summary>
    private void SolveNow()
    {
        if (Profile is not { } profile) return;
        var pin = _useSoulEater ? VoyageAlerts.SoulEaterChart(_session) : null;
        ApplySolution(_session.Solve(profile, TimeSpan.FromSeconds(3), pinChart: pin), profile);
    }

    private void ApplySolution(VoyageSolver.Solution solution, VoyageProfile profile)
    {
        _solution = solution;
        _steps = _session.Plan(_solution);

        RefreshSummary();

        RefreshBoard();
        RefreshPanel();
        RebuildModifiers();

        if (_solution.IsEmpty)
        {
            SolveInfo.Text = "No legal layout: the charts read cannot tile the board.";
            SetStatus("No legal layout found.", bad: true);
            return;
        }

        // Say which it got. An anytime result is usually excellent but not proven, and
        // presenting "good" as "best" would be a lie the user cannot check.
        var note = _solution.StrandedCells.Count == 0
            ? "every square joined to the route"
            : "square " + string.Join(", ", _solution.StrandedCells
                  .Select(c => VoyagePlan.SquareNumber(c, _session.Layout.Cols)).Order())
              + " cut off from the route";

        SolveInfo.Text = $"Score {_solution.Value:0.##} · "
                       + (_solution.ProvedOptimal ? "proved best" : "best found in 3s")
                       + $" · {note}\n{_solution.NodesExplored:N0} "
                       + (_solution.NodesExplored == 1 ? "layout" : "layouts")
                       + $" checked in {_solution.Elapsed.TotalMilliseconds:0} ms";
        SolveInfo.Foreground = new SolidColorBrush(_solution.StrandedCells.Count == 0
            ? Color.FromRgb(0x6B, 0x5F, 0x4E)
            : Color.FromRgb(0xB8, 0x50, 0x3E));
        SetStatus($"Placed {_solution.Placements.Count} charts for \"{profile.Name}\".");
    }

    /// <summary>
    /// The voyage was run. Spend the placed charts, clear the board, and point the user
    /// at the one thing that must happen before the next plan: re-reading the border,
    /// which rerolls every voyage. Unplaced charts keep their panel numbers -- those
    /// point at physical panel positions.
    /// </summary>
    private void OnNextVoyage(object sender, RoutedEventArgs e)
    {
        _solving?.Cancel();   // the board it is solving is about to stop existing
        if (_steps.Count == 0)
        {
            SetStatus("Nothing to complete \u2014 solve a board first.", bad: true);
            return;
        }

        var spent = _session.CompleteVoyage(_steps.Select(st => st.ChartNumber));
        _solution = null;
        _steps = [];
        _summary.Clear();
        SolveInfo.Text = "";
        _skipped.Clear();
        _alertsOpen = false;

        // The squares are the read target again: the border is new.
        SetTarget(Target.Square, 0);
        RefreshPanel();
        RefreshBoard();
        RebuildModifiers();
        RefreshProgress();
        Persist();

        SetStatus($"Voyage complete \u2014 {spent} charts spent. "
                  + "The border has rerolled: hover each square and press Ctrl+Alt+C.");
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        _session = new VoyageSession();
        VoyageSessionState.Delete();
        _solution = null;
        _steps = [];
        _summary.Clear();
        SolveInfo.Text = "";
        _skipped.Clear();
        SetTarget(Target.Chart, 0);
        RefreshPanel();
        RefreshBoard();
        RebuildModifiers();
        RefreshProgress();
        SetStatus("Cleared.");
    }

    /// <summary>
    /// Fill the view from the reference capture, for looking at the layout.
    ///
    /// Real pixels, real session, real solver -- the only thing invented is the hover
    /// text, which cannot come from a screenshot. Useful for design work without opening
    /// the game, and as a smoke test that the whole chain still produces a plan.
    ///
    /// The screenshot is passed in rather than shipped: it is a capture of somebody's
    /// game, and a test fixture has no place in a distributable. Without one this still
    /// populates the figurines, so the board chrome can be inspected on its own.
    /// </summary>
    public void LoadSample(string? screenshot = null)
    {
        // A clean slate, and nothing written back: the sample must not inherit or clobber
        // a real session that happens to be on disk.
        _persistenceEnabled = false;
        _session = new VoyageSession();

        if (screenshot is not null && System.IO.File.Exists(screenshot))
        {
            using var bmp = new System.Drawing.Bitmap(screenshot);
            using var pixels = new BitmapPixels(bmp);
            _session.ApplyPanelRead(new ChartPanelReader().Read(pixels));
        }

        var samples = new[]
        {
            "Tempest Reach\nAnchorfield\nItem Quantity: +42%\nDead Man's Sulphur: +14\n"
                + "Voyage Modifier: 8% increased Quantity of Items found in all Voyage Areas",
            "Drowned Shelf\nAnchorfield\nMonster Pack Size: +26%\n"
                + "Adjacent Modifier: Adjacent Areas contain 4 additional Strongboxes",
            "Salt Barrens\nAbyssal Plain\nItem Rarity: +31%\nGold Found: +80%",

            // Two of the modifiers the banner exists for, so the sample exercises that
            // path rather than only the ordinary one.
            "Sunken Reach\nAbyssal Plain\nItem Quantity: +18%\n"
                + "Voyage Modifier: Players in all Voyage Areas have Soul Eater",
            "Drowned Shelf\nAnchorfield\nItem Rarity: +22%\n"
                + "Adjacent Modifier: Rare Monsters adjacent in Areas drop 2 additional Divine Orbs",
        };
        var i = 0;
        foreach (var index in _session.ByPanelIndex.Keys.Order().Take(samples.Length))
            _session.ApplyChartText(index, samples[i++]);

        // Squares, not figurines: that is what the Area Modifiers panel reports and what
        // the capture hotkey fills in.
        for (var square = 1; square <= 5; square++)
            _session.ApplySquareModifiers(square,
                [$"Areas contain {4 + square} additional packs of Sea Beasts",
                 square % 2 == 0 ? "Areas have 15% increased Quantity of Items found" : "Areas have 20% increased Monster Pack Size"]);

        RefreshPanel();
        RefreshBoard();
        RebuildModifiers();
        RefreshProgress();
        SolveNow();
    }

    /// <summary>
    /// The banner above the board.
    ///
    /// Rebuilt with the modifier list rather than with the solve, because an alert is
    /// about what you HAVE, not about where it ended up -- a Divine Orb line is worth
    /// knowing the moment it is read, and waiting for a solve to mention it would be
    /// telling you after the decision it should have informed.
    /// </summary>
    private void RefreshAlerts()
    {
        _alerts.Clear();

        var soulEater = VoyageAlerts.SoulEaterChart(_session);
        foreach (var alert in VoyageAlerts.Scan(_session))
        {
            // The button belongs on the row that explains it. A control somewhere else
            // labelled "Use Soul Eater" would be a second thing to find and connect up.
            var offersButton = soulEater is not null && alert.ChartIndex == soulEater;
            _alerts.Add(new AlertRow(alert, offersButton, _useSoulEater));
        }

        // The toggle NAMES what it is hiding. A chevron reading "2 modifiers" would make
        // finding out whether they matter cost a click every time; "Divine Orbs · Soul
        // Eater" answers the question that would have prompted the click.
        AlertToggle.Visibility = _alerts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (_alerts.Count == 0) _alertsOpen = false;
        else
        {
            // Named, but bounded. Three is where a label stops reading as a phrase and
            // starts reading as a list that has outgrown its button.
            var names = _alerts.Take(3).Select(a => a.Headline).ToList();
            if (_alerts.Count > names.Count) names.Add($"+{_alerts.Count - names.Count} more");

            AlertToggle.Content = string.Join(" · ", names)
                                  + (_alertsOpen ? "  \u25B4" : "  \u25BE");
            AlertToggle.Foreground = _alerts[0].Accent;
        }

        AlertPanel.Visibility = _alertsOpen && _alerts.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>
    /// Open or close the banner.
    ///
    /// It lies OVER the board rather than above it, so opening one does not move the other
    /// -- the square numbers stay where they were while the modifier that concerns them is
    /// being read.
    /// </summary>
    private void OnToggleAlerts(object sender, RoutedEventArgs e)
    {
        _alertsOpen = !_alertsOpen;
        RefreshAlerts();
    }

    /// <summary>
    /// Take Soul Eater, or stop taking it.
    ///
    /// Solving again immediately is the point: the board is the answer to "given what I
    /// am willing to spend a square on", and having changed that, the old board answers a
    /// question no longer being asked.
    /// </summary>
    private async void OnToggleSoulEater(object sender, RoutedEventArgs e)
    {
        _useSoulEater = !_useSoulEater;
        RefreshAlerts();

        if (_steps.Count == 0)
        {
            SetStatus(_useSoulEater
                ? "Soul Eater will be placed on the cheapest square. Solve to see where."
                : "Soul Eater is no longer forced onto the board.");
            return;
        }

        // Awaited: the answer about WHERE it landed reads the new plan, which does not
        // exist until the background solve finishes.
        if (!await SolveAsync() || !_useSoulEater) return;
        var square = _steps.FirstOrDefault(st => st.ChartNumber == VoyageAlerts.SoulEaterChart(_session));
        if (square is not null) SetStatus($"Soul Eater forced onto square {square.Square}.");
        else SetStatus("Soul Eater could not be placed on this board.", bad: true);
    }

    /// <summary>
    /// What the solved board is worth.
    ///
    /// This replaced a list that repeated the plan -- nine rows of "square 1 <- chart 23"
    /// beside a board already showing exactly that at 34pt. Restating the answer is not
    /// information; what the board PAYS is, and none of it can be read off the squares.
    /// </summary>
    private void RefreshSummary()
    {
        _summary.Clear();
        if (_solution is null || _solution.IsEmpty) return;

        var board = new VoyageBoard(_session.Layout.Rows, _session.Layout.Cols);
        foreach (var placement in _solution.Placements) board.Place(placement);

        var numbers = _session.ByPanelIndex.ToDictionary(kv => kv.Value.Id, kv => kv.Key,
                                                         StringComparer.Ordinal);
        var summary = VoyageSummary.Build(board, numbers, _session.Layout.Cols);

        _summary.Add(SummaryRow.Heading(summary.Headline));

        // The order to run them in. The board says WHERE each chart goes; this says when,
        // and the two are different questions -- a square is entered from a neighbour that
        // opens onto it, and the valuable ones are worth reaching before the lanterns thin
        // out and before anything can go wrong.
        if (Profile is { } profile)
        {
            var route = _session.Route(profile, _solution);
            if (!route.IsEmpty)
            {
                _summary.Add(SummaryRow.Section("Route · from the bottom-left, best first"));
                _summary.Add(SummaryRow.Route(string.Join("  →  ", route.Squares)));
                if (route.Unreachable.Count > 0)
                    _summary.Add(SummaryRow.Detail(
                        "Never reached: square " + string.Join(", ", route.Unreachable)));
            }
        }

        foreach (var (stat, total) in summary.Stats)
            _summary.Add(SummaryRow.Stat(stat, $"{(total < 0 ? "" : "+")}{total:0.#}"));

        if (summary.VoyageWide.Count > 0)
        {
            _summary.Add(SummaryRow.Section(
                $"Voyage-wide · {summary.VoyageWide.Count} in effect"));
            foreach (var modifier in summary.VoyageWide)
                _summary.Add(SummaryRow.Detail(modifier));
        }

        if (summary.Adjacencies.Count > 0)
        {
            _summary.Add(SummaryRow.Section("Adjacency · reach decides the value"));
            foreach (var adjacency in summary.Adjacencies)
                _summary.Add(SummaryRow.Detail(
                    $"sq {adjacency.Square} · chart {adjacency.ChartNumber} — {adjacency.Modifier}",
                    $"×{adjacency.Reach}"));
        }

        var missing = _session.ChartsAwaitingDetail.Count;
        if (missing > 0)
            _summary.Add(SummaryRow.Detail(
                $"{missing} charts in the panel have no modifiers captured, so none of "
                + "their rewards are counted here."));
    }

    // ---- rendering ---------------------------------------------------------------

    private Border[,] _boardCells = new Border[0, 0];
    private Border[] _panelCells = [];
    private readonly Dictionary<int, Border> _figurineMarkers = new();

    private const double SquareSize = 138;
    private const double MarkerSize = 46;
    private const double MarkerScale = 1.25;

    /// <summary>
    /// Board plus the ring of figurines around it.
    ///
    /// The figurines belong ON the board, not in a list beside it. Each one buffs the ONE
    /// square it touches, so which square that is *is* the information -- reading "figurine
    /// 5: adjacent areas contain 8 packs" off a list tells you nothing about where to put
    /// the chart that wants it. Drawn in the perimeter ring, they answer that at a glance.
    ///
    /// Built in code rather than XAML because the count follows from the board size, the
    /// same reason BoardLayout derives it instead of hardcoding twelve.
    /// </summary>
    private void BuildBoard()
    {
        var rows = _session.Layout.Rows;
        var cols = _session.Layout.Cols;

        _boardCells = new Border[rows, cols];
        _figurineMarkers.Clear();
        BoardHost.Children.Clear();
        BoardHost.RowDefinitions.Clear();
        BoardHost.ColumnDefinitions.Clear();

        // One ring cell on each side, hence rows + 2.
        BoardHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(MarkerSize) });
        for (var r = 0; r < rows; r++)
            BoardHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(SquareSize) });
        BoardHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(MarkerSize) });

        BoardHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(MarkerSize) });
        for (var c = 0; c < cols; c++)
            BoardHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SquareSize) });
        BoardHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(MarkerSize) });

        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
            {
                var cell = new Border
                {
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x32, 0x2A)),
                    BorderThickness = new Thickness(1),
                    Background = new SolidColorBrush(Color.FromRgb(0x18, 0x15, 0x12)),
                    Margin = new Thickness(1),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Tag = r * cols + c + 1,
                };
                ToolTipService.SetInitialShowDelay(cell, 0);
                ToolTipService.SetShowDuration(cell, 120000);
                // Squares are the capture target for the Area Modifiers panel, so the
                // board is how you choose which one the hotkey applies to.
                cell.MouseLeftButtonUp += OnBoardSquareClicked;
                Grid.SetRow(cell, r + 1);
                Grid.SetColumn(cell, c + 1);
                _boardCells[r, c] = cell;
                BoardHost.Children.Add(cell);
            }
        }

        foreach (var slot in _session.Layout.Figurines)
        {
            if (RingPosition(slot, rows, cols) is not var (gridRow, gridCol)) continue;

            var marker = new Border
            {
                Background = Brushes.Transparent,   // still hit-testable, no chrome drawn
                Cursor = System.Windows.Input.Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Tag = slot.Index,
            };
            ToolTipService.SetInitialShowDelay(marker, 0);
            ToolTipService.SetShowDuration(marker, 120000);
            // Clicking a figurine aims the next Ctrl+C at it, so the board doubles as the
            // read-mode control -- the list is for reading the text back, not for driving.
            marker.MouseLeftButtonUp += OnFigurineMarkerClicked;
            Grid.SetRow(marker, gridRow);
            Grid.SetColumn(marker, gridCol);
            _figurineMarkers[slot.Index] = marker;
            BoardHost.Children.Add(marker);
        }

        RefreshBoard();
    }

    /// <summary>
    /// Where a figurine sits in the perimeter ring: outside the square it touches, on the
    /// edge it belongs to. Returns null when the slot names no cell to sit beside.
    /// </summary>
    private static (int Row, int Col)? RingPosition(
        BoardLayout.FigurineSlot slot, int rows, int cols)
    {
        if (slot.Adjacent.FirstOrDefault() is not { } near) return null;
        return slot.Edge switch
        {
            "top" => (0, near.Col + 1),
            "bottom" => (rows + 1, near.Col + 1),
            "left" => (near.Row + 1, 0),
            "right" => (near.Row + 1, cols + 1),
            _ => null,
        };
    }

    private void OnBoardSquareClicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: int square }) return;
        SetTarget(Target.Square, square,
                  $"Hover square {square} in game, then press Ctrl+Alt+C.");
        RefreshBoard();
        RefreshPanel();
        RebuildModifiers();
    }

    private void OnPanelCellClicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: int index }) return;
        if (!_session.ByPanelIndex.ContainsKey(index))
        {
            SetStatus($"Panel cell {index} is empty.", bad: true);
            return;
        }
        SetTarget(Target.Chart, index,
                  $"Chart {index} selected — copy or paste its text below.");
        RefreshBoard();
        RefreshPanel();
    }

    private void OnFigurineMarkerClicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: int index }) return;
        SetTarget(Target.Figurine, index, $"Figurine {index} selected — copy or type its text below.");
        RefreshBoard();
        RefreshPanel();
        RebuildModifiers();
    }

    private void RefreshBoard()
    {
        if (_boardCells.Length == 0) return;
        var cols = _session.Layout.Cols;
        RefreshFigurineMarkers();

        var stranded = _solution?.StrandedCells.ToHashSet() ?? [];

        for (var r = 0; r < _boardCells.GetLength(0); r++)
        {
            for (var c = 0; c < cols; c++)
            {
                var square = r * cols + c + 1;
                var step = _steps.FirstOrDefault(s => s.Square == square);
                var isStranded = stranded.Contains(new Cell(r, c));
                var isTarget = _target == Target.Square && _targetIndex == square;
                var hasModifiers = _session.SquareModifiers.ContainsKey(square);
                var cell = _boardCells[r, c];

                cell.Background = new SolidColorBrush(step is null
                    ? Color.FromRgb(0x0E, 0x0C, 0x0A)
                    : isStranded ? Color.FromRgb(0x22, 0x13, 0x10)
                    : Color.FromRgb(0x18, 0x14, 0x10));
                cell.BorderBrush = new SolidColorBrush(isTarget
                    ? Color.FromRgb(0xC9, 0xA2, 0x27)
                    : isStranded ? Color.FromRgb(0x6E, 0x2F, 0x25)
                    : hasModifiers ? Color.FromRgb(0x44, 0x5C, 0x38)
                    : step is null ? Color.FromRgb(0x1F, 0x1A, 0x15)
                    : Color.FromRgb(0x3A, 0x2E, 0x1C));
                cell.BorderThickness = new Thickness(isTarget ? 2 : 1);
                cell.Child = BoardSquareContent(square, step, isStranded);
                cell.ToolTip = SquareTip(square, step, isStranded);
            }
        }
    }

    /// <summary>
    /// Everything that applies to a square: what the chart placed there brings, and what
    /// the board gives it.
    ///
    /// Both, because that is the question being asked when you hover a solved square --
    /// "what do I actually get here?" -- and the answer is the sum of the two. Kept
    /// labelled so it stays obvious which half moves if the chart moves.
    /// </summary>
    private object SquareTip(int square, VoyagePlan.Step? step, bool stranded)
    {
        var lines = new List<string>();

        if (step is not null)
        {
            lines.Add($"— chart {step.ChartNumber} —");
            lines.AddRange(DescribeChart(step.Chart));
        }

        lines.Add(step is null ? "— board —" : "");
        lines.Add(step is null ? "" : "— board —");
        lines.RemoveAll(string.IsNullOrEmpty);

        if (_session.SquareModifiers.TryGetValue(square, out var mods))
            lines.AddRange(mods.Count == 0 ? ["No board modifiers on this square."] : mods);
        else if (_session.SquaresWithoutFigurines.Contains(square))
            lines.Add("No figurine reaches this square, so it never has board modifiers.");
        else
            lines.Add("Area modifiers not read yet.");

        return Tip(
            step is null ? $"Square {square}" : $"Square {square} · chart {step.ChartNumber}",
            // No shape and no rotation: the square is DRAWN, so its lines already say
            // which way round it goes and naming them is a caption on a picture.
            step is null
                ? "No chart yet — click to select, then hover it in game"
                : stranded ? "cut off from the route" : null,
            lines,
            haveDetail: true);
    }

    /// <summary>
    /// What a chart pays out, for hover.
    ///
    /// Rewards only. A rare chart carries a dozen affixes and most are monster difficulty
    /// -- "34% more Monster Life", "+29% Monster Physical Damage Reduction" -- which is
    /// not what you are asking when you look at a planned square. The held-back count is
    /// still reported, because silently showing three of twelve lines would read as a
    /// chart with three modifiers.
    /// </summary>
    private static IReadOnlyList<string> DescribeChart(Chart chart)
    {
        var lines = ChartRewards.Describe(chart).ToList();
        if (lines.Count == 0)
            return ["No rewards captured. Select it and copy its text."];

        var hidden = ChartRewards.DifficultyCount(chart);
        if (hidden > 0)
            lines.Add($"(+{hidden} monster {(hidden == 1 ? "modifier" : "modifiers")} not shown)");
        return lines;
    }

    private static bool HasCapturedDetail(Chart chart) =>
        !string.IsNullOrEmpty(chart.VoyageModifier) || !string.IsNullOrEmpty(chart.AdjacentModifier)
        || chart.Modifiers.Count > 0 || chart.StatLines().Any();

    /// <summary>
    /// Redraw each ring ornament in the state it is now in.
    ///
    /// A figurine only ever buffs the ONE square it sits beside, so what matters is where
    /// an unread one is, not how many are left. On the board that is answerable at a
    /// glance; in a list it is not.
    /// </summary>
    private void RefreshFigurineMarkers()
    {
        foreach (var slot in _session.Layout.Figurines)
        {
            if (!_figurineMarkers.TryGetValue(slot.Index, out var host)) continue;

            var text = _session.Figurines.GetValueOrDefault(slot.Index);
            var captured = !string.IsNullOrWhiteSpace(text);
            var state =
                _target == Target.Figurine && _targetIndex == slot.Index ? VoyageOrnament.State.Selected
                : captured ? VoyageOrnament.State.Captured
                : _skipped.Contains((Target.Figurine, slot.Index)) ? VoyageOrnament.State.Skipped
                : VoyageOrnament.State.Unread;

            host.Child = VoyageOrnament.Build(slot.Index, slot.Edge, state, MarkerScale);

            var squares = string.Join(", ", slot.Adjacent.Select(
                a => VoyagePlan.SquareNumber(a.ToCell(), _session.Layout.Cols)));
            host.ToolTip = Tip($"Figurine {slot.Index} · {slot.Edge}",
                               $"Buffs square {squares}",
                               captured ? [text!] : ["Not read yet. Click to select it."],
                               captured);
        }
    }

    /// <summary>A tooltip built the same way everywhere: title, subtitle, then lines.</summary>
    private static UIElement Tip(string title, string? subtitle,
                                 IEnumerable<string> lines, bool haveDetail)
    {
        var stack = new StackPanel { MaxWidth = 360 };
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontFamily = new FontFamily("Georgia"),
            FontSize = 13,
            Foreground = Brush(0xC9, 0xA2, 0x27),
        });
        if (!string.IsNullOrEmpty(subtitle))
            stack.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 11,
                Foreground = Brush(0x6B, 0x5F, 0x4E),
                Margin = new Thickness(0, 1, 0, 0),
            });

        var first = true;
        foreach (var line in lines)
        {
            stack.Children.Add(new TextBlock
            {
                Text = line,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = haveDetail ? Brush(0xE6, 0xDB, 0xC2) : Brush(0x94, 0x86, 0x6F),
                FontStyle = haveDetail ? FontStyles.Normal : FontStyles.Italic,
                Margin = new Thickness(0, first ? 8 : 3, 0, 0),
            });
            first = false;
        }
        return stack;
    }

    private static SolidColorBrush Brush(byte r, byte g, byte b) =>
        new(Color.FromRgb(r, g, b));

    /// <summary>
    /// What a board square shows.
    ///
    /// Before solving, the square number is a quiet label. AFTER solving it becomes the
    /// headline, because the plan is followed by matching numbers: every chart the plan
    /// uses is stamped with its square number over in the panel, so you find "5" there
    /// and drop it on the "5" here. Nine single digits beat sixty two-digit panel
    /// indices, and the chart number stays underneath for when you want it.
    /// </summary>
    private UIElement BoardSquareContent(int square, VoyagePlan.Step? step, bool stranded)
    {
        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (step is null)
        {
            stack.Children.Add(new TextBlock
            {
                Text = square.ToString(),
                FontFamily = new FontFamily("Georgia"),
                FontSize = 30,
                Foreground = Brush(0x3A, 0x30, 0x24),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            if (square == _session.StartSquare)
                stack.Children.Add(new TextBlock
                {
                    // "Voyages will always start in the bottom left Chart of the Voyage."
                    Text = "START",
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 9,
                    Foreground = Brush(0x5A, 0x50, 0x42),
                    HorizontalAlignment = HorizontalAlignment.Center,
                });
            return stack;
        }

        var accent = stranded
            ? Color.FromRgb(0xB8, 0x50, 0x3E)
            : Color.FromRgb(0xC9, 0xA2, 0x27);

        stack.Children.Add(Glyph(new ChartFace(step.Chart.Shape, step.Rotation), 56, accent));
        stack.Children.Add(new TextBlock
        {
            Text = square.ToString(),
            FontFamily = new FontFamily("Georgia"),
            FontSize = 34,
            Foreground = new SolidColorBrush(accent),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, -2, 0, 0),
        });
        stack.Children.Add(new TextBlock
        {
            Text = square == _session.StartSquare
                ? $"START · chart {step.ChartNumber}"
                : $"chart {step.ChartNumber}",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 10,
            Foreground = stranded ? Brush(0xB8, 0x50, 0x3E) : Brush(0x94, 0x86, 0x6F),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        if (stranded)
            stack.Children.Add(new TextBlock
            {
                Text = "stranded",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 10,
                Foreground = Brush(0xB8, 0x50, 0x3E),
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
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = i + 1,
            };
            // Skimming charts is the whole interaction; a half-second delay per hover
            // makes it feel broken.
            ToolTipService.SetInitialShowDelay(cell, 0);
            ToolTipService.SetShowDuration(cell, 120000);
            // Clickable so the pass is not strictly sequential: one chart that will not
            // copy should not force the other 59 to wait behind it.
            cell.MouseLeftButtonUp += OnPanelCellClicked;
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
                cell.Background = new SolidColorBrush(Color.FromRgb(0x0C, 0x0A, 0x09));
                cell.BorderBrush = new SolidColorBrush(Color.FromRgb(0x16, 0x13, 0x11));
                cell.BorderThickness = new Thickness(1);
                cell.Child = null;
                cell.ToolTip = null;
                continue;
            }

            var isPlanned = used.ContainsKey(index);
            // Not gated on read mode: manual entry works without it, and the highlight is
            // what tells you which chart the box below applies to.
            var isTarget = _target == Target.Chart && _targetIndex == index;
            var hasDetail = HasCapturedDetail(chart);

            cell.Background = new SolidColorBrush(isPlanned
                ? Color.FromRgb(0x2A, 0x21, 0x0E)
                : Color.FromRgb(0x16, 0x13, 0x11));
            cell.BorderBrush = new SolidColorBrush(isTarget
                ? Color.FromRgb(0xC9, 0xA2, 0x27)
                : isPlanned ? Color.FromRgb(0x8A, 0x6F, 0x22)
                : hasDetail ? Color.FromRgb(0x44, 0x5C, 0x38)
                : Color.FromRgb(0x24, 0x1D, 0x16));
            cell.BorderThickness = new Thickness(isTarget ? 2 : 1);

            var stack = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
            stack.Children.Add(new TextBlock
            {
                Text = index.ToString(),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 10,
                Foreground = new SolidColorBrush(isPlanned
                    ? Color.FromRgb(0x8A, 0x6F, 0x22)
                    : Color.FromRgb(0x6B, 0x5F, 0x4E)),
                HorizontalAlignment = HorizontalAlignment.Center,
            });

            if (isPlanned)
            {
                // The destination square, large. Once solved this is the only number
                // worth reading: find it here, drop it on the square with the same one.
                stack.Children.Add(new TextBlock
                {
                    Text = used[index].ToString(),
                    FontFamily = new FontFamily("Georgia"),
                    FontSize = 26,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xC9, 0xA2, 0x27)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, -3, 0, -3),
                });
            }
            else
            {
                stack.Children.Add(Glyph(new ChartFace(chart.Shape, 0), 24,
                    hasDetail ? Color.FromRgb(0x86, 0xA8, 0x6A)
                    : Color.FromRgb(0x7A, 0x6E, 0x5C)));
            }

            stack.Children.Add(new TextBlock
            {
                Text = chart.AreaLevel > 0 ? chart.AreaLevel.ToString() : "··",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromRgb(0x7A, 0x6E, 0x5C)),
                HorizontalAlignment = HorizontalAlignment.Center,
            });

            cell.Child = stack;
            cell.ToolTip = Tip(
                string.IsNullOrWhiteSpace(chart.Name) ? $"Chart {index}" : $"Chart {index} · {chart.Name}",
                isPlanned ? $"Goes on square {used[index]}" : "Not in the plan",
                DescribeChart(chart), hasDetail);
        }

        PanelHeader.Text = _session.Charts.Count == 0
            ? "C H A R T S"
            : $"C H A R T S      {_session.Charts.Count} read · "
              + $"{_session.ChartsAwaitingDetail.Count} without modifiers";
    }

    /// <summary>
    /// The rotation the panel read implied. Charts carry a shape, not a face, so the
    /// panel mirror shows the base orientation until the solver assigns one.
    /// </summary>
    private static ChartFace FaceOf(Chart chart) => new(chart.Shape, 0);

    /// <summary>
    /// Everything captured, in one list: the twelve figurines and every chart that has
    /// modifier text.
    ///
    /// One list rather than two because they answer the same question -- "what is actually
    /// affecting this board?" -- and splitting them by where the text came from would be
    /// organising the interface around the app's plumbing instead of the user's question.
    /// </summary>
    private void RebuildModifiers()
    {
        RefreshAlerts();
        var previous = (ModifierList.SelectedItem as ModifierRow)?.Key;
        _modifiers.Clear();

        // Only charts that are actually ON the board. A modifier from a chart sitting
        // unused in the panel applies to nothing -- listing it would describe a voyage
        // that is not the one being planned. Before a solve there is no board, so no
        // chart modifiers appear at all.
        var planned = _steps.ToDictionary(st => st.ChartNumber, st => st.Square);

        // Voyage-wide first: they apply wherever their chart sits, so unlike everything
        // else here their position is the one thing about them that does not matter.
        foreach (var (panelIndex, modifier) in _session.VoyageWideModifiers)
        {
            if (!planned.ContainsKey(panelIndex)) continue;
            _modifiers.Add(new ModifierRow(
                ModifierRow.Sort.VoyageWide, panelIndex,
                "Voyage-wide", $"chart {panelIndex}",
                modifier, captured: true,
                selected: _target == Target.Chart && _targetIndex == panelIndex));
        }

        // Tilesets next. There is no published list of them and they are not equal --
        // some are thick with Sunken Loot -- so showing which you hold is the only way to
        // find out what is worth writing a rule for.
        foreach (var (tileset, count) in _session.Tilesets)
        {
            var scored = Profile?.ScoreText([$"Area: {tileset}"]) ?? 0;
            _modifiers.Add(new ModifierRow(
                ModifierRow.Sort.Tileset, 0,
                tileset, count == 1 ? "1 chart" : $"{count} charts",
                scored != 0
                    ? $"Worth {scored:0.##} to \"{Profile?.Name}\""
                    : "No rule for this tileset yet",
                captured: scored != 0,
                selected: false,
                muted: scored == 0));
        }

        // Then squares: what the game aggregates, and what the solver places against.
        var unreachable = _session.SquaresWithoutFigurines.ToHashSet();
        for (var square = 1; square <= _session.Layout.Rows * _session.Layout.Cols; square++)
        {
            var read = _session.SquareModifiers.TryGetValue(square, out var lines);

            // Three states, not two: unreachable squares are not work left to do, so they
            // must not read as "Not read" and sit there looking unfinished.
            var text = unreachable.Contains(square) ? "No figurine reaches this square"
                : !read ? "Not read"
                : lines!.Count == 0 ? "No modifiers"
                : string.Join("\n", lines!);

            _modifiers.Add(new ModifierRow(
                ModifierRow.Sort.Square, square,
                $"Square {square}", "board", text,
                captured: read || unreachable.Contains(square),
                selected: _target == Target.Square && _targetIndex == square,
                muted: unreachable.Contains(square)));
        }

        foreach (var (index, chart) in _session.ByPanelIndex.OrderBy(kv => kv.Key))
        {
            if (!planned.TryGetValue(index, out var square)) continue;
            if (!HasCapturedDetail(chart)) continue;

            var rewards = ChartRewards.Describe(chart);
            if (rewards.Count == 0) continue;

            _modifiers.Add(new ModifierRow(
                ModifierRow.Sort.Chart, index,
                string.IsNullOrWhiteSpace(chart.Name) ? $"Chart {index}" : $"Chart {index} · {chart.Name}",
                $"sq {square}",
                string.Join("\n", rewards),
                captured: true,
                selected: _target == Target.Chart && _targetIndex == index));
        }

        var unread = _session.SquaresAwaitingModifiers.Count;
        ModifierHeader.Text = unread > 0
            ? $"M O D I F I E R S      {unread} squares unread"
            : _steps.Count == 0
                ? "M O D I F I E R S      solve to see chart rewards"
                : "M O D I F I E R S";

        if (previous is not null)
            ModifierList.SelectedItem = _modifiers.FirstOrDefault(m => m.Key == previous);
    }

    private void RefreshProgress()
    {
        var fraction = _session.ReadProgress;
        Progress.Text = $"{fraction:P0}";
        ProgressFill.Width = 90 * Math.Clamp(fraction, 0, 1);
    }

    private void SetStatus(string text, bool bad = false)
    {
        Status.Text = text;
        Status.Foreground = new SolidColorBrush(bad
            ? Color.FromRgb(0xB8, 0x50, 0x3E)
            : Color.FromRgb(0x94, 0x86, 0x6F));
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

    /// <summary>
    /// One row in the modifier list: a figurine or a chart, and the text captured for it.
    /// </summary>
    public sealed class ModifierRow
    {
        public enum Sort { VoyageWide, Tileset, Square, Figurine, Chart }

        public ModifierRow(Sort kind, int index, string title, string where,
                           string text, bool captured, bool selected, bool muted = false)
        {
            Kind = kind;
            Index = index;
            Title = title;
            Where = where;
            Text = text;
            Captured = captured;
            Selected = selected;
            Muted = muted;
        }

        public Sort Kind { get; }
        public int Index { get; }
        public string Title { get; }

        /// <summary>Which board square this affects — the reason position matters.</summary>
        public string Where { get; }

        public string Text { get; }
        public bool Captured { get; }
        public bool Selected { get; }

        /// <summary>Nothing to do here — shown for completeness, not as outstanding work.</summary>
        public bool Muted { get; }

        /// <summary>Stable identity, so a rebuild can restore the selection.</summary>
        public string Key => $"{Kind}:{Index}";

        public Brush Accent => new SolidColorBrush(
            Selected ? Color.FromRgb(0xC9, 0xA2, 0x27)
            : Kind == Sort.VoyageWide ? Color.FromRgb(0xC9, 0xA2, 0x27)
            : Kind == Sort.Tileset && Captured ? Color.FromRgb(0x8A, 0x6F, 0x22)
            : Muted ? Color.FromRgb(0x2A, 0x20, 0x18)
            : Captured ? Color.FromRgb(0x86, 0xA8, 0x6A)
            : Color.FromRgb(0x2A, 0x20, 0x18));

        public Brush TextBrush => new SolidColorBrush(
            Muted ? Color.FromRgb(0x5A, 0x50, 0x42)
            : Captured ? Color.FromRgb(0xE6, 0xDB, 0xC2)
            : Color.FromRgb(0x6B, 0x5F, 0x4E));

        public Brush RowBackground => new SolidColorBrush(
            Selected ? Color.FromRgb(0x1F, 0x1A, 0x10) : Color.FromRgb(0x13, 0x11, 0x10));
    }

    /// <summary>One line of the summary. Shape carries the meaning, so it is prebuilt.</summary>
    /// <summary>One banner row. Colour carries the kind, so the label never has to.</summary>
    public sealed class AlertRow
    {
        public AlertRow(VoyageAlert alert, bool offersButton, bool inUse)
        {
            Headline = alert.Headline;
            Detail = alert.Detail;
            Where = alert.Where;
            Accent = alert.Kind == AlertKind.Jackpot
                ? new SolidColorBrush(Color.FromRgb(0xC9, 0xA2, 0x27))    // brass
                : new SolidColorBrush(Color.FromRgb(0xB8, 0x50, 0x3E));   // rust
            ActionVisibility = offersButton ? Visibility.Visible : Visibility.Collapsed;
            ActionLabel = inUse ? "Drop Soul Eater" : "Use Soul Eater";
        }

        public string Headline { get; }
        public string Detail { get; }
        public string Where { get; }
        public Brush Accent { get; }
        public string ActionLabel { get; }
        public Visibility ActionVisibility { get; }
    }

    public sealed class SummaryRow
    {
        private SummaryRow(string label, string value, double size, string font, Brush brush)
        {
            Label = label;
            Value = value;
            Size = size;
            Font = font;
            Brush = brush;
        }

        public string Label { get; }
        public string Value { get; }
        public double Size { get; }
        public string Font { get; }
        public Brush Brush { get; }
        public Brush ValueBrush => new SolidColorBrush(Color.FromRgb(0xC9, 0xA2, 0x27));

        private static Brush Of(byte r, byte g, byte b) => new SolidColorBrush(Color.FromRgb(r, g, b));

        public static SummaryRow Heading(string text) =>
            new(text, "", 15, "Georgia", Of(0xE6, 0xDB, 0xC2));

        public static SummaryRow Stat(string name, string total) =>
            new(name, total, 13, "Segoe UI", Of(0xE6, 0xDB, 0xC2));

        public static SummaryRow Section(string text) =>
            new(text, "", 11, "Georgia", Of(0x6B, 0x5F, 0x4E));

        public static SummaryRow Detail(string text, string value = "") =>
            new(text, value, 12, "Segoe UI", Of(0x94, 0x86, 0x6F));

        public static SummaryRow Route(string order) =>
            new(order, "", 15, "Georgia", Of(0xC9, 0xA2, 0x27));
    }
}
