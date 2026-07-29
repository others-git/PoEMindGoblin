namespace MindGoblin.Core;

/// <summary>
/// Where the app keeps its files: a <c>data</c> directory NEXT TO THE EXE.
///
/// Deliberately not %LOCALAPPDATA%. This is a portable tool -- one exe you put
/// somewhere -- and its state belongs beside it, where it can be found, backed up,
/// or deleted by deleting the folder. Windows conventions scatter app state into
/// hidden roaming dirs that outlive the app; nothing here is worth hiding.
///
/// Earlier builds DID use %LOCALAPPDATA% (first as PoeMarketWatch, then as
/// MindGoblin), so the first run migrates whatever those folders hold: the Voyage
/// session is a screenshot and dozens of hovers, and starting empty because the
/// storage convention changed would be losing the user's work to an opinion.
/// Migration is a COPY and never overwrites -- if it goes wrong, the originals are
/// still there.
/// </summary>
public static class SettingsFolder
{
    /// <summary>The directory name, kept for the legacy %LOCALAPPDATA% path.</summary>
    public const string Name = "MindGoblin";

    /// <summary>What the folder was called before the rename.</summary>
    public const string PreviousName = "PoeMarketWatch";

    /// <summary>Files NOT brought across: credentials.dat held a DPAPI cookie for the
    /// deleted trade client; nothing reads it, so copying it would only spread it.</summary>
    private static readonly string[] Obsolete = ["credentials.dat"];

    /// <summary>data/ beside the exe. AppContext.BaseDirectory resolves to the exe's
    /// directory even for the single-file publish -- the assets/ load relies on the
    /// same fact.</summary>
    public static string Path_ => System.IO.Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>A file inside the data directory. Every store composes through this,
    /// so the storage location is one fact instead of nine copies of it.</summary>
    public static string FileIn(string name) => System.IO.Path.Combine(Path_, name);

    private static string LegacyPath(string name) => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), name);

    /// <summary>
    /// Bring files across from the %LOCALAPPDATA% era, newest name first -- copying
    /// never overwrites, so order is priority. Returns the names carried.
    /// </summary>
    public static IReadOnlyList<string> MigrateFromLegacyLocations()
    {
        // The folder exists from the very first run, so where state lives is visible
        // before there is any state -- and a failure (exe in a read-only place) surfaces
        // as each store's own error path rather than as a crash here.
        try { Directory.CreateDirectory(Path_); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        return [.. MigrateFromPreviousName(LegacyPath(Name))
            .Concat(MigrateFromPreviousName(LegacyPath(PreviousName)))];
    }

    /// <summary>The names of any files carried over. Empty when there was nothing to do.</summary>
    public static IReadOnlyList<string> MigrateFromPreviousName(
        string? from = null, string? to = null)
    {
        from ??= LegacyPath(PreviousName);
        to ??= Path_;

        var moved = new List<string>();
        try
        {
            if (!Directory.Exists(from) || string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
                return moved;

            Directory.CreateDirectory(to);
            foreach (var source in Directory.GetFiles(from))
            {
                var name = System.IO.Path.GetFileName(source);
                if (Obsolete.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;

                // Never overwrite: a file already here is the current one.
                var destination = System.IO.Path.Combine(to, name);
                if (File.Exists(destination)) continue;

                File.Copy(source, destination);
                moved.Add(name);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing the old settings is bad; failing to start over it is worse.
        }
        return moved;
    }
}
