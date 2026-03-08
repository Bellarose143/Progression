namespace CivSim.Core;

/// <summary>
/// A single entry in the agent's action history ring buffer.
/// Used by the Death Report UI to show what the agent was doing before death.
/// </summary>
public struct ActionRecord
{
    public ActionType Action;
    public int Tick;
    public string Detail;

    public ActionRecord(ActionType action, int tick, string detail)
    {
        Action = action;
        Tick = tick;
        Detail = detail;
    }
}

/// <summary>
/// GDD v1.7.2: Development stages for child progression.
/// Infant (0-5y): fed by adults, cannot act independently.
/// Youth (5-12y): can self-feed and gather at 0.5× efficiency.
/// Adult (12y+): full capabilities.
/// </summary>
public enum DevelopmentStage
{
    Infant,
    Youth,
    Adult
}

/// <summary>Type of relationship between two agents.</summary>
public enum RelationshipType { None, Spouse, Parent, Child, Sibling }

/// <summary>
/// An autonomous actor in the simulation.
/// Hunger: 100 = fully fed, 0 = starving (GDD convention).
/// Health: 0 = dead, 100 = perfect health.
/// GDD v1.8: Tick is internal heartbeat (invisible). Sim-day = TicksPerSimDay ticks.
/// Stage-based hunger drain. Exposure suppresses regen.
/// </summary>
public class Agent
{
    private static int _nextId = 1;

    /// <summary>Reset the static ID counter. Used by integration tests to ensure deterministic behavior.</summary>
    public static void ResetIdCounter() => _nextId = 1;

    // ── Identity ───────────────────────────────────────────────────────
    public int Id { get; }
    public string Name { get; set; }

    // ── Vital Stats ──────────────────────────────────────────────────
    /// <summary>100 = fully fed, 0 = starving. Decreases by HungerDrainPerTick each tick. Float for fractional child drain.</summary>
    public float Hunger { get; set; }

    /// <summary>0 = dead, 100 = perfect health.</summary>
    public int Health { get; set; }

    /// <summary>D19: Restlessness motivation. 0 = satisfied, 100 = very restless. Drives productive action scoring.</summary>
    public float Restlessness { get; set; }

    /// <summary>Age in ticks since birth. GDD v1.8: 13440 ticks = 1 year (480 ticks/sim-day × 28 sim-days/year).</summary>
    public int Age { get; set; }

    /// <summary>Whether this agent is still alive.</summary>
    public bool IsAlive { get; set; }

    /// <summary>GDD v1.8 Section 4: Gender for name generation from curated lists.</summary>
    public bool IsMale { get; set; }

    /// <summary>GDD v1.7.2: Computed development stage based on age. Infant → Youth → Adult.</summary>
    public DevelopmentStage Stage => Age < SimConfig.ChildInfantAge ? DevelopmentStage.Infant
        : Age < SimConfig.ChildYouthAge ? DevelopmentStage.Youth
        : DevelopmentStage.Adult;

    // ── Position ───────────────────────────────────────────────────────
    public int X { get; set; }
    public int Y { get; set; }

    // ── D26: Move interpolation metadata (renderer-only, no sim behavior change) ──
    public int MoveStartTick { get; set; }
    public int MoveEndTick { get; set; }
    public (int X, int Y) MoveOrigin { get; set; }
    public (int X, int Y) MoveDestination { get; set; }
    public bool IsMoving { get; set; }

    // ── Knowledge & Discovery ──────────────────────────────────────────
    /// <summary>Set of discovery IDs this agent has unlocked.</summary>
    public HashSet<string> Knowledge { get; }

    /// <summary>Tracks failed attempts per recipe for familiarity bonus.</summary>
    public Dictionary<string, int> Familiarity { get; }

    // ── Inventory ──────────────────────────────────────────────────────
    /// <summary>Resources the agent is carrying. Sum of values must not exceed InventoryCapacity.</summary>
    public Dictionary<ResourceType, int> Inventory { get; }

    // ── State ──────────────────────────────────────────────────────────
    /// <summary>What the agent is doing this tick (for display/rendering).</summary>
    public ActionType CurrentAction { get; set; }
    public ResourceType? LastGatheredResource { get; set; }

    /// <summary>Ticks remaining before this agent can reproduce again.</summary>
    public int ReproductionCooldownRemaining { get; set; }

    /// <summary>Directive #9: Set to true after child→adult maturation transition fires.
    /// Prevents the maturation reset from running more than once.</summary>
    public bool HasMatured { get; set; }

    /// <summary>D24 Fix 3: Whether this agent is in the new-adult bootstrap window.</summary>
    public bool IsNewAdult { get; set; }

    /// <summary>D24 Fix 3: Ticks remaining in the new-adult bootstrap window.</summary>
    public int NewAdultTicksRemaining { get; set; }

    // ── Multi-Tick Action State ──────────────────────────────────────
    /// <summary>The multi-tick action currently in progress, or null if idle.</summary>
    public ActionType? PendingAction { get; set; }

    /// <summary>v1.8 Corrections: Float-based progress for current action (0.0 to ActionDurationTicks).
    /// Each tick, progress advances by 1.0. Action completes when progress >= duration.</summary>
    public float ActionProgress { get; set; }

    /// <summary>v1.8 Corrections: Total duration of the current action in ticks (float for sub-tick movement).
    /// Progress advances by 1.0/tick. Completion when ActionProgress >= ActionDurationTicks.</summary>
    public float ActionDurationTicks { get; set; }

    /// <summary>Legacy: Integer ticks remaining (computed from float progress for backward compat).</summary>
    public int ActionTicksRemaining
    {
        get => Math.Max(0, (int)Math.Ceiling(ActionDurationTicks - ActionProgress));
        set => ActionProgress = ActionDurationTicks - value; // Backward compat setter
    }

    /// <summary>Legacy: Total duration as integer (for progress display).</summary>
    public int ActionTicksTotal
    {
        get => (int)Math.Ceiling(ActionDurationTicks);
        set => ActionDurationTicks = value; // Backward compat setter
    }

    /// <summary>Target tile for Move or Gather actions.</summary>
    public (int X, int Y)? ActionTarget { get; set; }

    /// <summary>Resource type being gathered (for Gather actions).</summary>
    public ResourceType? ActionTargetResource { get; set; }

    /// <summary>Recipe ID being experimented or crafted.</summary>
    public string? ActionTargetRecipe { get; set; }

    /// <summary>Agent ID of a collaborate/interact target.</summary>
    public int? ActionTargetAgentId { get; set; }

    /// <summary>D25b: The specific animal entity this agent is pursuing during Hunt.</summary>
    public int? HuntTargetAnimalId { get; set; }

    /// <summary>D25b: Ticks spent in active pursuit of fleeing prey.</summary>
    public int HuntPursuitTicks { get; set; }

    // D25c: Combat fields
    public int? CombatTargetAnimalId { get; set; }
    public int CombatTicksRemaining { get; set; }
    public bool IsInCombat => CombatTargetAnimalId.HasValue;

    // D25d: Domestication fields
    public int? TameTargetAnimalId { get; set; }    // Animal entity this agent is currently taming

    /// <summary>D25d: Check if this agent has a living dog companion (not penned).</summary>
    public bool HasDogCompanion(IReadOnlyList<Animal> animals)
    {
        foreach (var a in animals)
        {
            if (a.IsAlive && a.IsDog && a.OwnerAgentId == Id && !a.PenId.HasValue)
                return true;
        }
        return false;
    }

    // ── Behavioral Mode (v1.8 Behavioral Modes) ────────────────────────
    /// <summary>v1.8: The agent's current behavioral mode. Always exactly one.</summary>
    public BehaviorMode CurrentMode { get; set; } = BehaviorMode.Home;

    /// <summary>v1.8: Tick when the current mode was entered. Used for duration budgets and hysteresis.</summary>
    public int ModeEntryTick { get; set; }

    /// <summary>v1.8: Previous mode before entering Urgent. Null if not in Urgent.</summary>
    public BehaviorMode? PreviousMode { get; set; }

    /// <summary>v1.8: Committed targets for the current mode (forage target, build project, explore direction).</summary>
    public ModeCommitment ModeCommit { get; } = new();

    // Fix B6: Saved build project that persists across Build→Forage→Home transitions.
    // Set when Build mode exits to Forage for materials; cleared when project completes or agent re-enters Build.
    public string? SavedBuildRecipeId { get; set; }
    public (int X, int Y)? SavedBuildTargetTile { get; set; }

    /// <summary>v1.8: Transitions to a new mode, clearing committed state and recording entry tick.</summary>
    public void TransitionMode(BehaviorMode newMode, int currentTick)
    {
        if (newMode == BehaviorMode.Urgent && CurrentMode != BehaviorMode.Urgent)
            PreviousMode = CurrentMode;

        // Reset explore stuck counters on mode exit
        if (CurrentMode == BehaviorMode.Explore && newMode != BehaviorMode.Explore)
        {
            ExploreStuckTicks = 0;
            ExploreWaterBlockedTicks = 0;
        }

        CurrentMode = newMode;
        ModeEntryTick = currentTick;
        ModeCommit.Clear();
    }

    // ── Goal-Based Action Commitment (prevents decision thrashing) ───
    /// <summary>High-level goal the agent is working toward. Null = no active goal, full re-evaluation.</summary>
    public GoalType? CurrentGoal { get; set; }

    /// <summary>Target tile for the current goal (where the agent is heading).</summary>
    public (int X, int Y)? GoalTarget { get; set; }

    /// <summary>Resource type associated with the goal (for gather goals).</summary>
    public ResourceType? GoalResource { get; set; }

    /// <summary>Recipe ID associated with the goal (for build goals).</summary>
    public string? GoalRecipeId { get; set; }

    /// <summary>Tick when the current goal was set. Used for stale goal detection.
    /// Setting this also captures GoalSetX/GoalSetY for stuck detection.</summary>
    private int _goalStartTick;
    public int GoalStartTick
    {
        get => _goalStartTick;
        set
        {
            _goalStartTick = value;
            GoalSetX = X;
            GoalSetY = Y;
        }
    }

    /// <summary>Agent position when current goal was created. Used by ClearGoal to detect stuck agents.</summary>
    public int GoalSetX { get; private set; }
    public int GoalSetY { get; private set; }

    /// <summary>Clears all goal state, forcing full re-evaluation next tick.</summary>
    public void ClearGoal()
    {
        // D18.2 Fix 1: Only reset escalating pathfinding state if the agent actually
        // moved since the goal was set. If they haven't moved, they're stuck — preserve
        // MoveFailCount so the escalating pathfinder can advance to more aggressive strategies.
        // Must check BEFORE setting GoalStartTick=0 (which updates GoalSetX/GoalSetY via setter).
        bool hasMoved = X != GoalSetX || Y != GoalSetY;

        CurrentGoal = null;
        GoalTarget = null;
        GoalResource = null;
        GoalRecipeId = null;
        GoalStartTick = 0;
        WaypointPath = null;
        WaypointIndex = 0;

        if (hasMoved)
        {
            MoveFailCount = 0;
            RecoveryStartDistanceToGoal = 0f;
            RecoveryWaypoints = null;
        }
    }

