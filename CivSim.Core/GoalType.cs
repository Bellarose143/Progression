namespace CivSim.Core;

/// <summary>
/// High-level goals that commit an agent to multi-tick behavior sequences.
/// While a goal is active, the agent advances toward it instead of re-running full utility scoring.
/// This prevents decision thrashing where agents oscillate between tiles.
/// Goals are cleared on: completion, critical interrupt, impossibility, or staleness.
/// </summary>
public enum GoalType
{
    /// <summary>Move to target tile, then gather food resource.</summary>
    GatherFoodAt,

    /// <summary>Move to target tile, then gather non-food resource (wood, stone, etc.).</summary>
    GatherResourceAt,

    /// <summary>Move to home tile.</summary>
    ReturnHome,

    /// <summary>Move to build site tile, then build (utility scorer handles actual build action).</summary>
    BuildAtTile,

    /// <summary>Move toward remembered/scanned food (starving fallback).</summary>
    SeekFood,

    /// <summary>Move in a chosen direction for several tiles before re-evaluating.</summary>
    Explore,

    /// <summary>D25b: Move to animal's last known position, then pursue and kill.</summary>
    HuntAnimal,

    /// <summary>D25b: Move to carcass position, then harvest meat.</summary>
    HarvestCarcass,

    /// <summary>D25c: Move to target tile, then place trap.</summary>
    SetTrapAt,

    /// <summary>D25d: Move to animal's position, offer food to tame it.</summary>
    TameAnimal
}
