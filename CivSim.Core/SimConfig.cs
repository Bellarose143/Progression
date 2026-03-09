namespace CivSim.Core;

/// <summary>
/// Central tuning knobs for the entire simulation.
/// All magic numbers live here so balance changes are one-stop edits.
/// Values are static readonly (not const) so they can be replaced at startup
/// from a config file or CLI args without recompilation.
///
/// GDD v1.8 Time Model Corrections (approved Feb 2026):
///   Tick = internal simulation heartbeat (INVISIBLE to observer). 1 tick ≈ 2 sim-minutes.
///   Sim-day = the basic observable calendar unit = 480 ticks.
///   1 season = 28 sim-days (4 weeks), 1 year = 4 seasons = 112 sim-days = 53760 ticks.
///   World = 350×350 grid, each tile ≈ 15 ft (~5 m) across.
///   Movement is float-based progress per tick. Renderer interpolates at 60fps.
///
/// [RECALIBRATE] marks values that need tuning in a dedicated calibration pass.
///
/// World Scale Migration (Phase A):
///   Old baseline was 64×64 (~300 ft/tile). New baseline is 350×350 (~15 ft/tile).
///   All distance/range constants have been recalibrated for the new scale.
///   Old 64×64 values are noted in comments for reference.
/// </summary>
public static class SimConfig
{
    // ── World ──────────────────────────────────────────────────────────
    /// <summary>Default world size. 350×350 tiles (~15 ft/tile). Parameterized so tests can use smaller grids.
    /// Old 64×64 baseline: DefaultGridWidth=64, DefaultGridHeight=64.</summary>
    public static int DefaultGridWidth  = 350;
    public static int DefaultGridHeight = 350;

    // ── Distance Constants (recalibrated for 350×350, ~15 ft/tile) ──
    // Old 64×64 values noted in comments for reference.

    /// <summary>Home mode local gather radius (tiles). Old 64×64: 5.</summary>
    public static int HomeGatherRadius      = 8;
    /// <summary>Caretaker short-range gather tether (tiles). Old 64×64: 8.</summary>
    public static int CaretakerTetherRadius  = 10;
    /// <summary>Phase 1 distance safety check threshold (tiles). Old 64×64: 15.</summary>
    public static int SafetyReturnDist       = 40;
    /// <summary>Absolute non-Explore/non-Forage move ceiling (tiles). Old 64×64: 20.</summary>
    public static int HardMoveCeiling        = 50;
    /// <summary>Max forage trip distance from home (tiles). Old 64×64: 30.</summary>
    public static int ForageMaxRange         = 100;
    /// <summary>Max explore trip distance from home (tiles). Old 64×64: 45.</summary>
    public static int ExploreMaxRange        = 120;
    /// <summary>Home mode hard tether — return home if farther than this (tiles). Old 64×64: 10.</summary>
    public static int HomeTetherRadius       = 25;
    /// <summary>Caretaker distance escape threshold (tiles). Old 64×64: 12.</summary>
    public static int CaretakerEscapeDist    = 30;
    /// <summary>Caretaker ModeTransitionManager escape threshold (tiles). Old 64×64: 15.</summary>
    public static int CaretakerMTMEscapeDist = 40;
    /// <summary>D14 Fix 2: Home/Caretaker goal-continuation move leash for adults (tiles). Old 64×64: 15.</summary>
    public static int HomeModeMoveLeash      = 40;
    /// <summary>D14 Fix 2: Home/Caretaker goal-continuation move leash for children (tiles). Old 64×64: 10.</summary>
    public static int HomeModeMoveLeashChild = 25;

    // ── New 350×350 Distance Constants ────────────────────────────────
    /// <summary>Max range from settlement center for placing structures (tiles).</summary>
    public static readonly int StructureBuildRange = 20;
    /// <summary>Caretaker mode general range from home (tiles).</summary>
    public static readonly int CaretakerRange = 10;
    /// <summary>Home mode local gather range (tiles). Alias for HomeGatherRadius for clarity.</summary>
    public static readonly int HomeLocalGatherRange = 8;
    /// <summary>Radius to check food surplus around settlement (tiles).</summary>
    public static readonly int FoodSurplusCheckRadius = 15;
    /// <summary>Radius to check for nearby shelter (tiles).</summary>
    public static readonly int ShelterNearbyRadius = 15;

    // ══════════════════════════════════════════════════════════════════
    // TIME MODEL (GDD v1.8 Corrections — 480 ticks/day, 1 tick ≈ 2 min)
    // ══════════════════════════════════════════════════════════════════

    /// <summary>v1.8 Corrections: 480 ticks per sim-day. 1 tick ≈ 2 sim-minutes.
    /// Fine enough for proportional movement, mid-action interrupts, smooth visuals.</summary>
    public static readonly int TicksPerSimDay = 480;

    /// <summary>Sim-days per season. 28 sim-days (4 weeks) = 1 season.</summary>
    public static readonly int SimDaysPerSeason = 28;

    /// <summary>Seasons per year. 4 seasons = 1 year.</summary>
    public static readonly int SeasonsPerYear = 4;

    /// <summary>Sim-days per year. 112 sim-days = 1 year.</summary>
    public static readonly int SimDaysPerYear = 112; // SimDaysPerSeason * SeasonsPerYear

    /// <summary>Ticks per year (computed). 480 × 112 = 53760 ticks.</summary>
    public static readonly int TicksPerYear = 53760; // TicksPerSimDay * SimDaysPerYear

    // ── Hunger & Starvation ────────────────────────────────────────────
    // v1.8 Corrections: 480 ticks/day. Target behavior unchanged:
    //   Adult hunger 100→0 in ~1.5 sim-days without food = 720 ticks. 100/720 ≈ 0.139/tick.
    //   Starvation damage: death in ~2 sim-days at hunger 0 = 960 ticks. 100/960 ≈ 0.104/tick → use 0.1.

    /// <summary>v1.8 Corrections: Hunger drained per tick for adult agents.
    /// At 0.14/tick, hunger 100→0 in ~714 ticks (~1.5 sim-days).</summary>
    public static readonly float HungerDrainPerTick = 0.14f;

    /// <summary>Hunger drain per tick for Infant stage (0-5 years). Half of adult.</summary>
    public static readonly float InfantHungerDrain = 0.07f;

    /// <summary>Hunger drain per tick for Youth stage (5-12 years). 75% of adult.</summary>
    public static readonly float YouthHungerDrain = 0.105f;

    /// <summary>v1.8 Corrections: HP damage per tick while Hunger is at 0 (starving).
    /// Target: ~2 sim-days (960 ticks) from full health to death. 100/960 ≈ 0.1.
    /// Using float accumulator in UpdateNeeds for sub-integer damage.</summary>
    public static readonly float StarvationDamagePerTick = 0.1f;

    /// <summary>Hunger threshold at which multi-tick actions are interrupted to seek food.</summary>
    public static readonly float InterruptHungerThreshold = 30f;

    // ── Health Regeneration ────────────────────────────────────────────
    // v1.8 Corrections: With 480 ticks/day, integer HP regen per tick is way too fast.
    // Use float-based regen accumulator. Target: full heal in ~1 sim-day resting in shelter.
    // 100 HP / 480 ticks ≈ 0.21/tick resting+shelter. Scale others proportionally.

    /// <summary>v1.8 Corrections: HP restored per tick (baseline). Float accumulator.
    /// Target: ~4 sim-days to full heal passively. 100/(480×4) ≈ 0.052.</summary>
    public static readonly float HealthRegenBase = 0.05f;