    // ── Directive #7: Stuck Detection + Pathfinding ──────────────────
    /// <summary>Counts consecutive ticks where agent has a Move goal but hasn't made real progress.</summary>
    public int StuckCounter { get; set; }

    /// <summary>Ring buffer of last 5 positions, used for oscillation detection.</summary>
    public (int X, int Y)[] RecentPositions { get; } = new (int, int)[5];

    /// <summary>Write index into RecentPositions ring buffer.</summary>
    public int RecentPosIndex { get; set; }

    /// <summary>Pre-computed A* waypoint path for ReturnHome goals. Null if using greedy movement.</summary>
    public List<(int X, int Y)>? WaypointPath { get; set; }

    /// <summary>Current index into WaypointPath.</summary>
    public int WaypointIndex { get; set; }

    /// <summary>Records current position into the recent positions ring buffer.</summary>
    public void RecordPosition()
    {
        RecentPositions[RecentPosIndex] = (X, Y);
        RecentPosIndex = (RecentPosIndex + 1) % RecentPositions.Length;
    }

    /// <summary>Returns true if the given position is already in the recent positions buffer.</summary>
    public bool IsPositionRecent(int x, int y)
    {
        for (int i = 0; i < RecentPositions.Length; i++)
            if (RecentPositions[i].X == x && RecentPositions[i].Y == y)
                return true;
        return false;
    }

    // ── Memory ─────────────────────────────────────────────────────────
    /// <summary>Short-term memory buffer populated by Perceive(), decayed each tick.</summary>
    public List<MemoryEntry> Memory { get; }

    /// <summary>D25b: Separate animal/carcass sighting memory — isolated from main Memory to prevent RNG cascade.</summary>
    public List<MemoryEntry> AnimalMemory { get; } = new();

    /// <summary>GDD v1.7.2: Permanent landmark memory — structures never forgotten once seen.
    /// Does not decay, does not count against MemoryMaxEntries. (X, Y, StructureType).</summary>
    public HashSet<(int X, int Y, string StructureType)> KnownStructures { get; } = new();

    /// <summary>GDD v1.8 Section 5: Personal geographic discoveries not yet shared with settlement.
    /// Filled during perception when point features or resource patches are spotted.
    /// Cleared when agent returns home and transfers to settlement lore.
    /// Does NOT count against MemoryMaxEntries (these are permanent once shared).</summary>
    public List<(int X, int Y, string FeatureType, ResourceType? Resource)> PendingGeographicDiscoveries { get; } = new();

    /// <summary>GDD v1.8 Section 5: Positions already added to PendingGeographicDiscoveries or
    /// known to be in settlement lore. Prevents duplicate entries from repeated perception scans.</summary>
    private readonly HashSet<(int X, int Y)> _knownGeographicPositions = new();

    // ── Perception State (GDD v1.6.2) ────────────────────────────────
    /// <summary>Tick when the last active (full-radius) perception scan was performed.</summary>
    public int LastActivePerceptionTick { get; set; }

    /// <summary>When set, forces an active perception scan next tick (cleared after scan). Set by AgentAI on action completion/interrupt.</summary>
    public bool ForceActivePerception { get; set; }

    // ── Social ─────────────────────────────────────────────────────────
    /// <summary>The community this agent belongs to, if any. Phase 4 placeholder.</summary>
    public object? Community { get; set; }

    // ── GDD v1.7.1: Personality Traits ────────────────────────────────
    /// <summary>GDD v1.7.1: Two personality traits assigned at birth. Modify utility scoring weights.</summary>
    public PersonalityTrait[] Traits { get; }

    // ── GDD v1.7.1: Home Tile ─────────────────────────────────────────
    /// <summary>GDD v1.7.1: Position of agent's primary shelter. Null until first shelter built/adopted. Creates weighted pull via ReturnHome utility.</summary>
    public (int X, int Y)? HomeTile { get; set; }

    // ── GDD v1.7.1: Social Bonds ──────────────────────────────────────
    /// <summary>GDD v1.7.1: Per-agent interaction counts. Key = other agent ID, Value = interaction count. Decays over time if agents are apart.</summary>
    public Dictionary<int, int> SocialBonds { get; } = new();

    /// <summary>Relationship types to other agents (spouse, parent, child, sibling).</summary>
    public Dictionary<int, RelationshipType> Relationships { get; } = new();

    // ── Directive #5 Fix 1: Socialize Cooldown & Diminishing Returns ──
    /// <summary>Tick when the agent last completed a Socialize action. Used for cooldown multiplier.</summary>
    public int LastSocializedTick { get; set; }

    /// <summary>Number of Socialize completions today. Reset at dawn. Used for daily diminishing returns.</summary>
    public int SocializeCountToday { get; set; }

    /// <summary>Per-partner Socialize count this sim-day. Reset at dawn. Cap of 3 per partner per day.</summary>
    public Dictionary<int, int> SocializePartnerCountToday { get; } = new();

    /// <summary>Tick of last dawn reset for SocializeCountToday.</summary>
    public int LastSocializeDawnResetTick { get; set; }

    // ── GDD v1.7.1: Action Dampening ──────────────────────────────────
    /// <summary>GDD v1.7.1: Last utility action chosen (for dampening consecutive same-action). Null when not set.</summary>
    public ActionType? LastChosenUtilityAction { get; set; }

    /// <summary>GDD v1.7.1: Number of consecutive ticks the same utility action was chosen. Used for dampening multiplier.</summary>
    public int ConsecutiveSameActionTicks { get; set; }

    /// <summary>Consecutive ticks in P2 food-seek without eating. Triggers explore escape at threshold.</summary>
    public int ConsecutiveFoodSeekTicks { get; set; }

    // ── Directive #10 Fix 1: Move chain re-evaluation ────────────────
    /// <summary>Counts consecutive Move steps within a move chain. Reset on non-Move actions.</summary>
    public int ConsecutiveMoveCount { get; set; }

    /// <summary>When true, forces a full decision re-evaluation on next tick (set by move chain checkpoint).</summary>
    public bool ForceReevaluation { get; set; }

    // ── Directive #10 Fix 2: Stuck detection for eat override ────────
    /// <summary>Ticks the agent has been at the same position despite having a Move pending.</summary>
    public int StuckAtPositionCount { get; set; }

    /// <summary>Last recorded position for stuck detection.</summary>
    public (int X, int Y) LastStuckCheckPos { get; set; }

    /// <summary>True if agent has been stuck for 3+ ticks (position unchanged despite Move goal).</summary>
    public bool IsStuck => StuckAtPositionCount >= 3;

    // ── Post-Playtest Fix 1: Safety-return suppression when stuck ───
    /// <summary>Tick until which safety-return-home is suppressed (allows local survival after move failures).</summary>
    public int SafetyReturnSuppressedUntil { get; set; }

    // ── Directive #11 Fix 1: Move failure counter ───────────────────
    /// <summary>Consecutive MoveTo failures. Reset on successful move or non-Move action.</summary>
    public int MoveFailCount { get; set; }

    /// <summary>Distance to home when recovery tracking started. Used to detect progress.</summary>
    public int RecoveryStartDistanceToHome { get; set; }

    /// <summary>Ticks spent in Stage 4 random walk. After 10-20 ticks, resets to Stage 1.</summary>
    public int RandomWalkTickCount { get; set; }

    /// <summary>Distance to goal target when recovery started. Used to detect progress toward any goal (not just home).</summary>
    public float RecoveryStartDistanceToGoal { get; set; }

    /// <summary>A* computed waypoints for Stage 2 recovery. Consumed one at a time.</summary>
    public List<(int X, int Y)>? RecoveryWaypoints { get; set; }

    /// <summary>Computed recovery stage based on MoveFailCount.
    /// Stage 1 (1-3): Normal A*. Stage 2 (4-10): Doubled node budget.
    /// Stage 3 (11-20): Greedy single-tile steps. Stage 4 (21+): Emergency.</summary>
    public int StuckRecoveryStage
    {
        get
        {
            if (MoveFailCount <= 3) return 1;
            if (MoveFailCount <= 10) return 2;
            if (MoveFailCount <= 20) return 3;
            return 4;
        }
    }

    // ── D19: Restlessness Statistics ────────────────────────────
    public float PeakRestlessness { get; set; }
    public float RestlessnessSum { get; set; }
    public int RestlessnessSampleCount { get; set; }
    public int RestlessnessAbove50Ticks { get; set; }

    // ── Directive #12 Fix 1: Tile blacklist ──────────────────────────
    /// <summary>Tiles blacklisted due to repeated pathfinding failures. (x, y, expiryTick).</summary>
    private readonly List<(int X, int Y, int ExpiryTick)> _blacklistedTiles = new();

    /// <summary>Blacklist a tile for the given duration. Agent will avoid selecting it as a target.</summary>
    public void BlacklistTile(int x, int y, int expiryTick)
    {
        // Don't duplicate — update existing entry if present
        for (int i = 0; i < _blacklistedTiles.Count; i++)
        {
            if (_blacklistedTiles[i].X == x && _blacklistedTiles[i].Y == y)
            {
                _blacklistedTiles[i] = (x, y, Math.Max(_blacklistedTiles[i].ExpiryTick, expiryTick));
                return;
            }
        }
        _blacklistedTiles.Add((x, y, expiryTick));
    }

    /// <summary>Returns true if the tile is currently blacklisted.</summary>
    public bool IsTileBlacklisted(int x, int y, int currentTick)
    {
        for (int i = _blacklistedTiles.Count - 1; i >= 0; i--)
        {
            if (_blacklistedTiles[i].ExpiryTick <= currentTick)
            {
                _blacklistedTiles.RemoveAt(i); // Prune expired
                continue;
            }
            if (_blacklistedTiles[i].X == x && _blacklistedTiles[i].Y == y)
                return true;
        }
        return false;
    }

    /// <summary>D20 Fix 1: Returns all currently-blacklisted tile coordinates as a HashSet
    /// for passing to SimplePathfinder.FindPath. Prunes expired entries.</summary>
    public HashSet<(int X, int Y)> GetBlacklistedTileSet(int currentTick)
    {
        var result = new HashSet<(int X, int Y)>();
        for (int i = _blacklistedTiles.Count - 1; i >= 0; i--)
        {
            if (_blacklistedTiles[i].ExpiryTick <= currentTick)
            {
                _blacklistedTiles.RemoveAt(i);
                continue;
            }
            result.Add((_blacklistedTiles[i].X, _blacklistedTiles[i].Y));
        }
        return result;
    }

    // ── Directive #10 Fix 4: DepositHome cooldown ────────────────────
    /// <summary>Tick when the agent last completed a DepositHome action.</summary>
    public int LastDepositTick { get; set; }

    // ── Directive #10 Fix 3: Forage mode entry tick ──────────────────
    /// <summary>Tick when agent entered Forage mode. Used for minimum commitment window.</summary>
    public int ForageModeEntryTick { get; set; }

    // ── Directive #12 Fix 2: Forage gather decision counter ──────────
    /// <summary>Number of successful Gather completions during current Forage trip.</summary>
    public int ForageGatherCount { get; set; }

    // ── Directive #12 Fix 5: Curiosity ramp ─────────────────────────
    /// <summary>Tick when the agent's settlement last made a discovery. Used for curiosity bonus.</summary>
    public int LastSettlementDiscoveryTick { get; set; }

