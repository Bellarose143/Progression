namespace CivSim.Core;

/// <summary>
/// Discrete actions an agent can perform.
/// GDD v1.8 Corrections: 1 tick ≈ 2 sim-minutes. 480 ticks/sim-day.
/// Movement uses float-based progress (sub-tick per tile on plains).
/// GDD v1.8: Teach action REMOVED — knowledge propagation is communal within settlements.
/// </summary>
public enum ActionType
{
    Idle,        // No action — resting in place (still gets base health regen)
    Move,        // Relocating to an adjacent tile (0.125-0.44 ticks based on terrain)
    Gather,      // Collecting resources from the current tile (10-15 ticks based on tools)
    Eat,         // Consuming food from inventory (~6 ticks / 10-15 min)
    Rest,        // Intentional rest — elevated health regen (240 ticks / ~8 hours)
    Craft,       // Attempting a recipe that produces an item or tool (15-30+ ticks)
    Build,       // Contributing build progress to a structure (105-2160+ ticks)
    Experiment,  // Attempting a recipe that produces a discovery/knowledge unlock (45 ticks)
    Reproduce,   // Attempting reproduction with a nearby eligible agent
    TendFarm,    // Working a farm tile to boost grain production (45 ticks)
    Explore,     // Moving toward edge of known area
    Cook,        // GDD v1.7: Visible cooking action (12 ticks / ~24 min)
    Preserve,    // GDD v1.7: Food preservation action (45 ticks / ~1.5 hours)
    Deposit,     // GDD v1.7: Deposit food into a granary structure (2 ticks / ~4 min)
    DepositHome, // GDD v1.8 Section 7: Deposit food into home shelter storage (2 ticks / ~4 min)
    Withdraw,    // GDD v1.7: Withdraw food from a granary structure (2 ticks / ~4 min)
    Socialize,   // GDD v1.7.1: Move toward a visible agent for social interaction
    ReturnHome,  // GDD v1.7.1: Move toward home tile (quadratic pull)
    ShareFood,   // GDD v1.7.2: Give food to a starving adjacent agent (2 ticks / ~4 min)
    ClearLand,   // Directive #5 Fix 3b: Clear vegetation from adjacent tile for farming/building (40 ticks)
    TendAnimals, // Directive #5 Fix 3c: Tend animals at an animal pen for food production (25 ticks)
    Hunt,        // D25b: Pursue and kill an animal entity for meat
    Harvest,     // D25b: Butcher a carcass to produce meat (3 ticks)
    Combat,      // D25c: Fighting a dangerous animal (tick-by-tick damage exchange)
    SetTrap,     // D25c: Place a trap for passive rabbit catching (2 ticks)
    GrowingUp,   // D19.2: Display-only — child/youth inactivity, separate from adult Idle in diagnostics
    Tame,        // D25d: Offer food to a wild animal to tame it (multi-tick)
    PenAnimal,   // D25d: Lead a following domesticated animal into a pen (1 tick)
    ReleasePenAnimal, // D25d: Release an animal from a pen to follow agent (1 tick)
    FeedPen,     // D25d: Deposit grain from inventory into pen food store (1 tick)
    Slaughter    // D25d: Butcher a penned animal for Meat/Hide/Bone (2 ticks)
}