    /// <summary>v1.8 Corrections: HP restored per tick while performing the Rest action.
    /// Target: ~2 sim-days to full heal. 100/(480×2) ≈ 0.104.</summary>
    public static readonly float HealthRegenResting = 0.10f;

    /// <summary>v1.8 Corrections: HP restored per tick while resting inside a shelter.
    /// Target: ~1 sim-day to full heal. 100/480 ≈ 0.208.</summary>
    public static readonly float HealthRegenShelter = 0.21f;

    // ── Food ───────────────────────────────────────────────────────────
    /// <summary>Hunger points restored by eating raw (uncooked) food.</summary>
    public static readonly int FoodRestoreRaw = 40;

    /// <summary>Hunger points restored by eating cooked food (requires cooking knowledge).</summary>
    public static readonly int FoodRestoreCooked = 60;

    /// <summary>Hunger points restored by eating preserved food (dried meat, smoked fish).</summary>
    public static readonly int FoodRestorePreserved = 80;

    // ── Exposure ───────────────────────────────────────────────────────
    /// <summary>Exposure suppresses health regen rather than dealing direct damage.
    /// This value is the health regen multiplier when exposed (0 = no regen).</summary>
    public static readonly float ExposureRegenSuppression = 0f;

    /// <summary>Chebyshev radius from a shelter that counts as "sheltered". Old 64×64: 3.</summary>
    public static readonly int ExposureShelterRadius = 5;

    /// <summary>Multiplier for exposure effect when agent knows clothing/weaving (50% reduction).</summary>
    public static readonly float ExposureClothingReduction = 0.5f;

    // ── Death Report ──────────────────────────────────────────────────
    /// <summary>v1.8 Corrections: Ticks after death during which agent remains selectable.
    /// Target: ~5 sim-days. 5 × 480 = 2400 ticks.</summary>
    public static readonly int DeathReportDuration = 2400;

    /// <summary>Number of recent actions stored in ring buffer for death report.</summary>
    public static readonly int ActionHistorySize = 10;

    // ── Inventory ──────────────────────────────────────────────────────
    /// <summary>Maximum total items an agent can carry across all resource types.</summary>
    public static readonly int InventoryCapacity = 20;

    // ── Reproduction ───────────────────────────────────────────────────
    /// <summary>Minimum age before reproduction.
    /// 16 years × 53760 ticks/year = 860160 ticks.</summary>
    public static readonly int ReproductionMinAge = 860160;

    /// <summary>GDD v1.8 Section 3: Food consumed from parents on reproduction. [RECALIBRATE]
    /// Consumed from home storage first, then inventory.</summary>
    public static readonly int ReproductionFoodCost = 5;

    /// <summary>Cooldown between reproduction attempts.
    /// 2 years × 53760 ticks/year = 107520 ticks.</summary>
    public static readonly int ReproductionCooldown = 107520;

    /// <summary>GDD v1.8 Section 3: Base probability of reproduction succeeding.
    /// Kept for randomness after stability score gating. [RECALIBRATE]</summary>
    public static readonly float ReproductionBaseChance = 0.65f;

    // ── Stability Score (GDD v1.8 Section 3) ─────────────────────────
    /// <summary>Weight of food security in reproduction stability score.</summary>
    public static readonly float StabilityWeightFood = 0.4f;
    /// <summary>Weight of shelter quality in reproduction stability score.</summary>
    public static readonly float StabilityWeightShelter = 0.2f;
    /// <summary>Weight of existing dependents in reproduction stability score.</summary>
    public static readonly float StabilityWeightDependents = 0.2f;
    /// <summary>Weight of health trend in reproduction stability score.</summary>
    public static readonly float StabilityWeightHealth = 0.2f;

    /// <summary>Shelter quality score: lean-to (~0.4).</summary>
    public static readonly float ShelterQualityLeanTo = 0.4f;
    /// <summary>Shelter quality score: improved shelter (~0.7).</summary>
    public static readonly float ShelterQualityImproved = 0.7f;

    /// <summary>Stability score reduction per existing infant dependent.</summary>
    public static readonly float DependentReductionInfant = 0.4f;
    /// <summary>Stability score reduction per existing youth dependent.</summary>
    public static readonly float DependentReductionYouth = 0.2f;

    /// <summary>GDD v1.8 Section 3: Both agents must have shelter within this radius to reproduce. Old 64×64: 5.</summary>
    public static readonly int ReproductionShelterProximity = 15;

    // ── Initial Agent Age ──────────────────────────────────────────────
    /// <summary>Starting age for founding agents. 16 years × 53760 = 860160 ticks.</summary>
    public static readonly int InitialAgentAge = 860160;

    // ── Age & Mortality (GDD v1.8) ────────────────────────────────────
    /// <summary>Infant stage: 0 to 0.5 years. [RECALIBRATE] Reduced from 5 years (268800) for diagnostic runs.</summary>
    public static readonly int ChildInfantAge = TicksPerYear / 2; // 26880 ticks = 0.5 sim-years

    /// <summary>Youth stage: 0.5 to 1 year. [RECALIBRATE] Reduced from 12 years (645120) for diagnostic runs.</summary>
    public static readonly int ChildYouthAge = TicksPerYear; // 53760 ticks = 1 sim-year

    /// <summary>Old-age death chance begins. 55 × 53760 = 2956800 ticks.</summary>
    public static readonly int OldAgeThreshold = 2956800;

    /// <summary>Guaranteed natural death. 80 × 53760 = 4300800 ticks.</summary>
    public static readonly int GuaranteedDeathAge = 4300800;

    // ── Knowledge & Discovery ──────────────────────────────────────────
    /// <summary>Per-failed-attempt bonus added to an agent's next attempt at the same recipe.</summary>
    public static readonly float FamiliarityBonusPerFail = 0.02f;

    /// <summary>Maximum cumulative familiarity bonus (+20% at max fails).</summary>
    public static readonly float FamiliarityBonusCap = 0.20f;

    /// <summary>Maximum tracked failed attempts per recipe per agent.</summary>
    public static readonly int FamiliarityMaxFails = 10;

    /// <summary>GDD v1.8: Collaboration bonus per adjacent agent. +2% per agent, capped at +10%.</summary>
    public static readonly float CollaborationBonusPerAgent = 0.02f;

    /// <summary>GDD v1.8: Maximum collaboration bonus (cap). 10%.</summary>
    public static readonly float CollaborationBonusCap = 0.10f;

    /// <summary>Flat bonus to discovery chance when the agent possesses relevant tools.</summary>
    public static readonly float ToolDiscoveryBonus = 0.05f;

    // ── Communal Knowledge Propagation (GDD v1.8 Section 2) ─────────
    /// <summary>GDD v1.8: Oral propagation window in sim-days (pre-writing).
    /// Knowledge takes this many sim-days to spread through the settlement.
    /// Reduced to 1 sim-day if settlement has oral_tradition.</summary>
    public static readonly int OralPropagationSimDays = 2;

    /// <summary>GDD v1.8: Oral propagation window in sim-days AFTER oral_tradition is discovered.</summary>
    public static readonly int OralPropagationWithTraditionSimDays = 1;

    /// <summary>GDD v1.8: Number of discoveries propagated through oral system before
    /// oral_tradition auto-triggers. Replaces the old teach-count threshold.</summary>
    public static readonly int OralTraditionPropagationThreshold = 5;

