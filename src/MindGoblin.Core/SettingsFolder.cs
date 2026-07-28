namespace MindGoblin.Core;

/// <summary>
/// Where the app keeps its files, and the one-time move from where it used to keep them.
///
/// Renaming the app renamed its settings folder, which would have silently orphaned
/// everything: the Voyage session (a screenshot and dozens of hovers), the panel
/// calibration, the tuned rule profiles. All of it would still have been on disk, under a
/// name nothing looked at any more, and the app would simply have started empty.
///
/// So the first run under the new name brings the old folder across. Deliberately a COPY
/// rather than a move -- if this goes wrong the old folder is still there to fall back to
/// -- and it never overwrites, so running an older build afterwards cannot clobber
/// anything.
/// </summary>
public static class SettingsFolder
{
    public const string Name = "MindGoblin";

    /// <summary>What the folder was called before the rename.</summary>
    public const string PreviousName = "PoeMarketWatch";

    /// <summary>
    /// Files NOT brought across.
    ///
    /// credentials.dat held a DPAPI-encrypted session cookie for the trade site. Nothing
    /// reads it any more -- the whole live-search stack is gone -- so copying it forward
    /// would only spread an encrypted secret to a second location for no reason.
    /// </summary>
    private static readonly string[] Obsolete = ["credentials.dat"];

    public static string Path_ => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name);

    public static string PreviousPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), PreviousName);

    /// <summary>The names of any files carried over. Empty when there was nothing to do.</summary>
    public static IReadOnlyList<string> MigrateFromPreviousName(
        string? from = null, string? to = null)
    {
        from ??= PreviousPath;
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
