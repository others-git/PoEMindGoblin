namespace MindGoblin.Core.Voyage;

/// <summary>Board edge directions, clockwise from north.</summary>
public enum Side { North = 0, East = 1, South = 2, West = 3 }

/// <summary>
/// The five chart shapes. These are not invented -- they are the exact values of the
/// trade site's <c>chart_shape</c> filter, so the set is closed and authoritative.
/// </summary>
public enum ChartShape
{
    /// <summary>One open edge. The half-step dead end.</summary>
    End,
    /// <summary>Two open edges at right angles.</summary>
    Corner,
    /// <summary>Two open edges facing away from each other.</summary>
    Straight,
    /// <summary>Three open edges (a T).</summary>
    Junction,
    /// <summary>All four edges open.</summary>
    Crossing,
}

/// <summary>
/// A chart shape in a specific rotation, reduced to which of its four edges carry a path.
///
/// Everything downstream -- placement, connectivity, the solver -- works on this bitmask
/// rather than on shapes and angles, which keeps the rules in one place and makes the
/// symmetry of Straight and Crossing fall out for free instead of needing special cases.
/// </summary>
public readonly record struct ChartFace(ChartShape Shape, int Rotation)
{
    /// <summary>Rotation in quarter turns clockwise, normalised to 0-3.</summary>
    public int Rotation { get; } = ((Rotation % 4) + 4) % 4;

    /// <summary>Open edges of the shape at rotation 0.</summary>
    private static Side[] BaseSides(ChartShape shape) => shape switch
    {
        ChartShape.End => [Side.North],
        ChartShape.Corner => [Side.North, Side.East],
        ChartShape.Straight => [Side.North, Side.South],
        ChartShape.Junction => [Side.North, Side.East, Side.South],
        ChartShape.Crossing => [Side.North, Side.East, Side.South, Side.West],
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };

    /// <summary>True when this face has a path leading out of the given side.</summary>
    public bool IsOpen(Side side)
    {
        foreach (var b in BaseSides(Shape))
            if ((int)b + Rotation is var rotated && (Side)(rotated % 4) == side)
                return true;
        return false;
    }

    public IEnumerable<Side> OpenSides()
    {
        foreach (var side in Enum.GetValues<Side>())
            if (IsOpen(side)) yield return side;
    }

    public int OpenCount() => OpenSides().Count();

    /// <summary>
    /// The rotations that produce genuinely different edge patterns.
    ///
    /// Without this a solver wastes most of its search on duplicates: a Straight has only
    /// two distinct orientations, not four, and a Crossing has exactly one.
    /// </summary>
    public static IReadOnlyList<int> DistinctRotations(ChartShape shape) => shape switch
    {
        ChartShape.Crossing => [0],
        ChartShape.Straight => [0, 1],
        _ => [0, 1, 2, 3],
    };

    public static IEnumerable<ChartFace> AllFaces(ChartShape shape) =>
        DistinctRotations(shape).Select(r => new ChartFace(shape, r));

    public override string ToString() =>
        $"{Shape}@{Rotation * 90}° [{string.Join(",", OpenSides())}]";
}

public static class SideExtensions
{
    public static Side Opposite(this Side side) => (Side)(((int)side + 2) % 4);

    /// <summary>Grid delta for a side, with row increasing southwards.</summary>
    public static (int dRow, int dCol) Delta(this Side side) => side switch
    {
        Side.North => (-1, 0),
        Side.East => (0, 1),
        Side.South => (1, 0),
        Side.West => (0, -1),
        _ => (0, 0),
    };
}
