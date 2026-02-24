namespace CivSim.Core;

/// <summary>
/// GDD v1.8: Personality traits that modify utility scoring weights.
/// Each agent gets 2 traits at birth (can be duplicates for double-stacking).
/// Traits are inherited from parents with 15% mutation chance per slot.
/// </summary>
public enum PersonalityTrait
{
    Builder,   // +40% Build/Craft, +20% GatherResource
    Explorer,  // +50% Explore, -20% ReturnHome
    Social,    // +40% Knowledge Propagation Speed, +40% Socialize, +20% Reproduce
    Cautious,  // +30% GatherFood, +30% ReturnHome, -20% Explore
    Curious    // +50% Experiment, +20% Explore
}