    // ── Stagnation Detection ───────────────────────────────────────────
    /// <summary>Ticks with no discoveries before stagnation warning.
    /// 5 years × 53760 ticks/year = 268800 ticks.</summary>
    public static readonly int StagnationWarningTicks = 268800;

    // ── Spatial ────────────────────────────────────────────────────────
    /// <summary>Radius (in tiles) an agent scans when evaluating tile viability. Old 64×64: 20.</summary>
    public static readonly int ViabilityScanRadius = 30;

    // ── Perception & Memory ─────────────────────────────────────────
    /// <summary>Chebyshev radius for full active perception scans. Old 64×64: 8.
    /// Default fallback; biome-specific radii below are used when biome-dependent perception is enabled.</summary>
    public static readonly int PerceptionRadius = 20;

    // ── Biome-Specific Perception Radii ──
    /// <summary>Perception radius on Plains tiles.</summary>
    public static readonly int PerceptionRadiusPlains = 30;
    /// <summary>Perception radius on Forest tiles.</summary>
    public static readonly int PerceptionRadiusForest = 12;
    /// <summary>Perception radius on Mountain tiles.</summary>
    public static readonly int PerceptionRadiusMountain = 20;
    /// <summary>Perception radius on Desert tiles.</summary>
    public static readonly int PerceptionRadiusDesert = 35;
    /// <summary>Perception radius when on a tile adjacent to water (open sightlines).</summary>
    public static readonly int PerceptionRadiusWaterEdge = 25;

    /// <summary>Returns the perception radius for the given biome type.</summary>
    public static int GetPerceptionRadius(BiomeType biome) => biome switch
    {
        BiomeType.Plains => PerceptionRadiusPlains,
        BiomeType.Forest => PerceptionRadiusForest,
        BiomeType.Mountain => PerceptionRadiusMountain,
        BiomeType.Desert => PerceptionRadiusDesert,
        BiomeType.Water => PerceptionRadius, // Agents shouldn't be on water, fallback
        _ => PerceptionRadius,
    };

    /// <summary>Returns the biome-based perception multiplier relative to the default PerceptionRadius.
    /// Used to scale animal detection ranges by biome.</summary>
    public static float GetBiomePerceptionMultiplier(BiomeType biome) =>
        (float)GetPerceptionRadius(biome) / PerceptionRadius;

    /// <summary>v1.8 Corrections: Chebyshev radius for immediate perception (every tick).
    /// 1-2 tiles per corrections doc. Used for interrupt checks.</summary>
    public static readonly int PerceptionImmediateRadius = 2;

    /// <summary>v1.8 Corrections: Ticks between active perception scans when idle.
    /// ~1 hour of sim-time = 30 ticks.</summary>
    public static readonly int PerceptionActiveIdleInterval = 30;

    /// <summary>v1.8 Corrections: Ticks between active perception scans when busy.
    /// ~2 hours of sim-time = 60 ticks.</summary>
    public static readonly int PerceptionActiveBusyInterval = 60;

    /// <summary>v1.8 Corrections: Memory entries older than this many ticks are purged.
    /// Target: ~30 sim-days. 30 × 480 = 14400 ticks.</summary>
    public static readonly int MemoryDecayTicks = 14400;

    /// <summary>Maximum memory entries an agent can retain.</summary>
    public static readonly int MemoryMaxEntries = 30;

    // ── Spawn Clustering ────────────────────────────────────────────
    /// <summary>Radius around spawn center within which new agents are placed. Old 64×64: 5.</summary>
    public static readonly int SpawnClusterRadius = 12;

    // ── Movement ────────────────────────────────────────────────────
    /// <summary>Extra hunger drain from terrain: round((cost - 1.0) * scale).</summary>
    public static readonly float MovementHungerCostScale = 0.5f;

    /// <summary>v1.8 Corrections: Base walking speed in meters per sim-minute.
    /// ~3.5 km/h = ~58 meters per minute.</summary>
    public static readonly float BaseWalkingSpeedMPerMin = 58f;

    /// <summary>v1.8 Corrections: Tile size in meters (~15m per tile).</summary>
    public static readonly float TileSizeMeters = 15f;

    // ── Movement Durations (v1.8 Corrections — float ticks per tile) ──
    // 1 tick = 2 sim-minutes. Plains tile (15m at 58m/min) = 15/58 = 0.259 min = 0.129 ticks.
    // Rounded to the values from the corrections doc action table.

    /// <summary>v1.8 Corrections: Ticks to walk 1 plains tile. ~15 sec → 0.125 ticks.</summary>
    public static readonly float MoveDurationPlains = 0.125f;

    /// <summary>v1.8 Corrections: Ticks to walk 1 forest tile. ~22 sec → 0.18 ticks.</summary>
    public static readonly float MoveDurationForest = 0.18f;

    /// <summary>v1.8 Corrections: Ticks to walk 1 desert tile. ~20 sec → 0.17 ticks.</summary>
    public static readonly float MoveDurationDesert = 0.17f;

    /// <summary>v1.8 Corrections: Ticks to walk 1 mountain tile. ~45-60 sec → 0.375-0.5 ticks.
    /// Using midpoint 0.44.</summary>
    public static readonly float MoveDurationMountain = 0.44f;

    // ── Resource Regeneration (per-resource tick intervals) ─────────
    // v1.8 Corrections: Regen intervals in ticks. Same sim-day targets, now × 480.

    /// <summary>v1.8 Corrections: Wood regenerates 1 unit every 15 sim-days = 7200 ticks.</summary>
    public static readonly int RegenIntervalWood = 7200;
    /// <summary>v1.8 Corrections: Berries regenerate 1 unit every 8 sim-days = 3840 ticks.</summary>
    public static readonly int RegenIntervalBerries = 3840;
    /// <summary>v1.8 Corrections: Animals regenerate 1 unit every 20 sim-days = 9600 ticks.</summary>
    public static readonly int RegenIntervalAnimals = 9600;
    /// <summary>v1.8 Corrections: Fish regenerate 1 unit every 8 sim-days = 3840 ticks.</summary>
    public static readonly int RegenIntervalFish = 3840;
    /// <summary>Stone does NOT regenerate (non-renewable). 0 = disabled.</summary>
    public static readonly int RegenIntervalStone = 0;
    /// <summary>Ore does NOT regenerate (non-renewable). 0 = disabled.</summary>
    public static readonly int RegenIntervalOre = 0;
    /// <summary>v1.8 Corrections: Wild/untended grain regenerates every 10 sim-days = 4800 ticks.</summary>
    public static readonly int RegenIntervalGrain = 4800;
    /// <summary>v1.8 Corrections: Farmed grain regenerates every 3 sim-days when tended = 1440 ticks.</summary>
    public static readonly int RegenIntervalGrainFarmed = 1440;
    /// <summary>Amount of grain regenerated per interval on tended farm tiles.</summary>
    public static readonly int RegenAmountGrainFarmed = 2;

    // ── Per-Resource Capacities ─────────────────────────────────────
    public static readonly int CapacityWood = 20;
    public static readonly int CapacityBerries = 10;
    public static readonly int CapacityGrain = 15;
    public static readonly int CapacityAnimals = 5;
    public static readonly int CapacityFish = 8;
    public static readonly int CapacityStone = 30;
    public static readonly int CapacityOre = 10;

    // ── Building ────────────────────────────────────────────────────
    // v1.8 Corrections: Build lean-to ≈ 3-4 hours ≈ 90-120 ticks.

