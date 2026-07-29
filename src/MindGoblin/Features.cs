namespace MindGoblin;

/// <summary>
/// Feature switches, deliberately compile-time: a hidden tool should cost nothing at
/// startup and leave no half-working UI to stumble into.
///
/// static readonly rather than const so the guarded branches stay compiled (const false
/// makes them unreachable and the compiler starts warning about the code it will delete).
/// </summary>
internal static class Features
{
    /// <summary>
    /// The Gem RoI tab. OFF until it earns its way back: the pricing side needs a fresh
    /// look before it ships, and the Voyage planner is the tool being used. The code and
    /// its tests stay in the build so it cannot rot unnoticed in the meantime.
    /// </summary>
    public static readonly bool GemRoi = false;
}
