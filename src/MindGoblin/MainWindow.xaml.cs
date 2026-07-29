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

        // Before anything reads a settings file. State lives ONLY beside the exe; no
        // reach into %LOCALAPPDATA%, ever -- a legacy migration that did was how a
        // freshly unzipped release came to "load chart data from somewhere".
        SettingsFolder.EnsureExists();

        var settings = AppSettings.Load();

        // Both tools are self-contained. The gem calculator wants public price data; the
        // Voyage planner reads the screen and the clipboard. Neither needs the other.
        //
        // Gated OFF for now (Features.GemRoi): the tab is collapsed and the view is never
        // constructed, so a disabled tool costs no startup work and shows no dead UI.
        if (Features.GemRoi)
            GemTabHost.Content = new GemRoiView(settings);
        else
            GemTab.Visibility = Visibility.Collapsed;

        var voyage = new VoyageView();
        VoyageTabHost.Content = voyage;

        // The planner owns a file watcher and a system-wide hotkey; neither is released
        // by the control going away.
        Closed += (_, _) => voyage.Dispose();
    }
}