    /// <summary>v1.8 Corrections: Ticks to build a basic lean-to shelter. ~3-4 hours = 105 ticks.</summary>
    public static readonly int ShelterBuildTicks = 105;
    /// <summary>Wood consumed on lean-to shelter completion.</summary>
    public static readonly int ShelterWoodCost = 3;
    /// <summary>Stone consumed on lean-to shelter completion. Directive #11 Fix 2: 0 — a lean-to is sticks/leaves, no stone.</summary>
    public static readonly int ShelterStoneCost = 0;
    /// <summary>Skip building if any shelter exists within this Chebyshev radius. Old 64×64: 5.</summary>
    public static readonly int ShelterProximityRadius = 15;

    // ── Action Durations (v1.8 Corrections) ──────────────────────────
    // 1 tick = 2 sim-minutes. All values from the corrections doc action table.

    /// <summary>v1.8 Corrections: Eat a meal. ~10-15 min = 5-8 ticks.</summary>
    public static readonly int EatDuration = 6;

    /// <summary>v1.8 Corrections: Cook food. ~20-30 min = 10-15 ticks.</summary>
    public static readonly int CookDuration = 12;

    /// <summary>v1.8 Corrections: Base gather duration. ~20-30 min = 10-15 ticks.</summary>
    public static readonly int GatherDurationBase = 12;

    /// <summary>v1.8 Corrections: Rest (overnight). ~8 hours = 240 ticks.</summary>
    public static readonly int RestDuration = 240;

    /// <summary>v1.8 Corrections: Base experiment duration. ~1-2 hours = 30-60 ticks.</summary>
    public static readonly int ExperimentDurationBase = 45;

    /// <summary>v1.8 Corrections: Tend farm duration. ~1-2 hours = 30-60 ticks.</summary>
    public static readonly int TendFarmDuration = 45;

    /// <summary>v1.8 Corrections: Base craft (simple item). ~30 min - 1 hour = 15-30 ticks.</summary>
    public static readonly int CraftDurationBase = 22;

    /// <summary>v1.8 Corrections: Food preservation action. ~1-2 hours = 30-60 ticks.</summary>
    public static readonly int PreserveFoodDuration = 45;

    /// <summary>v1.8 Corrections: Share food. ~2-5 min = 1-3 ticks.</summary>
    public static readonly int ShareFoodDuration = 2;

    /// <summary>v1.8 Corrections: Store/deposit food. ~2-5 min = 1-3 ticks.</summary>
    public static readonly int DepositDuration = 2;

    // ── Granary ────────────────────────────────────────────────────
    /// <summary>Maximum total food items a granary can store.</summary>
    public static readonly int GranaryCapacity = 50;

    /// <summary>Wood consumed on granary construction.</summary>
    public static readonly int GranaryWoodCost = 5;

    /// <summary>Stone consumed on granary construction.</summary>
    public static readonly int GranaryStoneCost = 3;

    /// <summary>v1.8 Corrections: Ticks to build a granary. ~4-5 sim-days = 1920-2400 ticks.</summary>
    public static readonly int GranaryBuildTicks = 2160;

    // ── Home Storage (GDD v1.8 Section 7) ────────────────────────
    /// <summary>Section 7: Lean-to shelter food storage capacity.</summary>
    public static readonly int HomeStorageCapacityLeanTo = 10;
    /// <summary>Section 7: Improved shelter food storage capacity.</summary>
    public static readonly int HomeStorageCapacityImproved = 20;
    /// <summary>Section 7: Lean-to home storage decay — 1 food unit every 20 sim-days = 9600 ticks.</summary>
    public static readonly int HomeStorageDecayIntervalLeanTo = 9600;
    /// <summary>Section 7: Improved shelter decay — 1 food unit every 30 sim-days = 14400 ticks.</summary>
    public static readonly int HomeStorageDecayIntervalImproved = 14400;
    /// <summary>Section 7: Inventory food decay — 1 food unit every 10 sim-days = 4800 ticks. [RECALIBRATE]
    /// Creates mild pressure to return home and store food.</summary>
    public static readonly int InventoryFoodDecayInterval = 4800;
    /// <summary>Section 7: Base utility score for DepositHome action.</summary>
    public static readonly float DepositHomeBaseUtility = 0.4f;
    /// <summary>Section 7: Must have more than this food to consider depositing at home. [RECALIBRATE]</summary>
    public static readonly int DepositHomeFoodThreshold = 3;
    /// <summary>Fix 2A: Minimum total depositable inventory (food + materials) before DepositHome scores > 0.</summary>
    public const int DEPOSIT_HOME_MIN_THRESHOLD = 3;
    /// <summary>Fix 2B: Ticks after a successful DepositHome before it can score > 0 again.</summary>
    public const int DEPOSIT_HOME_COOLDOWN = 5;

    // ── D21 Fix 3: Opportunistic Pickup ────────────────────────────
    /// <summary>D21: Chance per eligible move to pick up 1 unit of Stone/Ore/Wood. [CALIBRATE LATER]</summary>
    public const float OPPORTUNISTIC_PICKUP_CHANCE = 0.25f;

    // ── D22: Non-food Inventory Thresholds (extracted from hardcoded values) ──
    /// <summary>D22: Non-food soft cap for opportunistic pickup eligibility.</summary>
    public const int NonFoodPickupCap = 5;
    /// <summary>D22: Non-food count below which gather scoring gives full score.</summary>
    public const int NonFoodGatherFullScoreCap = 5;
    /// <summary>D22: Non-food count at which gather scoring applies reduced multiplier.</summary>
    public const int NonFoodGatherReducedCap = 8;
    /// <summary>D22: Non-food hard cap — block all non-food adds at this count.</summary>
    public const int NonFoodInventoryHardCap = 7;
    /// <summary>D22: Scoring multiplier in the 5-8 non-food band.</summary>
    public const float NonFoodGatherReducedMultiplier = 0.5f;
    /// <summary>D22: Hunger threshold below which non-food pickup is blocked.</summary>
    public const float NonFoodPickupHungerGate = 50f;

    // ── Pressure Map Cadence ──────────────────────────────────────
    /// <summary>v1.8 Corrections: Rebuild pressure map every N ticks. ~1 sim-day = 480 ticks.</summary>
    public static readonly int PressureMapUpdateInterval = 480;

    /// <summary>If agent moves/tick exceeds this, force pressure map rebuild.</summary>
    public static readonly int PressureMapMigrationTrigger = 5;

    // ── Farm Placement ──────────────────────────────────────────────
    /// <summary>Maximum farm tiles per settlement before a granary is built.</summary>
    public static readonly int MaxFarmTilesPreGranary = 12;

    /// <summary>Preferred maximum Chebyshev distance from home for farm placement. Old 64×64: 3.</summary>
    public static readonly int FarmPreferredHomeDistance = 10;

    // ── Ecology ──────────────────────────────────────────────────────
    /// <summary>Radius around agents that creates gathering pressure on tiles. Old 64×64: 5.</summary>
    public static readonly int GatheringPressureRadius = 12;
    /// <summary>Agent count threshold for 0.75x regen rate.</summary>
    public static readonly int GatheringPressureThreshold1 = 3;
    /// <summary>Agent count threshold for 0.5x regen rate.</summary>
    public static readonly int GatheringPressureThreshold2 = 6;
    /// <summary>Agent count threshold for 0.25x regen rate.</summary>
    public static readonly int GatheringPressureThreshold3 = 10;

