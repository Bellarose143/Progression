namespace CivSim.Core;

/// <summary>
/// All gatherable/craftable resource types in the simulation.
/// Split from the V1 generic "Food" into distinct food sources so agents
/// can discover cooking, fishing, farming, and hunting independently.
/// GDD v1.7: added PreservedFood as output of food preservation.
/// </summary>
public enum ResourceType
{
    Wood,
    Stone,
    Berries,        // Forageable, edible raw
    Grain,          // Requires farming knowledge to cultivate; edible raw but better cooked
    Animals,        // Requires hunting; must be cooked for full value
    Ore,            // Smelting/metalworking input — unlocked at higher tech tiers
    Fish,           // Requires proximity to Water biome; edible raw or cooked
    PreservedFood   // GDD v1.7: Output of food preservation — dried meat, smoked fish. Restores 70 hunger.
}
