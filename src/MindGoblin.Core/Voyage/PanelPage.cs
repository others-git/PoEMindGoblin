namespace MindGoblin.Core.Voyage;

/// <summary>
/// One TAB of the chart inventory.
///
/// The game paginates the panel, and a screenshot can only ever show the tab that is
/// open. That makes a panel index meaningless on its own -- "chart 7" is a different
/// chart on tab 1 and tab 2 -- so indices are numbered straight through the tabs
/// instead: tab 1 owns 1..Size, tab 2 owns Size+1..2*Size, and a chart's index says
/// which tab it is on as well as where.
///
/// Numbering through rather than storing a tab beside each chart keeps every existing
/// session file valid (its indices are all tab 1) and every consumer unchanged: the
/// solver, the plan and the stash search never need to know tabs exist.
/// </summary>
/// <param name="Number">1-based tab number.</param>
/// <param name="Size">Cells per tab -- rows x columns of ONE tab.</param>
public readonly record struct PanelPage(int Number, int Size)
{
    /// <summary>
    /// Every index at once: what a read means when the panel has a single tab, and the
    /// behaviour every caller had before tabs existed. A read scoped to All reconciles
    /// the whole session, which is right when the whole session is what was on screen.
    /// </summary>
    public static readonly PanelPage All = new(0, 0);

    public bool IsAll => Size <= 0;

    /// <summary>First global index this tab owns.</summary>
    public int First => IsAll ? 1 : (Math.Max(1, Number) - 1) * Size + 1;

    /// <summary>Last global index this tab owns.</summary>
    public int Last => IsAll ? int.MaxValue : Math.Max(1, Number) * Size;

    public bool Contains(int globalIndex) => globalIndex >= First && globalIndex <= Last;

    /// <summary>Turn a cell index the reader produced (1..Size, counted from the top-left
    /// of whatever tab was on screen) into the index this session stores it under.</summary>
    public int ToGlobal(int localIndex) => IsAll ? localIndex : First + localIndex - 1;

    /// <summary>The inverse, for drawing: which cell of this tab a stored index sits in.</summary>
    public int ToLocal(int globalIndex) => IsAll ? globalIndex : globalIndex - First + 1;

    /// <summary>
    /// How a chart is NAMED to the user, everywhere.
    ///
    /// The plan's whole job is "go and fetch this one", and on a tabbed inventory a bare
    /// number cannot say where: chart 67 is not the sixty-seventh thing the user can
    /// see, it is the seventh cell of the second tab. So a multi-tab panel names charts
    /// "2·7" and a single-tab one stays plain "7" -- nobody should read a tab number
    /// that never changes.
    ///
    /// One function because the board, the panel tile, the plan and every status line
    /// have to agree. Two numbering schemes on one screen is worse than either.
    /// </summary>
    public static string Label(int globalIndex, int pageSize, int pages)
    {
        if (pages <= 1 || pageSize <= 0) return globalIndex.ToString();
        var page = (globalIndex - 1) / pageSize + 1;
        var cell = (globalIndex - 1) % pageSize + 1;
        return cell + Superscript(page);
    }

    /// <summary>
    /// The tab as an EXPONENT -- "32²" is cell 32 of tab 2.
    ///
    /// Unicode superscript digits rather than rich text, because a chart gets named in
    /// plain strings as often as in a TextBlock: status lines, tooltips, the slurp's
    /// running commentary. One representation that works in all of them beats a pretty
    /// one that works in two and degrades to markup in the rest.
    ///
    /// It reads as an annotation rather than another number, which is the point: the
    /// cell is what the user looks for, the tab is only which drawer it is in.
    /// </summary>
    private static string Superscript(int number)
    {
        const string digits = "⁰¹²³⁴⁵⁶⁷⁸⁹";
        var text = number.ToString();
        return string.Concat(text.Select(c => char.IsAsciiDigit(c) ? digits[c - '0'] : c));
    }
}