    /// <summary>Resource ratio below which regen rate halves (25% of capacity).</summary>
    public static readonly float OvergrazingThresholdLow = 0.25f;
    /// <summary>Resource ratio below which regen pauses entirely (10% of capacity).</summary>
    public static readonly float OvergrazingThresholdCritical = 0.10f;
    /// <summary>v1.8 Corrections: Ticks overgrazed tile must be left alone. ~8 sim-days = 3840 ticks.</summary>
    public static readonly int OvergrazingRecoveryTicks = 3840;
    /// <summary>v1.8 Corrections: Ticks before untended farm reverts to wild regen. ~30 sim-days = 14400 ticks.</summary>
    public static readonly int FarmTendedGracePeriod = 14400;

    // ── Animal Entities (D25a) ──────────────────────────────────────
    /// <summary>D25a: Ticks between animal replenishment checks.</summary>
    public static readonly int AnimalReplenishInterval = 5000;
    /// <summary>D25a: Ticks before extinct herd respawns.</summary>
    public static readonly int HerdRespawnDelay = 10000;
    /// <summary>D25a: Ticks before carcass rots completely.</summary>
    public static readonly int CarcassDecayTicks = 20;
    /// <summary>D25a: Ticks before carcass loses 50% meat.</summary>
    public static readonly int CarcassHalfDecayTicks = 10;

    // ── Hunting & Harvest (D25b) ──────────────────────────────────────
    /// <summary>D25b: Duration of Harvest action in ticks.</summary>
    public static readonly int HarvestDuration = 3;
    /// <summary>D25b: Raw meat hunger restoration (40/100).</summary>
    public static readonly float MeatHungerValue = 40f;
    /// <summary>D25b: Cooked meat hunger restoration (60/100).</summary>
    public static readonly float CookedMeatHungerValue = 60f;
    /// <summary>D25b: Hunt score multiplier (keeps hunting below easy gathering).</summary>
    public static readonly float HuntScoreMultiplier = 1.0f;
    /// <summary>D25b: Max distance from home before hunt is abandoned. Old 64×64: 15.</summary>
    public static readonly int HuntMaxDistanceFromHome = 40;

    // ── Combat (D25c) ──────────────────────────────────────────────
    /// <summary>Old 64×64: 2.</summary>
    public static readonly int BoarChargeRange = 5;
    /// <summary>Old 64×64: 3.</summary>
    public static readonly int WolfAggroRange = 8;
    /// <summary>Old 64×64: 2.</summary>
    public static readonly int WolfAttackRange = 4;
    /// <summary>Old 64×64: 3.</summary>
    public static readonly int StructureDeterrentRange = 10;
    /// <summary>Old 64×64: 4.</summary>
    public static readonly int BoarMaxPursuitFromTerritory = 10;
    /// <summary>Old 64×64: 6.</summary>
    public static readonly int WolfMaxPursuitFromTerritory = 15;
    public static readonly int BoarDisengageCooldown = 10;
    public static readonly int WolfDisengageCooldown = 15;
    /// <summary>Old 64×64: 5.</summary>
    public static readonly int WolfPackConvergeRange = 15;
    public static readonly float DisengageHealthThreshold = 30f;
    public static readonly float BoarFleeSuccessChance = 0.70f;
    public static readonly float WolfSingleFleeSuccessChance = 0.40f;
    public static readonly float WolfPackFleeSuccessChance = 0.25f;
    /// <summary>Old 64×64: 2.</summary>
    public static readonly int DisengageFleeDistance = 5;
    // Melee damage per tick by tool tier
    public static readonly int DamageBareHands = 2;
    public static readonly int DamageStoneKnife = 5;
    public static readonly int DamageCrudeAxe = 7;
    public static readonly int DamageHaftedTools = 8;
    public static readonly int DamageSpearMelee = 10;
    public static readonly int DamageBowMelee = 5;
    // Ranged damage
    public static readonly int SpearRangedDamage = 15;
    /// <summary>Old 64×64: 2.</summary>
    public static readonly int SpearRangedRange = 4;
    public static readonly int BowRangedDamage = 10;
    /// <summary>Old 64×64: 3.</summary>
    public static readonly int BowRangedRange = 6;
    public static readonly int BowRangedTicks = 2;
    // Animal damage per tick
    public static readonly int BoarDamagePerTick = 12;
    public static readonly int WolfSingleDamagePerTick = 12;
    public static readonly int WolfPackExtraDamagePerTick = 8;
    // Hunt scoring for dangerous prey
    public static readonly float BoarHuntDangerPenalty = 0.4f;
    public static readonly float WolfHuntDangerPenalty = 0.3f;
    public static readonly float BoarHuntMinHealth = 70f;
    public static readonly float WolfHuntMinHealth = 80f;
    public static readonly float WolfPackSizeScorePenalty = 0.1f;
    /// <summary>Night wolf bonus detection range. Old 64×64: 1.</summary>
    public static readonly int WolfNightDetectionBonus = 3;

    // ── D25c: Trapping ──────────────────────────────────────────────────
    public static readonly int TrapPlacementTicks = 2;
    public static readonly float TrapCatchChance = 0.03f; // 3% per tick if rabbit nearby
    public static readonly int TrapDecayTicks = 200;
    public static readonly int MaxTrapsPerAgent = 3;
    public static readonly float TrapBaseScore = 0.15f;
    /// <summary>Old 64×64: 8.</summary>
    public static readonly int TrapPlacementRadius = 25; // tiles from home
    /// <summary>Radius around trap that can catch animals (tiles).</summary>
    public static readonly int TrapCatchRadius = 3;

    // ── Diagnostic Performance ──────────────────────────────────────
    /// <summary>v1.8 Corrections: Diagnostic snapshot every N ticks. Must NOT be a multiple of
    /// TicksPerSimDay (480) to avoid stroboscopic aliasing where every sample hits the same time of day.</summary>
    public static readonly int DiagnosticSampleInterval = 479;

    /// <summary>Flush CSV to disk every N ticks. 1 year = 53760 ticks.</summary>
    public static readonly int DiagnosticFlushInterval = 53760;

    // ── Home & Territory ─────────────────────────────────────────────
    /// <summary>Base pull strength for ReturnHome utility. Score = HomePullStrength / (1 + dist²).</summary>
    public static readonly float HomePullStrength = 4.0f;

    // ── Day/Night Cycle (Systemic 4) ──────────────────────────────────
    /// <summary>Tick within a sim-day when "night" begins (~9PM = tick 420 of 480).</summary>
    public static readonly int NightStartHour = 420;

    /// <summary>Tick within a sim-day when "morning" begins (~5AM = tick 100 of 480).</summary>
    public static readonly int NightEndHour = 100;

    /// <summary>Duration of a night rest cycle in ticks (~8 sim-hours = 160 ticks).</summary>
    public static readonly int NightRestDuration = 160;

    // ── Personality Traits ───────────────────────────────────────────
    /// <summary>Probability that a child's trait mutates to a random trait instead of inheriting.</summary>
    public static readonly float TraitMutationChance = 0.15f;

    // ── Social Bonds ─────────────────────────────────────────────────
    /// <summary>Ticks between social bond decay sweeps. ~5 years × 53760 = 268800 ticks.</summary>
    public static readonly int SocialBondDecayInterval = 268800;

