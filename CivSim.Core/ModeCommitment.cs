namespace CivSim.Core;

/// <summary>
/// v1.8 Behavioral Modes: Mode-specific committed state set on mode entry.
/// Each mode stores different data; unused fields are null/default.
/// Cleared on every mode transition via Agent.TransitionMode().
/// </summary>
public class ModeCommitment
{
    // ── Forage ──────────────────────────────────────────────────────────
    /// <summary>Resource type being foraged for (food, wood, stone, ore).</summary>
    public ResourceType? ForageTargetResource { get; set; }

    /// <summary>Target tile to forage at (from memory/perception).</summary>
    public (int X, int Y)? ForageTargetTile { get; set; }

    /// <summary>Inventory food count that triggers return home.</summary>
    public int ForageReturnFoodThreshold { get; set; } = 6;

    // ── Build ───────────────────────────────────────────────────────────
    /// <summary>Recipe ID of the build project (lean_to, granary, etc.).</summary>
    public string? BuildRecipeId { get; set; }

    /// <summary>Tile where the build project is located.</summary>
    public (int X, int Y)? BuildTargetTile { get; set; }

    // ── Explore ─────────────────────────────────────────────────────────
    /// <summary>Direction vector (dx, dy) for committed exploration.</summary>
    public (int Dx, int Dy)? ExploreDirection { get; set; }

    /// <summary>Maximum ticks to explore before mandatory return.</summary>
    public int ExploreBudget { get; set; } = 300;

    /// <summary>Clears all committed state. Called on every mode transition.</summary>
    public void Clear()
    {
        ForageTargetResource = null;
        ForageTargetTile = null;
        ForageReturnFoodThreshold = 6;
        BuildRecipeId = null;
        BuildTargetTile = null;
        ExploreDirection = null;
        ExploreBudget = 300;
    }
}
