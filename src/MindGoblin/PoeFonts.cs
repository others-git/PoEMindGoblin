using System.IO;
using System.Windows.Media;
using MindGoblin.Core;

namespace MindGoblin;

/// <summary>
/// The game's own typeface, when it can be had. Fontin (exljbris freeware) is what PoE's
/// UI uses, but its license forbids redistribution -- the TTFs cannot ship in this repo
/// or its releases. So they load PRIVATELY from MindGoblin_data/fonts/ when the user has
/// dropped them there, fall back to a system-installed Fontin, and land on Georgia when
/// neither exists. The composite family strings make the fallback WPF's job, not ours.
/// </summary>
public static class PoeFonts
{
    /// <summary>Headers, big numbers, anything display-weight: Fontin SmallCaps.</summary>
    public static readonly FontFamily Display;

    /// <summary>Prose: Fontin Regular.</summary>
    public static readonly FontFamily Body;

    static PoeFonts()
    {
        var dir = Path.Combine(SettingsFolder.Path_, "fonts");
        if (File.Exists(Path.Combine(dir, "Fontin-SmallCaps.ttf")))
        {
            var baseUri = new Uri(dir + Path.DirectorySeparatorChar);
            Display = new FontFamily(baseUri, "./#Fontin SmallCaps, Georgia");
            Body = new FontFamily(baseUri, "./#Fontin, Georgia");
        }
        else
        {
            Display = new FontFamily("Fontin SmallCaps, Palatino Linotype, Georgia");
            Body = new FontFamily("Fontin, Georgia");
        }
    }
}