    // ── Action History (GDD v1.7) ────────────────────────────────────
    /// <summary>Ring buffer of recent actions for death report UI. Capacity = ActionHistorySize (10).</summary>
    private readonly ActionRecord[] _actionHistory = new ActionRecord[SimConfig.ActionHistorySize];
    private int _actionHistoryIndex = 0;
    private int _actionHistoryCount = 0;

    // ── Death Tracking (GDD v1.7) ────────────────────────────────────
    /// <summary>Tick at which this agent died, or -1 if still alive.</summary>
    public int DeathTick { get; set; } = -1;

    /// <summary>Cause of death: "starvation", "old age", or "exposure". Null if alive.</summary>
    public string? DeathCause { get; set; }

    // ── Exposure (GDD v1.7) ──────────────────────────────────────────
    /// <summary>Set by Simulation.Tick() each tick. True if agent is NOT within shelter radius.</summary>
    public bool IsExposed { get; set; }

    /// <summary>Accumulates fractional exposure damage (0.3/tick). When >= 1.0, applies 1 HP damage.</summary>
    public float ExposureDamageAccumulator { get; set; }

    // ── v1.8 Corrections: Float accumulators for sub-integer-per-tick rates ──
    /// <summary>Accumulates fractional health regen. When >= 1.0, applies 1 HP heal.</summary>
    public float HealthRegenAccumulator { get; set; }

    /// <summary>Accumulates fractional starvation damage. When >= 1.0, applies 1 HP damage.</summary>
    public float StarvationDamageAccumulator { get; set; }

    // ── Explore Blacklist (Fix 5: Explore Direction Safety) ──────────
    /// <summary>Positions blacklisted during explore due to stuck detection.
    /// Key = (X, Y), Value = tick when blacklist expires.</summary>
    private readonly Dictionary<(int, int), int> _exploreBlacklist = new();

    /// <summary>Consecutive ticks the agent has been stuck during Explore mode.</summary>
    public int ExploreStuckTicks { get; set; }

    /// <summary>Consecutive ticks the committed explore direction was blocked by water.
    /// When this reaches a threshold, the agent aborts explore — the direction leads into water.</summary>
    public int ExploreWaterBlockedTicks { get; set; }

    /// <summary>D24 Fix 1B: Recently explored directions (last 3 trips). Used for cooldown scoring.</summary>
    public List<(int Dx, int Dy)> RecentExploreDirections { get; } = new();

    /// <summary>D24 Fix 1B: Tick of last explore trip end. Used for direction memory decay.</summary>
    public int LastExploreTripEndTick { get; set; }

    /// <summary>D24 Fix 1C: Agent position when Explore mode was entered. Used for budget waste detection.</summary>
    public (int X, int Y)? ExploreStartPosition { get; set; }

    /// <summary>Last Chebyshev-distance milestone (multiple of 5) at which return path was validated.</summary>
    public int LastReturnPathCheckDistance { get; set; } = 0;

    /// <summary>Blacklists a position so the agent avoids it during explore direction selection.</summary>
    public void BlacklistPosition(int x, int y, int expiryTick)
    {
        _exploreBlacklist[(x, y)] = expiryTick;
    }

    /// <summary>Returns true if the given position is currently blacklisted.</summary>
    public bool IsPositionBlacklisted(int x, int y, int currentTick)
    {
        if (_exploreBlacklist.TryGetValue((x, y), out int expiry))
        {
            if (currentTick < expiry) return true;
            _exploreBlacklist.Remove((x, y)); // Expired — clean up
        }
        return false;
    }

    /// <summary>Returns the explore blacklist (read-only access for direction selection).</summary>
    public IReadOnlyDictionary<(int, int), int> ExploreBlacklist => _exploreBlacklist;

    // ── Run Summary Tracking (lightweight per-tick counters) ──────────
    /// <summary>Per-action-type tick counter for run summary. Incremented each tick based on CurrentAction.</summary>
    public Dictionary<ActionType, int> ActionTickCounts { get; } = new();

    /// <summary>Per-mode tick counter for run summary. Incremented each tick based on CurrentMode.</summary>
    public Dictionary<BehaviorMode, int> ModeTickCounts { get; } = new();

    /// <summary>Number of distinct stuck episodes (StuckCounter crossed threshold).</summary>
    public int StuckEpisodeCount { get; set; }

    /// <summary>Total ticks spent stuck across all episodes.</summary>
    public int TotalStuckTicks { get; set; }

    /// <summary>Whether agent was stuck last tick (for episode boundary detection).</summary>
    public bool WasStuckLastTick { get; set; }

    /// <summary>Maximum Chebyshev distance from home tile observed during lifetime.</summary>
    public int MaxDistanceFromHome { get; set; }

    /// <summary>Tick when this agent was born (for birth record).</summary>
    public int BirthTick { get; set; }

    /// <summary>Parent 1 name (for birth record). Null for founding agents.</summary>
    public string? Parent1Name { get; set; }

    /// <summary>Parent 2 name (for birth record). Null for founding agents.</summary>
    public string? Parent2Name { get; set; }

    /// <summary>Increments per-tick tracking counters for action, mode, stuck, and distance from home.</summary>
    public void UpdateSummaryCounters()
    {
        // Action distribution
        ActionTickCounts.TryGetValue(CurrentAction, out int actionCount);
        ActionTickCounts[CurrentAction] = actionCount + 1;

        // Mode distribution
        ModeTickCounts.TryGetValue(CurrentMode, out int modeCount);
        ModeTickCounts[CurrentMode] = modeCount + 1;

        // Stuck tracking
        if (IsStuck)
        {
            TotalStuckTicks++;
            if (!WasStuckLastTick)
                StuckEpisodeCount++;
            WasStuckLastTick = true;
        }
        else
        {
            WasStuckLastTick = false;
        }

        // Max distance from home
        if (HomeTile.HasValue)
        {
            int dist = Math.Max(Math.Abs(X - HomeTile.Value.X), Math.Abs(Y - HomeTile.Value.Y));
            if (dist > MaxDistanceFromHome)
                MaxDistanceFromHome = dist;
        }
    }

    /// <summary>D19: Track restlessness statistics per tick.</summary>
    public void UpdateRestlessnessStats()
    {
        if (Restlessness > PeakRestlessness) PeakRestlessness = Restlessness;
        RestlessnessSum += Restlessness;
        RestlessnessSampleCount++;
        if (Restlessness > 50f) RestlessnessAbove50Ticks++;
    }

    // ── Day/Night and Rest ─────────────────────────────────────────────
    /// <summary>Tick when the agent last completed a rest cycle.</summary>
    public int LastRestTick { get; set; }

    // ── Directive #6 Fix 3: Decision heartbeat monitor ────────────────
    /// <summary>Tick when the agent last made a decision (any action chosen).
    /// Used to detect stuck agents — gap > 100 ticks triggers forced re-evaluation.</summary>
    public int LastDecisionTick { get; set; }

    /// <summary>Checks if it's nighttime based on the current tick.</summary>
    public static bool IsNightTime(int currentTick)
    {
        int hourOfDay = currentTick % SimConfig.TicksPerSimDay;
        return hourOfDay >= SimConfig.NightStartHour || hourOfDay < SimConfig.NightEndHour;
    }

    /// <summary>Checks if the agent needs rest (nighttime + hasn't rested recently).</summary>
    public bool NeedsRest(int currentTick)
    {
        return IsNightTime(currentTick) && (currentTick - LastRestTick > (int)(SimConfig.TicksPerSimDay * 0.7));
    }

    // ── GDD v1.8 Section 7: Inventory Food Decay ──────────────────────
    /// <summary>Section 7: Tick when inventory food last decayed. Creates pressure to store food at home.</summary>
    public int LastInventoryDecayTick { get; set; }

    // ── GDD v1.8 Section 3: Stability Score History ─────────────────
    /// <summary>Section 3: Rolling 7-sim-day food availability history. Sampled once per sim-day.</summary>
    private readonly float[] _foodHistory = new float[7];
    /// <summary>Section 3: Rolling 7-sim-day health history. Sampled once per sim-day.</summary>
    private readonly int[] _healthHistory = new int[7];
    /// <summary>Section 3: Current write index into history ring buffers.</summary>
    private int _historyIndex;
    /// <summary>Section 3: How many history samples have been recorded (0-7). Used to avoid computing trends from empty buffers.</summary>
    private int _historySampleCount;
    /// <summary>Section 3: Tick when history was last sampled.</summary>
    public int LastHistorySampleTick { get; set; }

    // ── Constructor ────────────────────────────────────────────────────
    /// <summary>
    /// Creates a new agent. Accepts optional traits and seeded Random for reproducibility.
    /// First-gen agents get 2 random traits from the pool. Children inherit via Reproduce().
    /// GDD v1.8: IsMale gender property, curated name support, 8-tier tool gathering multipliers.
    /// </summary>
    public Agent(int x, int y, string? name = null, int startingAge = 0,
        PersonalityTrait[]? traits = null, Random? rng = null, bool? isMale = null,
        IEnumerable<string>? livingNames = null)
    {
        var r = rng ?? Random.Shared;
        Id = _nextId++;
        IsMale = isMale ?? (r.Next(2) == 0);
        Name = name ?? NameGenerator.Generate(r, IsMale, livingNames);

        X = x;
        Y = y;

        Hunger = 100f; // Fully fed (GDD: 100 = full, 0 = starving)
        Health = 100;
        Age = startingAge;
        IsAlive = true;
        // D19: Newborns start calm; founders get RestlessnessFounderStart set by Simulation.SpawnAgent()
        Restlessness = 0f;
        CurrentAction = ActionType.Idle;
        ReproductionCooldownRemaining = 0;

        // Multi-tick state starts clear
        PendingAction = null;
        ActionProgress = 0f;
        ActionDurationTicks = 0f;
        ActionTarget = null;
        ActionTargetResource = null;
        ActionTargetRecipe = null;
        ActionTargetAgentId = null;
        HuntTargetAnimalId = null;
        HuntPursuitTicks = 0;
        CombatTargetAnimalId = null;
        CombatTicksRemaining = 0;

        Knowledge = new HashSet<string>();
        Familiarity = new Dictionary<string, int>();
        Inventory = new Dictionary<ResourceType, int>();
        Memory = new List<MemoryEntry>();

        LastActivePerceptionTick = 0;
        ForceActivePerception = false;
        Community = null;

        // GDD v1.7.1: Personality traits — 2 per agent
        if (traits != null)
        {
            Traits = traits;
        }
        else
        {
            var allTraits = Enum.GetValues<PersonalityTrait>();
            var t1 = allTraits[r.Next(allTraits.Length)];
            PersonalityTrait t2;
            do { t2 = allTraits[r.Next(allTraits.Length)]; } while (t2 == t1 && allTraits.Length > 1);
            Traits = new[] { t1, t2 };
        }

        // GDD v1.7.1: Dampening state starts clear
        LastChosenUtilityAction = null;
        ConsecutiveSameActionTicks = 0;
        HomeTile = null;

        // v1.8: Behavioral mode starts at Home
        CurrentMode = BehaviorMode.Home;
        ModeEntryTick = 0;
        PreviousMode = null;
    }

