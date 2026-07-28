using System.Windows;
using MindGoblin.Core;

namespace MindGoblin;

/// <summary>
/// The shell: a tab per tool.
///
/// It used to host a live trade-search watcher and carried the credentials, rate limiter
/// and session handling for it. That is all gone -- nothing here talks to an
/// authenticated endpoint any more, so there is no session state to hold and no chrome
/// to show for it.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Before anything reads a settings file: the rename moved the folder, and the
        // Voyage session in it is a screenshot plus dozens of hovers that must not be
        // silently abandoned.
        var carried = SettingsFolder.MigrateFromPreviousName();

        var settings = AppSettings.Load();

        // Both tools are self-contained. The gem calculator wants public price data; the
        // Voyage planner reads the screen and the clipboard. Neither needs the other.
        GemTabHost.Content = new GemRoiView(settings);

        var voyage = new VoyageView();
        VoyageTabHost.Content = voyage;

        // The planner owns a file watcher and a system-wide hotkey; neither is released
        // by the control going away.
        Closed += (_, _) => voyage.Dispose();

        if (carried.Count > 0)
            Title = $"MindGoblin  —  carried {carried.Count} settings files over from the old name";
    }
}
