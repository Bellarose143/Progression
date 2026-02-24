namespace CivSim.Core;

/// <summary>
/// Types of biomes in the world. Each biome has different resource availability.
/// </summary>
public enum BiomeType
{
    Forest,   // Wood, food (berries/animals)
    Plains,   // Food, farming potential, open space
    Mountain, // Stone, ore/metals
    Water,    // Fish, blocks land movement
    Desert    // Sparse resources, harsh environment
}
