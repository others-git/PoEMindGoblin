using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

    /// <summary>Whether the alert banner is dropped down over the board.</summary>
    private bool _alertsOpen;
    private bool _weightsOpen;
    private readonly VoyageWeightStore _weights = new();
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

    /// <summary>Steps still ahead of the slurp cursor: border FIGURINES first (hover +
    /// tooltip OCR, no key injection at all), then charts (hover + one Ctrl+C).
    /// Non-null while the Slurp toggle is armed.</summary>
    private Queue<(bool IsFigurine, int Index)>? _slurp;
    private string _lastClipboard = "";

    /// <summary>Items deliberately passed over, so the checklist cannot stall on one of them.</summary>
    private readonly HashSet<(Target, int)> _skipped = new();

    /// <summary>Fades the alch reminder out on its own; a click beats it to it.</summary>
    private readonly DispatcherTimer _alchTimer =
        new() { Interval = TimeSpan.FromSeconds(6) };

    /// <summary>Same, for the post-dive "read the new border" reminder.</summary>
    private readonly DispatcherTimer _boardNoteTimer =
        new() { Interval = TimeSpan.FromSeconds(8) };

    public VoyageView()
    {
        InitializeComponent();
        _alchTimer.Tick += (_, _) => { _alchTimer.Stop(); AlchNote.IsOpen = false; };
        _boardNoteTimer.Tick += (_, _) => { _boardNoteTimer.Stop(); BoardNote.IsOpen = false; };

        ModifierList.ItemsSource = _modifiers;
        SummaryList.ItemsSource = _summary;
        AlertList.ItemsSource = _alerts;

        // Reading a board is slow -- a screenshot, nine hovers, then a hover per chart.
        // Throwing that away on exit would make the tool worse than doing it by hand.
        var (restored, state) = VoyageSession.Restore();
        _session = restored;
        _restoredProfile = state?.Profile;

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
        ReportGameResolution();

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
            _session.Save(profile: Profile?.Name);
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

    /// <summary>
    /// Detect the game's resolution AT LAUNCH and say so, rather than at the first
    /// capture and silently.
    ///
    /// Every calibrated coordinate is scaled to this number, so being wrong about it
    /// looks like "the reader found nothing" — a symptom that sends people to the
    /// calibrate window for a problem calibration cannot fix. Saying it up front turns
    /// that into something the user can see before they press anything.
    /// </summary>
    private void ReportGameResolution()
    {
        try
        {
            var bounds = ScreenCapture.ResolveGameBounds();
            ResolutionHint.Text = bounds.Describe;
            ResolutionHint.Foreground = new SolidColorBrush(
                bounds.Source == ScreenCapture.BoundsSource.PrimaryScreen
                    ? Color.FromRgb(0xB8, 0x50, 0x3E)     // a guess, and it should look like one
                    : Color.FromRgb(0x6B, 0x5F, 0x4E));
            ResolutionHint.ToolTip = bounds.Source switch
            {
                ScreenCapture.BoundsSource.Detected =>
                    "Read from the game's own window — nothing to configure.",
                ScreenCapture.BoundsSource.Pinned =>
                    "The resolution you set under Calibrate. Switch it back to Auto once "
                    + "the game is running.",
                _ => "The game was not found, so this is the primary screen — right only "
                     + "if the game is fullscreen on it. Set your resolution under Calibrate.",
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            ResolutionHint.Text = "";
        }
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

    /// <summary>The active profile with the user's category sliders applied -- what
    /// every scoring path uses, so the sliders are never silently ignored.</summary>
    private VoyageProfile? Tuned =>
        Profile is { } p ? WeightCategories.Blended(p, _weights.For(p.Name), _rules.Profiles) : null;

    private void OnProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Profile is { Description: { } d } && !string.IsNullOrWhiteSpace(d)) SetStatus(d);
        if (IsLoaded) Persist();          // so the app reopens on the same objective
        if (IsLoaded) RefreshWeights();   // sliders belong to the profile being tuned
    }

    // ---- category weight sliders ---------------------------------------------------

    /// <summary>Render-harness hook: show the weights panel in an offscreen shot.</summary>
    public void OpenWeightsForRender()
    {
        _weightsOpen = true;
        RefreshWeights();
    }

    private void OnToggleWeights(object sender, RoutedEventArgs e)
    {
        _weightsOpen = !_weightsOpen;
        RefreshWeights();
    }

    private void OnResetWeights(object sender, RoutedEventArgs e)
    {
        if (Profile is not { } profile) return;
        _weights.Reset(profile.Name);
        RefreshWeights();
        SetStatus($"Weights for \"{profile.Name}\" back to shipped \u2014 Solve to replan.");
    }

    /// <summary>
    /// Rebuild the slider rows for the active profile: one per category it has rules
    /// in, 1-20 with the shipped weights at 10. Saving happens on every tick; the
    /// re-solve waits for the drag to END, because a solve per tick would race the
    /// solver against the user's hand.
    /// </summary>
    private void RefreshWeights()
    {
        WeightsBtn.Content = Profile is { } p0 && _weights.AnyTuned(p0.Name)
            ? "Weights ●" : "Weights";
        WeightsPanel.Visibility = _weightsOpen && Profile is not null
            ? Visibility.Visible : Visibility.Collapsed;
        if (!_weightsOpen || Profile is not { } profile) return;

        WeightsTitle.Text = $"W E I G H T S   ·   {profile.Name}";
        WeightsList.Children.Clear();
        foreach (var category in WeightCategories.SliderCategories(profile))
        {
            var def = WeightCategories.DefaultFor(profile, category);
            var value = _weights.Get(profile.Name, category, def);
            var borrowed = def == 0;

            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(146) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });

            // The LABEL, not the identifier. A slider reading "LootLock" told the user
            // nothing about the mod that deletes their equipment, and one reading "Meta"
            // or "Player" was worse -- so every row names itself in words and explains
            // itself on hover. The tooltip is what makes a weight safe to move: several
            // of these want a NEGATIVE weight and nothing on the row could say so.
            var label = new TextBlock
            {
                Text = WeightCategories.LabelOf(category),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,   // the tip carries the rest
                // Hit-testable across the whole column rather than only where the glyphs
                // are: a tooltip you have to find the letters of is one nobody reads.
                Background = Brushes.Transparent,
            };
            ToolTipService.SetInitialShowDelay(label, 0);
            ToolTipService.SetShowDuration(label, 120000);
            var description = WeightCategories.DescriptionOf(category);
            if (description.Length > 0 || borrowed)
                label.ToolTip = Tip(
                    WeightCategories.LabelOf(category),
                    borrowed
                        ? $"Not part of \"{profile.Name}\" — raising it prices these mods "
                          + "at catalog value × the slider"
                        : $"Weighted by \"{profile.Name}\"",
                    description.Length > 0 ? [description] : [],
                    haveDetail: true);
            row.Children.Add(label);

            var readout = new TextBlock
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            Grid.SetColumn(readout, 2);
            row.Children.Add(readout);

            void Paint(int v)
            {
                readout.Text = v == 0 ? "off" : $"×{WeightCategories.Multiplier(v):0.0}";
                readout.Foreground = new SolidColorBrush(v == def
                    ? Color.FromRgb(0x6B, 0x5F, 0x4E) : Color.FromRgb(0xC8, 0xAA, 0x6E));
                label.Foreground = new SolidColorBrush(v == 0
                    ? Color.FromRgb(0x6B, 0x5F, 0x4E) : Color.FromRgb(0xB8, 0xB1, 0xA2));
            }
            Paint(value);

            var slider = new Slider
            {
                Minimum = WeightCategories.Min,
                Maximum = WeightCategories.Max,
                Value = value,
                IsSnapToTickEnabled = true,
                TickFrequency = 1,
                Margin = new Thickness(8, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Style = TryFindResource("Tuner") as Style,
            };
            var cat = category;   // capture per row, not the loop variable
            var catDefault = def;
            slider.ValueChanged += (_, args) =>
            {
                var v = (int)Math.Round(args.NewValue);
                _weights.Set(profile.Name, cat, v, catDefault);
                Paint(v);
                WeightsBtn.Content = _weights.AnyTuned(profile.Name) ? "Weights ●" : "Weights";
            };
            slider.LostMouseCapture += (_, _) =>
            {
                if (_steps.Count > 0)
                    SetStatus("Weights changed \u2014 Solve to replan under them.");
            };
            Grid.SetColumn(slider, 1);
            row.Children.Add(slider);

            WeightsList.Children.Add(row);
        }
    }

    private void OnCalibrate(object sender, RoutedEventArgs e)
    {
        // The in-app calibrator needs a capture to draw over; until one exists the
        // best help is saying how to make one.
        if (!File.Exists(CalibrationWindow.CapturePath))
        {
            SetStatus("Press Identify Charts once first \u2014 calibration draws over that capture.",
                      bad: true);
            return;
        }
        try
        {
            var window = new CalibrationWindow { Owner = Window.GetWindow(this) };
            if (window.ShowDialog() == true)
                SetStatus("Calibration saved \u2014 press Identify Charts to re-read with it.");
        }
        catch (Exception ex)
        {
            SetStatus($"Calibration window failed: {ex.Message}", bad: true);
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

    private void OnReadPanel(object sender, RoutedEventArgs e) => IdentifyChartsNow();

    /// <summary>
    /// Screenshot the game and decode the chart panel into the session. Shared by the
    /// Identify Charts button and the slurp's arming step, so both agree on what a
    /// read is. True when at least one chart decoded.
    /// </summary>
    private bool IdentifyChartsNow()
    {
        try
        {
            // The GAME's rectangle, not the screen's: the calibration is relative to
            // what the game draws, so capturing anything else shifts every coordinate.
            var bounds = ScreenCapture.ResolveGameBounds();
            using var bmp = ScreenCapture.CaptureRegion(bounds.Rect);
            // Always keep the last capture: when a read goes wrong, the pixels ARE the
            // bug report -- VoyageProbe decodes this file offline, overlay and all.
            try { bmp.Save(MindGoblin.Core.SettingsFolder.FileIn("last-identify.png")); }
            catch (System.Runtime.InteropServices.ExternalException) { }
            var pixels = new BitmapPixels(bmp);
            var cells = new ChartPanelReader(
                ChartPanelReader.Options.Load(), LevelReader.LoadWithUserTemplates()).Read(pixels);

            if (cells.Count == 0)
            {
                SetStatus("No charts found. Is the Voyage screen open on the primary monitor?",
                          bad: true);
                return false;
            }

            _session.ApplyPanelRead(cells);
            _solution = null;
            _summary.Clear();
            Persist();
            RefreshPanel();
            RefreshBoard();
            RefreshProgress();

            var unread = cells.Count(c => c.Level is null);
            SetStatus((unread == 0
                    ? $"Read {cells.Count} charts"
                    : $"Read {cells.Count} charts; {unread} had an unreadable level")
                + $" · {bounds.Describe}.");
            return true;
        }
        catch (Exception ex)
        {
            SetStatus($"Capture failed: {ex.Message}", bad: true);
            return false;
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
            // The lambda is async void once it awaits, so it carries its own guard:
            // HotkeyService cannot catch what escapes past the first await, and an
            // unhandled dispatcher exception ends the app rather than the capture.
            () => Dispatcher.Invoke(async () =>
            {
                try { await CaptureAreaModifiersAsync(); }
                catch (Exception ex) { SetStatus($"Capture failed: {ex.Message}", bad: true); }
            }));

        UpdateCaptureHint();
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
            if (!await ReadAreaPanelInto(square, hoverHint: $"Hover square {square} in game "
                                                           + "before pressing Ctrl+Alt+C."))
                return;
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

    /// <summary>
    /// The OCR core both capture routes share: screenshot the Area Modifiers panel and
    /// record what it says about ONE square. The hotkey route and the slurp must land
    /// in exactly the same place, or the checklist would track one and not the other.
    /// </summary>
    private async Task<bool> ReadAreaPanelInto(int square, string hoverHint)
    {
        var options = AreaModifierPanel.Options.Load();
        var game = ScreenCapture.ResolveGameBounds().Rect;
        // Fractions of the GAME's rectangle, then offset onto the screen: a windowed
        // game's panel is at the same fraction of its client area and nowhere near the
        // same fraction of the desktop.
        var (x, y, w, h) = options.ToPixels(game.Width, game.Height);
        var raw = await ScreenOcr.ReadRegionAsync(
            new System.Drawing.Rectangle(game.X + x, game.Y + y, w, h), options.Upscale);

        var reading = AreaModifierPanel.Read(raw);
        if (!reading.IsRead)
        {
            // An empty panel has three meanings and only one of them is an error the
            // user can fix by hovering. Saying which is the difference between a
            // useful message and a wrong one.
            SetStatus(reading.State == AreaModifierPanel.PanelState.Placeholder
                ? hoverHint
                : "Could not find the Area Modifiers panel — open the Voyage screen, "
                  + "or adjust the region under Calibrate.",
                bad: true);
            return false;
        }

        _session.ApplySquareModifiers(square, reading.Lines);
        _solution = null;
        Persist();
        SetStatus(reading.Lines.Count == 0
            ? $"Square {square}: no board modifiers."
            : $"Square {square}: read {reading.Lines.Count} "
              + (reading.Lines.Count == 1 ? "modifier." : "modifiers."));
        return true;
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
            if (_targetIndex == 0) _clipboardPoll.Stop();
            SetStatus("Guided pass off. Click any chart or square to re-read just it.");
        }
        UpdateCaptureHint();
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
        if (_slurp is not null) return;   // the F9 handler owns the clipboard mid-slurp
        Capture(text, "copied");
    }

    // ---- step-through slurp: F9 captures one chart per press -----------------------

    private int? _slurpHotkey;

    /// <summary>
    /// Arm or disarm the step-through slurp. Each F9 press -- pressed by the user, in
    /// the game -- performs ONE hover-and-copy on the next read cell: the TradeMacro
    /// shape, one action per keypress, nothing on a timer (see GameInput). The modal
    /// coaches which chart the next press will take and tracks the pass.
    /// </summary>
    private void OnSlurpChanged(object sender, RoutedEventArgs e)
    {
        if (SlurpBtn.IsChecked == true)
        {
            // Identify first, always: the slurp's chart list is only as fresh as the
            // last panel read, and ApplyPanelRead merges -- survivors keep their
            // detail, new cells join as unidentified, spent cells drop. If the game
            // is not on screen the identify says so and the slurp stands down.
            if (!IdentifyChartsNow())
            {
                SlurpBtn.IsChecked = false;
                return;
            }

            // Figurines lead: the border rerolls every voyage, and the figurine
            // TOOLTIP is the authoritative text -- hovering the tile misses its
            // adjacent-scope lines entirely (field-confirmed). Only unread ones,
            // so a partial pass resumes and a post-dive slurp is the fresh ring.
            var pending = new List<(bool IsFigurine, int Index)>();
            if (ScreenOcr.IsAvailable)
                pending.AddRange(_session.FigurinesAwaitingDetail.Select(f => (true, f.Index)));
            // Only charts still AWAITING detail: after Dive Again the survivors keep
            // their data, so a post-dive slurp is squares-only -- and re-arming after
            // a partial pass resumes exactly the gaps. Re-read a changed chart by
            // clicking it and copying, as ever.
            pending.AddRange(_session.ChartsAwaitingDetail.Order().Select(i => (false, i)));
            if (pending.Count == 0)
            {
                SlurpBtn.IsChecked = false;
                SetStatus("Nothing to slurp \u2014 the border is read and every chart has its details.", bad: true);
                return;
            }
            if (Window.GetWindow(this) is not { } window)
            {
                SlurpBtn.IsChecked = false;
                return;
            }
            _hotkeys ??= new HotkeyService(window);
            _slurpHotkey ??= _hotkeys.Register(
                System.Windows.Input.Key.F9, HotkeyService.Mod.None, OnSlurpKey);
            if (_slurpHotkey is null)
            {
                SlurpBtn.IsChecked = false;
                SetStatus("F9 is taken by another program \u2014 free it and arm the slurp again.", bad: true);
                return;
            }
            _slurp = new Queue<(bool, int)>(pending);
            _slurpTotal = pending.Count;
            _lastClipboard = SafeClipboardText();     // ignore whatever is already there
            RefreshSlurpPanel();
            SetStatus($"Slurp armed: {pending.Count(p => p.IsFigurine)} figurines, "
                      + $"{pending.Count(p => !p.IsFigurine)} charts \u2014 switch to PoE and press F9."
                      + (ScreenOcr.IsAvailable ? "" : " (No OCR pack: figurines skipped.)"));
        }
        else
        {
            StopSlurp("Slurp off.");
        }
        UpdateCaptureHint();
    }

    private int _slurpTotal;

    private void RefreshSlurpPanel()
    {
        if (_slurp is not { } queue || queue.Count == 0)
        {
            SlurpPanel.Visibility = Visibility.Collapsed;
            return;
        }
        var done = _slurpTotal - queue.Count;
        var (isFigurine, next) = queue.Peek();
        SlurpInfo.Text = $"Press F9 in game \u2014 captures "
                       + (isFigurine ? $"figurine {next} ({FigurineEdge(next)})" : $"chart {next}")
                       + $" ({done} of {_slurpTotal} done). Keep the Voyage screen open and unscrolled.";
        SlurpPanel.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// One F9 press: hover the next cell, one Ctrl+C, read what arrived. Runs inside
    /// the press, never from a timer; the delays only give the game time to draw the
    /// tooltip and fill the clipboard for THIS press's single copy.
    /// </summary>
    /// <summary>One capture is in flight: further F9s and Skips must wait for it.
    /// The handler awaits on the UI thread, so without this a rapid second press
    /// interleaved at an await -- double injection, and a blind dequeue that could
    /// silently drop a step Skip had already advanced past.</summary>
    private bool _slurpBusy;

    private async void OnSlurpKey()
    {
        if (_slurpBusy) return;
        if (_slurp is not { Count: > 0 })
        {
            FinishSlurp();
            return;
        }
        var (isFigurine, index) = _slurp.Peek();
        _slurpBusy = true;
        try
        {
            await SlurpStep(isFigurine, index);
        }
        catch (Exception ex)
        {
            // async void: HotkeyService's try/catch only covers the synchronous part, so
            // anything thrown past the first await is unhandled on the dispatcher and
            // takes the app down mid-pass. The step is left queued -- F9 retries it.
            SetStatus($"Figurine/chart {index} failed: {ex.Message} — F9 retries it, or Skip.",
                      bad: true);
            RefreshSlurpPanel();
        }
        finally
        {
            _slurpBusy = false;
        }
    }

    private async Task SlurpStep(bool isFigurine, int index)
    {
        if (isFigurine)
        {
            await SlurpFigurine(index);
            return;
        }
        SlurpInfo.Text = $"Copying chart {index}…";   // the press visibly landed
        // ForScreen, not the raw calibration: the reader scales its own copy to the
        // screen it decoded, so hovering the unscaled coordinates put the cursor on a
        // different cell than the one the plan numbered on any other resolution.
        var game = ScreenCapture.ResolveGameBounds().Rect;
        var o = ChartPanelReader.Options.Load().ForScreen(game.Width, game.Height);
        var (row, col) = ((index - 1) / o.Cols, (index - 1) % o.Cols);
        // Client-relative like everything else, plus the window origin -- this is the
        // one place that has to know where on the DESKTOP the game happens to sit.
        GameInput.HoverAt(game.X + (int)Math.Round(o.OriginX + col * o.Pitch),
                          game.Y + (int)Math.Round(o.OriginY + row * o.Pitch));
        await Task.Delay(140);
        if (!GameInput.SendCopy())
        {
            SetStatus("Windows refused the Ctrl+C injection \u2014 is the game running elevated?", bad: true);
            RefreshSlurpPanel();
            return;
        }

        // The game fills the clipboard asynchronously; poll briefly rather than betting
        // one fixed delay covers every frame-rate. Still ONE copy for this ONE press.
        var text = "";
        for (var wait = 0; wait < 5 && text.Length == 0; wait++)
        {
            await Task.Delay(80);
            var read = SafeClipboardText();
            if (read.Length > 0 && read != _lastClipboard) text = read;
        }
        if (text.Length == 0)
        {
            SetStatus($"Chart {index}: nothing copied \u2014 F9 retries it, or Skip.", bad: true);
            RefreshSlurpPanel();
            return;
        }
        _lastClipboard = text;
        if (!_session.ApplyChartText(index, text))
        {
            SetStatus($"Chart {index}: the copied text did not match its cell \u2014 F9 retries it, or Skip.", bad: true);
            return;
        }

        // Stop may have disarmed the queue while the capture was in flight.
        if (_slurp is not { Count: > 0 } || _slurp.Peek() != (IsFigurine: false, Index: index)) return;
        _slurp.Dequeue();
        _solution = null;
        Persist();
        RefreshPanel();
        RefreshProgress();
        RefreshSlurpPanel();
        if (_slurp.Count == 0) FinishSlurp();
        else SetStatus($"Chart {index} captured \u2014 {_slurp.Count} to go.");
    }

    /// <summary>Where a figurine's Edge and cell put it on screen: the border square's
    /// centre pushed OUTWARD past the frame by the calibrated inset.</summary>
    private static (int X, int Y) FigurinePosition(
        BoardLayout.FigurineSlot slot, AreaModifierPanel.Options o)
    {
        var cell = slot.Adjacent[0];
        var x = o.BoardOriginX + cell.Col * o.BoardPitch;
        var y = o.BoardOriginY + cell.Row * o.BoardPitch;
        return slot.Edge switch
        {
            "top" => (x, y - o.FigurineInset),
            "bottom" => (x, y + o.FigurineInset),
            "left" => (x - o.FigurineInset, y),
            _ => (x + o.FigurineInset, y),
        };
    }

    /// <summary>
    /// Bounding box of the magic-blue tooltip text inside a captured region, padded and
    /// returned in SCREEN coordinates. Blue this saturated appears nowhere else on the
    /// Voyage screen, which makes it a better tooltip detector than any dark-box
    /// heuristic -- the chart stash is dark too.
    /// </summary>
    private static System.Drawing.Rectangle? BlueTextBounds(
        BitmapPixels pixels, System.Drawing.Rectangle region, double scale)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, count = 0;
        for (var y = 0; y < pixels.Height; y += 2)
            for (var x = 0; x < pixels.Width; x += 2)
            {
                var (r, g, b) = pixels.At(x, y);
                if (b > 130 && b > r + 40 && b > g + 30)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                    count++;
                }
            }
        // A real mod line is hundreds of samples; a stray blue pixel is not a tooltip.
        // Scaled by AREA, because that is what a sample count is: the same tooltip on a
        // 1080p screen offers barely half the samples, and a fixed 40 quietly asked a
        // smaller picture for twice the evidence before it would believe there was a
        // tooltip there at all.
        if (count < Math.Max(8, (int)Math.Round(40 * scale * scale))) return null;
        // Padded outwards, and deliberately unclamped: the caller intersects with the
        // screen, which is the only rectangle worth clamping to.
        var padX = (int)Math.Round(14 * scale);
        var padY = (int)Math.Round(10 * scale);
        return new System.Drawing.Rectangle(
            region.X + minX - padX, region.Y + minY - padY,
            maxX - minX + 2 * padX, maxY - minY + 2 * padY);
    }

    private string FigurineEdge(int index) =>
        _session.Layout.Figurines.FirstOrDefault(f => f.Index == index)?.Edge ?? "?";

    /// <summary>
    /// One F9 press on a border figurine: hover the carving so its tooltip shows --
    /// the authoritative modifier text; the Area Modifiers panel never lists a
    /// figurine's adjacent-scope lines -- then OCR a generous region around the
    /// hover point and keep what survives the chrome filter.
    /// No input reaches the game beyond the hover itself.
    /// </summary>
    private async Task SlurpFigurine(int index)
    {
        if (_slurp is null || _capturing) return;
        var slot = _session.Layout.Figurines.FirstOrDefault(f => f.Index == index);
        if (slot is null) { OnSlurpSkip(this, new RoutedEventArgs()); return; }

        SlurpInfo.Text = $"Reading figurine {index} ({slot.Edge})…";
        var game = ScreenCapture.ResolveGameBounds().Rect;
        var screen = ScreenCapture.PrimaryScreenBounds();
        // The board coordinates are PIXELS, so they need the same rescale the panel
        // rectangle gets for free by being fractional -- against the GAME's size.
        var raw = AreaModifierPanel.Options.Load();
        var o = raw.ForScreen(game.Width, game.Height);
        // The tooltip search box is measured in pixels too, so it scales with the rest.
        // Left fixed it swept a third of a 1080p screen looking for blue text and found
        // whatever else was blue on the way.
        var s = Math.Min(game.Width / (double)raw.ReferenceWidth,
                         game.Height / (double)raw.ReferenceHeight);
        var (cx, cy) = FigurinePosition(slot, o);
        var (hx, hy) = (game.X + cx, game.Y + cy);
        GameInput.HoverAt(hx, hy);
        await Task.Delay(240);   // the tooltip fades in

        _capturing = true;
        try
        {
            // Tooltips anchor wherever they fit, so the search region is generous --
            // but OCRing all of it swept up chart captions, the HUD, whatever sat
            // behind the tooltip, and clipped long mods at the region edge. The mod
            // text is MAGIC BLUE, unique on this screen: find its bounding box first
            // and read only that.
            var region = System.Drawing.Rectangle.Intersect(screen,
                new System.Drawing.Rectangle(
                    hx - (int)Math.Round(1000 * s), hy - (int)Math.Round(380 * s),
                    (int)Math.Round(1760 * s), (int)Math.Round(600 * s)));
            System.Drawing.Rectangle? tooltip;
            using (var shot = ScreenCapture.CaptureRegion(region))
                tooltip = BlueTextBounds(new BitmapPixels(shot), region, s);
            if (tooltip is not { } found)
            {
                SetStatus($"Figurine {index}: no tooltip found \u2014 F9 retries it "
                          + "(check Calibrate's figurine inset), or Skip.", bad: true);
                RefreshSlurpPanel();
                return;
            }
            var box = System.Drawing.Rectangle.Intersect(screen, found);
            var lines = await ScreenOcr.ReadRegionAsync(box, upscale: 3, blueText: true);
            var mods = AreaModifierPanel.TooltipLines(lines);
            if (mods.Count == 0)
            {
                SetStatus($"Figurine {index}: no tooltip read \u2014 F9 retries it "
                          + "(check Calibrate's figurine inset), or Skip.", bad: true);
                RefreshSlurpPanel();
                return;
            }
            _session.ApplyFigurineText(index, string.Join("\n", mods));
            _solution = null;
            Persist();
            SetStatus($"Figurine {index} ({slot.Edge}): read {mods.Count} "
                      + (mods.Count == 1 ? "modifier." : "modifiers."));
        }
        catch (Exception ex)
        {
            SetStatus($"Could not read the tooltip: {ex.Message}", bad: true);
            return;
        }
        finally
        {
            _capturing = false;
        }

        if (_slurp is not { Count: > 0 } || _slurp.Peek() != (IsFigurine: true, index)) return;
        _slurp.Dequeue();
        RefreshBoard();
        RebuildModifiers();
        RefreshProgress();
        RefreshSlurpPanel();
        if (_slurp.Count == 0) FinishSlurp();
    }

    /// <summary>Move past a cell that will not copy (an empty tooltip, a mis-read).</summary>
    private void OnSlurpSkip(object sender, RoutedEventArgs e)
    {
        if (_slurpBusy) return;   // never advance under a capture in flight
        if (_slurp is not { Count: > 0 }) return;
        var (wasFigurine, skipped) = _slurp.Dequeue();
        RefreshSlurpPanel();
        if (_slurp.Count == 0) FinishSlurp();
        else SetStatus((wasFigurine ? $"Figurine {skipped}" : $"Chart {skipped}")
                       + $" skipped \u2014 it stays unread.");
    }

    private void OnSlurpStop(object sender, RoutedEventArgs e) => SlurpBtn.IsChecked = false;

    private void StopSlurp(string status)
    {
        _slurp = null;
        if (_slurpHotkey is { } id) { _hotkeys?.Unregister(id); _slurpHotkey = null; }
        SlurpPanel.Visibility = Visibility.Collapsed;
        SetStatus(status);
    }

    private void FinishSlurp()
    {
        SlurpBtn.IsChecked = false;      // routes through StopSlurp via OnSlurpChanged
        StopSlurp("Slurp complete \u2014 Solve when ready.");
        RebuildModifiers();
    }
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
        if (ReadModeBtn.IsChecked == true) AdvanceTarget();
        else SetTarget(_target, 0);          // one-off done: disarm, back to idle
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

            // ARM the capture for this target, whether or not the guided pass is on.
            // Re-reading one chart used to require Load Details mode -- clicking the
            // chart selected it but nothing listened for the copy, which read as "Ctrl+C
            // is unreliable" when it was actually "Ctrl+C is ignored". The baseline
            // reset matters: without it, whatever was already on the clipboard would be
            // ingested as if it were the new copy.
            _lastClipboard = SafeClipboardText();
            _clipboardPoll.Start();
        }
        else if (ReadModeBtn.IsChecked != true)
        {
            _clipboardPoll.Stop();
        }
        UpdateCaptureHint();
        if (status is not null) SetStatus(status);
    }

    /// <summary>
    /// The hint says what a copy would do RIGHT NOW, or nothing at all.
    ///
    /// It used to read "Hover a square in game, press Ctrl+Alt+C" permanently, which
    /// implied the hotkey always did something. It is tied to the selection now: no
    /// target, no hint.
    /// </summary>
    private void UpdateCaptureHint()
    {
        if (_targetIndex == 0)
        {
            CaptureHint.Text = ReadModeBtn.IsChecked == true
                ? "Everything is read \u2014 toggle off, or click anything to re-read it"
                : "";
            return;
        }

        CaptureHint.Text = _target switch
        {
            Target.Square when _captureHotkey is null =>
                "Ctrl+Alt+C is taken by another app \u2014 type the modifiers below",
            Target.Square when !ScreenOcr.IsAvailable =>
                "No OCR language pack \u2014 type the modifiers below",
            Target.Square =>
                $"Hover square {_targetIndex} in game, press Ctrl+Alt+C",
            Target.Figurine =>
                $"Figurine {_targetIndex}: usually not copyable \u2014 type it below",
            _ => $"Hover chart {_targetIndex} in game, press Ctrl+C",
        };
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
        if (Tuned is not { } profile) { SetStatus("No rule profile loaded.", bad: true); return false; }
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

        var snapshot = VoyageSession.FromState(_session.ToState());
        VoyageSolver.Solution solution;
        try
        {
            solution = await Task.Run(
                () => snapshot.Solve(profile, TimeSpan.FromSeconds(3), cts.Token),
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
        if (Tuned is not { } profile) return;
        ApplySolution(_session.Solve(profile, TimeSpan.FromSeconds(3)), profile);
    }

    private void ApplySolution(VoyageSolver.Solution solution, VoyageProfile profile)
    {
        // A solve races Clear and Dive Again: cancellation covers the common case, but
        // a result that slipped past it must not be applied over a session that no
        // longer holds its charts -- that is a crash wearing a plan's clothes.
        var known = _session.ByPanelIndex.Values
            .Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        if (solution.Placements.Any(p => !known.Contains(p.Chart.Id))) return;

        _solution = solution;
        _steps = _session.Plan(_solution);
        _badges = _session.Badges(_solution);

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
        // A star the solver could not honour must be said out loud: the bonus prefers
        // any board that seats the chart, so an unseated one means NO legal layout has
        // room for it -- not that the planner ignored the mark.
        var unseated = _session.Required
            .Where(r => _session.ByPanelIndex.ContainsKey(r))
            .Where(r => _steps.All(st => st.ChartNumber != r))
            .Order().ToList();
        if (unseated.Count > 0)
            SetStatus("No legal layout can seat required chart"
                      + (unseated.Count == 1 ? " " : "s ") + string.Join(", ", unseated)
                      + " \u2014 the rest were placed.", bad: true);
        else SetStatus($"Placed {_solution.Placements.Count} charts for \"{profile.Name}\".");

        // The plan is made; the next thing that goes wrong is at the vendor.
        ShowAlchReminder();
    }

    private void ShowAlchReminder()
    {
        if (!IsLoaded) return;   // offscreen renders have no window for a popup to float over
        AlchNote.IsOpen = true;
        _alchTimer.Stop();
        _alchTimer.Start();
    }

    private void OnAlchNoteClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _alchTimer.Stop();
        AlchNote.IsOpen = false;
    }

    private void ShowBoardNote()
    {
        if (!IsLoaded) return;
        BoardNote.IsOpen = true;
        _boardNoteTimer.Stop();
        _boardNoteTimer.Start();
    }

    private void OnBoardNoteClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _boardNoteTimer.Stop();
        BoardNote.IsOpen = false;
    }

    /// <summary>Copy a stash search string that lights up the solved board's charts.</summary>
    private void OnCopyStashSearch(object sender, RoutedEventArgs e)
    {
        if (_steps.Count == 0)
        {
            SetStatus("Nothing to search for \u2014 solve a board first.", bad: true);
            return;
        }
        var search = _session.StashSearch(_steps.Select(st => st.ChartNumber));
        if (search.Length == 0)
        {
            SetStatus("No chart names captured \u2014 the search string would be empty.", bad: true);
            return;
        }
        try { Clipboard.SetText(search); } catch (System.Runtime.InteropServices.COMException) { }
        SetStatus($"Copied {search} \u2014 paste it into the chart stash search box.");
    }

    /// <summary>
    /// The voyage was run. Spend the placed charts, clear the board, and point the user
    /// at the one thing that must happen before the next plan: re-reading the border,
    /// which rerolls every voyage. Unplaced charts keep their panel numbers -- those
    /// point at physical panel positions.
    ///
    /// With a chart-refund chance in play the spend is not a foregone conclusion, so
    /// the panel asks which charts came back first; otherwise all nine go.
    /// </summary>
    private void OnNextVoyage(object sender, RoutedEventArgs e)
    {
        _solving?.Cancel();   // the board it is solving is about to stop existing
        _badges = new Dictionary<int, VoyageSession.SquareBadges>();
        if (_steps.Count == 0)
        {
            SetStatus("Nothing to complete \u2014 solve a board first.", bad: true);
            return;
        }

        if (_session.PreserveChanceInPlay(_steps.Select(st => st.ChartNumber)))
        {
            ShowPreservePanel();
            return;
        }
        FinishVoyage(_steps.Select(st => st.ChartNumber));
    }

    private void ShowPreservePanel()
    {
        PreserveList.Children.Clear();
        foreach (var step in _steps.OrderBy(st => st.ChartNumber))
        {
            var chart = _session.ByPanelIndex.GetValueOrDefault(step.ChartNumber);
            var name = chart is { Name.Length: > 0 } ? chart.Name : $"chart {step.ChartNumber}";
            PreserveList.Children.Add(new ToggleButton
            {
                Content = $"{step.ChartNumber} · {name}",
                Tag = step.ChartNumber,
                Margin = new Thickness(0, 0, 6, 6),
                Padding = new Thickness(8, 3, 8, 3),
                ToolTip = "Toggled = it came back to the panel; untoggled = consumed",
            });
        }
        PreservePanel.Visibility = Visibility.Visible;
    }

    private void OnPreserveCancel(object sender, RoutedEventArgs e) =>
        PreservePanel.Visibility = Visibility.Collapsed;

    private void OnPreserveConfirm(object sender, RoutedEventArgs e)
    {
        var survived = PreserveList.Children.OfType<ToggleButton>()
            .Where(t => t.IsChecked == true)
            .Select(t => (int)t.Tag)
            .ToHashSet();
        PreservePanel.Visibility = Visibility.Collapsed;
        FinishVoyage(_steps.Select(st => st.ChartNumber).Where(i => !survived.Contains(i)),
                     kept: survived.Count);
    }

    private void FinishVoyage(IEnumerable<int> toSpend, int kept = 0)
    {
        var spent = _session.CompleteVoyage(toSpend);
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

        SetStatus($"Voyage complete \u2014 {spent} charts spent"
                  + (kept > 0 ? $", {kept} preserved" : "") + ". "
                  + "The border has rerolled: hover each square and press Ctrl+Alt+C.");
        ShowBoardNote();
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        _solving?.Cancel();          // the board it is solving just stopped existing
        if (SlurpBtn.IsChecked == true) SlurpBtn.IsChecked = false;
        _session = new VoyageSession();
        VoyageSessionState.Delete();
        _solution = null;
        _steps = [];
        _badges = new Dictionary<int, VoyageSession.SquareBadges>();
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
            _session.ApplyPanelRead(new ChartPanelReader().Read(new BitmapPixels(bmp)));
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
        // Badge-worthy squares, so the sample exercises the icon row too.
        _session.ApplySquareModifiers(6, ["Area contains 4 additional Golden Lanterns"]);
        _session.ApplySquareModifiers(8, ["Area contains Filthscrabble",
                                          "Rare Monsters in Area drop Dead Man's Sulphur"]);
        _session.ApplySquareModifiers(9, ["Area contains 2 additional Diviner's Strongboxes"]);

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

        foreach (var alert in VoyageAlerts.Scan(_session))
            _alerts.Add(new AlertRow(alert));

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
        if (Tuned is { } profile)
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
                    Background = Face(0x18, 0x15, 0x12),
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
        if (RemoveBtn.IsChecked == true)
        {
            if (_session.RemoveSquareModifiers(square))
                FinishRemoval($"Square {square} panel read erased.");
            else SetStatus($"Square {square} has no panel read to erase "
                           + "(its figurines are removed individually).", bad: true);
            return;
        }
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
        if (RemoveBtn.IsChecked == true)
        {
            // Two gears: the first click strips a bad hover read back to shape-and-
            // level; a second click (now detail-less) removes the chart outright.
            if (_session.ByPanelIndex.TryGetValue(index, out var chart)
                && HasCapturedDetail(chart))
            {
                _session.RemoveChartDetail(index);
                FinishRemoval($"Chart {index} detail erased \u2014 shape and level kept; "
                              + "click again to remove the chart entirely.");
            }
            else
            {
                _session.RemoveChart(index);
                FinishRemoval($"Chart {index} removed from the panel.");
            }
            return;
        }
        SetTarget(Target.Chart, index,
                  $"Chart {index} selected — copy or paste its text below.");
        RefreshBoard();
        RefreshPanel();
    }

    // ---- targeted removal ----------------------------------------------------------

    private void OnRemoveModeChanged(object sender, RoutedEventArgs e)
    {
        SetStatus(RemoveBtn.IsChecked == true
            ? "Remove armed \u2014 click a figurine, square or chart to erase its reading."
            : "Remove off.");
    }

    /// <summary>The bookkeeping every targeted removal shares: the plan was computed
    /// against data that just changed, so it goes; everything else stays.</summary>
    private void FinishRemoval(string status)
    {
        _solution = null;
        _steps = [];
        _badges = new Dictionary<int, VoyageSession.SquareBadges>();
        _summary.Clear();
        SolveInfo.Text = "";
        Persist();
        RefreshBoard();
        RefreshPanel();
        RebuildModifiers();
        RefreshProgress();
        SetStatus(status);
    }

    private void OnFigurineMarkerClicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: int index }) return;
        if (RemoveBtn.IsChecked == true)
        {
            if (_session.RemoveFigurine(index))
                FinishRemoval($"Figurine {index} reading erased \u2014 re-slurp or click it to re-read.");
            else SetStatus($"Figurine {index} has no reading to erase.", bad: true);
            return;
        }
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
                var hasModifiers = _session.SquareBoardKnown(square);
                var cell = _boardCells[r, c];

                cell.Background = step is null
                    ? Face(0x0E, 0x0C, 0x0A)
                    : isStranded ? Face(0x22, 0x13, 0x10)
                    : Face(0x18, 0x14, 0x10);
                cell.BorderBrush = new SolidColorBrush(isTarget
                    ? Color.FromRgb(0xC8, 0xAA, 0x6E)
                    : isStranded ? Color.FromRgb(0x6E, 0x2F, 0x25)
                    : hasModifiers ? Color.FromRgb(0x44, 0x5C, 0x38)
                    : step is null ? Color.FromRgb(0x1F, 0x1A, 0x15)
                    : Color.FromRgb(0x3A, 0x2E, 0x1C));
                cell.BorderThickness = new Thickness(isTarget ? 2 : 1);
                cell.Child = BoardSquareContent(square, step, isStranded,
                                                _badges.GetValueOrDefault(square));
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

        lines.Add("— board —");

        if (_session.SquareBoardKnown(square))
        {
            var mods = _session.EffectiveSquareModifiers(square);
            lines.AddRange(mods.Count == 0 ? ["No board modifiers on this square."] : mods);
        }
        else if (_session.SquaresWithoutFigurines.Contains(square))
            lines.Add("No figurine reaches this square, so it never has board modifiers.");
        else
            lines.Add("Area modifiers not read yet.");

        // What the NEIGHBOURS push in: the answer to "why is this square worth more
        // than its chart", shown where the question gets asked.
        if (step is not null && _solution is not null && Tuned is { } tuned)
        {
            var received = _session.ReceivedOnSquare(tuned, _solution, square);
            if (received.Count > 0)
            {
                lines.Add($"— gained from adjacent —");
                foreach (var (from, modifier, value) in received)
                    lines.Add($"square {from}: {modifier}"
                              + (value > 0.5 ? $"  (+{value:0})" : ""));
            }
        }

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
    /// <summary>A cell face lit faintly from above -- the difference between a flat
    /// rectangle and something that reads as a carved tile.</summary>
    private static Brush Face(Color top, Color bottom) =>
        new LinearGradientBrush(top, bottom, 90);

    private static Brush Face(byte r, byte g, byte b) =>
        Face(Color.FromRgb((byte)Math.Min(255, r + 8), (byte)Math.Min(255, g + 7),
                           (byte)Math.Min(255, b + 6)),
             Color.FromRgb((byte)(r * 2 / 3), (byte)(g * 2 / 3), (byte)(b * 2 / 3)));

    private static UIElement Tip(string title, string? subtitle,
                                 IEnumerable<string> lines, bool haveDetail)
    {
        var stack = new StackPanel { MaxWidth = 420 };
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontFamily = PoeFonts.Display,
            FontSize = 16,
            Foreground = Brush(0xC8, 0xAA, 0x6E),
        });
        if (!string.IsNullOrEmpty(subtitle))
            stack.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 12,
                Foreground = Brush(0x6B, 0x5F, 0x4E),
                Margin = new Thickness(0, 1, 0, 0),
            });

        var first = true;
        foreach (var line in lines)
        {
            // The section break arrives as a sentinel and leaves as an actual LINE:
            // a rule across 70% of the tip's inner width, left-aligned, separating
            // what the tile is (stats) from what it does (modifiers).
            if (line == ChartRewards.SectionBreak)
            {
                var rule = new Border
                {
                    Height = 1,
                    Background = Brush(0x6B, 0x5F, 0x4E),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 8, 0, 3),
                };
                rule.SetBinding(FrameworkElement.WidthProperty, new System.Windows.Data.Binding
                {
                    Source = stack,
                    Path = new PropertyPath(nameof(StackPanel.ActualWidth)),
                    Converter = new SeventyPercent(),
                });
                stack.Children.Add(rule);
                first = false;
                continue;
            }
            stack.Children.Add(ModifierLine(line, haveDetail,
                                            new Thickness(0, first ? 9 : 4, 0, 0)));
            first = false;
        }
        return stack;
    }

    /// <summary>70% of whatever width the tip settles at, so the rule never dictates it.</summary>
    private sealed class SeventyPercent : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c) =>
            value is double w && w > 0 ? w * 0.7 : 0.0;
        public object ConvertBack(object v, Type t, object p, System.Globalization.CultureInfo c) =>
            throw new NotSupportedException();
    }

    /// <summary>The values inside a modifier line, for colouring them apart.</summary>
    private static readonly System.Text.RegularExpressions.Regex NumberToken =
        new(@"[+\-]?\d+(?:\.\d+)?%?",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// A modifier line set for READING AT A GLANCE: 13.5pt, and the numbers pulled out
    /// of the prose in bright brass. A tooltip full of same-colour 12px text made every
    /// value a hunt; the number is the part being compared, so it is the part that
    /// carries the colour.
    /// </summary>
    private static TextBlock ModifierLine(string line, bool haveDetail, Thickness margin)
    {
        var block = new TextBlock
        {
            FontSize = 13.5,
            TextWrapping = TextWrapping.Wrap,
            FontStyle = haveDetail ? FontStyles.Normal : FontStyles.Italic,
            Margin = margin,
        };
        // Scope carries the game's own colour language: adjacent mods read in rare
        // yellow, voyage-wide ones in unique orange, and a plain area mod stays prose.
        var scoped = line.Contains("all Voyage Areas", StringComparison.OrdinalIgnoreCase)
                     || line.StartsWith("Voyage Modifier", StringComparison.OrdinalIgnoreCase)
            ? Brush(0xAF, 0x60, 0x25)
            : line.Contains("adjacent", StringComparison.OrdinalIgnoreCase)
                ? Brush(0xD9, 0xC9, 0x56)
                : null;
        var prose = scoped ?? (haveDetail ? Brush(0xC8, 0xBC, 0xA4) : Brush(0x94, 0x86, 0x6F));
        var number = haveDetail ? Brush(0xF0, 0xD2, 0x64) : Brush(0xB0, 0x9E, 0x7A);

        var at = 0;
        foreach (System.Text.RegularExpressions.Match m in NumberToken.Matches(line))
        {
            if (m.Index > at)
                block.Inlines.Add(new System.Windows.Documents.Run(line[at..m.Index])
                    { Foreground = prose });
            block.Inlines.Add(new System.Windows.Documents.Run(m.Value)
                { Foreground = number, FontWeight = FontWeights.SemiBold });
            at = m.Index + m.Length;
        }
        if (at < line.Length)
            block.Inlines.Add(new System.Windows.Documents.Run(line[at..]) { Foreground = prose });
        return block;
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
    /// <summary>Per-square icons for the current plan, rebuilt with each solve.</summary>
    private IReadOnlyDictionary<int, VoyageSession.SquareBadges> _badges =
        new Dictionary<int, VoyageSession.SquareBadges>();

    private UIElement BoardSquareContent(int square, VoyagePlan.Step? step, bool stranded,
                                         VoyageSession.SquareBadges? badges = null)
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
                FontFamily = PoeFonts.Display,
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
            : Color.FromRgb(0xC8, 0xAA, 0x6E);

        stack.Children.Add(Glyph(new ChartFace(step.Chart.Shape, step.Rotation), 56, accent));
        stack.Children.Add(new TextBlock
        {
            Text = square.ToString(),
            FontFamily = PoeFonts.Display,
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

        // The icon row: what the square CONTAINS, at a glance. A golden lantern to
        // grab, a named boss to expect. Sourced from the same lines the scoring uses,
        // so an icon never promises something the plan did not price.
        if (badges is { } b && (b.GoldenLanterns > 0 || b.FreeLanterns || b.Bottles > 0
                                || b.Containers.Count > 0 || b.Bosses.Count > 0
                                || b.Payouts.Count > 0 || b.Strongboxes.Count > 0
                                || b.Dangers.Count > 0))
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0),
            };
            if (b.GoldenLanterns > 0)
            {
                var lantern = new StackPanel
                    { Orientation = Orientation.Horizontal, Margin = new Thickness(3, 0, 3, 0) };
                lantern.Children.Add(LanternIcon());
                if (b.GoldenLanterns > 1)
                    lantern.Children.Add(new TextBlock
                    {
                        Text = "\u00d7" + b.GoldenLanterns.ToString("0"),
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 10,
                        Foreground = Brush(0xF0, 0xD2, 0x64),
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(2, 0, 0, 0),
                    });
                lantern.ToolTip = $"{b.GoldenLanterns:0} Golden Lantern{(b.GoldenLanterns > 1 ? "s" : "")} \u2014 grab early, the quantity buff pays for the rest of the run";
                row.Children.Add(lantern);
            }
            if (b.Bottles > 0)
            {
                var bottle = new StackPanel
                    { Orientation = Orientation.Horizontal, Margin = new Thickness(3, 0, 3, 0) };
                bottle.Children.Add(BottleIcon());
                if (b.Bottles > 1)
                    bottle.Children.Add(new TextBlock
                    {
                        Text = "×" + b.Bottles.ToString("0"),
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 10,
                        Foreground = Brush(0xF0, 0xD2, 0x64),
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(2, 0, 0, 0),
                    });
                bottle.ToolTip = $"{b.Bottles:0} Message{(b.Bottles > 1 ? "s" : "")} in a Bottle "
                               + "\u2014 the profit chase; open every one";
                row.Children.Add(bottle);
            }
            if (b.Containers.Count > 0)
            {
                var keg = new StackPanel
                    { Orientation = Orientation.Horizontal, Margin = new Thickness(3, 0, 3, 0) };
                keg.Children.Add(BarrelIcon());
                keg.ToolTip = "Openables here: " + string.Join(", ", b.Containers);
                row.Children.Add(keg);
            }
            if (b.FreeLanterns)
            {
                // The lantern with an infinity beside it: placing FROM here is free.
                var free = new StackPanel
                    { Orientation = Orientation.Horizontal, Margin = new Thickness(3, 0, 3, 0) };
                free.Children.Add(LanternIcon());
                free.Children.Add(new TextBlock
                {
                    Text = "∞",
                    FontFamily = new FontFamily("Segoe UI Symbol"),
                    FontSize = 11,
                    Foreground = Brush(0xF0, 0xD2, 0x64),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(1, 0, 0, 0),
                });
                free.ToolTip = "Placing Lanterns does not reduce your Lantern count here "
                             + "\u2014 place every lantern this square offers, they are free";
                row.Children.Add(free);
            }
            foreach (var boss in b.Bosses)
            {
                row.Children.Add(new TextBlock
                {
                    Text = "\u2620",
                    FontFamily = new FontFamily("Segoe UI Symbol"),
                    FontSize = 13,
                    Foreground = Brush(0xB8, 0x50, 0x3E),
                    Margin = new Thickness(3, 0, 3, 0),
                    ToolTip = $"{boss} \u2014 named boss in this square",
                });
            }
            if (b.Payouts.Count > 0)
            {
                var coin = CoinIcon();
                coin.ToolTip = "Per-rare payout: " + string.Join(", ", b.Payouts)
                               + " \u2014 every rare here pays";
                row.Children.Add(coin);
            }
            if (b.Strongboxes.Count > 0)
            {
                var chest = ChestIcon();
                chest.ToolTip = string.Join(", ", b.Strongboxes) + " in this square";
                row.Children.Add(chest);
            }
            if (b.Dangers.Count > 0)
            {
                row.Children.Add(new TextBlock
                {
                    Text = "\u26a0",
                    FontFamily = new FontFamily("Segoe UI Symbol"),
                    FontSize = 12,
                    Foreground = Brush(0xB8, 0x50, 0x3E),
                    Margin = new Thickness(3, 0, 3, 0),
                    ToolTip = "Fight carefully \u2014 " + string.Join("; ", b.Dangers),
                });
            }
            stack.Children.Add(row);
        }
        return stack;
    }

    /// <summary>A brass coin for the payout squares: a rimmed disc with a stamped dot.</summary>
    private static FrameworkElement CoinIcon()
    {
        var g = new Grid { Width = 12, Height = 12, Margin = new Thickness(3, 0, 3, 0) };
        g.Children.Add(new Border
        {
            Width = 11, Height = 11,
            CornerRadius = new CornerRadius(6),
            Background = Brush(0xF0, 0xD2, 0x64),
            BorderBrush = Brush(0xA3, 0x8D, 0x6D),
            BorderThickness = new Thickness(1.5),
        });
        g.Children.Add(new Border
        {
            Width = 3, Height = 3,
            CornerRadius = new CornerRadius(2),
            Background = Brush(0xA3, 0x8D, 0x6D),
        });
        return g;
    }

    /// <summary>A small chest: dark body, gold lid line, centre latch.</summary>
    private static FrameworkElement ChestIcon()
    {
        var g = new Grid { Width = 13, Height = 11, Margin = new Thickness(3, 0, 3, 0) };
        g.Children.Add(new Border
        {
            Width = 12, Height = 10,
            CornerRadius = new CornerRadius(3, 3, 1, 1),
            Background = Brush(0x5C, 0x4C, 0x2C),
            BorderBrush = Brush(0xC8, 0xAA, 0x6E),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Bottom,
        });
        g.Children.Add(new Border
        {
            Width = 12, Height = 1.5,
            Background = Brush(0xC8, 0xAA, 0x6E),
            VerticalAlignment = VerticalAlignment.Center,
        });
        g.Children.Add(new Border
        {
            Width = 3, Height = 4,
            Background = Brush(0xF0, 0xD2, 0x64),
            VerticalAlignment = VerticalAlignment.Center,
        });
        return g;
    }

    /// <summary>A little golden lantern, drawn rather than fonted: a glowing body under
    /// a dark cap, unmistakable at 12px against the slate.</summary>
    /// <summary>A corked bottle: neck, shoulder, body. The profit chase's mark.</summary>
    private static UIElement BottleIcon()
    {
        var g = new Grid { Width = 8, Height = 13 };
        g.Children.Add(new Border      // cork
        {
            Width = 3, Height = 2,
            Background = Brush(0xA3, 0x8D, 0x6D),
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        g.Children.Add(new Border      // neck
        {
            Width = 3, Height = 4,
            Background = Brush(0x8F, 0xB8, 0x9A),
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0),
        });
        g.Children.Add(new Border      // body
        {
            Width = 8, Height = 8,
            CornerRadius = new CornerRadius(2, 2, 3, 3),
            Background = Brush(0x6E, 0xA8, 0x84),
            BorderBrush = Brush(0x3A, 0x5C, 0x48),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Bottom,
        });
        return g;
    }

    /// <summary>A barrel: staves and two hoops, for the other openables.</summary>
    private static UIElement BarrelIcon()
    {
        var g = new Grid { Width = 9, Height = 11 };
        g.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(3),
            Background = Brush(0x8A, 0x6A, 0x3C),
            BorderBrush = Brush(0x4A, 0x38, 0x20),
            BorderThickness = new Thickness(1),
        });
        foreach (var y in new[] { 3.0, 7.0 })
            g.Children.Add(new Border
            {
                Height = 1,
                Background = Brush(0x3A, 0x2C, 0x18),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(1, y, 1, 0),
            });
        return g;
    }

    private static UIElement LanternIcon()
    {
        var g = new Grid { Width = 10, Height = 13 };
        g.Children.Add(new Border
        {
            Width = 4, Height = 2,
            Background = Brush(0xA3, 0x8D, 0x6D),
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        g.Children.Add(new Border
        {
            Width = 8, Height = 9,
            CornerRadius = new CornerRadius(2, 2, 3, 3),
            Background = Brush(0xF0, 0xD2, 0x64),
            BorderBrush = Brush(0xA3, 0x8D, 0x6D),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0),
        });
        g.Children.Add(new Border
        {
            Width = 6, Height = 2,
            Background = Brush(0xA3, 0x8D, 0x6D),
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        return g;
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
            cell.MouseRightButtonUp += OnPanelCellMarked;
            _panelCells[i] = cell;
            PanelGrid.Children.Add(cell);
        }
        RefreshPanel();
    }

    /// <summary>
    /// Right-click cycles a chart's mark: X (never place it), then star (always place
    /// it), then clear. The rules say what a chart is WORTH; the marks are the user
    /// overruling them in either direction -- an X for a chart being saved or sold, a
    /// star for the Soul Eater problem, where the payoff is player power and no profile
    /// can price it.
    /// </summary>
    private void OnPanelCellMarked(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: int index }) return;
        if (!_session.ByPanelIndex.ContainsKey(index)) return;
        e.Handled = true;

        var mark = _session.CycleMark(index);
        Persist();
        RefreshPanel();
        SetStatus(mark switch
        {
            ChartMark.Excluded => $"Chart {index} excluded \u2014 Solve to replan without it.",
            ChartMark.Required => $"Chart {index} required \u2014 Solve to force it onto the board.",
            _ => $"Chart {index} back in the pool \u2014 Solve to replan.",
        });
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

            var isExcluded = _session.IsExcluded(index);
            var isRequired = _session.IsRequired(index);
            var isPlanned = used.ContainsKey(index);
            // Not gated on read mode: manual entry works without it, and the highlight is
            // what tells you which chart the box below applies to.
            var isTarget = _target == Target.Chart && _targetIndex == index;
            var hasDetail = HasCapturedDetail(chart);

            cell.Background = isPlanned
                ? Face(0x2A, 0x21, 0x0E)
                : Face(0x16, 0x13, 0x11);
            cell.BorderBrush = new SolidColorBrush(isTarget
                ? Color.FromRgb(0xC8, 0xAA, 0x6E)
                : isPlanned ? Color.FromRgb(0xA3, 0x8D, 0x6D)
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
                    ? Color.FromRgb(0xA3, 0x8D, 0x6D)
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
                    FontFamily = PoeFonts.Display,
                    FontSize = 26,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xAA, 0x6E)),
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

            if (isExcluded)
            {
                // The X sits OVER the dimmed cell: the chart stays identifiable under
                // its veto, and a second right-click lifts it.
                stack.Opacity = 0.3;
                var host = new Grid();
                host.Children.Add(stack);
                host.Children.Add(new TextBlock
                {
                    Text = "\u2715",
                    FontFamily = PoeFonts.Display,
                    FontSize = 30,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xB8, 0x50, 0x3E)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                cell.Child = host;
            }
            else if (isRequired)
            {
                // The star sits in the corner rather than over the face: a required
                // chart still has a shape and a destination worth reading.
                var host = new Grid();
                host.Children.Add(stack);
                host.Children.Add(new TextBlock
                {
                    Text = "\u2605",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xAA, 0x6E)),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 0, 2, 0),
                });
                cell.Child = host;
            }
            else
            {
                cell.Child = stack;
            }
            cell.ToolTip = Tip(
                string.IsNullOrWhiteSpace(chart.Name) ? $"Chart {index}" : $"Chart {index} · {chart.Name}",
                isExcluded ? "Excluded \u2014 right-click again to require it, twice to clear"
                    : isRequired ? "Required \u2014 the solver must place it; right-click to clear"
                    : isPlanned ? $"Goes on square {used[index]}" : "Not in the plan",
                DescribeChart(chart), hasDetail);
        }

        PanelHeader.Text = _session.Charts.Count == 0
            ? "C H A R T S"
            : $"C H A R T S      {_session.Charts.Count} read · "
              + $"{_session.ChartsAwaitingDetail.Count} without modifiers";
    }

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
            var scored = Tuned?.ScoreText([$"Area: {tileset}"]) ?? 0;
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
            var read = _session.SquareBoardKnown(square);
            var lines = read ? _session.EffectiveSquareModifiers(square) : [];

            // Three states, not two: unreachable squares are not work left to do, so they
            // must not read as "Not read" and sit there looking unfinished.
            var text = unreachable.Contains(square) ? "No figurine reaches this square"
                : !read ? "Not read"
                : lines.Count == 0 ? "No modifiers"
                : string.Join("\n", lines);

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
                // The section-break sentinel is for the tooltip's drawn rule; in this
                // flat list it would print as literal dashes.
                string.Join("\n", rewards.Where(r => r != ChartRewards.SectionBreak)),
                captured: true,
                selected: _target == Target.Chart && _targetIndex == index));
        }

        var unread = _session.SquaresAwaitingModifiers.Count;
        ModifierHeader.Text = unread > 0
            ? $"M O D I F I E R S      {unread} square{(unread == 1 ? "" : "s")} unread"
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
        /// <summary>Figurines are deliberately absent: they reach the list through the
        /// SQUARE they buff (EffectiveSquareModifiers), and they are re-targeted by
        /// clicking the ring, which is where their position is the information.</summary>
        public enum Sort { VoyageWide, Tileset, Square, Chart }

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

            // Built once, frozen: these are read by the binding engine on every row
            // refresh, and a fresh SolidColorBrush per read is an allocation and a
            // missed chance to share one.
            Accent = Frozen(
                Selected ? Color.FromRgb(0xC8, 0xAA, 0x6E)
                : Kind == Sort.VoyageWide ? Color.FromRgb(0xC8, 0xAA, 0x6E)
                : Kind == Sort.Tileset && Captured ? Color.FromRgb(0xA3, 0x8D, 0x6D)
                : Muted ? Color.FromRgb(0x2A, 0x20, 0x18)
                : Captured ? Color.FromRgb(0x86, 0xA8, 0x6A)
                : Color.FromRgb(0x2A, 0x20, 0x18));
            TextBrush = Frozen(
                Muted ? Color.FromRgb(0x5A, 0x50, 0x42)
                : Captured ? Color.FromRgb(0xE6, 0xDB, 0xC2)
                : Color.FromRgb(0x6B, 0x5F, 0x4E));
            RowBackground = Frozen(
                Selected ? Color.FromRgb(0x1F, 0x1A, 0x10) : Color.FromRgb(0x13, 0x11, 0x10));
        }

        private static Brush Frozen(Color colour)
        {
            var brush = new SolidColorBrush(colour);
            brush.Freeze();
            return brush;
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

        public Brush Accent { get; }
        public Brush TextBrush { get; }
        public Brush RowBackground { get; }
    }

    /// <summary>One line of the summary. Shape carries the meaning, so it is prebuilt.</summary>
    /// <summary>One banner row. Colour carries the kind, so the label never has to.</summary>
    public sealed class AlertRow
    {
        public AlertRow(VoyageAlert alert)
        {
            Headline = alert.Headline;
            Detail = alert.Detail;
            Where = alert.Where;
            // Grail rows shout: bright gold on a lit row, bigger headline. The two
            // modifiers that decide a whole voyage must not look like the rest.
            Accent = alert.Kind switch
            {
                AlertKind.Grail => new SolidColorBrush(Color.FromRgb(0xF0, 0xD2, 0x6A)),
                AlertKind.Jackpot => new SolidColorBrush(Color.FromRgb(0xC8, 0xAA, 0x6E)),
                _ => new SolidColorBrush(Color.FromRgb(0xB8, 0x50, 0x3E)),
            };
            RowBackground = alert.Kind == AlertKind.Grail
                ? new SolidColorBrush(Color.FromRgb(0x2A, 0x22, 0x0E))
                : new SolidColorBrush(Color.FromRgb(0x13, 0x11, 0x10));
            HeadlineSize = alert.Kind == AlertKind.Grail ? 17 : 14;
        }

        public Brush RowBackground { get; }
        public double HeadlineSize { get; }

        public string Headline { get; }
        public string Detail { get; }
        public string Where { get; }
        public Brush Accent { get; }
    }

    public sealed class SummaryRow
    {
        private SummaryRow(string label, string value, double size, FontFamily font, Brush brush)
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
        public FontFamily Font { get; }
        public Brush Brush { get; }

        /// <summary>One shared brush rather than one per binding read: every row has
        /// the same value colour and none of them ever changes it.</summary>
        public Brush ValueBrush { get; } = Of(0xC8, 0xAA, 0x6E);

        private static Brush Of(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        public static SummaryRow Heading(string text) =>
            new(text, "", 15, PoeFonts.Display, Of(0xE6, 0xDB, 0xC2));

        public static SummaryRow Stat(string name, string total) =>
            new(name, total, 13, PoeFonts.Body, Of(0xE6, 0xDB, 0xC2));

        public static SummaryRow Section(string text) =>
            new(text, "", 11, PoeFonts.Display, Of(0x6B, 0x5F, 0x4E));

        public static SummaryRow Detail(string text, string value = "") =>
            new(text, value, 12, PoeFonts.Body, Of(0x94, 0x86, 0x6F));

        public static SummaryRow Route(string order) =>
            new(order, "", 15, PoeFonts.Display, Of(0xC8, 0xAA, 0x6E));
    }
}