    /// <summary>Clears all multi-tick action state.</summary>
    public void ClearPendingAction()
    {
        PendingAction = null;
        ActionProgress = 0f;
        ActionDurationTicks = 0f;
        ActionTarget = null;
        ActionTargetResource = null;
        ActionTargetRecipe = null;
        ActionTargetAgentId = null;
        HuntTargetAnimalId = null;
        HuntPursuitTicks = 0;
        CombatTargetAnimalId = null;
        CombatTicksRemaining = 0;
        IsMoving = false;
    }

    /// <summary>D26: Set move interpolation metadata for smooth rendering.</summary>
    public void SetMoveInterpolation(int destX, int destY)
    {
        MoveOrigin = (X, Y);
        MoveDestination = (destX, destY);
        IsMoving = true;
    }

    /// <summary>Returns true if the agent is currently performing a multi-tick action.</summary>
    public bool IsBusy => PendingAction != null && ActionProgress < ActionDurationTicks;

    /// <summary>GDD v1.7: Records an action in the ring buffer for death report inspection.</summary>
    public void RecordAction(ActionType action, int tick, string detail)
    {
        _actionHistory[_actionHistoryIndex] = new ActionRecord(action, tick, detail);
        _actionHistoryIndex = (_actionHistoryIndex + 1) % _actionHistory.Length;
        if (_actionHistoryCount < _actionHistory.Length)
            _actionHistoryCount++;
    }

    /// <summary>GDD v1.7: Returns the last N actions from the ring buffer, most recent first.</summary>
    public List<ActionRecord> GetLastActions(int count = 8)
    {
        var result = new List<ActionRecord>();
        int toReturn = Math.Min(count, _actionHistoryCount);
        for (int i = 0; i < toReturn; i++)
        {
            int idx = (_actionHistoryIndex - 1 - i + _actionHistory.Length) % _actionHistory.Length;
            result.Add(_actionHistory[idx]);
        }
        return result;
    }

    /// <summary>Returns total items in inventory.</summary>
    public int InventoryCount()
    {
        int total = 0;
        foreach (var kvp in Inventory)
            total += kvp.Value;
        return total;
    }

    /// <summary>Returns true if the agent has room to carry more items.</summary>
    public bool HasInventorySpace(int amount = 1)
    {
        int capacity = SimConfig.InventoryCapacity;
        // improved_shelter grants +10 inventory capacity
        if (Knowledge.Contains("improved_shelter"))
            capacity += 10;
        return InventoryCount() + amount <= capacity;
    }

    /// <summary>
    /// Centralized inventory add with global invariants.
    /// Enforces: capacity limit, non-food cap (≥9 blocks), hunger gate (hunger &lt; 50 blocks non-food).
    /// Returns true if item was added, false if blocked by any invariant.
    /// Use enforceNonFoodGuards=false for paths that don't currently check non-food limits
    /// (e.g., CompleteClearLand, TryDispatchBuild material withdrawal).
    /// </summary>
    public bool TryAddToInventory(ResourceType type, int amount, bool enforceNonFoodGuards = true)
    {
        if (amount <= 0) return false;

        // Invariant 1: Capacity limit
        if (!HasInventorySpace(amount)) return false;

        // Non-food guards (Stone, Ore, Wood, Hide, Bone)
        bool isNonFood = type == ResourceType.Stone || type == ResourceType.Ore || type == ResourceType.Wood
                      || type == ResourceType.Hide || type == ResourceType.Bone;
        if (enforceNonFoodGuards && isNonFood)
        {
            // Invariant 2: Non-food cap — block if carrying 9+ non-food items
            int nonFoodCount = Inventory.GetValueOrDefault(ResourceType.Stone, 0)
                             + Inventory.GetValueOrDefault(ResourceType.Ore, 0)
                             + Inventory.GetValueOrDefault(ResourceType.Wood, 0)
                             + Inventory.GetValueOrDefault(ResourceType.Hide, 0)
                             + Inventory.GetValueOrDefault(ResourceType.Bone, 0);
            if (nonFoodCount >= SimConfig.NonFoodInventoryHardCap) return false;

            // Invariant 3: Hunger gate — hungry agents don't pick up non-food
            if (Hunger < SimConfig.NonFoodPickupHungerGate) return false;
        }

        // Add to inventory
        Inventory[type] = Inventory.GetValueOrDefault(type, 0) + amount;
        return true;
    }

    /// <summary>
    /// D22 Fix 3: Centralized eat-from-home-storage.
    /// Checks if the tile has home storage with food, withdraws 1 item, and applies hunger restoration.
    /// Returns true if the agent ate. Callers handle logging/events.
    /// </summary>
    public bool TryEatFromHomeStorage(Tile homeTile)
    {
        if (!homeTile.HasHomeStorage || homeTile.HomeTotalFood <= 0)
            return false;

        var (wType, wAmount) = homeTile.WithdrawAnyFoodFromHome(1);
        if (wAmount <= 0) return false;

        int restore = wType == ResourceType.PreservedFood ? SimConfig.FoodRestorePreserved : SimConfig.FoodRestoreRaw;
        Hunger = Math.Min(100f, Hunger + restore);
        return true;
    }

    /// <summary>Returns true if agent knows lean_to or improved_shelter.</summary>
    public bool KnowsAnyShelterRecipe() =>
        Knowledge.Contains("lean_to") || Knowledge.Contains("improved_shelter");

    /// <summary>Returns true if the agent has Wood in inventory.</summary>
    public bool HasWoodInInventory() =>
        Inventory.TryGetValue(ResourceType.Wood, out int w) && w > 0;

    /// <summary>Returns total food (Berries + Grain + Animals + Fish + PreservedFood) in inventory.</summary>
    public int FoodInInventory()
    {
        int total = 0;
        if (Inventory.TryGetValue(ResourceType.Berries, out int b)) total += b;
        if (Inventory.TryGetValue(ResourceType.Grain, out int g)) total += g;
        if (Inventory.TryGetValue(ResourceType.Meat, out int mt)) total += mt;
        if (Inventory.TryGetValue(ResourceType.Fish, out int f)) total += f;
        if (Inventory.TryGetValue(ResourceType.PreservedFood, out int p)) total += p;
        return total;
    }

    // ── Lifecycle ──────────────────────────────────────────────────────

    /// <summary>
    /// Updates needs each tick: hunger drain, starvation damage, health regen,
    /// age increment, reproduction cooldown, and old-age mortality.
    /// </summary>
    public void UpdateNeeds(Tile? currentTile = null, int currentTick = 0)
    {
        if (!IsAlive) return;

        Age++;

        // GDD v1.7.2: Stage-based hunger drain (Infant=4, Youth=6, Adult=8)
        float hungerDrain = Stage switch
        {
            DevelopmentStage.Infant => SimConfig.InfantHungerDrain,
            DevelopmentStage.Youth => SimConfig.YouthHungerDrain,
            _ => SimConfig.HungerDrainPerTick
        };

        Hunger = Math.Max(0f, Hunger - hungerDrain);

        // Starvation damage at 0 hunger — uses float accumulator for sub-integer per-tick damage
        if (Hunger <= 0f)
        {
            StarvationDamageAccumulator += SimConfig.StarvationDamagePerTick;
            if (StarvationDamageAccumulator >= 1f)
            {
                int dmg = (int)StarvationDamageAccumulator;
                Health = Math.Max(0, Health - dmg);
                StarvationDamageAccumulator -= dmg;
            }
        }
        else
        {
            StarvationDamageAccumulator = 0f; // Reset when not starving

            if (Hunger > 50f)
            {
                // GDD v1.7.2: Exposure suppresses health regen instead of dealing direct damage.
                // BALANCE FIX: Exposed agents without shelter/clothing still get minimal regen
                // to prevent permanent health loss from minor starvation dips in early game.
                float regenMultiplier = 1f;
                if (IsExposed)
                {
                    if (Knowledge.Contains("clothing") || Knowledge.Contains("weaving"))
                        regenMultiplier = SimConfig.ExposureClothingReduction; // 50% regen with clothing
                    else
                        regenMultiplier = 0.5f; // Minimal regen when fully exposed (prevents death spiral)
                }

                // Health regen uses float accumulator for sub-integer per-tick rates
                float regen;
                bool inShelter = currentTile?.HasShelter ?? false;

                if ((CurrentAction == ActionType.Rest || CurrentAction == ActionType.GrowingUp) && inShelter)
                    regen = SimConfig.HealthRegenShelter;
                else if (CurrentAction == ActionType.Rest || CurrentAction == ActionType.GrowingUp)
                    regen = SimConfig.HealthRegenResting;
                else
                    regen = SimConfig.HealthRegenBase;

                // Fire knowledge gives warmth regen boost
                if (Knowledge.Contains("fire"))
                    regen += 0.04f; // Proportional boost (~2x base when not resting)

                // Weaving gives health regen in shelter
                if (inShelter && Knowledge.Contains("weaving"))
                    regen += 0.02f;

                float finalRegen = regen * regenMultiplier;
                HealthRegenAccumulator += Math.Max(0f, finalRegen);
                if (HealthRegenAccumulator >= 1f)
                {
                    int heal = (int)HealthRegenAccumulator;
                    Health = Math.Min(100, Health + heal);
                    HealthRegenAccumulator -= heal;
                }
            }
        }

        // Reproduction cooldown tick-down
        if (ReproductionCooldownRemaining > 0)
            ReproductionCooldownRemaining--;

        // GDD v1.8 Section 7: Inventory food decay — mild pressure to store food at home
        if (currentTick > 0 && FoodInInventory() > 0
            && currentTick - LastInventoryDecayTick >= SimConfig.InventoryFoodDecayInterval)
        {
            DecayOneInventoryFood();
            LastInventoryDecayTick = currentTick;
        }

        // Old-age mortality — set health to 0 so Simulation handles death with tile context
        if (Age >= SimConfig.GuaranteedDeathAge)
        {
            Health = 0;
        }
        else if (Age >= SimConfig.OldAgeThreshold)
        {
            float deathChance = (float)(Age - SimConfig.OldAgeThreshold)
                / (SimConfig.GuaranteedDeathAge - SimConfig.OldAgeThreshold);
            if (Random.Shared.NextDouble() < deathChance)
                Health = 0;
        }
    }

    /// <summary>
    /// Kills the agent and drops inventory to the specified tile (if provided).
    /// GDD v1.7: Accepts cause and tick for death report system.
    /// </summary>
    public void Die(Tile? tile, string cause = "unknown", int tick = -1, World? world = null)
    {
        if (!IsAlive) return;

        IsAlive = false;
        Health = 0;
        DeathCause = cause;
        DeathTick = tick;
        RecordAction(CurrentAction, tick, $"Died: {cause}");
        ClearPendingAction();

        // Drop inventory to tile (skip PreservedFood — keep in Inventory for death report display)
        if (tile != null)
        {
            foreach (var kvp in Inventory)
            {
                if (!tile.Resources.ContainsKey(kvp.Key))
                    tile.Resources[kvp.Key] = 0;
                tile.Resources[kvp.Key] += kvp.Value;

                // Update world food counter for food items returned to tile
                if (world != null && ModeTransitionManager.IsFoodResource(kvp.Key))
                    world.AdjustWorldFood(kvp.Value);
            }
        }
        Inventory.Clear();
    }

