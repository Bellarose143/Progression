namespace CivSim.Core;

/// <summary>
/// Default terrain modifiers per biome.
/// Tiles start with these values but can be overridden individually
/// (e.g., a road through a Forest reduces movement cost).
/// </summary>
public static class BiomeDefaults
{
    /// <summary>
    /// Movement cost multiplier — higher means slower traversal.
    /// Base movement costs 1 tick; actual cost = 1 * multiplier (rounded up).
    /// </summary>
    public static readonly Dictionary<BiomeType, float> MovementCost = new()
    {
        { BiomeType.Forest,   1.5f },
        { BiomeType.Plains,   1.0f },
        { BiomeType.Mountain, 2.5f },
        { BiomeType.Water,    float.PositiveInfinity },
        { BiomeType.Desert,   1.2f }
    };

    /// <summary>
    /// Gathering efficiency multiplier — higher means more resources per gather action.
    /// </summary>
    public static readonly Dictionary<BiomeType, float> GatheringEfficiency = new()
    {
        { BiomeType.Forest,   1.3f },
        { BiomeType.Plains,   1.0f },
        { BiomeType.Mountain, 0.7f },
        { BiomeType.Water,    1.0f },
        { BiomeType.Desert,   0.4f }
    };

    /// <summary>
    /// Default per-tile resource capacity limits by biome.
    /// </summary>
    public static readonly Dictionary<BiomeType, int> ResourceCapacity = new()
    {
        { BiomeType.Forest,   20 },
        { BiomeType.Plains,   15 },
        { BiomeType.Mountain, 25 },
        { BiomeType.Water,    10 },
        { BiomeType.Desert,    5 }
    };
}