    /// <summary>InteractionCount threshold for "friend" status — adds utility bonus to Socialize.</summary>
    public static readonly int SocialBondFriendThreshold = 5;

    /// <summary>Utility score bonus added to Socialize when targeting a bonded (friend) agent.</summary>
    public static readonly float SocialBondUtilityBonus = 0.2f;

    /// <summary>Starting InteractionCount for parent-child family bonds.</summary>
    public static readonly int SocialBondFamilyStart = 10;

    /// <summary>Chebyshev radius within which social bonds do NOT decay. Old 64×64: 3.</summary>
    public static readonly int SocialBondProximityRadius = 8;

    // ── Action Dampening ─────────────────────────────────────────────
    /// <summary>Multiplier per consecutive tick of same utility action. Prevents oscillation.</summary>
    public static readonly float ActionDampeningFactor = 0.9f;

    /// <summary>Minimum dampening multiplier (floor). Score never goes below base * this.</summary>
    public static readonly float ActionDampeningFloor = 0.73f;

    // ── Survival Gating ──────────────────────────────────────────────
    /// <summary>Hunger threshold below which growth actions (Experiment, Explore) are suppressed.
    /// Agents should focus on food/shelter when moderately hungry. 0=full, 100=starving.
    /// Higher = agents must be better fed before experimenting.</summary>
    public static readonly float ExperimentHungerGate = 65f;

    // ── Settlements ──────────────────────────────────────────────────
    /// <summary>Minimum shelters in a cluster to qualify as a settlement.
    /// v1.8 Fix: Lowered from 3 to 1. A couple sharing a lean-to is the beginning of a settlement.
    /// Knowledge propagation starts as soon as the first shelter is built.</summary>
    public static readonly int SettlementShelterThreshold = 1;

    /// <summary>Chebyshev radius for settlement cluster detection. Old 64×64: 5.</summary>
    public static readonly int SettlementRadius = 15;

    // ── ShareFood Utility ───────────────────────────────────────────
    /// <summary>Base utility score for ShareFood action (feeding starving adjacent agent).</summary>
    public static readonly float ShareFoodBaseUtility = 0.7f;

    // ── D19: Restlessness System ────────────────────────────────────
    /// <summary>Restlessness gain per comfort-idle tick (adult rate). CALIBRATE LATER.</summary>
    public static readonly float RestlessnessGainRate = 0.5f;
    /// <summary>Youth (age 5-12) accumulate at half adult rate.</summary>
    public static readonly float RestlessnessYouthGainRate = 0.25f;
    /// <summary>Initial restlessness for founders (starting agents).</summary>
    public static readonly float RestlessnessFounderStart = 30f;
    // D19 drain rates per productive action tick:
    public static readonly float RestlessnessExperimentDrain = 1.5f;
    public static readonly float RestlessnessBuildDrain = 1.0f;
    public static readonly float RestlessnessTendFarmDrain = 0.8f;
    public static readonly float RestlessnessGatherDrain = 0.5f;
    public static readonly float RestlessnessCookDrain = 0.5f;
    public static readonly float RestlessnessPreserveDrain = 0.5f;
    public static readonly float RestlessnessExploreDrain = 0.3f;
    // D19 scoring maxBoost values:
    public static readonly float RestlessnessExperimentBoost = 0.8f;
    public static readonly float RestlessnessBuildBoost = 0.4f;
    public static readonly float RestlessnessTendFarmBoost = 0.4f;
    public static readonly float RestlessnessGatherBoost = 0.2f;

    // ══════════════════════════════════════════════════════════════════
    // BEHAVIORAL MODE THRESHOLDS (v1.8 Behavioral Modes)
    // All transition thresholds come in entry/exit pairs with hysteresis.
    // ══════════════════════════════════════════════════════════════════

    // ── Urgent Mode ──
    /// <summary>Hunger threshold to ENTER Urgent mode.</summary>
    public static readonly float UrgentEntryHunger = 25f;
    /// <summary>Health threshold to ENTER Urgent mode.</summary>
    public static readonly int UrgentEntryHealth = 20;
    /// <summary>Hunger threshold to EXIT Urgent mode (hysteresis above entry).</summary>
    public static readonly float UrgentExitHunger = 40f;
    /// <summary>Health threshold to EXIT Urgent mode (hysteresis above entry).</summary>
    public static readonly int UrgentExitHealth = 30;

    // ── Forage Mode ──
    /// <summary>Personal food below this triggers Forage entry (combined with home storage check).</summary>
    public static readonly int ForageEntryFoodInventory = 6;
    /// <summary>Home storage food below this (combined with personal) triggers Forage.</summary>
    public static readonly int ForageEntryHomeStorage = 15;
    /// <summary>Hunger must be above this to enter Forage (hysteresis with Forage exit).</summary>
    public static readonly float ForageEntryHunger = 55f;
    /// <summary>Hunger below this exits Forage (go home and eat).</summary>
    public static readonly float ForageExitHunger = 45f;
    /// <summary>Default food return threshold — go home when carrying this much food.</summary>
    public static readonly int ForageReturnFoodDefault = 6;
    /// <summary>Caretaker mode return food threshold — shorter trips.</summary>
    public static readonly int ForageReturnFoodCaretaker = 4;
    /// <summary>Maximum ticks in Forage before forced return (~7 sim-hours).</summary>
    public static readonly int ForageMaxDuration = 200;
    /// <summary>Distance from home at which forager commits to continuing rather than returning (tiles).</summary>
    public static readonly int ForageCommitmentDistance = 70;
    /// <summary>Distance from home at which forager gives up and returns (tiles).</summary>
    public static readonly int ForageGiveUpDistance = 100;

    // ── Explore Mode ──
    /// <summary>Minimum ticks in Home mode before Explore is allowed (settle first).</summary>
    public static readonly int ExploreMinHomeDwell = 100;
    /// <summary>Hunger must be above this to enter Explore.</summary>
    public static readonly float ExploreEntryHunger = 80f;
    /// <summary>Health must be above this to enter Explore.</summary>
    public static readonly int ExploreEntryHealth = 80;
    /// <summary>Must carry this much food to enter Explore.</summary>
    public static readonly int ExploreEntryFood = 4; // [RECALIBRATE] Reduced from 6 — Directive #8 Fix 5
    /// <summary>Explorer trait lowers hunger entry threshold.</summary>
    public static readonly float ExploreEntryHungerExplorer = 70f;
    /// <summary>Explorer trait lowers health entry threshold.</summary>
    public static readonly int ExploreEntryHealthExplorer = 70;
    /// <summary>Explorer trait lowers food entry threshold.</summary>
    public static readonly int ExploreEntryFoodExplorer = 4;
    /// <summary>Default explore tick budget (~10 sim-hours).</summary>
    public static readonly int ExploreBudgetDefault = 300;
    /// <summary>Explorer trait tick budget (~15 sim-hours).</summary>
    public static readonly int ExploreBudgetExplorer = 500;
    /// <summary>Hunger threshold to exit Explore early (head home).</summary>
    public static readonly float ExploreExitHunger = 50f;
    /// <summary>Health threshold to exit Explore early.</summary>
    public static readonly int ExploreExitHealth = 50;