    /// <summary>
    /// Drops up to 'amount' non-food resources from inventory onto the specified tile.
    /// Prioritizes the most abundant non-food resource first (Wood, Stone, Ore).
    /// Used by AgentAI when a starving agent has a full inventory of crafting materials.
    /// Returns the total count of resources actually dropped.
    /// </summary>
    public int DropNonFoodToTile(Tile tile, int amount)
    {
        if (!IsAlive || amount <= 0) return 0;

        ResourceType[] nonFoodTypes = { ResourceType.Wood, ResourceType.Stone, ResourceType.Ore };
        int dropped = 0;

        // Sort by quantity descending — drop the most abundant resource first
        var sorted = nonFoodTypes
            .Where(r => Inventory.ContainsKey(r) && Inventory[r] > 0)
            .OrderByDescending(r => Inventory[r])
            .ToList();

        foreach (var resource in sorted)
        {
            if (dropped >= amount) break;

            int toDrop = Math.Min(Inventory[resource], amount - dropped);
            Inventory[resource] -= toDrop;
            if (Inventory[resource] <= 0)
                Inventory.Remove(resource);

            // Return resources to tile (same pattern as Die())
            if (!tile.Resources.ContainsKey(resource))
                tile.Resources[resource] = 0;
            tile.Resources[resource] += toDrop;

            dropped += toDrop;
        }

        return dropped;
    }

    // ── Actions ────────────────────────────────────────────────────────

