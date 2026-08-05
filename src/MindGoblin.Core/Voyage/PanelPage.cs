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
}