    // ── D24: Explore Direction Diversity ──
    /// <summary>D24 Fix 1A: Number of top directions for weighted random selection.</summary>
    public static readonly int ExploreTopNDirections = 3;
    /// <summary>D24 Fix 1B: Score multiplier for recently explored directions.</summary>
    public static readonly float ExploreRecentDirectionPenalty = 0.35f;
    /// <summary>D24 Fix 1B: Max recent directions to remember.
    /// Bug 3 fix: Increased from 3 to 7 so cooldown covers nearly all 8 directions.
    /// With depth=3, a direction fell off the recent list after 3 trips, allowing the
    /// same corner to win the memory-based scoring repeatedly.</summary>
    public static readonly int ExploreRecentDirectionDepth = 7;
    /// <summary>D24 Fix 1B: Ticks without exploring before direction memory decays.</summary>
    public static readonly int ExploreDirectionDecayTicks = 2000;
    /// <summary>D24 Fix 1C: Fraction of budget spent near start that triggers early return.</summary>
    public static readonly float ExploreBudgetWasteThreshold = 0.5f;
    /// <summary>D24 Fix 1C: Max distance from explore start position to be considered "near start". Old 64×64: 3.</summary>
    public static readonly int ExploreBudgetWasteRadius = 8;

    // ── New Exploration Constants (350×350) ──
    /// <summary>Distance from home to check for return path viability during explore (tiles).</summary>
    public static readonly int ExploreReturnPathCheck = 15;
    /// <summary>Tiles of non-movement before explorer is considered stuck.</summary>
    public static readonly int ExploreStuckDetectionTiles = 8;
    /// <summary>Maximum distance an explorer may travel from home (tiles).</summary>
    public static readonly int MaxExploreDistance = 120;
    /// <summary>Radius around a blacklisted position to avoid (tiles).</summary>
    public static readonly int BlacklistRadius = 8;

    // ── D24: New Adult Bootstrap ──
    /// <summary>D24 Fix 3: Duration of productivity boost after maturation (ticks). ~1 sim-day.</summary>
    public static readonly int NewAdultBootstrapDuration = 500;
    /// <summary>D24 Fix 3: Gather score multiplier during bootstrap.</summary>
    public static readonly float NewAdultGatherBoost = 1.5f;
    /// <summary>D24 Fix 3: Explore score multiplier during bootstrap.</summary>
    public static readonly float NewAdultExploreBoost = 1.5f;
    /// <summary>D24 Fix 3: Experiment score multiplier during bootstrap.</summary>
    public static readonly float NewAdultExperimentBoost = 1.3f;
    /// <summary>D24 Fix 3: Build score multiplier during bootstrap.</summary>
    public static readonly float NewAdultBuildBoost = 1.2f;

    // ── Caretaker Mode ──
    /// <summary>Max forage distance from home in Caretaker mode (tiles, Chebyshev). Old 64×64: 8.</summary>
    public static readonly int CaretakerForageRange = 20;
    /// <summary>Child hunger check interval in ticks.</summary>
    public static readonly int CaretakerChildCheckInterval = 30;
    /// <summary>Age in ticks when youngest child exits care threshold. [RECALIBRATE] Reduced from 4 years (215040).</summary>
    public static readonly int CaretakerExitChildAge = TicksPerYear / 2; // 26880 ticks = 0.5 sim-years

    // ── Build Mode ──
    /// <summary>Build only at/adjacent to HomeTile (Chebyshev distance). Old 64×64: 1.</summary>
    public static readonly int BuildMaxDistFromHome = 3;

    // ── Rendering ───────────────────────────────────────────────────
    /// <summary>Maximum resource sprites per resource type per tile.</summary>
    public static readonly int MaxSpritesPerTile = 5;

    // ══════════════════════════════════════════════════════════════════
    // RESOURCE DISTRIBUTION (GDD v1.8 Section 6)
    // ══════════════════════════════════════════════════════════════════

    // ── Tier 1: Primary Resources (Perlin variance within biomes) ──
    /// <summary>Section 6: Perlin noise scale for resource density variance within biomes.</summary>
    public static readonly double ResourceDensityNoiseScale = 0.06;
    /// <summary>Section 6: Minimum density multiplier from noise (0.25 = poorest area gets 25% of normal).</summary>
    public static readonly float ResourceDensityMin = 0.25f;

    // ── Tier 2: Secondary Patches (clustered resources) ─────────────
    /// <summary>Section 6: Minimum tiles per secondary resource patch.</summary>
    public static readonly int PatchSizeMin = 3;
    /// <summary>Section 6: Maximum tiles per secondary resource patch.</summary>
    public static readonly int PatchSizeMax = 7;
    /// <summary>Section 6: Minimum Chebyshev distance between patch centers of the same type.</summary>
    public static readonly int PatchMinSpacing = 12;

    /// <summary>Section 6: Target berry patches per 256x256 map (Forest biome).</summary>
    public static readonly int BerryPatchCount = 18;
    /// <summary>Section 6: Target animal herd locations per map (Plains + Forest-edge).</summary>
    public static readonly int AnimalHerdCount = 12;
    /// <summary>Section 6: Target fish school locations per map (Water adjacent to land).</summary>
    public static readonly int FishSchoolCount = 10;
    /// <summary>Section 6: Target grain concentration patches per map (Plains biome).</summary>
    public static readonly int GrainPatchCount = 14;

    /// <summary>Section 6: Berry amount per patch tile (concentrated source).</summary>
    public static readonly int PatchBerryAmount = 8;
    /// <summary>Section 6: Animal count per herd tile.</summary>
    public static readonly int PatchAnimalAmount = 4;
    /// <summary>Section 6: Fish count per school tile.</summary>
    public static readonly int PatchFishAmount = 6;
    /// <summary>Section 6: Grain amount per concentration tile.</summary>
    public static readonly int PatchGrainAmount = 10;

    // ── Tier 3: Point Features (rare, notable) ──────────────────────
    /// <summary>Section 6: Cave entrances per map (Mountain biome). Landmark, no resources.</summary>
    public static readonly int CaveCount = 4;
    /// <summary>Section 6: Ore vein features per map (Mountain biome). Rich ore deposits.</summary>
    public static readonly int OreVeinCount = 6;
    /// <summary>Section 6: Natural spring features per map (Forest/Plains near water). Landmark.</summary>
    public static readonly int NaturalSpringCount = 3;
    /// <summary>Section 6: Rich stone quarry features per map (Mountain biome).</summary>
    public static readonly int RichQuarryCount = 4;

    /// <summary>Section 6: Ore amount at ore vein point features (much higher than normal mountain ore).</summary>
    public static readonly int OreVeinAmount = 30;
    /// <summary>Section 6: Stone amount at rich quarry point features.</summary>
    public static readonly int RichQuarryStoneAmount = 50;
    /// <summary>Section 6: Minimum spacing between point features of the same type (Chebyshev).</summary>
    public static readonly int PointFeatureMinSpacing = 20;

    // ══════════════════════════════════════════════════════════════════
    // TIME FORMATTING (GDD v1.8 — observer sees days/seasons/years)
    // ══════════════════════════════════════════════════════════════════

    // These are convenience helpers. The tick is NEVER shown to the observer.

    /// <summary>Convert ticks to sim-days.</summary>
    public static int TicksToSimDays(int ticks) => ticks / TicksPerSimDay;

    /// <summary>Convert sim-days to ticks.</summary>
    public static int SimDaysToTicks(int simDays) => simDays * TicksPerSimDay;

    /// <summary>Convert years to ticks.</summary>
    public static int YearsToTicks(int years) => years * SimDaysPerYear * TicksPerSimDay;

    /// <summary>Convert ticks to fractional years.</summary>
    public static float TicksToYears(int ticks) => (float)ticks / (SimDaysPerYear * TicksPerSimDay);