    /// <summary>
    /// Consumes one food item from inventory. Returns true if food was eaten.
    /// GDD v1.7: PreservedFood checked first (best value), then Berries, Grain, Animals, Fish.
    /// Cooking is now a visible 2-tick action handled by AgentAI — Eat() handles raw/preserved only.
    /// Use EatCooked() for the completion of a Cook action.
    /// </summary>
    public bool Eat(bool cooked = false)
    {
        if (!IsAlive) return false;

        // GDD v1.7: Try preserved food first (restores 70, no cooking needed)
        if (Inventory.TryGetValue(ResourceType.PreservedFood, out int preserved) && preserved > 0)
        {
            Inventory[ResourceType.PreservedFood]--;
            if (Inventory[ResourceType.PreservedFood] <= 0)
                Inventory.Remove(ResourceType.PreservedFood);

            Hunger = Math.Min(100f, Hunger + SimConfig.FoodRestorePreserved);
            CurrentAction = ActionType.Eat;
            return true;
        }

        ResourceType[] foodTypes = { ResourceType.Berries, ResourceType.Grain, ResourceType.Meat, ResourceType.Fish };

        foreach (var food in foodTypes)
        {
            if (Inventory.TryGetValue(food, out int amount) && amount > 0)
            {
                Inventory[food]--;
                if (Inventory[food] <= 0)
                    Inventory.Remove(food);

                // GDD v1.7: cooked flag is set when called from Cook action completion
                int restore = cooked ? SimConfig.FoodRestoreCooked : SimConfig.FoodRestoreRaw;
                Hunger = Math.Min(100f, Hunger + restore);
                CurrentAction = ActionType.Eat;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// GDD v1.7: Returns true if agent has food that can be cooked (Animals, Fish, Grain — not Berries, not PreservedFood).
    /// </summary>
    public bool HasCookableFood()
    {
        ResourceType[] cookable = { ResourceType.Meat, ResourceType.Fish, ResourceType.Grain };
        return cookable.Any(f => Inventory.TryGetValue(f, out int amt) && amt > 0);
    }

    /// <summary>
    /// GDD v1.7: Returns true if agent only has Berries (which can't be cooked) or PreservedFood.
    /// </summary>
    public bool HasOnlyRawFood()
    {
        if (Inventory.TryGetValue(ResourceType.PreservedFood, out int p) && p > 0) return false;
        if (Inventory.TryGetValue(ResourceType.Berries, out int b) && b > 0) return true;
        return false;
    }

    /// <summary>
    /// Gathers a specific resource type from a tile, respecting efficiency and inventory capacity.
    /// Returns the amount gathered. Used as the completion step of the multi-tick gather action.
    /// </summary>
    public int GatherFrom(Tile tile, ResourceType resource, int baseAmount = 3, World? world = null)
    {
        if (!IsAlive) return 0;
        if (!tile.Resources.TryGetValue(resource, out int available) || available <= 0)
            return 0;

        // Tool multiplier: higher-tier tools give better gathering
        float toolMultiplier = GetGatheringMultiplier();

        // Bone tools: 2x fishing efficiency
        if (resource == ResourceType.Fish && Knowledge.Contains("bone_tools"))
            toolMultiplier *= 2.0f;

        // Apply gathering efficiency (biome * tool)
        int effectiveAmount = (int)Math.Ceiling(baseAmount * tile.GatheringEfficiencyMultiplier * toolMultiplier);

        // Respect tile availability
        effectiveAmount = Math.Min(effectiveAmount, available);

        // Respect inventory capacity — use effective capacity (with improved_shelter bonus)
        int capacity = SimConfig.InventoryCapacity;
        if (Knowledge.Contains("improved_shelter")) capacity += 10;
        int spaceLeft = capacity - InventoryCount();
        effectiveAmount = Math.Min(effectiveAmount, spaceLeft);

        if (effectiveAmount <= 0) return 0;

        // D24: Hard-cap non-food at gather completion (safety net — callers should check,
        // but multi-tick gathers can accumulate past the cap between dispatch and completion)
        if (resource == ResourceType.Stone || resource == ResourceType.Ore || resource == ResourceType.Wood
            || resource == ResourceType.Hide || resource == ResourceType.Bone)
        {
            int nonFoodNow = Inventory.GetValueOrDefault(ResourceType.Stone, 0)
                           + Inventory.GetValueOrDefault(ResourceType.Ore, 0)
                           + Inventory.GetValueOrDefault(ResourceType.Wood, 0)
                           + Inventory.GetValueOrDefault(ResourceType.Hide, 0)
                           + Inventory.GetValueOrDefault(ResourceType.Bone, 0);
            int room = SimConfig.NonFoodInventoryHardCap - nonFoodNow;
            if (room <= 0) return 0;
            effectiveAmount = Math.Min(effectiveAmount, room);
        }

        // D22: Route through TryAddToInventory for centralized add.
        // enforceNonFoodGuards=false: no hunger gate here (applied at dispatch, not completion).
        if (!TryAddToInventory(resource, effectiveAmount, enforceNonFoodGuards: false))
            return 0;

        // Remove from tile (after successful inventory add to prevent resource loss)
        tile.Resources[resource] -= effectiveAmount;
        if (tile.Resources[resource] <= 0)
            tile.Resources.Remove(resource);

        // Update world food counter
        if (world != null && ModeTransitionManager.IsFoodResource(resource))
            world.AdjustWorldFood(-effectiveAmount);

        CurrentAction = ActionType.Gather;
        LastGatheredResource = resource;
        return effectiveAmount;
    }

    /// <summary>
    /// Returns the gathering speed multiplier based on the agent's tool knowledge.
    /// GDD v1.8 Phase 2 tech tree: stone_knife=1.25x, crude_axe=1.5x, refined=1.75x,
    /// hafted=2.0x, copper=2.5x, bronze=3.0x, iron=4.0x, steel=4.5x.
    /// Tool multipliers replace within category (agent uses best tool).
    /// </summary>
    public float GetGatheringMultiplier()
    {
        if (Knowledge.Contains("steel_working")) return 4.5f;
        if (Knowledge.Contains("iron_working")) return 4.0f;
        if (Knowledge.Contains("bronze_working")) return 3.0f;
        if (Knowledge.Contains("copper_working")) return 2.5f;
        if (Knowledge.Contains("hafted_tools")) return 2.0f;
        if (Knowledge.Contains("refined_tools")) return 1.75f;
        if (Knowledge.Contains("crude_axe")) return 1.5f;
        if (Knowledge.Contains("stone_knife")) return 1.25f;
        return 1.0f;
    }

    /// <summary>
    /// Returns the build speed multiplier based on the agent's tool knowledge.
    /// GDD v1.8: bronze=2x, iron=3x, steel=4x build speed.
    /// </summary>
    public float GetBuildSpeedMultiplier()
    {
        if (Knowledge.Contains("steel_working")) return 4.0f;
        if (Knowledge.Contains("iron_working")) return 3.0f;
        if (Knowledge.Contains("bronze_working")) return 2.0f;
        return 1.0f;
    }

    /// <summary>
    /// Move to a new position. Validates terrain and bounds.
    /// Returns true if move was successful.
    /// In multi-tick mode, this is called by CompleteMove after the duration elapses.
    /// </summary>
    public bool MoveTo(int newX, int newY, World world)
    {
        if (!IsAlive) return false;

        if (!world.IsInBounds(newX, newY))
            return false;

        // Directive #5 Fix 5: Teleportation guard — no agent should ever move > 2 tiles per tick.
        // A jump of 97 tiles (seed 5005) indicates an uninitialized position or array index bug.
        int dx = Math.Abs(newX - X), dy = Math.Abs(newY - Y);
        if (dx > 2 || dy > 2)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[TELEPORT GUARD] Agent {Id} ({Name}) attempted teleport from ({X},{Y}) to ({newX},{newY}) — rejected (delta {dx},{dy})");
            return false;
        }

        var targetTile = world.GetTile(newX, newY);

        // Impassable terrain check
        if (float.IsPositiveInfinity(targetTile.MovementCostMultiplier))
            return false;

        // Mode-aware hard distance ceiling from home.
        // Explore: ExploreMaxRange, Forage: ForageMaxRange, others: HardMoveCeiling.
        // D15 Fix: Allow moves that DECREASE distance (walking home) even past the ceiling.
        if (HomeTile.HasValue)
        {
            int currentDist = Math.Max(Math.Abs(X - HomeTile.Value.X), Math.Abs(Y - HomeTile.Value.Y));
            int newDist = Math.Max(Math.Abs(newX - HomeTile.Value.X), Math.Abs(newY - HomeTile.Value.Y));
            int moveLimit = CurrentMode == BehaviorMode.Explore ? SimConfig.ExploreMaxRange
                : CurrentMode == BehaviorMode.Forage ? SimConfig.ForageMaxRange
                : SimConfig.HardMoveCeiling;
            if (newDist > moveLimit && newDist >= currentDist)
            {
                return false;
            }
        }

        // GDD v1.6.2: Incremental spatial index update
        int oldX = X, oldY = Y;
        X = newX;
        Y = newY;
        world.UpdateAgentPosition(this, oldX, oldY, newX, newY);
        CurrentAction = ActionType.Move;
        IsMoving = false; // D26: Move complete, position updated

        // Movement hunger cost: extra drain from difficult terrain
        float extraDrain = (float)Math.Round((targetTile.MovementCostMultiplier - 1.0f) * SimConfig.MovementHungerCostScale);

        // Clothing reduces movement hunger cost by 50%
        if (Knowledge.Contains("clothing"))
            extraDrain *= 0.5f;

        if (extraDrain > 0f)
            Hunger = Math.Max(0f, Hunger - extraDrain);

        return true;
    }

    /// <summary>
    /// v1.8 Corrections: Returns the movement duration in ticks (float) for moving to a tile.
    /// Sub-tick movement is accumulated via float progress. Plains ≈ 0.125 ticks per tile.
    /// </summary>
    public static float GetMoveDuration(BiomeType biome)
    {
        return biome switch
        {
            BiomeType.Plains => SimConfig.MoveDurationPlains,
            BiomeType.Forest => SimConfig.MoveDurationForest,
            BiomeType.Desert => SimConfig.MoveDurationDesert,
            BiomeType.Mountain => SimConfig.MoveDurationMountain,
            _ => SimConfig.MoveDurationPlains
        };
    }

    /// <summary>
    /// Returns the gather duration in ticks, modified by tool knowledge.
    /// v1.8 Phase 2: Base 12 ticks (~24 min). Better tools reduce duration.
    /// </summary>
    public int GetGatherDuration()
    {
        float duration = SimConfig.GatherDurationBase;
        if (Knowledge.Contains("steel_working"))
            duration *= 0.35f; // Steel: 65% faster
        else if (Knowledge.Contains("iron_working"))
            duration *= 0.4f; // Iron: 60% faster
        else if (Knowledge.Contains("bronze_working"))
            duration *= 0.5f; // Bronze: 50% faster
        else if (Knowledge.Contains("copper_working"))
            duration *= 0.6f; // Copper: 40% faster
        else if (Knowledge.Contains("hafted_tools"))
            duration *= 0.65f; // Hafted: 35% faster
        else if (Knowledge.Contains("refined_tools"))
            duration *= 0.7f; // Refined: 30% faster
        else if (Knowledge.Contains("crude_axe") || Knowledge.Contains("stone_knife"))
            duration *= 0.8f; // Basic: 20% faster
        return Math.Max(3, (int)duration);
    }

    /// <summary>
    /// GDD v1.8 Section 3: Hard gates only — age, cooldown, adult stage.
    /// Fixed hunger/health thresholds are REMOVED — replaced by stability score.
    /// </summary>
    public bool CanReproduce()
    {
        return IsAlive
            && Stage == DevelopmentStage.Adult
            && Age >= SimConfig.ReproductionMinAge
            && ReproductionCooldownRemaining <= 0;
    }

    /// <summary>
    /// GDD v1.8 Section 3: Additional hard gate — both agents must have shelter within 5 tiles.
    /// "No breeding in the wild." Uses KnownStructures (permanent memory) for efficiency.
    /// </summary>
    public bool CanReproduceWithPartner(Agent partner, World world)
    {
        if (IsMale == partner.IsMale) return false;
        if (!CanReproduce() || !partner.CanReproduce()) return false;
        return HasShelterNearby(world, SimConfig.ReproductionShelterProximity)
            && partner.HasShelterNearby(world, SimConfig.ReproductionShelterProximity);
    }

    /// <summary>Section 3: Checks if any known shelter is within the given radius of this agent.</summary>
    private bool HasShelterNearby(World world, int radius)
    {
        // Check permanent landmark memory first (efficient, no world lookups)
        foreach (var ks in KnownStructures)
        {
            if ((ks.StructureType == "lean_to" || ks.StructureType == "shelter" || ks.StructureType == "improved_shelter")
                && Math.Max(Math.Abs(ks.X - X), Math.Abs(ks.Y - Y)) <= radius)
                return true;
        }
        // Fallback: scan nearby tiles in the world (covers edge case where structure
        // was built but not yet perceived into permanent memory)
        for (int dx = -radius; dx <= radius; dx++)
            for (int dy = -radius; dy <= radius; dy++)
            {
                int tx = X + dx, ty = Y + dy;
                if (world.IsInBounds(tx, ty) && world.GetTile(tx, ty).HasShelter)
                    return true;
            }
        return false;
    }

    /// <summary>
    /// GDD v1.8 Section 3: Computes the composite stability score (0.0-1.0) for reproduction.
    /// 0.4 × FoodSecurity + 0.2 × ShelterQuality + 0.2 × Dependents + 0.2 × HealthTrend
    /// </summary>
    public float ComputeStabilityScore(World world, List<Agent> allAgents)
    {
        // 1. Food security (0.4 weight) — trend + current reserves
        float foodTrend = GetFoodTrend(); // 0 (depleting) to 1 (growing)
        float currentFood = FoodInInventory();
        if (HomeTile.HasValue && world.IsInBounds(HomeTile.Value.X, HomeTile.Value.Y))
        {
            var homeTile = world.GetTile(HomeTile.Value.X, HomeTile.Value.Y);
            if (homeTile.HasHomeStorage)
                currentFood += homeTile.HomeTotalFood;
        }
        // Reserve bonus: up to +0.3 for 9+ food
        float reserveBonus = Math.Min(0.3f, currentFood / 30f);
        float foodSecurity = Math.Clamp(foodTrend + reserveBonus, 0f, 1f);

        // 2. Shelter quality (0.2 weight)
        float shelterQuality = 0f;
        if (HomeTile.HasValue && world.IsInBounds(HomeTile.Value.X, HomeTile.Value.Y))
        {
            var homeTile = world.GetTile(HomeTile.Value.X, HomeTile.Value.Y);
            if (homeTile.Structures.Contains("improved_shelter"))
                shelterQuality = SimConfig.ShelterQualityImproved;
            else if (homeTile.HasShelter)
                shelterQuality = SimConfig.ShelterQualityLeanTo;
        }

        // 3. Existing dependents (0.2 weight) — infants/youth with family bonds
        float dependentPenalty = 0f;
        foreach (var other in allAgents)
        {
            if (!other.IsAlive || other.Id == Id) continue;
            if (!SocialBonds.TryGetValue(other.Id, out int bond)) continue;
            if (bond < SimConfig.SocialBondFamilyStart) continue;

            if (other.Stage == DevelopmentStage.Infant)
                dependentPenalty += SimConfig.DependentReductionInfant;
            else if (other.Stage == DevelopmentStage.Youth)
                dependentPenalty += SimConfig.DependentReductionYouth;
        }
        float dependentScore = Math.Max(0f, 1f - dependentPenalty);

        // 4. Health trend (0.2 weight)
        float healthTrend = GetHealthTrend(); // 0 (declining) to 1 (improving)
        // Sharp penalty if health below 50
        if (Health < 50) healthTrend *= 0.3f;

        return SimConfig.StabilityWeightFood * foodSecurity
             + SimConfig.StabilityWeightShelter * shelterQuality
             + SimConfig.StabilityWeightDependents * dependentScore
             + SimConfig.StabilityWeightHealth * healthTrend;
    }

    /// <summary>
    /// GDD v1.8 Section 3: Attempts reproduction with a partner.
    /// Returns the child agent or null on failure.
    /// Stability score handles quality gating; flat base chance provides some randomness.
    /// Food consumed from home storage first, then inventory.
    /// Child inherits settlement knowledge + clothing innate knowledge.
    /// GDD v1.7.1: Child inherits traits from parent pool (4 traits), 15% mutation per slot.
    /// </summary>
    public Agent? Reproduce(Agent partner, World world, Tile currentTile,
        Random? rng = null, SettlementKnowledge? knowledgeSystem = null, List<Settlement>? settlements = null,
        IEnumerable<string>? livingNames = null)
    {
        if (!CanReproduce() || !partner.CanReproduce())
            return null;

        var r = rng ?? Random.Shared;

        // GDD v1.8: Flat base chance (stability score already gates via utility scorer)
        if (r.NextDouble() > SimConfig.ReproductionBaseChance)
            return null;

        // GDD v1.8 Section 3: Consume food from home storage first, then inventory
        ConsumeFoodFromHomeFirst(SimConfig.ReproductionFoodCost, world);
        partner.ConsumeFoodFromHomeFirst(SimConfig.ReproductionFoodCost, world);

        // Set cooldowns
        ReproductionCooldownRemaining = SimConfig.ReproductionCooldown;
        partner.ReproductionCooldownRemaining = SimConfig.ReproductionCooldown;

        // GDD v1.7.1: Trait inheritance — draw 2 from parent pool of 4 with 15% mutation
        var parentPool = new[] { Traits[0], Traits[1], partner.Traits[0], partner.Traits[1] };
        var allTraits = Enum.GetValues<PersonalityTrait>();
        var childTraits = new PersonalityTrait[2];
        for (int i = 0; i < 2; i++)
        {
            if (r.NextDouble() < SimConfig.TraitMutationChance)
                childTraits[i] = allTraits[r.Next(allTraits.Length)]; // Mutation
            else
                childTraits[i] = parentPool[r.Next(parentPool.Length)]; // Inherit
        }

        // Fix 3: Create child at HomeTile (not parent's current position — prevents off-tile spawn bug)
        int childX = HomeTile.HasValue ? HomeTile.Value.X : X;
        int childY = HomeTile.HasValue ? HomeTile.Value.Y : Y;
        var child = new Agent(childX, childY, traits: childTraits, rng: r, livingNames: livingNames);
        child.Hunger = 80f; // Child starts slightly hungry per GDD
        child.Age = 0;

        // Birth record tracking for run summary
        child.Parent1Name = Name;
        child.Parent2Name = partner.Name;

        // GDD v1.7.1: Child inherits parent's HomeTile
        child.HomeTile = HomeTile;

        // GDD v1.8 Section 3: Child inherits clothing as innate knowledge
        child.Knowledge.Add("clothing");

        // GDD v1.8 Section 3: Child inherits settlement knowledge
        if (knowledgeSystem != null && settlements != null)
        {
            var settlementKnowledge = knowledgeSystem.GetSettlementKnowledge(settlements, X, Y);
            foreach (var k in settlementKnowledge)
                child.Knowledge.Add(k);
        }

        // GDD v1.7.1: Family bonds — parent↔child start at SocialBondFamilyStart
        int familyStart = SimConfig.SocialBondFamilyStart;
        SocialBonds[child.Id] = familyStart;
        child.SocialBonds[Id] = familyStart;
        partner.SocialBonds[child.Id] = familyStart;
        child.SocialBonds[partner.Id] = familyStart;

        // Set relationship types
        Relationships[partner.Id] = RelationshipType.Spouse;
        partner.Relationships[Id] = RelationshipType.Spouse;
        Relationships[child.Id] = RelationshipType.Child;
        partner.Relationships[child.Id] = RelationshipType.Child;
        child.Relationships[Id] = RelationshipType.Parent;
        child.Relationships[partner.Id] = RelationshipType.Parent;

        // Set sibling relationships with existing children
        foreach (var kvp in Relationships)
        {
            if (kvp.Value == RelationshipType.Child && kvp.Key != child.Id)
            {
                child.Relationships[kvp.Key] = RelationshipType.Sibling;
                // Note: the sibling agent object isn't available here, they'll get it via partner's relationships
            }
        }
        foreach (var kvp in partner.Relationships)
        {
            if (kvp.Value == RelationshipType.Child && kvp.Key != child.Id)
            {
                child.Relationships[kvp.Key] = RelationshipType.Sibling;
            }
        }

        // Safety net: children also inherit union of both parents' knowledge
        foreach (var k in Knowledge) child.Knowledge.Add(k);
        foreach (var k in partner.Knowledge) child.Knowledge.Add(k);

        CurrentAction = ActionType.Reproduce;
        partner.CurrentAction = ActionType.Reproduce;

        return child;
    }

    // ── Knowledge ──────────────────────────────────────────────────────

    /// <summary>Adds a discovery to this agent's knowledge.</summary>
    public void LearnDiscovery(string discoveryId)
    {
        Knowledge.Add(discoveryId);
    }

    /// <summary>Records a failed recipe attempt, incrementing familiarity (capped).</summary>
    public void RecordFailedAttempt(string recipeId)
    {
        if (!Familiarity.ContainsKey(recipeId))
            Familiarity[recipeId] = 0;

        if (Familiarity[recipeId] < SimConfig.FamiliarityMaxFails)
            Familiarity[recipeId]++;
    }

    /// <summary>Gets the familiarity bonus for a recipe (0.0 to FamiliarityBonusCap).</summary>
    public float GetFamiliarityBonus(string recipeId)
    {
        if (!Familiarity.TryGetValue(recipeId, out int fails))
            return 0f;

        return Math.Min(fails * SimConfig.FamiliarityBonusPerFail, SimConfig.FamiliarityBonusCap);
    }

    // ── Perception & Memory ─────────────────────────────────────────────

    /// <summary>
    /// GDD v1.6.2: Tiered perception system.
    /// - Immediate scan (radius 2): every tick, always runs.
    /// - Active scan (full PerceptionRadius=8): fires on cadence, emergency, or force flag.
    ///   Cadence: every 6 ticks when idle, every 12 ticks when busy.
    ///   Emergency: hunger ≤ 20 or health ≤ 30.
    ///   Force: ForceActivePerception flag (set by AgentAI on action completion/interrupt).
    /// ~75% reduction in perception tile lookups at pop 20+.
    /// </summary>
    public void Perceive(World world, int currentTick)
    {
        if (!IsAlive) return;

        // Always: immediate scan (small radius, every tick)
        ScanRadius(world, currentTick, SimConfig.PerceptionImmediateRadius);

        // Determine if active scan should fire
        bool doActiveScan = false;

        // Emergency: critical needs override cadence (matches AI interrupt threshold)
        if (Hunger <= SimConfig.InterruptHungerThreshold || Health <= 30)
            doActiveScan = true;

        // Force flag: set by AgentAI after action completion or interrupt
        if (ForceActivePerception)
        {
            doActiveScan = true;
            ForceActivePerception = false;
        }

        // Cadence: idle agents scan more frequently than busy ones
        if (!doActiveScan)
        {
            int interval = IsBusy
                ? SimConfig.PerceptionActiveBusyInterval
                : SimConfig.PerceptionActiveIdleInterval;

            if (currentTick - LastActivePerceptionTick >= interval)
                doActiveScan = true;
        }

        if (doActiveScan)
        {
            // D25d: Dog companion extends active perception radius
            int activeRadius = SimConfig.PerceptionRadius;
            if (HasDogCompanion(world.Animals))
                activeRadius += SimConfig.DogPerceptionBonus;
            ScanRadius(world, currentTick, activeRadius);
            LastActivePerceptionTick = currentTick;
        }
    }

    /// <summary>Scans all tiles within the given Chebyshev radius and updates memory.
    /// GDD v1.8 Section 5: Also detects point features and resource patches for geographic lore.</summary>
    private void ScanRadius(World world, int currentTick, int radius)
    {
        for (int dx = -radius; dx <= radius; dx++)
        {
            for (int dy = -radius; dy <= radius; dy++)
            {
                int tx = X + dx;
                int ty = Y + dy;
                if (!world.IsInBounds(tx, ty)) continue;

                var tile = world.GetTile(tx, ty);

                // D24 Fix 2B: Don't memorize resources on impassable tiles (e.g., fish on water)
                bool tilePassable = !float.IsPositiveInfinity(tile.MovementCostMultiplier);

                // Record resources
                if (tilePassable)
                {
                    foreach (var kvp in tile.Resources)
                    {
                        if (kvp.Value <= 0) continue;
                        UpdateOrAddMemory(tx, ty, MemoryType.Resource, currentTick,
                            resource: kvp.Key, quantity: kvp.Value);
                    }
                }

                // Record structures
                foreach (var structure in tile.Structures)
                {
                    UpdateOrAddMemory(tx, ty, MemoryType.Structure, currentTick,
                        structureId: structure);
                    // GDD v1.7.2: Permanent landmark memory — never decays
                    KnownStructures.Add((tx, ty, structure));
                }

                // Record agents
                var agents = world.GetAgentsAt(tx, ty);
                foreach (var other in agents)
                {
                    if (other.Id == Id || !other.IsAlive) continue;
                    UpdateOrAddMemory(tx, ty, MemoryType.AgentSighting, currentTick,
                        agentId: other.Id);
                }

                // D25b: Record animal sightings into separate AnimalMemory (isolated from main Memory)
                if (tilePassable)
                {
                    var animals = world.GetAnimalsAt(tx, ty);
                    foreach (var animal in animals)
                    {
                        if (!animal.IsAlive) continue;
                        if (animal.State == AnimalState.Fleeing) continue;
                        UpdateOrAddAnimalMemory(tx, ty, MemoryType.AnimalSighting, currentTick,
                            animalId: animal.Id, animalSpecies: animal.Species);
                    }

                    // D25b: Record carcass sightings
                    foreach (var carcass in world.Carcasses)
                    {
                        if (!carcass.IsActive) continue;
                        if (carcass.X != tx || carcass.Y != ty) continue;
                        UpdateOrAddAnimalMemory(tx, ty, MemoryType.CarcassSighting, currentTick,
                            animalId: carcass.Id, animalSpecies: carcass.Species,
                            quantity: carcass.MeatYield);
                    }
                }

                // GDD v1.8 Section 5: Detect notable geographic features for permanent lore
                // Point features: single-tile rare features (caves, ore veins, springs, quarries)
                if (tile.IsPointFeature && tile.PointFeatureType != null
                    && !_knownGeographicPositions.Contains((tx, ty)))
                {
                    _knownGeographicPositions.Add((tx, ty));

                    // Determine the resource type for this point feature
                    ResourceType? featureResource = tile.PointFeatureType switch
                    {
                        "ore_vein" => ResourceType.Ore,
                        "rich_quarry" => ResourceType.Stone,
                        _ => null // caves and springs are landmarks without a specific resource
                    };

                    PendingGeographicDiscoveries.Add((tx, ty, tile.PointFeatureType, featureResource));
                }

                // Resource patches: clusters of 3-7 tiles with concentrated resources
                // Only record the first tile seen per patch (patches share PatchId)
                if (tile.IsResourcePatch && tile.PatchId != null
                    && !_knownGeographicPositions.Contains((tx, ty)))
                {
                    // Deduplicate by proximity: skip if we already know a position within 3 tiles
                    // (same patch tiles are clustered, so seeing one = knowing the patch area)
                    bool alreadyNearKnown = false;
                    foreach (var pending in PendingGeographicDiscoveries)
                    {
                        if (Math.Max(Math.Abs(pending.X - tx), Math.Abs(pending.Y - ty)) <= 3)
                        {
                            alreadyNearKnown = true;
                            break;
                        }
                    }

                    if (!alreadyNearKnown)
                    {
                        _knownGeographicPositions.Add((tx, ty));

                        // Determine feature type and resource from the patch's content
                        ResourceType? patchResource = null;
                        string featureType = "resource_patch";

                        // Infer from PatchId format: "{ResourceType}_{seq}" e.g. "Berries_3"
                        if (tile.PatchId.StartsWith("Berries")) { patchResource = ResourceType.Berries; featureType = "berry_patch"; }
                        else if (tile.PatchId.StartsWith("Animals")) { patchResource = ResourceType.Meat; featureType = "animal_herd"; }
                        else if (tile.PatchId.StartsWith("Fish")) { patchResource = ResourceType.Fish; featureType = "fish_school"; }
                        else if (tile.PatchId.StartsWith("Grain")) { patchResource = ResourceType.Grain; featureType = "grain_field"; }

                        PendingGeographicDiscoveries.Add((tx, ty, featureType, patchResource));
                    }
                }
            }
        }
    }

    /// <summary>Removes expired entries and enforces max memory cap.</summary>
    public void DecayMemory(int currentTick)
    {
        Memory.RemoveAll(m => currentTick - m.TickObserved > SimConfig.MemoryDecayTicks);

        // Enforce cap — keep most recent entries
        if (Memory.Count > SimConfig.MemoryMaxEntries)
        {
            Memory.Sort((a, b) => b.TickObserved.CompareTo(a.TickObserved));
            Memory.RemoveRange(SimConfig.MemoryMaxEntries, Memory.Count - SimConfig.MemoryMaxEntries);
        }

        // D25b: Decay animal memory separately
        DecayAnimalMemory(currentTick);
    }

    /// <summary>GDD v1.8 Section 5: Removes a specific resource memory when agent arrives and
    /// finds the resource is gone. "Acting on stale memory and finding nothing clears it immediately."</summary>
    public void ClearStaleMemory(int x, int y, ResourceType resource)
    {
        Memory.RemoveAll(m =>
            m.X == x && m.Y == y
            && m.Type == MemoryType.Resource
            && m.Resource == resource);
    }

    /// <summary>Clears all food resource memories. Used when food-seek is stuck on depleted areas.</summary>
    public void ClearAllFoodMemories()
    {
        Memory.RemoveAll(m =>
            m.Type == MemoryType.Resource
            && m.Resource.HasValue
            && (m.Resource.Value == ResourceType.Berries || m.Resource.Value == ResourceType.Grain
                || m.Resource.Value == ResourceType.Meat || m.Resource.Value == ResourceType.Fish));
    }

    /// <summary>GDD v1.8 Section 5: Marks a geographic position as already known (in pending list
    /// or settlement lore). Called by SettlementKnowledge when initializing agent awareness.</summary>
    public void MarkGeographicPositionKnown(int x, int y)
    {
        _knownGeographicPositions.Add((x, y));
    }

    /// <summary>GDD v1.8 Section 5: Clears pending geographic discoveries after transferring
    /// them to settlement lore. Called when agent returns home.</summary>
    public void ClearPendingGeographicDiscoveries()
    {
        PendingGeographicDiscoveries.Clear();
    }

    /// <summary>Returns remembered food resource entries (not expired).</summary>
    public List<MemoryEntry> GetRememberedFood(int currentTick)
    {
        ResourceType[] foodTypes = { ResourceType.Berries, ResourceType.Grain, ResourceType.Meat, ResourceType.Fish };
        return Memory.Where(m =>
            m.Type == MemoryType.Resource
            && m.Resource.HasValue
            && foodTypes.Contains(m.Resource.Value)
            && currentTick - m.TickObserved <= SimConfig.MemoryDecayTicks
        ).ToList();
    }

    /// <summary>Returns remembered agent sighting entries (not expired).</summary>
    public List<MemoryEntry> GetRememberedAgents(int currentTick)
    {
        return Memory.Where(m =>
            m.Type == MemoryType.AgentSighting
            && currentTick - m.TickObserved <= SimConfig.MemoryDecayTicks
        ).ToList();
    }

    /// <summary>Returns remembered shelter/structure entries (not expired).</summary>
    public List<MemoryEntry> GetRememberedShelters(int currentTick)
    {
        return Memory.Where(m =>
            m.Type == MemoryType.Structure
            && (m.StructureId == "lean_to" || m.StructureId == "shelter" || m.StructureId == "improved_shelter")
            && currentTick - m.TickObserved <= SimConfig.MemoryDecayTicks
        ).ToList();
    }

    /// <summary>D25b/D25c: Returns remembered huntable animal sightings (not expired).
    /// D25c: Includes Boar (if agent has spear) and Wolf (if agent has bow).</summary>
    public List<MemoryEntry> GetRememberedHuntableAnimals(int currentTick)
    {
        bool hasSpear = Knowledge.Contains("spear");
        bool hasBow = Knowledge.Contains("bow");
        return AnimalMemory.Where(m =>
            m.Type == MemoryType.AnimalSighting
            && m.AnimalSpecies.HasValue
            && currentTick - m.TickObserved <= SimConfig.MemoryDecayTicks
            && (m.AnimalSpecies.Value != AnimalSpecies.Boar || hasSpear)
            && (m.AnimalSpecies.Value != AnimalSpecies.Wolf || hasBow)
        ).ToList();
    }

    /// <summary>D25b: Returns remembered carcass sightings (not expired).</summary>
    public List<MemoryEntry> GetRememberedCarcasses(int currentTick)
    {
        return AnimalMemory.Where(m =>
            m.Type == MemoryType.CarcassSighting
            && currentTick - m.TickObserved <= SimConfig.MemoryDecayTicks
        ).ToList();
    }

    private void UpdateOrAddMemory(int x, int y, MemoryType type, int tick,
        ResourceType? resource = null, int quantity = 0,
        int? agentId = null, string? structureId = null,
        int? animalId = null, AnimalSpecies? animalSpecies = null)
    {
        // Try to update existing entry at same position with same type/subtype
        var existing = Memory.FirstOrDefault(m =>
            m.X == x && m.Y == y && m.Type == type
            && m.Resource == resource
            && m.AgentId == agentId
            && m.StructureId == structureId);

        if (existing != null)
        {
            existing.TickObserved = tick;
            existing.Quantity = quantity;
        }
        else
        {
            Memory.Add(new MemoryEntry
            {
                X = x, Y = y,
                Type = type,
                Resource = resource,
                Quantity = quantity,
                AgentId = agentId,
                StructureId = structureId,
                TickObserved = tick
            });
        }
    }

    /// <summary>D25b: Update or add to AnimalMemory (separate from main Memory to prevent RNG cascade).</summary>
    private void UpdateOrAddAnimalMemory(int x, int y, MemoryType type, int tick,
        int? animalId = null, AnimalSpecies? animalSpecies = null, int quantity = 0)
    {
        var existing = AnimalMemory.FirstOrDefault(m =>
            m.X == x && m.Y == y && m.Type == type && m.AnimalId == animalId);

        if (existing != null)
        {
            existing.TickObserved = tick;
            existing.Quantity = quantity;
        }
        else
        {
            AnimalMemory.Add(new MemoryEntry
            {
                X = x, Y = y,
                Type = type,
                AnimalId = animalId,
                AnimalSpecies = animalSpecies,
                Quantity = quantity,
                TickObserved = tick
            });
        }
    }

    /// <summary>D25b: Decay animal memory separately from main memory.</summary>
    public void DecayAnimalMemory(int currentTick)
    {
        AnimalMemory.RemoveAll(m => currentTick - m.TickObserved > SimConfig.MemoryDecayTicks);
        if (AnimalMemory.Count > 20)
        {
            AnimalMemory.Sort((a, b) => b.TickObserved.CompareTo(a.TickObserved));
            AnimalMemory.RemoveRange(20, AnimalMemory.Count - 20);
        }
    }

    // ── GDD v1.8 Section 3: Stability Score History Methods ──────────

    /// <summary>Section 3: Samples current food availability and health into 7-day ring buffer.
    /// Called once per sim-day from Simulation.Tick().</summary>
    public void SampleHistory(int currentTick, World world)
    {
        // Total available food: inventory + home storage
        float totalFood = FoodInInventory();
        if (HomeTile.HasValue && world.IsInBounds(HomeTile.Value.X, HomeTile.Value.Y))
        {
            var homeTile = world.GetTile(HomeTile.Value.X, HomeTile.Value.Y);
            if (homeTile.HasHomeStorage)
                totalFood += homeTile.HomeTotalFood;
        }

        _foodHistory[_historyIndex] = totalFood;
        _healthHistory[_historyIndex] = Health;
        _historyIndex = (_historyIndex + 1) % 7;
        if (_historySampleCount < 7) _historySampleCount++;
        LastHistorySampleTick = currentTick;
    }

    /// <summary>Section 3: Returns food trend as 0.0 (depleting) to 1.0 (growing).
    /// Compares recent 3 samples to earlier 3 samples. Returns 0.5 (neutral) if insufficient data.</summary>
    public float GetFoodTrend()
    {
        if (_historySampleCount < 4) return 0.5f; // Neutral — not enough data

        // Compute average of recent 3 vs earlier samples
        float recentSum = 0f, earlierSum = 0f;
        int recentCount = 0, earlierCount = 0;

        for (int i = 0; i < _historySampleCount && i < 7; i++)
        {
            // Ring buffer: index goes _historyIndex-1, -2, ... (most recent first)
            int idx = ((_historyIndex - 1 - i) + 7) % 7;
            if (i < 3)
            {
                recentSum += _foodHistory[idx];
                recentCount++;
            }
            else
            {
                earlierSum += _foodHistory[idx];
                earlierCount++;
            }
        }

        if (recentCount == 0 || earlierCount == 0) return 0.5f;

        float recentAvg = recentSum / recentCount;
        float earlierAvg = earlierSum / earlierCount;

        // Normalize: difference mapped to 0-1 range. +10 food = 1.0, -10 food = 0.0
        float diff = recentAvg - earlierAvg;
        return Math.Clamp(0.5f + diff / 20f, 0f, 1f);
    }

    /// <summary>Section 3: Returns health trend as 0.0 (declining) to 1.0 (improving).
    /// Compares recent 3 samples to earlier 3 samples. Returns 0.5 (neutral) if insufficient data.</summary>
    public float GetHealthTrend()
    {
        if (_historySampleCount < 4) return 0.5f;

        float recentSum = 0f, earlierSum = 0f;
        int recentCount = 0, earlierCount = 0;

        for (int i = 0; i < _historySampleCount && i < 7; i++)
        {
            int idx = ((_historyIndex - 1 - i) + 7) % 7;
            if (i < 3)
            {
                recentSum += _healthHistory[idx];
                recentCount++;
            }
            else
            {
                earlierSum += _healthHistory[idx];
                earlierCount++;
            }
        }

        if (recentCount == 0 || earlierCount == 0) return 0.5f;

        float recentAvg = recentSum / recentCount;
        float earlierAvg = earlierSum / earlierCount;

        // Normalize: +30 HP trend = 1.0, -30 HP trend = 0.0
        float diff = recentAvg - earlierAvg;
        return Math.Clamp(0.5f + diff / 60f, 0f, 1f);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    /// <summary>GDD v1.8 Section 7: Decays one food item from inventory (non-preserved first).
    /// Creates mild pressure to return home and store food in shelter.</summary>
    private void DecayOneInventoryFood()
    {
        // Decay non-preserved food first (berries rot faster than preserved food)
        ResourceType[] decayOrder = { ResourceType.Berries, ResourceType.Fish, ResourceType.Meat, ResourceType.Grain, ResourceType.PreservedFood };
        foreach (var food in decayOrder)
        {
            if (Inventory.TryGetValue(food, out int amt) && amt > 0)
            {
                Inventory[food]--;
                if (Inventory[food] <= 0)
                    Inventory.Remove(food);
                return;
            }
        }
    }

    /// <summary>GDD v1.8 Section 3: Consumes food from home storage first, then inventory.
    /// Used for reproduction food cost so agents draw from their pantry before personal stock.</summary>
    private void ConsumeFoodFromHomeFirst(int amount, World world)
    {
        int remaining = amount;

        // Try home storage first
        if (HomeTile.HasValue && world.IsInBounds(HomeTile.Value.X, HomeTile.Value.Y))
        {
            var homeTile = world.GetTile(HomeTile.Value.X, HomeTile.Value.Y);
            if (homeTile.HasHomeStorage && homeTile.HomeTotalFood > 0)
            {
                int toTake = Math.Min(remaining, homeTile.HomeTotalFood);
                int taken = 0;
                while (taken < toTake)
                {
                    var (_, withdrew) = homeTile.WithdrawAnyFoodFromHome(1);
                    if (withdrew <= 0) break;
                    taken += withdrew;
                }
                remaining -= taken;
            }
        }

        // Then from inventory
        if (remaining > 0)
            ConsumeFood(remaining);
    }

    /// <summary>Consumes the specified amount of food from inventory (any food type, including preserved).</summary>
    private void ConsumeFood(int amount)
    {
        ResourceType[] foodTypes = { ResourceType.Berries, ResourceType.Grain, ResourceType.Meat, ResourceType.Fish, ResourceType.PreservedFood };

        int remaining = amount;
        foreach (var food in foodTypes)
        {
            if (remaining <= 0) break;
            if (Inventory.TryGetValue(food, out int available) && available > 0)
            {
                int toConsume = Math.Min(available, remaining);
                Inventory[food] -= toConsume;
                if (Inventory[food] <= 0)
                    Inventory.Remove(food);
                remaining -= toConsume;
            }
        }
    }

    /// <summary>Formats age as human-readable time string.</summary>
    public string FormatAge()
    {
        return FormatTicks(Age);
    }

    /// <summary>GDD v1.8: Formats ticks as human-readable time string.
    /// The tick is invisible to the observer. Display uses sim-days, seasons, and years.
    /// 1 sim-day = TicksPerSimDay ticks. 7 sim-days = 1 season. 28 sim-days = 1 year.</summary>
    public static string FormatTicks(int ticks)
    {
        int simDays = ticks / SimConfig.TicksPerSimDay;
        if (simDays < SimConfig.SimDaysPerSeason)
            return $"{simDays}d";
        if (simDays < SimConfig.SimDaysPerYear)
        {
            int seasons = simDays / SimConfig.SimDaysPerSeason;
            int remDays = simDays % SimConfig.SimDaysPerSeason;
            return remDays > 0 ? $"{seasons}sn {remDays}d" : $"{seasons}sn";
        }
        int years = simDays / SimConfig.SimDaysPerYear;
        int remSimDays = simDays % SimConfig.SimDaysPerYear;
        int remSeasons = remSimDays / SimConfig.SimDaysPerSeason;
        if (remSeasons > 0)
            return $"{years}y {remSeasons}sn";
        return $"{years}y";
    }

    public override string ToString()
    {
        return $"Agent {Id} ({Name}) at ({X},{Y}) - Hunger: {Hunger:F0}, Health: {Health}, Alive: {IsAlive}";
    }
}
