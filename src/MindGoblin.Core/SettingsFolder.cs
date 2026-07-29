namespace MindGoblin.Core;

/// <summary>
/// Where the app keeps its files: a <c>MindGoblin_data</c> directory NEXT TO THE EXE.
///
/// Deliberately not %LOCALAPPDATA%. This is a portable tool -- one exe you put
/// somewhere -- and its state belongs beside it, where it can be found, backed up, or
/// deleted by deleting the folder. Windows conventions scatter app state into hidden
/// roaming dirs that outlive the app; nothing here is worth hiding.
///
/// And ONLY beside it. There used to be a one-time migration that reached back into
/// the %LOCALAPPDATA% folders of earlier builds, and it turned every fresh copy of the
/// exe into a resurrection: unzip the release in Downloads, run it, and last week's
/// session teleports in from a hidden folder the user thought they had left behind.
/// A portable app's state comes from the folder it is in, full stop -- moving state
/// between installs is a job for copying MindGoblin_data by hand, visibly.
/// </summary>
public static class SettingsFolder
{
    public const string Name = "MindGoblin";

    /// <summary>MindGoblin_data/ beside the exe. AppContext.BaseDirectory resolves to
    /// the exe's directory even for the single-file publish.</summary>
    public static string Path_ => System.IO.Path.Combine(AppContext.BaseDirectory, Name + "_data");

    /// <summary>A file inside the data directory. Every store composes through this,
    /// so the storage location is one fact instead of nine copies of it.</summary>
    public static string FileIn(string name) => System.IO.Path.Combine(Path_, name);

    /// <summary>
    /// Create the folder up front, so where state lives is visible before there is any
    /// state. A failure (exe in a read-only place) surfaces as each store's own error
    /// path rather than as a crash here.
    /// </summary>
    public static void EnsureExists()
    {
        try { Directory.CreateDirectory(Path_); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