    // ══════════════════════════════════════════════════════════════════
    // SPEED TIERS (v1.8 Corrections — fractional ticks/sec)
    // ══════════════════════════════════════════════════════════════════

    /// <summary>v1.8 Corrections: 5-tier speed system. Ticks per second for each speed level.
    /// At 1x: 480 ticks / 600 seconds = 0.8 ticks/sec → ~10 min per sim-day.
    /// Uses float because 1x and 2x are sub-integer.</summary>
    public static readonly float[] SpeedTierTicksPerSecond = { 0.8f, 1.6f, 4f, 8f, 16f };

    /// <summary>GDD v1.8: Human-readable labels for each speed tier.</summary>
    public static readonly string[] SpeedTierLabels = { "1x", "2x", "5x", "10x", "20x" };

    /// <summary>GDD v1.8: Number of speed tiers available.</summary>
    public static readonly int SpeedTierCount = 5;

    /// <summary>GDD v1.8: Default speed tier index (0 = 1x).</summary>
    public static readonly int DefaultSpeedTier = 0;

    // ══════════════════════════════════════════════════════════════════
    // SETTLEMENT DETECTION INTERVAL
    // ══════════════════════════════════════════════════════════════════

    /// <summary>Ticks between settlement detection passes. ~1 sim-day = 480 ticks.
    /// Was 13440 (1 season) but that's too slow — first shelter needs to form a settlement
    /// quickly so knowledge propagation can start.</summary>
    public static readonly int SettlementDetectionInterval = 480;

    /// <summary>Ticks between milestone log entries. ~1 year = 53760 ticks.</summary>
    public static readonly int MilestoneLogInterval = 53760;

    // ══════════════════════════════════════════════════════════════════
    // D25d: DOMESTICATION, PENS, BREEDING
    // ══════════════════════════════════════════════════════════════════

    // ── Taming ──
    /// <summary>Ticks between food offerings during taming.</summary>
    public static readonly int TameOfferingInterval = 5;
    /// <summary>Ticks without an offering before tame progress decays by 1.</summary>
    public static readonly int TameDecayInterval = 30;
    /// <summary>Tame progress decay rate when agent leaves perception range (1 per N ticks).</summary>
    public static readonly int TameOutOfRangeDecayRate = 10;
    /// <summary>Target domesticated animal count — taming score reaches 0 at this value.</summary>
    public static readonly int TameTargetDomesticCount = 6;
    /// <summary>Base tame score multiplier.</summary>
    public static readonly float TameBaseScore = 0.20f;
    /// <summary>Bonus tame score when food surplus > 10.</summary>
    public static readonly float TameFoodSurplusBonus = 0.1f;

    // Taming thresholds per species
    public static readonly int TameThresholdRabbit = 3;
    public static readonly int TameThresholdCow = 5;
    public static readonly int TameThresholdSheep = 4;
    public static readonly int TameThresholdDeer = 8;
    public static readonly int TameThresholdBoar = 10;
    public static readonly int TameThresholdWolf = 15;

    // ── Pens ──
    /// <summary>Max animals in a small-animal pen (Rabbit, Boar/Pig).</summary>
    public static readonly int PenCapacitySmall = 5;
    /// <summary>Max animals in a large-animal pen (Deer, Cow).</summary>
    public static readonly int PenCapacityLarge = 3;
    /// <summary>Max grain a pen can store.</summary>
    public static readonly int PenMaxFoodStore = 50;
    /// <summary>Ticks between grain consumption per penned animal.</summary>
    public static readonly int PenFeedInterval = 200;
    /// <summary>Health damage per tick when pen food store is empty.</summary>
    public static readonly int PenStarvationDamage = 2;
    /// <summary>Tame score boost when a pen has exactly 1 animal (needs breeding partner).</summary>
    public static readonly float TameBreedingPairUrgencyScore = 0.35f;
    /// <summary>Build duration for a pen (ticks).</summary>
    public static readonly int PenBuildDuration = 8;
    /// <summary>Max distance from home to build a pen. Old 64×64: 3.</summary>
    public static readonly int PenMaxDistFromHome = 8;
    /// <summary>Base score for FeedPen action (25-50% food level).</summary>
    public static readonly float FeedPenBaseScore = 0.25f;
    /// <summary>Critical score for FeedPen action (below 25% food level).</summary>
    public static readonly float FeedPenCriticalScore = 0.40f;
    /// <summary>Caretaker mode FeedPen score (same priority as child feeding).</summary>
    public static readonly float FeedPenCaretakerScore = 0.35f;

    // ── Breeding ──
    /// <summary>Rabbit breed check interval (ticks).</summary>
    public static readonly int BreedIntervalRabbit = 40;
    /// <summary>Cow breed check interval (ticks).</summary>
    public static readonly int BreedIntervalCow = 80;
    /// <summary>Sheep breed check interval (ticks).</summary>
    public static readonly int BreedIntervalSheep = 60;
    /// <summary>Deer breed check interval (ticks).</summary>
    public static readonly int BreedIntervalDeer = 100;
    /// <summary>Boar/Pig breed check interval (ticks).</summary>
    public static readonly int BreedIntervalBoar = 60;
    /// <summary>Rabbit breed chance per eligible pair.</summary>
    public static readonly float BreedChanceRabbit = 0.60f;
    /// <summary>Cow breed chance per eligible pair.</summary>
    public static readonly float BreedChanceCow = 0.40f;
    /// <summary>Sheep breed chance per eligible pair.</summary>
    public static readonly float BreedChanceSheep = 0.50f;
    /// <summary>Deer breed chance per eligible pair.</summary>
    public static readonly float BreedChanceDeer = 0.30f;
    /// <summary>Boar/Pig breed chance per eligible pair.</summary>
    public static readonly float BreedChanceBoar = 0.50f;

    // ── Slaughter ──
    /// <summary>Slaughter action duration (ticks).</summary>
    public static readonly int SlaughterDuration = 2;
    /// <summary>Base slaughter scoring multiplier.</summary>
    public static readonly float SlaughterBaseScore = 0.20f;
    /// <summary>Minimum same-species animals to preserve (breeding pair).</summary>
    public static readonly int SlaughterBreedingPairMin = 2;

    // ── Wolf → Dog ──
    /// <summary>Ticks between wolf pup spawn checks per pack.</summary>
    public static readonly int WolfPupSpawnInterval = 400;
    /// <summary>Min ticks a wolf pack must exist before pup spawning.</summary>
    public static readonly int WolfPackEstablishedAge = 200;
    /// <summary>Chance of pup spawning per check.</summary>
    public static readonly float WolfPupSpawnChance = 0.30f;
    /// <summary>Max pups per wolf pack at any time.</summary>
    public static readonly int WolfMaxPupsPerPack = 2;
    /// <summary>Dog perception radius bonus for owner. Old 64×64: 3.</summary>
    public static readonly int DogPerceptionBonus = 6;
    /// <summary>Dog hunt success bonus.</summary>
    public static readonly float DogHuntBonus = 0.10f;
    /// <summary>Dog night guard alert range. Old 64×64: 5.</summary>
    public static readonly int DogNightGuardRange = 12;
    /// <summary>Dog restlessness decay reduction for owner (15%).</summary>
    public static readonly float DogRestlessnessReduction = 0.15f;
    /// <summary>Dog health points.</summary>
    public static readonly int DogHealth = 15;
}
