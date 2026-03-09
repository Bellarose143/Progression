using CivSim.Core.Events;

namespace CivSim.Core;

/// <summary>
/// Utility-based AI system for agents (GDD v1.8).
/// Each tick, if the agent is busy with a multi-tick action, it continues unless interrupted.
/// Otherwise the highest-applicable priority fires and starts a new action.
/// All decisions use perception + memory, not raw world scans.
/// GDD v1.6.2: Uses EventBus for typed event dispatch. Sets ForceActivePerception on action completion.
/// GDD v1.7: Priority reorder — Build moved to P8, Granary at P1/P4/P5, Cook action, weighted explore.
/// GDD v1.8: stone_knife/crude_axe migration, RecipeId on discovery events, gendered name support.
/// </summary>
public class AgentAI
{
    private readonly Random random;

    /// <summary>GDD v1.8: Callback invoked when an agent discovers something.
    /// Simulation sets this to route discoveries to the communal knowledge system.</summary>
    public Action<Agent, string>? OnDiscoveryCallback { get; set; }

    /// <summary>GDD v1.8 Testing Infrastructure: Optional run logger.
    /// Set from outside (Simulation.RunLogger) to capture decisions and completions.</summary>
    public RunLogger? Logger { get; set; }

    /// <summary>Convenience wrapper — logs a decision to the RunLogger if one is attached.</summary>
    private void LogDecision(int tick, Agent agent, string action, string target,
        float score, string runnerUp = "", float runnerUpScore = 0f)
        => Logger?.LogDecision(tick, agent, action, target, score, runnerUp, runnerUpScore);

    // GDD v1.8: Transient references set during DecideAction for reproduction dispatch
    private SettlementKnowledge? _currentKnowledgeSystem;
    private List<Settlement>? _currentSettlements;
    private List<Agent>? _currentAllAgents;

    // Recursion guard: prevents DecideHome→DecideForage→DecideHome cycles within one tick
    private bool _inModeDispatch;

    // Temporary diagnostic counters for goal commitment debugging
    internal static int _dbgPhase3Entered;
    internal static int _dbgPhase3GatherAdvanced;
    internal static int _dbgPhase3GatherFailed;
    internal static int _dbgPhase4Ran;
    internal static int _dbgPhase4WithActiveGatherGoal;
    internal static int _dbgPhase3SkippedModeChange;
    internal static int _dbgPhase3SkippedForceReeval;
    internal static int _dbgPhase3SkippedBusy;
    internal static int _dbgPhase3SkippedNoGoal;
    // TryAdvanceGoal failure reasons for gather goals
    internal static int _dbgAdvFail_NoGoalTarget;
    internal static int _dbgAdvFail_HomeCaretakerLeash;
    internal static int _dbgAdvFail_DistanceLeash;
    internal static int _dbgAdvFail_HungerInterrupt;
    internal static int _dbgAdvFail_StaleGoal;
    internal static int _dbgAdvFail_MoveChainAbandon;
    internal static int _dbgAdvFail_MoveChainLeash;
    internal static int _dbgAdvFail_WaypointBlocked;
    internal static int _dbgAdvFail_MoveEscFailed;
    internal static int _dbgAdvFail_ExecAtTarget;
    internal static int _dbgRedirectSuccess;
    internal static int _dbgRedirectFailed;
    internal static int _dbgExecAtTargetRedirectAttempted;

    public static void PrintGoalDiagnostics()
    {
        Console.WriteLine($"\n  ── Goal Commitment Debug Counters ──");
        Console.WriteLine($"    Phase3 entered (gather goals): {_dbgPhase3Entered}");
        Console.WriteLine($"    Phase3 advanced (succeed):     {_dbgPhase3GatherAdvanced}");
        Console.WriteLine($"    Phase3 failed (fall-through):  {_dbgPhase3GatherFailed}");
        Console.WriteLine($"    Phase3 SKIPPED (mode change):  {_dbgPhase3SkippedModeChange}");
        Console.WriteLine($"    Phase3 SKIPPED (force reeval): {_dbgPhase3SkippedForceReeval}");
        Console.WriteLine($"    Phase3 SKIPPED (busy):         {_dbgPhase3SkippedBusy}");
        Console.WriteLine($"    Phase3 SKIPPED (no goal):      {_dbgPhase3SkippedNoGoal}");
        Console.WriteLine($"    Phase4 ran (total):            {_dbgPhase4Ran}");
        Console.WriteLine($"    Phase4 with active gather:     {_dbgPhase4WithActiveGatherGoal}");
        Console.WriteLine($"  ── TryAdvanceGoal Failure Reasons (gather) ──");
        Console.WriteLine($"    No goal/target:     {_dbgAdvFail_NoGoalTarget}");
        Console.WriteLine($"    Home/Caretaker leash: {_dbgAdvFail_HomeCaretakerLeash}");
        Console.WriteLine($"    Distance leash:     {_dbgAdvFail_DistanceLeash}");
        Console.WriteLine($"    Hunger interrupt:   {_dbgAdvFail_HungerInterrupt}");
        Console.WriteLine($"    Stale goal (60t):   {_dbgAdvFail_StaleGoal}");
        Console.WriteLine($"    Move chain abandon: {_dbgAdvFail_MoveChainAbandon}");
        Console.WriteLine($"    Move chain leash:   {_dbgAdvFail_MoveChainLeash}");
        Console.WriteLine($"    Waypoint blocked:   {_dbgAdvFail_WaypointBlocked}");
        Console.WriteLine($"    Move escalation fail: {_dbgAdvFail_MoveEscFailed}");
        Console.WriteLine($"    Exec at target fail:  {_dbgAdvFail_ExecAtTarget}");
        Console.WriteLine($"  ── Redirect Counters ──");
        Console.WriteLine($"    Redirect success:     {_dbgRedirectSuccess}");
        Console.WriteLine($"    Redirect failed:      {_dbgRedirectFailed}");
        Console.WriteLine($"    Exec redirect tried:  {_dbgExecAtTargetRedirectAttempted}");
    }

    public AgentAI(Random random)
    {
        this.random = random;
    }

    /// <summary>D19: Update agent restlessness based on current action and state.</summary>
    public static void UpdateRestlessness(Agent agent, int currentTick, World? world = null)
    {
        // Infants don't accumulate restlessness (D19: youth stage onset = when accumulation begins)
        if (agent.Stage == DevelopmentStage.Infant) return;

        bool isYouth = agent.Stage != DevelopmentStage.Adult;
        float gainRate = isYouth ? SimConfig.RestlessnessYouthGainRate : SimConfig.RestlessnessGainRate;

        // D25d: Dog companion reduces restlessness gain
        if (world != null && agent.HasDogCompanion(world.Animals))
            gainRate *= (1f - SimConfig.DogRestlessnessReduction);

        var action = agent.CurrentAction;

        // Productive actions DRAIN restlessness
        float drain = action switch
        {
            ActionType.Experiment => SimConfig.RestlessnessExperimentDrain,
            ActionType.Build => SimConfig.RestlessnessBuildDrain,
            ActionType.TendFarm => SimConfig.RestlessnessTendFarmDrain,
            ActionType.Gather => SimConfig.RestlessnessGatherDrain,
            ActionType.Cook => SimConfig.RestlessnessCookDrain,
            ActionType.Preserve => SimConfig.RestlessnessPreserveDrain,
            ActionType.Explore => SimConfig.RestlessnessExploreDrain,
            _ => 0f
        };

        if (drain > 0f)
        {
            agent.Restlessness = Math.Max(0f, agent.Restlessness - drain);
            return;
        }

        // Comfort-idle ticks GAIN restlessness
        // Nighttime rest doesn't count
        bool isNight = Agent.IsNightTime(currentTick);
        if ((action == ActionType.Rest || action == ActionType.GrowingUp) && isNight) return;

        // Only accumulate during idle-type actions (including Socialize as comfort-idle)
        bool isComfortIdle = action == ActionType.Idle
            || action == ActionType.Rest  // daytime rest
            || action == ActionType.GrowingUp // child/youth inactivity
            || action == ActionType.Move  // non-goal transit
            || action == ActionType.Socialize; // social comfort

        if (isComfortIdle)
        {
            agent.Restlessness = Math.Min(100f, agent.Restlessness + gainRate);
            if (agent.Restlessness >= 100f)
            {
                // D19 diagnostic: agent is maximally restless — might indicate scoring problem
                System.Diagnostics.Debug.WriteLine($"[D19] RESTLESSNESS_CAPPED: {agent.Name} at tick {currentTick}, action={action}");
            }
        }
    }

    /// <summary>
    /// Decides what action the agent should take this tick.
    /// Multi-tick actions: if the agent is busy, tick down and optionally complete.
    /// Critical interrupts can break multi-tick actions.
    ///
    /// GDD v1.8 Priority levels (highest first):
    ///  P1:    Eat/Cook/Withdraw if hunger &lt;= 30 and has food or granary nearby
    ///  P1.5:  Feed nearby hungry child (instant, if adult has food + child hunger &lt; 40)
    ///  P2:    Seek food if hunger &lt;= 30 and no food
    ///  P3:    Rest if health &lt;= 30 and hunger &gt; 30
    ///  P4-P12: Utility scoring (TendFarm, Build, Reproduce, Experiment, Explore, etc.)
    ///  GDD v1.8: Teach REMOVED — knowledge propagation is communal within settlements.
    /// </summary>
    public void DecideAction(Agent agent, World world, EventBus bus, int currentTick, List<Agent>? allAgents = null,
        SettlementKnowledge? knowledgeSystem = null, List<Settlement>? settlements = null, Action<string>? trace = null)
    {
        if (!agent.IsAlive) return;

        // Store transient refs for reproduction dispatch and name generation
        _currentKnowledgeSystem = knowledgeSystem;
        _currentSettlements = settlements;
        _currentAllAgents = allAgents;

        var currentTile = world.GetTile(agent.X, agent.Y);
        bool isChild = agent.Stage != DevelopmentStage.Adult;

        // ── Multi-tick continuation ──────────────────────────────────
        if (agent.IsBusy)
        {
            // Directive #8: Caretaker exit check during multi-tick actions
            // Without this, a Caretaker in a multi-tick action never reaches EvaluateTransitions
            // and stays locked in Caretaker mode even after the child ages out.
            if (agent.CurrentMode == BehaviorMode.Caretaker && allAgents != null)
            {
                bool stillHasDependents = false;
                foreach (var kvp in agent.Relationships)
                {
                    if (kvp.Value != RelationshipType.Child) continue;
                    var child = allAgents.FirstOrDefault(a => a.Id == kvp.Key);
                    if (child != null && child.IsAlive && child.Age < SimConfig.CaretakerExitChildAge)
                    { stillHasDependents = true; break; }
                }
                if (!stillHasDependents)
                {
                    agent.ClearPendingAction();
                    agent.ClearGoal();
                    // Fall through to EvaluateTransitions which will exit Caretaker
                }
            }
        }

        // D25c: Check for involuntary combat — agent wandered near a dangerous animal
        if (!agent.IsInCombat && agent.Stage == DevelopmentStage.Adult)
        {
            CheckInvoluntaryCombat(agent, world, currentTick, trace);
        }

        // D25c: Combat tick-by-tick processing — mutually exclusive with all other actions
        if (agent.IsInCombat)
        {
            var combatAnimal = world.Animals.FirstOrDefault(a => a.Id == agent.CombatTargetAnimalId!.Value);

            // Combat target dead or gone
            if (combatAnimal == null || !combatAnimal.IsAlive)
            {
                trace?.Invoke($"[TRACE Agent {agent.Id}] COMBAT: target dead/gone, exiting combat");
                agent.CombatTargetAnimalId = null;
                agent.CombatTicksRemaining = 0;
                agent.ClearPendingAction();
                agent.CurrentAction = ActionType.Idle;
            }
            else
            {
                agent.CombatTicksRemaining--;
                agent.CurrentAction = ActionType.Combat;

                // Calculate distances
                int distToAnimal = Math.Max(Math.Abs(agent.X - combatAnimal.X), Math.Abs(agent.Y - combatAnimal.Y));
                bool hasSpear = agent.Knowledge.Contains("spear");
                bool hasBow = agent.Knowledge.Contains("bow");

                if (hasBow && distToAnimal >= 2 && distToAnimal <= SimConfig.BowRangedRange)
                {
                    // Bow ranged phase
                    combatAnimal.Health -= SimConfig.BowRangedDamage;
                    trace?.Invoke($"[TRACE Agent {agent.Id}] COMBAT: bow ranged hit for {SimConfig.BowRangedDamage} (animal HP: {combatAnimal.Health})");
                }
                else if (hasSpear && distToAnimal == SimConfig.SpearRangedRange)
                {
                    // Spear ranged phase
                    combatAnimal.Health -= SimConfig.SpearRangedDamage;
                    trace?.Invoke($"[TRACE Agent {agent.Id}] COMBAT: spear ranged hit for {SimConfig.SpearRangedDamage} (animal HP: {combatAnimal.Health})");
                }
                else if (distToAnimal <= 1)
                {
                    // Melee phase - both deal damage
                    int agentDamage = GetAgentMeleeDamage(agent);
                    combatAnimal.Health -= agentDamage;

                    // Animal damages agent
                    int animalDamage = GetAnimalDamage(combatAnimal, world);
                    agent.Health = Math.Max(0, agent.Health - animalDamage);

                    trace?.Invoke($"[TRACE Agent {agent.Id}] COMBAT: melee exchange — dealt {agentDamage}, took {animalDamage} (HP: {agent.Health}, animal HP: {combatAnimal.Health})");
                }

                // Check animal death
                if (combatAnimal.Health <= 0)
                {
                    combatAnimal.Die();
                    var animalConfig = Animal.SpeciesConfig[combatAnimal.Species];
                    var carcass = new Carcass(combatAnimal.X, combatAnimal.Y, combatAnimal.Species,
                        animalConfig.MeatYield, animalConfig.HideYield, animalConfig.BoneYield);
                    world.Carcasses.Add(carcass);

                    agent.RecordAction(ActionType.Combat, currentTick, $"Killed {combatAnimal.Species} in combat");
                    bus.Emit(currentTick,
                        $"{agent.Name} killed a {combatAnimal.Species} in combat at ({combatAnimal.X},{combatAnimal.Y})",
                        EventType.Action, agentId: agent.Id);

                    agent.CombatTargetAnimalId = null;
                    agent.CombatTicksRemaining = 0;
                    agent.ClearPendingAction();
                    agent.CurrentAction = ActionType.Idle;
                    ClearAnimalAggroTags(combatAnimal, world);
                    trace?.Invoke($"[TRACE Agent {agent.Id}] COMBAT: victory over {combatAnimal.Species}");
                }
                // Check agent death
                else if (agent.Health <= 0)
                {
                    agent.RecordAction(ActionType.Combat, currentTick, $"Killed by {combatAnimal.Species}");
                    bus.Emit(currentTick,
                        $"{agent.Name} was killed by a {combatAnimal.Species} near ({agent.X},{agent.Y})",
                        EventType.Death, agentId: agent.Id);
                    agent.CombatTargetAnimalId = null;
                    agent.CombatTicksRemaining = 0;
                    ClearAnimalAggroTags(combatAnimal, world);
                }
                // Check disengage (low health)
                else if (agent.Health < SimConfig.DisengageHealthThreshold)
                {
                    float fleeChance = GetCombatFleeChance(combatAnimal, world);
                    if (random.NextDouble() < fleeChance)
                    {
                        trace?.Invoke($"[TRACE Agent {agent.Id}] COMBAT: disengaged from {combatAnimal.Species} at health {agent.Health}");
                        agent.RecordAction(ActionType.Combat, currentTick, $"Fled from {combatAnimal.Species} (health: {agent.Health})");
                        bus.Emit(currentTick,
                            $"{agent.Name} fled from a {combatAnimal.Species} (health: {agent.Health})",
                            EventType.Action, agentId: agent.Id);

                        // Move 2 tiles away from animal
                        int dx = agent.X - combatAnimal.X;
                        int dy = agent.Y - combatAnimal.Y;
                        dx = dx == 0 ? 1 : (dx > 0 ? 1 : -1);
                        dy = dy == 0 ? 0 : (dy > 0 ? 1 : -1);
                        int fleeX = Math.Clamp(agent.X + dx * SimConfig.DisengageFleeDistance, 0, world.Width - 1);
                        int fleeY = Math.Clamp(agent.Y + dy * SimConfig.DisengageFleeDistance, 0, world.Height - 1);
                        agent.X = fleeX;
                        agent.Y = fleeY;

                        agent.CombatTargetAnimalId = null;
                        agent.CombatTicksRemaining = 0;
                        agent.ClearPendingAction();
                        agent.CurrentAction = ActionType.Idle;
                        agent.ClearGoal();
                        ClearAnimalAggroTags(combatAnimal, world);
                    }
                    else
                    {
                        trace?.Invoke($"[TRACE Agent {agent.Id}] COMBAT: failed to disengage from {combatAnimal.Species}");
                    }
                }
                // Safety net: if combat duration expired, force disengage
                else if (agent.CombatTicksRemaining <= 0)
                {
                    trace?.Invoke($"[TRACE Agent {agent.Id}] COMBAT: duration expired, force disengage");
                    agent.CombatTargetAnimalId = null;
                    agent.CombatTicksRemaining = 0;
                    agent.ClearPendingAction();
                    agent.CurrentAction = ActionType.Idle;
                    agent.ClearGoal();
                    ClearAnimalAggroTags(combatAnimal, world);
                }
            }
            return; // Combat is mutually exclusive with other actions
        }

        if (agent.IsBusy)
        {
            // Check for critical interrupts
            if (ShouldInterrupt(agent))
            {
                trace?.Invoke($"[TRACE Agent {agent.Id}] INTERRUPT: Cancelling {agent.PendingAction} (hunger={agent.Hunger:F0}, health={agent.Health}, food={agent.FoodInInventory()})");

                // Log the interrupted completion before clearing
                if (agent.PendingAction.HasValue)
                    Logger?.LogCompletion(currentTick, agent, agent.PendingAction.Value.ToString(),
                        "interrupted", "hunger critical", (int)agent.ActionProgress);

                // Build progress is preserved on the tile; all other actions lose progress
                agent.ClearPendingAction();
                agent.ClearGoal(); // Clear goal commitment on interrupt
                agent.ForceActivePerception = true; // GDD v1.6.2: force active scan after interrupt
                // Fall through to priority cascade
            }
            else
            {
                // D25b: Hunt pursuit — special tick-by-tick logic (skips normal progress increment)
                if (agent.PendingAction == ActionType.Hunt && agent.HuntTargetAnimalId.HasValue)
                {
                    var targetAnimal = world.Animals.FirstOrDefault(a => a.Id == agent.HuntTargetAnimalId.Value);

                    // Abandon hunt: target died or doesn't exist
                    if (targetAnimal == null || !targetAnimal.IsAlive)
                    {
                        trace?.Invoke($"[TRACE Agent {agent.Id}] HUNT: target animal gone, abandoning");
                        agent.ClearPendingAction();
                        agent.CurrentAction = ActionType.Idle;
                        agent.ClearGoal();
                        agent.HuntTargetAnimalId = null;
                        // Fall through to decision cascade
                    }
                    // Abandon hunt: agent health too low
                    else if (agent.Health < 30)
                    {
                        trace?.Invoke($"[TRACE Agent {agent.Id}] HUNT: health too low ({agent.Health}), abandoning");
                        agent.RecordAction(ActionType.Hunt, currentTick, $"Abandoned hunt — health {agent.Health}");
                        agent.ClearPendingAction();
                        agent.CurrentAction = ActionType.Idle;
                        agent.ClearGoal();
                        agent.HuntTargetAnimalId = null;
                    }
                    // Abandon hunt: too far from home
                    else if (agent.HomeTile.HasValue &&
                        Math.Max(Math.Abs(agent.X - agent.HomeTile.Value.X),
                                 Math.Abs(agent.Y - agent.HomeTile.Value.Y)) > SimConfig.HuntMaxDistanceFromHome)
                    {
                        trace?.Invoke($"[TRACE Agent {agent.Id}] HUNT: too far from home, abandoning");
                        agent.RecordAction(ActionType.Hunt, currentTick, "Abandoned hunt — too far from home");
                        agent.ClearPendingAction();
                        agent.CurrentAction = ActionType.Idle;
                        agent.ClearGoal();
                        agent.HuntTargetAnimalId = null;
                    }
                    else
                    {
                        // Get max pursuit ticks for species
                        int maxPursuit = targetAnimal.Species switch
                        {
                            AnimalSpecies.Rabbit => 4,
                            AnimalSpecies.Deer => 8,
                            AnimalSpecies.Cow => 6,
                            AnimalSpecies.Sheep => 5,
                            AnimalSpecies.Boar => 6,
                            AnimalSpecies.Wolf => 8,
                            _ => 4
                        };

                        agent.HuntPursuitTicks++;
                        agent.CurrentAction = ActionType.Hunt;

                        // Check if adjacent to animal
                        int distToAnimal = Math.Max(Math.Abs(agent.X - targetAnimal.X), Math.Abs(agent.Y - targetAnimal.Y));

                        if (distToAnimal <= 1)
                        {
                            // D25c: Dangerous prey → combat instead of instant kill
                            if (targetAnimal.Species == AnimalSpecies.Boar || targetAnimal.Species == AnimalSpecies.Wolf)
                            {
                                agent.CombatTargetAnimalId = targetAnimal.Id;
                                agent.CombatTicksRemaining = 30;
                                agent.PendingAction = ActionType.Combat;
                                agent.CurrentAction = ActionType.Combat;
                                agent.HuntTargetAnimalId = null;
                                agent.HuntPursuitTicks = 0;
                                // Tag animal for combat tracking (don't change state — preserves RNG)
                                targetAnimal.AggressiveTargetAgentId = agent.Id;
                                trace?.Invoke($"[TRACE Agent {agent.Id}] HUNT→COMBAT: engaging {targetAnimal.Species} in combat");
                                return; // Don't process further — combat handles resolution
                            }

                            // Safe prey: existing hunt success roll
                            float successChance = GetHuntSuccessChance(agent, targetAnimal.Species, world);
                            if (random.NextDouble() < successChance)
                            {
                                // Kill!
                                targetAnimal.Health = 0;
                                targetAnimal.State = AnimalState.Dead;

                                var config = Animal.SpeciesConfig[targetAnimal.Species];
                                var carcass = new Carcass(targetAnimal.X, targetAnimal.Y, targetAnimal.Species, config.MeatYield, config.HideYield, config.BoneYield);
                                world.Carcasses.Add(carcass);

                                agent.RecordAction(ActionType.Hunt, currentTick, $"Killed a {targetAnimal.Species}");
                                bus.Emit(currentTick,
                                    $"{agent.Name} killed a {targetAnimal.Species} at ({targetAnimal.X},{targetAnimal.Y})",
                                    EventType.Action, agentId: agent.Id);

                                trace?.Invoke($"[TRACE Agent {agent.Id}] HUNT: killed {targetAnimal.Species} #{targetAnimal.Id}");
                                agent.ClearPendingAction();
                                agent.CurrentAction = ActionType.Idle;
                                agent.ClearGoal();
                                agent.HuntTargetAnimalId = null;
                            }
                            else
                            {
                                trace?.Invoke($"[TRACE Agent {agent.Id}] HUNT: missed {targetAnimal.Species} (chance={successChance:F2})");
                            }
                        }
                        else
                        {
                            // Chase: move toward animal
                            TryStartMove(agent, targetAnimal.X, targetAnimal.Y, world, trace);
                        }

                        // Check pursuit expired
                        if (agent.HuntTargetAnimalId.HasValue && agent.HuntPursuitTicks >= maxPursuit)
                        {
                            agent.RecordAction(ActionType.Hunt, currentTick, $"Failed to catch {targetAnimal.Species}");
                            bus.Emit(currentTick,
                                $"{agent.Name} failed to catch a {targetAnimal.Species}",
                                EventType.Action, agentId: agent.Id);

                            trace?.Invoke($"[TRACE Agent {agent.Id}] HUNT: pursuit expired after {maxPursuit} ticks");
                            agent.ClearPendingAction();
                            agent.CurrentAction = ActionType.Idle;
                            agent.ClearGoal();
                            agent.HuntTargetAnimalId = null;
                        }
                    }
                    // Don't increment ActionProgress normally for Hunt — handled above
                    return;
                }

                // Continue the action — advance float progress by 1.0 per tick
                agent.ActionProgress += 1.0f;
                agent.CurrentAction = agent.PendingAction!.Value;

                // Per-tick effects for Rest (regen applied each tick)
                if (agent.PendingAction == ActionType.Rest)
                {
                    // Health regen is already handled in Agent.UpdateNeeds() based on CurrentAction
                }

                // Per-tick effects for Build (increment build progress each tick)
                if (agent.PendingAction == ActionType.Build && agent.ActionTarget.HasValue)
                {
                    var buildTile = world.GetTile(agent.ActionTarget.Value.X, agent.ActionTarget.Value.Y);
                    if (agent.ActionTargetRecipe != null && buildTile.BuildProgress.ContainsKey(agent.ActionTargetRecipe))
                    {
                        buildTile.BuildProgress[agent.ActionTargetRecipe]++;
                    }
                }

                if (agent.ActionProgress >= agent.ActionDurationTicks)
                {
                    CompleteAction(agent, world, bus, currentTick, trace);
                    // BALANCE FIX: After action completes, fall through to priority cascade
                    // so the agent can immediately eat if starving with food in inventory.
                    // Previously, the return here caused a 1-tick gap where agents had food
                    // but couldn't eat until next tick, wasting 5 hunger per gap.
                }
                else
                {
                    // Soft eat interrupt: snack from inventory mid-action without canceling
                    // Prevents hunger dipping below 40 during long gather/build/move actions
                    if (agent.Hunger < 50f && agent.FoodInInventory() > 0
                        && agent.CurrentMode != BehaviorMode.Urgent
                        && agent.PendingAction != ActionType.Eat
                        && agent.PendingAction != ActionType.Cook)
                    {
                        if (agent.Eat())
                        {
                            agent.RecordAction(ActionType.Eat, currentTick,
                                $"Snacked mid-action (hunger: {agent.Hunger:F0})");
                        }
                    }
                    return;
                }
            }
        }

        // ── GDD v1.7.2: Stage-based child behavior (children don't use modes) ──
        if (isChild)
        {
            DecideChild(agent, world, bus, currentTick, trace);
            return;
        }

        // ── D18.2 Fix 2: ABSOLUTE CRITICAL EAT — nothing preempts P1 ──
        // An agent with food must NEVER starve. This runs before the Hard Distance
        // Ceiling so that stuck agents eat instead of endlessly attempting to move home.
        if (agent.Hunger <= SimConfig.InterruptHungerThreshold && agent.FoodInInventory() > 0)
        {
            trace?.Invoke($"[TRACE Agent {agent.Id}] D18.2-CRITICAL-EAT: hunger={agent.Hunger:F0}, food={agent.FoodInInventory()}, eating before ceiling");
            agent.ClearPendingAction();
            if (agent.Eat())
            {
                agent.RecordAction(ActionType.Eat, currentTick,
                    $"D18.2 critical eat (hunger: {agent.Hunger:F0})");
                LogDecision(currentTick, agent, "Eat", "d18.2-critical-eat", 100f);
            }
            // Don't return — fall through to ceiling and normal pipeline
        }

        // ── D15 HARD DISTANCE CEILING ─────────────────────────────────
        // Runs AFTER IsBusy (so moves can complete first), BEFORE safety overrides.
        // Mode-specific limits: Explore=ExploreMaxRange, Forage=ForageMaxRange, others=ForageMaxRange.
        // If the agent already has a ReturnHome goal and position changed (move worked),
        // skip the ceiling to let Phase 3 continue the return trip.
        // Post-Playtest Fix 1: Skip when safety-return is suppressed (stuck agent surviving locally)
        if (agent.HomeTile.HasValue && currentTick >= agent.SafetyReturnSuppressedUntil)
        {
            int absDistFromHome = Math.Max(
                Math.Abs(agent.X - agent.HomeTile.Value.X),
                Math.Abs(agent.Y - agent.HomeTile.Value.Y));
            int ceilingDist = agent.CurrentMode == BehaviorMode.Explore
                ? SimConfig.ExploreMaxRange : SimConfig.ForageMaxRange;
            if (absDistFromHome > ceilingDist)
            {
                // D18.2: If the ceiling has been firing every tick but the agent hasn't moved,
                // they're trapped. Activate safety-return suppression so the stuck-and-starving
                // system can engage (local gathering, eating, random walk escape).
                if (agent.MoveFailCount >= 20)
                {
                    agent.SafetyReturnSuppressedUntil = currentTick + 500;
                    trace?.Invoke($"[TRACE Agent {agent.Id}] CEILING-SUPPRESS: MoveFailCount={agent.MoveFailCount}, suppressing ceiling for 500 ticks to let stuck recovery engage");
                    // Fall through to Phase 1 safety overrides
                }
                else
                {

                bool activelyReturning = agent.CurrentGoal == GoalType.ReturnHome
                    && (agent.X != agent.LastStuckCheckPos.X || agent.Y != agent.LastStuckCheckPos.Y);

                if (!activelyReturning)
                {
                    agent.ClearPendingAction();
                    agent.ClearGoal();
                    if (agent.CurrentMode != BehaviorMode.Home)
                        agent.TransitionMode(BehaviorMode.Home, currentTick);
                    agent.CurrentGoal = GoalType.ReturnHome;
                    agent.GoalTarget = agent.HomeTile;
                    agent.GoalStartTick = currentTick;

                    // D20 Fix 1: Pass agent's blacklisted tiles to A*
                    var avoidCeiling = agent.GetBlacklistedTileSet(currentTick);
                    var path = SimplePathfinder.FindPath(agent.X, agent.Y,
                        agent.HomeTile.Value.X, agent.HomeTile.Value.Y, world,
                        avoidTiles: avoidCeiling.Count > 0 ? avoidCeiling : null);
                    if (path != null && path.Count > 0)
                    {
                        agent.WaypointPath = path;
                        agent.WaypointIndex = 0;
                        var wp = path[0];
                        TryStartMove(agent, wp.X, wp.Y, world, trace);
                        agent.RecordAction(ActionType.Move, currentTick,
                            $"Hard ceiling return (dist={absDistFromHome}, path={path.Count})");
                        LogDecision(currentTick, agent, "Move", "hard-ceiling-return", 100f);
                    }
                    else
                    {
                        // No A* path — try a greedy 1-tile step toward home.
                        bool greedyOk = TryGreedyStepToward(agent, agent.HomeTile.Value.X, agent.HomeTile.Value.Y, world, trace);
                        if (greedyOk)
                        {
                            agent.RecordAction(ActionType.Move, currentTick,
                                $"Hard ceiling greedy step (dist={absDistFromHome}, A* failed)");
                        }
                        else
                        {
                            // D18.2: A* failed AND greedy step failed — agent is truly stuck.
                            // Fall through to Phase 1 safety overrides and stuck detection
                            // instead of returning. The escalating pathfinder and stuck-and-starving
                            // system can only help if we don't monopolize the tick.
                            trace?.Invoke($"[TRACE Agent {agent.Id}] CEILING-STUCK: A* and greedy both failed at ({agent.X},{agent.Y}), dist={absDistFromHome}, falling through");
                        }
                    }
                    agent.LastDecisionTick = currentTick;
                    if (agent.IsBusy) return; // Only return if a move was actually started
                }
                // activelyReturning = true: agent is making progress, let Phase 3 continue

                } // end else (MoveFailCount < 20)
            }
        }

        // ════════════════════════════════════════════════════════════════
        // Directive #7: Restructured Decision Pipeline
        // Phase 1: Safety overrides (run every tick, BEFORE goal continuation)
        // Phase 2: Mode transitions
        // Phase 3: Goal continuation
        // Phase 4: Mode-specific decisions
        // ════════════════════════════════════════════════════════════════

        // ── PHASE 1: SAFETY OVERRIDES ─────────────────────────────────
        // These run every tick regardless of goal state and have authority
        // to clear the current goal. Cannot be bypassed by TryAdvanceGoal.

        // Directive #10 Fix 2: Update stuck detection (position unchanged for 3+ ticks)
        if (agent.PendingAction == ActionType.Move || agent.CurrentGoal.HasValue)
        {
            if (agent.X == agent.LastStuckCheckPos.X && agent.Y == agent.LastStuckCheckPos.Y)
                agent.StuckAtPositionCount++;
            else
            {
                agent.StuckAtPositionCount = 0;
                agent.LastStuckCheckPos = (agent.X, agent.Y);
            }
        }
        else
        {
            agent.StuckAtPositionCount = 0;
            agent.LastStuckCheckPos = (agent.X, agent.Y);
        }

        // Post-Playtest Fix 1: After 3 consecutive stuck ticks, suppress safety-return-home
        // so the agent can survive locally instead of repeating a failing move forever.
        if (agent.IsStuck && agent.CurrentGoal == GoalType.ReturnHome
            && agent.SafetyReturnSuppressedUntil <= currentTick)
        {
            agent.SafetyReturnSuppressedUntil = currentTick + 50;
            agent.ClearGoal();
            agent.ClearPendingAction();
            agent.StuckAtPositionCount = 0;
            trace?.Invoke($"[TRACE Agent {agent.Id}] STUCK-SUPPRESS: suppressing safety-return-home for 50 ticks, allowing local survival");
        }

        // Post-Playtest Fix 1: Force local gather when stuck 10+ ticks and hungry
        // Agent should never starve with food on adjacent tiles.
        if (agent.StuckAtPositionCount >= 10 && agent.Hunger < 40f)
        {
            // Try to gather nearest food within 3 tiles
            bool gathered = false;
            for (int radius = 0; radius <= 3 && !gathered; radius++)
            {
                for (int dx = -radius; dx <= radius && !gathered; dx++)
                {
                    for (int dy = -radius; dy <= radius && !gathered; dy++)
                    {
                        if (Math.Abs(dx) < radius && Math.Abs(dy) < radius) continue; // skip inner ring
                        int gx = agent.X + dx, gy = agent.Y + dy;
                        if (!world.IsInBounds(gx, gy)) continue;
                        var gatherTile = world.GetTile(gx, gy);
                        foreach (var kvp in gatherTile.Resources)
                        {
                            if (kvp.Value > 0 && IsEdibleResource(kvp.Key))
                            {
                                agent.ClearGoal();
                                agent.ClearPendingAction();
                                if (radius == 0)
                                {
                                    // Gather directly from current tile
                                    agent.GatherFrom(gatherTile, kvp.Key);
                                    agent.RecordAction(ActionType.Gather, currentTick,
                                        $"Stuck emergency gather {kvp.Key} (hunger: {agent.Hunger:F0})");
                                }
                                else
                                {
                                    // Move toward the food tile first
                                    TryStartMove(agent, gx, gy, world, trace);
                                    agent.RecordAction(ActionType.Move, currentTick,
                                        $"Stuck moving to food at ({gx},{gy}) (hunger: {agent.Hunger:F0})");
                                }
                                LogDecision(currentTick, agent, "Gather", "stuck-emergency-gather", 100f);
                                agent.LastDecisionTick = currentTick;
                                gathered = true;
                            }
                        }
                    }
                }
            }
            if (gathered) return;
        }

        // Phase 1a: CRITICAL STARVATION EAT — absolute safety net
        // An agent with food should NEVER die of starvation. Period.
        // This fires unconditionally when hunger is critically low (< UrgentEntryHunger = 25).
        if (agent.Hunger < SimConfig.UrgentEntryHunger && agent.FoodInInventory() > 0)
        {
            trace?.Invoke($"[TRACE Agent {agent.Id}] CRITICAL-EAT: hunger={agent.Hunger:F0}, food={agent.FoodInInventory()}, eating to survive");
            agent.ClearGoal();
            agent.ClearPendingAction();
            if (agent.Eat())
            {
                agent.RecordAction(ActionType.Eat, currentTick,
                    $"Critical starvation eat (hunger: {agent.Hunger:F0})");
                LogDecision(currentTick, agent, "Eat", "critical-starvation-eat", 100f);
                // Don't return — fall through to mode transitions and decisions
            }
        }
        // Stuck emergency eat — fires when stuck + hungry + has food
        else if (agent.Hunger < 45f && agent.FoodInInventory() > 0 && agent.IsStuck)
        {
            trace?.Invoke($"[TRACE Agent {agent.Id}] STUCK-EAT: hunger={agent.Hunger:F0}, food={agent.FoodInInventory()}, stuck={agent.StuckAtPositionCount} ticks, eating");
            agent.ClearGoal();
            agent.ClearPendingAction();
            if (agent.Eat())
            {
                agent.RecordAction(ActionType.Eat, currentTick,
                    $"Stuck emergency eat (hunger: {agent.Hunger:F0}, stuck: {agent.StuckAtPositionCount})");
                LogDecision(currentTick, agent, "Eat", "stuck-eat-override", 100f);
                // Don't return — fall through to mode transitions and decisions
            }
        }
        // Standard emergency eat — fires when has food + active goal + moderately hungry
        // Skip Urgent/Forage (they have dedicated eat logic) and pre-Urgent hunger levels.
        else if (agent.Hunger < 50f && agent.Hunger >= SimConfig.UrgentEntryHunger
            && agent.FoodInInventory() > 0
            && agent.CurrentGoal.HasValue
            && agent.CurrentMode != BehaviorMode.Urgent
            && agent.CurrentMode != BehaviorMode.Forage)
        {
            trace?.Invoke($"[TRACE Agent {agent.Id}] SAFETY-EAT: hunger={agent.Hunger:F0}, food={agent.FoodInInventory()}, goal={agent.CurrentGoal}, eating then continuing pipeline");
            agent.ClearGoal();
            agent.ClearPendingAction();
            if (agent.Eat())
            {
                agent.RecordAction(ActionType.Eat, currentTick,
                    $"Emergency eat (hunger: {agent.Hunger:F0})");
                LogDecision(currentTick, agent, "Eat", "safety-override", 100f);
                // Don't return — fall through to mode transitions and decisions
            }
        }

        // Phase 1b: DISTANCE SAFETY — mode-aware distance check
        // Home/Caretaker/Build/Urgent: SafetyReturnDist (15)
        // Forage: ForageMaxRange (30)  — handled by hard ceiling above
        // Explore: ExploreMaxRange (45) — handled by hard ceiling above
        // Post-Playtest Fix 1: Skip when safety-return is suppressed (stuck agent surviving locally)
        if (agent.HomeTile.HasValue && agent.CurrentMode != BehaviorMode.Explore
            && agent.CurrentMode != BehaviorMode.Forage
            && currentTick >= agent.SafetyReturnSuppressedUntil)
        {
            int distFromHome = Math.Max(
                Math.Abs(agent.X - agent.HomeTile.Value.X),
                Math.Abs(agent.Y - agent.HomeTile.Value.Y));
            if (distFromHome > SimConfig.SafetyReturnDist)
            {
                // D20 Fix 1: Back off when MoveFailCount is high — let escalation system work
                if (agent.MoveFailCount >= 20)
                {
                    trace?.Invoke($"[TRACE Agent {agent.Id}] SAFETY-DISTANCE-BACKOFF: {distFromHome} tiles from home but MoveFailCount={agent.MoveFailCount}, letting escalation handle it");
                    // Don't re-issue ReturnHome — the escalating pathfinder is already working
                }
                else
                {
                    trace?.Invoke($"[TRACE Agent {agent.Id}] SAFETY-DISTANCE: {distFromHome} tiles from home in mode={agent.CurrentMode}, forcing return");
                    agent.ClearGoal();
                    agent.ClearPendingAction();
                    agent.TransitionMode(BehaviorMode.Home, currentTick);

                    // D20 Fix 1: Pass agent's blacklisted tiles to A* so it routes around known-blocked tiles
                    var avoidTiles = agent.GetBlacklistedTileSet(currentTick);

                    // Compute A* waypoint path for reliable return
                    var path = SimplePathfinder.FindPath(agent.X, agent.Y,
                        agent.HomeTile.Value.X, agent.HomeTile.Value.Y, world,
                        avoidTiles: avoidTiles.Count > 0 ? avoidTiles : null);
                    if (path != null && path.Count > 0)
                    {
                        agent.WaypointPath = path;
                        agent.WaypointIndex = 0;
                        agent.CurrentGoal = GoalType.ReturnHome;
                        agent.GoalTarget = agent.HomeTile;
                        agent.GoalStartTick = currentTick;
                        var wp = path[0];
                        TryStartMove(agent, wp.X, wp.Y, world, trace);
                        agent.RecordAction(ActionType.Move, currentTick,
                            $"Safety return home (dist={distFromHome}, A* path={path.Count} steps)");
                        LogDecision(currentTick, agent, "Move", "safety-return-home", 90f);
                    }
                    else
                    {
                        // No A* path — try greedy 1-tile step instead of teleporting
                        trace?.Invoke($"[TRACE Agent {agent.Id}] SAFETY-NO-PATH: A* failed, trying greedy step home");
                        TryGreedyStepToward(agent, agent.HomeTile.Value.X, agent.HomeTile.Value.Y, world, trace);
                        agent.RecordAction(ActionType.Move, currentTick,
                            $"Safety greedy step (no A* path, dist={distFromHome})");
                    }
                    agent.LastDecisionTick = currentTick;
                    return;
                }
            }
        }

        // Phase 1c: STUCK DETECTION — position oscillation check
        {
            agent.RecordPosition(); // Always record position each tick
            if (agent.CurrentGoal.HasValue && agent.IsBusy
                && agent.PendingAction == ActionType.Move)
            {
                // Check if current position is already in recent positions buffer
                // (meaning we're oscillating between the same tiles)
                bool isOscillating = false;
                int distinctPositions = 0;
                var seen = new HashSet<(int, int)>();
                for (int i = 0; i < agent.RecentPositions.Length; i++)
                {
                    var p = agent.RecentPositions[i];
                    if (p != (0, 0)) // skip uninitialized
                        seen.Add(p);
                }
                distinctPositions = seen.Count;
                isOscillating = distinctPositions > 0 && distinctPositions <= 3 && agent.StuckCounter >= 10;

                if (agent.IsPositionRecent(agent.X, agent.Y))
                    agent.StuckCounter++;
                else
                    agent.StuckCounter = 0;

                if (isOscillating)
                {
                    trace?.Invoke($"[TRACE Agent {agent.Id}] SAFETY-STUCK: oscillating between {distinctPositions} tiles for {agent.StuckCounter} ticks, goal={agent.CurrentGoal}, clearing");
                    agent.ClearGoal();
                    agent.ClearPendingAction();
                    agent.StuckCounter = 0;
                    // Fall through to Phase 4 for fresh decision
                }
            }
            else if (!agent.IsBusy || agent.PendingAction != ActionType.Move)
            {
                agent.StuckCounter = 0; // Reset when not moving
            }
        }

        // ── PHASE 2: MODE TRANSITIONS ─────────────────────────────────
        // Save goal state BEFORE EvaluateTransitions, because transition helpers
        // (SetReturnHomeGoalIfNeeded, TransitionToForage) overwrite the goal internally.
        // We restore the original goal after if ShouldPreserveGoal says it's still relevant.
        var oldMode = agent.CurrentMode;
        var savedGoal = agent.CurrentGoal;
        var savedGoalTarget = agent.GoalTarget;
        var savedGoalStartTick = agent.GoalStartTick;
        var savedGoalResource = agent.GoalResource;
        bool goalPreserved = false;
        bool modeChanged = ModeTransitionManager.EvaluateTransitions(agent, world, currentTick, allAgents);
        if (modeChanged)
        {
            // Check if the ORIGINAL goal (before EvaluateTransitions may have overwritten it)
            // should be preserved across this mode transition.
            if (savedGoal.HasValue && ShouldPreserveGoal(savedGoal.Value, oldMode, agent.CurrentMode, agent))
            {
                // Restore the original goal that EvaluateTransitions overwrote.
                // Reset GoalStartTick to current tick so the stale goal timer restarts
                // from the transition point. Without this, goals preserved across multiple
                // mode transitions accumulate time and hit the stale timeout prematurely.
                agent.CurrentGoal = savedGoal;
                agent.GoalTarget = savedGoalTarget;
                agent.GoalStartTick = currentTick;
                agent.GoalResource = savedGoalResource;
                goalPreserved = true;
                trace?.Invoke($"[TRACE Agent {agent.Id}] MODE-CHANGE: {oldMode}->{agent.CurrentMode}, restored preserved {savedGoal} goal");
            }
            else
            {
                agent.ClearGoal();
                trace?.Invoke($"[TRACE Agent {agent.Id}] MODE-CHANGE: {oldMode}->{agent.CurrentMode}, cleared goal, re-evaluating");
            }
        }

        // Phase 2b: Caretaker distance escape — catch drifted caretakers before 15-tile safety
        if (agent.CurrentMode == BehaviorMode.Caretaker && agent.HomeTile.HasValue)
        {
            int caretakerDist = Math.Max(
                Math.Abs(agent.X - agent.HomeTile.Value.X),
                Math.Abs(agent.Y - agent.HomeTile.Value.Y));
            if (caretakerDist > SimConfig.CaretakerEscapeDist)
            {
                trace?.Invoke($"[TRACE Agent {agent.Id}] CARETAKER-ESCAPE: dist={caretakerDist}, transitioning to Home");
                agent.ClearGoal();
                agent.TransitionMode(BehaviorMode.Home, currentTick);
                modeChanged = true;
            }
        }

        // Directive #10 Fix 1: Consume ForceReevaluation flag — skip goal continuation this tick
        if (agent.ForceReevaluation)
        {
            agent.ForceReevaluation = false;
            if (agent.CurrentGoal == GoalType.GatherFoodAt || agent.CurrentGoal == GoalType.GatherResourceAt)
                _dbgPhase3SkippedForceReeval++;
            // Don't clear the goal — just skip Phase 3 so Phase 4 re-evaluates
        }

        // ── PHASE 3: GOAL CONTINUATION ────────────────────────────────
        // Only runs if safety checks in Phase 1 did NOT clear the goal.
        // Goals preserved across mode transitions (goalPreserved) continue normally —
        // e.g., GatherFoodAt survives Forage↔Home oscillation, ReturnHome survives
        // transitions to Home mode.
        else if ((!modeChanged || goalPreserved)
                 && agent.CurrentGoal.HasValue && !agent.IsBusy)
        {
            bool wasGatherGoal = agent.CurrentGoal == GoalType.GatherFoodAt || agent.CurrentGoal == GoalType.GatherResourceAt;
            if (wasGatherGoal) _dbgPhase3Entered++;
            if (TryAdvanceGoal(agent, world, bus, currentTick, trace))
            {
                if (wasGatherGoal) _dbgPhase3GatherAdvanced++;
                agent.LastDecisionTick = currentTick;
                return;
            }

            if (wasGatherGoal) _dbgPhase3GatherFailed++;

            // Goal Commitment Fix: For gather goals that TryAdvanceGoal didn't clear
            // internally (temporary movement failure, stale timeout), keep the goal
            // alive so the escalating pathfinder can try harder next tick.
            // Only clear if TryAdvanceGoal already cleared the goal (leash, etc.),
            // or if ForceReevaluation was set (move chain checkpoint abandonment).
            if (wasGatherGoal && agent.CurrentGoal.HasValue && !agent.ForceReevaluation)
            {
                // Goal still set — TryAdvanceGoal couldn't move this tick but didn't
                // reject the goal. Keep it alive for retry with escalated pathfinding.
                trace?.Invoke($"[TRACE Agent {agent.Id}] GOAL-RETRY: {agent.CurrentGoal} blocked this tick, keeping for retry (moveFailCount={agent.MoveFailCount})");
                agent.LastDecisionTick = currentTick;
                return; // Skip Phase 4 — goal is preserved, retry next tick
            }

            // Goal was already cleared by TryAdvanceGoal (leash, etc.) or ForceReevaluation set
            agent.ClearGoal();
            trace?.Invoke($"[TRACE Agent {agent.Id}] GOAL-CLEARED: falling through to full decision");
        }
        else if (agent.CurrentGoal == GoalType.GatherFoodAt || agent.CurrentGoal == GoalType.GatherResourceAt)
        {
            // Track WHY Phase 3 was skipped for gather goals
            if (modeChanged && !goalPreserved) _dbgPhase3SkippedModeChange++;
            else if (agent.IsBusy) _dbgPhase3SkippedBusy++;
            else if (!agent.CurrentGoal.HasValue) _dbgPhase3SkippedNoGoal++;
        }

        // ── PHASE 4: MODE-SPECIFIC DECISIONS ──────────────────────────
        _dbgPhase4Ran++;
        if (agent.CurrentGoal == GoalType.GatherFoodAt || agent.CurrentGoal == GoalType.GatherResourceAt)
            _dbgPhase4WithActiveGatherGoal++;

        agent.CurrentAction = ActionType.Idle;

        switch (agent.CurrentMode)
        {
            case BehaviorMode.Urgent:
                DecideUrgent(agent, world, bus, currentTick, allAgents, trace);
                break;
            case BehaviorMode.Home:
                DecideHome(agent, world, bus, currentTick, allAgents, trace);
                break;
            case BehaviorMode.Forage:
                DecideForage(agent, world, bus, currentTick, trace);
                break;
            case BehaviorMode.Build:
                DecideBuild(agent, world, bus, currentTick, trace);
                break;
            case BehaviorMode.Explore:
                DecideExplore(agent, world, bus, currentTick, trace);
                break;
            case BehaviorMode.Caretaker:
                DecideCaretaker(agent, world, bus, currentTick, allAgents, trace);
                break;
        }

        // Fallback: if no decision produced an action, try alternatives before idling.
        if (agent.CurrentAction == ActionType.Idle && !agent.IsBusy)
        {
            // Exposed + fed agents should experiment, not idle. This prevents the
            // early-game deadlock where homeless agents waste thousands of ticks.
            if (agent.IsExposed && !agent.KnowsAnyShelterRecipe() && agent.Hunger > 50)
            {
                // Try to start a real experiment via the scoring/dispatch pipeline
                var available = RecipeRegistry.GetAvailableRecipes(agent, _currentKnowledgeSystem, _currentSettlements);
                var shelterRecipe = available.FirstOrDefault(r => r.Id == "lean_to") ?? (available.Count > 0 ? available[0] : null);
                if (shelterRecipe != null)
                {
                    StartExperiment(agent, shelterRecipe, world, bus, currentTick, trace);
                    agent.RecordAction(ActionType.Experiment, currentTick, "Experiment (homeless fallback — seeking shelter recipe)");
                    trace?.Invoke($"[TRACE Agent {agent.Id}] FALLBACK: exposed+fed, forcing Experiment instead of Idle");
                }
                else
                {
                    // No available recipes — just idle
                    agent.RecordAction(ActionType.Idle, currentTick, "Idle (no recipes available)");
                }
            }
            else if (agent.HomeTile.HasValue
                && (agent.X != agent.HomeTile.Value.X || agent.Y != agent.HomeTile.Value.Y))
            {
                if (TryStartMove(agent, agent.HomeTile.Value.X, agent.HomeTile.Value.Y, world, trace))
                {
                    agent.CurrentGoal = GoalType.ReturnHome;
                    agent.GoalTarget = agent.HomeTile;
                    agent.GoalStartTick = currentTick;
                    trace?.Invoke($"[TRACE Agent {agent.Id}] FALLBACK: no valid action, returning home");
                    agent.RecordAction(ActionType.Move, currentTick, "Fallback: returning home (no valid action)");
                }
                else
                {
                    agent.CurrentAction = ActionType.Idle;
                    agent.RecordAction(ActionType.Idle, currentTick, "Idle (fallback move failed)");
                    trace?.Invoke($"[TRACE Agent {agent.Id}] FALLBACK: move home failed, idling");
                }
            }
            else
            {
                agent.RecordAction(ActionType.Idle, currentTick, "Idle at home (no scored actions)");
            }
        }

        // Track decision heartbeat — any non-idle action counts as a decision
        if (agent.CurrentAction != ActionType.Idle || agent.IsBusy)
            agent.LastDecisionTick = currentTick;
    }

    // ═════════════════════════════════════════════════════════════════
    // v1.8 Mode-Specific Decision Methods
    // ═════════════════════════════════════════════════════════════════

    /// <summary>v1.8: Urgent mode — pure survival. Eat, seek food, rest.</summary>
    private void DecideUrgent(Agent agent, World world, EventBus bus, int currentTick,
        List<Agent>? allAgents, Action<string>? trace)
    {
        var currentTile = world.GetTile(agent.X, agent.Y);
        int food = agent.FoodInInventory();

        // Eat from inventory if has food
        if (food > 0)
        {
            agent.ConsecutiveFoodSeekTicks = 0;
            trace?.Invoke($"[TRACE Agent {agent.Id}] URGENT Eat: FIRE (hunger={agent.Hunger:F0}, food={food})");
            LogDecision(currentTick, agent, "Eat", "inventory", 100f);

            if (agent.Knowledge.Contains("cooking") && agent.HasCookableFood())
            {
                bool hasFireSource = false;
                if (agent.HomeTile.HasValue && agent.X == agent.HomeTile.Value.X && agent.Y == agent.HomeTile.Value.Y
                    && agent.Knowledge.Contains("fire"))
                    hasFireSource = true;
                if (currentTile.Structures.Contains("campfire") || currentTile.Structures.Contains("hearth"))
                    hasFireSource = true;

                if (hasFireSource)
                {
                    StartCook(agent, currentTick, trace);
                    agent.RecordAction(ActionType.Cook, currentTick, "Cooking food (urgent)");
                    return;
                }
            }

            if (agent.Eat())
            {
                agent.RecordAction(ActionType.Eat, currentTick, $"Ate food (hunger: {agent.Hunger:F0})");
                bus.Emit(currentTick, $"{agent.Name} ate food (Hunger: {agent.Hunger:F0})", EventType.Action, agentId: agent.Id);
                return;
            }
        }

        // Eat from home storage
        if (food <= 0 && agent.HomeTile.HasValue
            && agent.X == agent.HomeTile.Value.X && agent.Y == agent.HomeTile.Value.Y)
        {
            if (agent.TryEatFromHomeStorage(currentTile))
            {
                bus.Emit(currentTick, $"{agent.Name} ate from home storage (Hunger: {agent.Hunger:F0})", EventType.Action, agentId: agent.Id);
                agent.RecordAction(ActionType.Eat, currentTick, $"Ate from home storage (hunger: {agent.Hunger:F0})");
                return;
            }
        }

        // Move home for stored food — use SeekFood goal so the commitment persists
        // across ticks (ReturnHome would be interrupted by TryAdvanceGoal's hunger check).
        // SeekFood is exempt from the hunger interrupt, so the agent keeps walking home.
        if (food <= 0 && agent.HomeTile.HasValue
            && (agent.X != agent.HomeTile.Value.X || agent.Y != agent.HomeTile.Value.Y))
        {
            var homeTile = world.GetTile(agent.HomeTile.Value.X, agent.HomeTile.Value.Y);
            if (homeTile.HasHomeStorage && homeTile.HomeTotalFood > 0)
            {
                agent.CurrentGoal = GoalType.SeekFood;
                agent.GoalTarget = (agent.HomeTile.Value.X, agent.HomeTile.Value.Y);
                agent.GoalStartTick = currentTick;

                if (TryStartMove(agent, agent.HomeTile.Value.X, agent.HomeTile.Value.Y, world, trace))
                {
                    LogDecision(currentTick, agent, "Move", $"home({agent.HomeTile.Value.X},{agent.HomeTile.Value.Y})", 100f);
                    agent.RecordAction(ActionType.Move, currentTick, "Moving home for stored food (urgent)");
                    return;
                }
                // TryStartMove failed this tick — keep goal alive so TryAdvanceGoal retries next tick
                trace?.Invoke($"[TRACE Agent {agent.Id}] URGENT: can't move home, goal set for retry");
                return; // Don't fall through to seek-food cascade — we have a plan (home has food)
            }
        }

        // Granary withdraw
        if (food <= 0)
        {
            var granaryTile = FindNearbyGranaryWithFood(agent, world, currentTick);
            if (granaryTile != null)
            {
                if (granaryTile.X == agent.X && granaryTile.Y == agent.Y)
                {
                    StartWithdraw(agent, granaryTile, currentTick, trace);
                    agent.RecordAction(ActionType.Withdraw, currentTick, "Withdrawing food from granary (urgent)");
                    return;
                }
                else if (TryStartMove(agent, granaryTile.X, granaryTile.Y, world, trace))
                {
                    agent.RecordAction(ActionType.Move, currentTick, $"Moving to granary at ({granaryTile.X},{granaryTile.Y})");
                    return;
                }
            }
        }

        // Rush home to feed starving child — only if carrying enough food to actually help
        if (allAgents != null && agent.HomeTile.HasValue && agent.FoodInInventory() >= 3)
        {
            foreach (var kvp in agent.Relationships)
            {
                if (kvp.Value == RelationshipType.Child)
                {
                    var child = allAgents.FirstOrDefault(a => a.Id == kvp.Key);
                    if (child != null && child.IsAlive && child.Hunger < 50f
                        && child.X == agent.HomeTile.Value.X && child.Y == agent.HomeTile.Value.Y
                        && (agent.X != agent.HomeTile.Value.X || agent.Y != agent.HomeTile.Value.Y))
                    {
                        if (TryStartMove(agent, agent.HomeTile.Value.X, agent.HomeTile.Value.Y, world, trace))
                        {
                            agent.RecordAction(ActionType.Move, currentTick, "Rushing home to feed starving child");
                            return;
                        }
                    }
                }
            }
        }

        // Feed nearby hungry child
        if (agent.FoodInInventory() > 0)
            TryFeedNearbyChild(agent, world, bus, currentTick, trace);

        // Seek food (P2 equivalent)
        if (agent.Hunger <= SimConfig.UrgentEntryHunger && agent.FoodInInventory() <= 0)
        {
            agent.ConsecutiveFoodSeekTicks++;
            LogDecision(currentTick, agent, "Gather", "food-seek", 90f);

            // Escape valve: after 50 ticks, clear memories and explore
            if (agent.ConsecutiveFoodSeekTicks > 50)
            {
                agent.ClearAllFoodMemories();
                agent.ConsecutiveFoodSeekTicks = 0;
                StartExplore(agent, world, bus, currentTick, trace);
                agent.RecordAction(ActionType.Explore, currentTick, "Exploring for food (desperate)");
                return;
            }

            if (!agent.HasInventorySpace())
            {
                int dropped = agent.DropNonFoodToTile(currentTile, 3);
                if (dropped > 0)
                    agent.RecordAction(ActionType.Idle, currentTick, $"Dropped {dropped} non-food items (starving)");
            }

            if (TryStartGatherFood(agent, currentTile, world, bus, currentTick, trace))
            {
                agent.RecordAction(ActionType.Gather, currentTick, "Gathering food (urgent)");
                return;
            }

            if (TryStartMoveTowardsFood(agent, world, bus, currentTick, trace))
            {
                agent.RecordAction(ActionType.Move, currentTick, "Moving toward food (urgent)");
                return;
            }

            StartExplore(agent, world, bus, currentTick, trace);
            agent.RecordAction(ActionType.Explore, currentTick, "Exploring for food (urgent)");
            return;
        }

        // Rest if health critical
        if (agent.Health <= SimConfig.UrgentExitHealth && agent.Hunger > SimConfig.UrgentEntryHunger)
        {
            LogDecision(currentTick, agent, "Rest", "emergency", 80f);

            if (!currentTile.HasShelter)
            {
                var shelters = agent.GetRememberedShelters(currentTick);
                if (shelters.Count > 0)
                {
                    var nearest = GetNearest(agent, shelters);
                    if (nearest != null && TryStartMove(agent, nearest.X, nearest.Y, world, trace))
                    {
                        agent.RecordAction(ActionType.Move, currentTick, $"Seeking shelter at ({nearest.X},{nearest.Y})");
                        return;
                    }
                }
            }

            StartRest(agent, trace);
            agent.LastRestTick = currentTick;
            agent.RecordAction(ActionType.Rest, currentTick, $"Resting (health={agent.Health})");
            return;
        }

        // Nothing to do in Urgent — idle
        agent.CurrentAction = ActionType.Idle;
    }

    /// <summary>v1.8: Home mode — settlement activities, experimenting, socializing.</summary>
    private void DecideHome(Agent agent, World world, EventBus bus, int currentTick,
        List<Agent>? allAgents, Action<string>? trace)
    {
        var currentTile = world.GetTile(agent.X, agent.Y);
        int food = agent.FoodInInventory();

        // Feed nearby hungry child (always check in Home)
        if (food > 0)
            TryFeedNearbyChild(agent, world, bus, currentTick, trace);

        food = agent.FoodInInventory(); // May have changed after feeding

        // Fix B4: Withdraw from home storage to feed hungry child when parent has no food.
        // Prevents "baby starving next to full pantry" in Home mode.
        if (food <= 0 && agent.HomeTile.HasValue
            && agent.X == agent.HomeTile.Value.X && agent.Y == agent.HomeTile.Value.Y
            && currentTile.HasHomeStorage && currentTile.HomeTotalFood > 0
            && allAgents != null)
        {
            foreach (var kvp in agent.Relationships)
            {
                if (kvp.Value != RelationshipType.Child) continue;
                var child = allAgents.FirstOrDefault(a => a.Id == kvp.Key);
                if (child == null || !child.IsAlive || child.Hunger >= 70f) continue;
                if (child.X != agent.X || child.Y != agent.Y) continue;

                var (wType, wAmount) = currentTile.WithdrawAnyFoodFromHome(1);
                if (wAmount > 0)
                {
                    // Direct add — original v1.8 code had no capacity check here.
                    // This is a transient step: food goes into inventory then immediately to child.
                    agent.Inventory[wType] = agent.Inventory.GetValueOrDefault(wType, 0) + wAmount;
                    TryFeedNearbyChild(agent, world, bus, currentTick, trace);
                    food = agent.FoodInInventory();
                    trace?.Invoke($"[TRACE Agent {agent.Id}] Home: withdrew from storage to feed child");
                }
                break;
            }
        }

        // Directive Fix 2: Home mode hard tether — if >10 tiles from home, go home first.
        // Exception: homeless agents (pre-shelter bootstrap) can roam freely.
        if (agent.HomeTile.HasValue)
        {
            int distFromHome = Math.Max(Math.Abs(agent.X - agent.HomeTile.Value.X),
                Math.Abs(agent.Y - agent.HomeTile.Value.Y));
            if (distFromHome > SimConfig.HomeTetherRadius)
            {
                trace?.Invoke($"[TRACE Agent {agent.Id}] HOME-TETHER: {distFromHome} tiles from home, returning");
                agent.CurrentGoal = GoalType.ReturnHome;
                agent.GoalTarget = agent.HomeTile;
                agent.GoalStartTick = currentTick;
                if (TryStartMove(agent, agent.HomeTile.Value.X, agent.HomeTile.Value.Y, world, trace))
                {
                    agent.RecordAction(ActionType.Move, currentTick, "Returning home (too far)");
                    return;
                }
            }
        }

        // Post-Playtest Fix 3: Night rest — single rest covers remaining night.
        // Duration = remaining ticks until dawn so agent wakes exactly at morning.
        bool canBuildShelter = agent.IsExposed
            && agent.Knowledge.Contains("lean_to")
            && agent.Inventory.GetValueOrDefault(ResourceType.Wood, 0) >= SimConfig.ShelterWoodCost;

        if (Agent.IsNightTime(currentTick) && agent.Hunger > 40f && !canBuildShelter)
        {
            bool atHome = agent.HomeTile.HasValue && agent.X == agent.HomeTile.Value.X && agent.Y == agent.HomeTile.Value.Y;
            int remainingNight = CalculateRemainingNightTicks(currentTick);

            if (currentTile.HasShelter || atHome)
            {
                LogDecision(currentTick, agent, "Rest", "night", 75f);
                agent.PendingAction = ActionType.Rest;
                agent.ActionProgress = 0f;
                agent.ActionDurationTicks = Math.Max(remainingNight, 30);
                agent.CurrentAction = ActionType.Rest;
                agent.LastRestTick = currentTick;
                agent.RecordAction(ActionType.Rest, currentTick, "Resting for the night");
                return;
            }
            else if (agent.HomeTile.HasValue)
            {
                // Not at home — head home to sleep
                if (TryStartMove(agent, agent.HomeTile.Value.X, agent.HomeTile.Value.Y, world, trace))
                {
                    agent.RecordAction(ActionType.Move, currentTick, "Heading home to sleep");
                    return;
                }
            }
            // No home / can't reach home — rest in the open
            agent.PendingAction = ActionType.Rest;
            agent.ActionProgress = 0f;
            agent.ActionDurationTicks = Math.Max(remainingNight, 30);
            agent.CurrentAction = ActionType.Rest;
            agent.LastRestTick = currentTick;
            agent.RecordAction(ActionType.Rest, currentTick, "Resting in the open (night)");
            return;
        }

        // Fix 3: Suppress self-eat when a nearby child is hungrier — feed child first
        bool childHungrier = HasHungrierChild(agent, allAgents);

        // Eat from inventory when actually hungry — not when nearly full
        // Hunger > 75 = well-fed, save the food. Hunger <= 75 = getting hungry, eat.
        if (agent.Hunger <= 65f && food > 0 && !childHungrier)
        {
            if (TryEatOrCook(agent, currentTile, bus, currentTick, trace))
                return;
        }

        // Eat from home storage when actually hungry and at home
        if (agent.Hunger <= 75f && food <= 0 && !childHungrier
            && agent.HomeTile.HasValue
            && agent.X == agent.HomeTile.Value.X && agent.Y == agent.HomeTile.Value.Y)
        {
            if (agent.TryEatFromHomeStorage(currentTile))
            {
                agent.RecordAction(ActionType.Eat, currentTick, $"Ate from home storage (hunger: {agent.Hunger:F0})");
                return;
            }
        }

        // Score Home-mode actions via utility scorer
        var scoredActions = UtilityScorer.ScoreHomeActions(agent, world, currentTick, random,
            allAgents, _currentKnowledgeSystem, _currentSettlements, trace);

        // D25c: Score trapping directly in DecideHome (not in ScoreHomeActions to avoid RNG cascade)
        if (agent.Knowledge.Contains("trapping") && agent.HomeTile.HasValue)
        {
            int activeTraps = 0;
            foreach (var t in world.Traps)
                if (t.IsActive && t.PlacedByAgentId == agent.Id) activeTraps++;

            if (activeTraps < SimConfig.MaxTrapsPerAgent)
            {
                var (hx, hy) = agent.HomeTile.Value;
                (int X, int Y)? bestTrapTile = null;

                for (int dx = -SimConfig.TrapPlacementRadius; dx <= SimConfig.TrapPlacementRadius && bestTrapTile == null; dx++)
                    for (int dy = -SimConfig.TrapPlacementRadius; dy <= SimConfig.TrapPlacementRadius && bestTrapTile == null; dy++)
                    {
                        int tx = hx + dx, ty = hy + dy;
                        if (!world.IsInBounds(tx, ty)) continue;
                        var tile = world.GetTile(tx, ty);
                        if (tile.Biome != BiomeType.Forest) continue;
                        // No existing active trap on this tile
                        bool hasTrap = false;
                        foreach (var t in world.Traps)
                            if (t.IsActive && t.X == tx && t.Y == ty) { hasTrap = true; break; }
                        if (hasTrap) continue;
                        bestTrapTile = (tx, ty);
                    }

                if (bestTrapTile.HasValue)
                {
                    float trapScore = SimConfig.TrapBaseScore;
                    if (agent.Hunger < 50f) trapScore *= 1.3f; // More food needed (low hunger = more hungry)
                    scoredActions.Add(new ScoredAction(ActionType.SetTrap, trapScore, bestTrapTile));
                    trace?.Invoke($"[TRACE Agent {agent.Id}] HOME-TRAP: scored {trapScore:F3} at ({bestTrapTile.Value.X},{bestTrapTile.Value.Y})");
                }
            }
            // Re-sort after adding trap score
            scoredActions.Sort((a, b) => b.Score.CompareTo(a.Score));
        }

        // Trace top 3
        if (trace != null && scoredActions.Count > 0)
            trace($"[TRACE Agent {agent.Id}] HOME TOP3: {string.Join(", ", scoredActions.Take(3).Select(s => $"{s.Action}={s.Score:F3}"))}");

        foreach (var candidate in scoredActions)
        {
            // Build action triggers Build mode transition
            if (candidate.Action == ActionType.Build && candidate.TargetTile.HasValue && candidate.TargetRecipeId != null)
            {
                ModeTransitionManager.TransitionToBuild(agent, currentTick,
                    candidate.TargetRecipeId, candidate.TargetTile.Value);
                trace?.Invoke($"[TRACE Agent {agent.Id}] HOME→BUILD: {candidate.TargetRecipeId} at ({candidate.TargetTile.Value.X},{candidate.TargetTile.Value.Y})");
                DecideBuild(agent, world, bus, currentTick, trace);
                return;
            }

            // Gather on a remote tile (> 2 tiles away) → transition to Forage mode
            // Nearby gathering (1-2 tiles) stays in Home mode — it's local, not a trip
            // Guard: skip if already in a mode dispatch cycle (prevents DecideHome↔DecideForage recursion)
            if (!_inModeDispatch && candidate.Action == ActionType.Gather && candidate.TargetTile.HasValue
                && Math.Max(Math.Abs(candidate.TargetTile.Value.X - agent.X),
                    Math.Abs(candidate.TargetTile.Value.Y - agent.Y)) > 2)
            {
                var res = candidate.TargetResource ?? ResourceType.Berries;
                bool isFood = ModeTransitionManager.IsFoodResource(res);
                int returnThreshold = isFood ? SimConfig.ForageReturnFoodDefault : SimConfig.ForageReturnFoodDefault;
                agent.TransitionMode(BehaviorMode.Forage, currentTick);
                agent.ForageGatherCount = 0; // Fix 1A: Reset gather counter (matches TransitionToForage)
                agent.ModeCommit.ForageTargetResource = res;
                agent.ModeCommit.ForageTargetTile = candidate.TargetTile;
                agent.ModeCommit.ForageReturnFoodThreshold = returnThreshold;
                agent.CurrentGoal = isFood ? GoalType.GatherFoodAt : GoalType.GatherResourceAt;
                agent.GoalTarget = candidate.TargetTile;
                agent.GoalResource = res;
                agent.GoalStartTick = currentTick;
                trace?.Invoke($"[TRACE Agent {agent.Id}] HOME→FORAGE: {res} at ({candidate.TargetTile.Value.X},{candidate.TargetTile.Value.Y})");
                _inModeDispatch = true;
                try { DecideForage(agent, world, bus, currentTick, trace); }
                finally { _inModeDispatch = false; }
                return;
            }

            if (TryDispatchUtilityAction(agent, candidate, world, bus, currentTick, trace))
            {
                LogScoredDecision(currentTick, agent, candidate, scoredActions);
                UpdateDampening(agent, candidate.Action);
                return;
            }
        }

        // Fallback: idle at home
        agent.CurrentAction = ActionType.Idle;
        agent.RecordAction(ActionType.Idle, currentTick, "Idle at home");
    }

    /// <summary>v1.8: Forage mode — committed gather trip, return when threshold met.</summary>
    private void DecideForage(Agent agent, World world, EventBus bus, int currentTick, Action<string>? trace)
    {
        var currentTile = world.GetTile(agent.X, agent.Y);

        // D15 Fix: Distance guard — if already beyond ForageMaxRange, abort forage and go home.
        // Without this, DecideForage repeatedly starts 1-tile moves toward far targets,
        // bypassing TryAdvanceGoal's distance leash and causing indefinite drift.
        if (agent.HomeTile.HasValue)
        {
            int forageDist = Math.Max(
                Math.Abs(agent.X - agent.HomeTile.Value.X),
                Math.Abs(agent.Y - agent.HomeTile.Value.Y));
            if (forageDist > SimConfig.ForageMaxRange)
            {
                trace?.Invoke($"[TRACE Agent {agent.Id}] FORAGE-DISTANCE-GUARD: dist={forageDist} > ForageMaxRange={SimConfig.ForageMaxRange}, aborting forage");
                agent.TransitionMode(BehaviorMode.Home, currentTick);
                agent.ClearGoal();
                agent.CurrentGoal = GoalType.ReturnHome;
                agent.GoalTarget = agent.HomeTile;
                agent.GoalStartTick = currentTick;
                LogDecision(currentTick, agent, "Move", "forage-distance-abort", 90f);
                return;
            }
        }

        // Directive #11 Fix 3: Time-based night rest. Foragers return home to rest —
        // but only if there's a reason to go home (food/inventory). Otherwise rest in field.
        if (Agent.IsNightTime(currentTick))
        {
            bool homeHasFood = false;
            if (agent.HomeTile.HasValue && world.IsInBounds(agent.HomeTile.Value.X, agent.HomeTile.Value.Y))
            {
                var homeTile = world.GetTile(agent.HomeTile.Value.X, agent.HomeTile.Value.Y);
                homeHasFood = homeTile.HasHomeStorage && homeTile.HomeTotalFood > 0;
            }

            if (agent.HomeTile.HasValue && (homeHasFood || agent.FoodInInventory() > 0))
            {
                // Transition to Home mode — the Home mode rest logic will handle going home
                // Goal Commitment Fix: Don't clear gather goals here — let Phase 2's
                // ShouldPreserveGoal handle it properly on the next tick.
                // The gather goal will be preserved if commitment isn't met.
                agent.TransitionMode(BehaviorMode.Home, currentTick);
                bool hadGatherGoal = agent.CurrentGoal == GoalType.GatherFoodAt
                    || agent.CurrentGoal == GoalType.GatherResourceAt;
                if (!hadGatherGoal)
                    agent.ClearGoal();
                trace?.Invoke($"[TRACE Agent {agent.Id}] FORAGE→HOME: night rest, returning home{(hadGatherGoal ? " (preserving gather goal)" : "")}");
                _inModeDispatch = true;
                try { DecideHome(agent, world, bus, currentTick, _currentAllAgents, trace); }
                finally { _inModeDispatch = false; }
                return;
            }
            // No home or no food to return to — rest in field
            agent.PendingAction = ActionType.Rest;
            agent.ActionProgress = 0f;
            agent.ActionDurationTicks = Math.Max(CalculateRemainingNightTicks(currentTick), 30);
            agent.CurrentAction = ActionType.Rest;
            agent.LastRestTick = currentTick;
            agent.RecordAction(ActionType.Rest, currentTick, "Resting while foraging (no food at home)");
            return;
        }

        // Emergency eat from inventory
        if (agent.Hunger <= 75f && agent.FoodInInventory() > 0)
        {
            if (TryEatOrCook(agent, currentTile, bus, currentTick, trace))
                return;
        }

        // If we have a target and are at it, gather
        if (agent.ModeCommit.ForageTargetTile.HasValue)
        {
            var (tx, ty) = agent.ModeCommit.ForageTargetTile.Value;
            if (agent.X == tx && agent.Y == ty)
            {
                // Try gathering the committed resource
                if (agent.ModeCommit.ForageTargetResource.HasValue && agent.HasInventorySpace())
                {
                    var res = agent.ModeCommit.ForageTargetResource.Value;
                    // D21 Hotfix 2: Block non-food forage gather at 9+ non-food items
                    if (!ModeTransitionManager.IsFoodResource(res))
                    {
                        int nfCommit = agent.Inventory.GetValueOrDefault(ResourceType.Stone, 0)
                                     + agent.Inventory.GetValueOrDefault(ResourceType.Ore, 0)
                                     + agent.Inventory.GetValueOrDefault(ResourceType.Wood, 0)
                                     + agent.Inventory.GetValueOrDefault(ResourceType.Hide, 0)
                                     + agent.Inventory.GetValueOrDefault(ResourceType.Bone, 0);
                        if (nfCommit >= 9)
                        {
                            agent.ModeCommit.ForageTargetTile = null;
                            agent.ClearGoal();
                            // Fall through — will re-evaluate next tick
                        }
                    }
                    if (agent.ModeCommit.ForageTargetTile.HasValue
                        && currentTile.Resources.TryGetValue(res, out int amt) && amt > 0
                        && TryStartGatherAction(agent, tx, ty, res, currentTick, trace))
                    {
                        LogDecision(currentTick, agent, "Gather", $"{res} at ({tx},{ty})", 1f);
                        agent.RecordAction(ActionType.Gather, currentTick, $"Gathering {res} (forage)");
                        return;
                    }

                    // Try any food if this was a food forage
                    if (ModeTransitionManager.IsFoodResource(res)
                        && TryStartGatherFood(agent, currentTile, world, bus, currentTick, trace))
                    {
                        agent.RecordAction(ActionType.Gather, currentTick, "Gathering food (forage, alternate)");
                        return;
                    }

                    // Resource depleted — clear stale memory and update target
                    agent.ClearStaleMemory(tx, ty, res);
                    agent.ModeCommit.ForageTargetTile = null;
                    agent.ClearGoal();
                    // Will be re-evaluated next tick by ModeTransitionManager
                }
            }
        }

        // Goal Commitment Fix: If we have a committed target and are NOT at it, move toward it.
        // This is a defensive fallback — normally Phase 3 (TryAdvanceGoal) handles movement
        // toward the target, but if Phase 4 runs for any reason while a target is set,
        // we should continue toward it instead of falling through to random search.
        if (agent.ModeCommit.ForageTargetTile.HasValue && agent.GoalTarget.HasValue)
        {
            var (ftx, fty) = agent.ModeCommit.ForageTargetTile.Value;
            if (agent.X != ftx || agent.Y != fty)
            {
                if (TryStartMove(agent, ftx, fty, world, trace))
                {
                    agent.RecordAction(ActionType.Move, currentTick, $"Moving toward forage target ({ftx},{fty})");
                    return;
                }
            }
        }

        // Opportunistic gather: if on a tile with the committed resource, grab it
        if (agent.ModeCommit.ForageTargetResource.HasValue && agent.HasInventorySpace())
        {
            var res = agent.ModeCommit.ForageTargetResource.Value;
            // D21 Hotfix 2: Block non-food forage gather at 9+ non-food items
            bool forageBlocked = false;
            if (!ModeTransitionManager.IsFoodResource(res))
            {
                int nfForage = agent.Inventory.GetValueOrDefault(ResourceType.Stone, 0)
                             + agent.Inventory.GetValueOrDefault(ResourceType.Ore, 0)
                             + agent.Inventory.GetValueOrDefault(ResourceType.Wood, 0)
                             + agent.Inventory.GetValueOrDefault(ResourceType.Hide, 0)
                             + agent.Inventory.GetValueOrDefault(ResourceType.Bone, 0);
                if (nfForage >= 9)
                {
                    agent.ModeCommit.ForageTargetTile = null;
                    agent.ClearGoal();
                    forageBlocked = true;
                }
            }
            if (!forageBlocked && currentTile.Resources.TryGetValue(res, out int curAmt) && curAmt > 0
                && TryStartGatherAction(agent, agent.X, agent.Y, res, currentTick, trace))
            {
                agent.RecordAction(ActionType.Gather, currentTick, $"Gathering {res} (forage, opportunistic)");
                return;
            }
        }

        // No target or depleted: look for new target using memory
        if (!agent.ModeCommit.ForageTargetTile.HasValue && agent.ModeCommit.ForageTargetResource.HasValue)
        {
            var res = agent.ModeCommit.ForageTargetResource.Value;
            bool isFood = ModeTransitionManager.IsFoodResource(res);

            if (isFood)
            {
                var remembered = agent.GetRememberedFood(currentTick);
                if (remembered.Count > 0)
                {
                    // D15 Fix: Cap retarget distance to ForageMaxRange from home to prevent drift.
                    // Without this, agents chain forage targets further and further from home.
                    var candidates = remembered.Where(m =>
                    {
                        if (!agent.HomeTile.HasValue) return true;
                        int dh = Math.Max(Math.Abs(m.X - agent.HomeTile.Value.X),
                                          Math.Abs(m.Y - agent.HomeTile.Value.Y));
                        return dh <= SimConfig.ForageMaxRange;
                    }).ToList();
                    if (candidates.Count > 0)
                    {
                        // Prefer food closer to home (not just closer to agent) to prevent drift
                        var best = candidates.OrderBy(m =>
                        {
                            int distToAgent = Math.Max(Math.Abs(m.X - agent.X), Math.Abs(m.Y - agent.Y));
                            int distToHome = agent.HomeTile.HasValue
                                ? Math.Max(Math.Abs(m.X - agent.HomeTile.Value.X), Math.Abs(m.Y - agent.HomeTile.Value.Y))
                                : 0;
                            return distToAgent + distToHome / 2; // Home-weighted distance
                        }).First();
                        agent.ModeCommit.ForageTargetTile = (best.X, best.Y);
                        agent.CurrentGoal = GoalType.GatherFoodAt;
                        agent.GoalTarget = (best.X, best.Y);
                        agent.GoalResource = best.Resource;
                        agent.GoalStartTick = currentTick;
                    }
                }
            }
            else
            {
                var memories = agent.Memory.Where(m =>
                    m.Type == MemoryType.Resource && m.Resource == res && m.Quantity > 0
                    && currentTick - m.TickObserved <= SimConfig.MemoryDecayTicks
                    // D15 Fix: Cap retarget distance to ForageMaxRange from home
                    && (!agent.HomeTile.HasValue || Math.Max(
                        Math.Abs(m.X - agent.HomeTile.Value.X),
                        Math.Abs(m.Y - agent.HomeTile.Value.Y)) <= SimConfig.ForageMaxRange)
                ).ToList();
                if (memories.Count > 0)
                {
                    // Prefer resources closer to home to prevent drift
                    var best = memories.OrderBy(m =>
                    {
                        int distToAgent = Math.Max(Math.Abs(m.X - agent.X), Math.Abs(m.Y - agent.Y));
                        int distToHome = agent.HomeTile.HasValue
                            ? Math.Max(Math.Abs(m.X - agent.HomeTile.Value.X), Math.Abs(m.Y - agent.HomeTile.Value.Y))
                            : 0;
                        return distToAgent + distToHome / 2;
                    }).First();
                    agent.ModeCommit.ForageTargetTile = (best.X, best.Y);
                    agent.CurrentGoal = GoalType.GatherResourceAt;
                    agent.GoalTarget = (best.X, best.Y);
                    agent.GoalResource = res;
                    agent.GoalStartTick = currentTick;
                }
            }

            // If we found a new target, move toward it via goal system next tick
            if (agent.ModeCommit.ForageTargetTile.HasValue && agent.GoalTarget.HasValue)
            {
                if (TryStartMove(agent, agent.GoalTarget.Value.X, agent.GoalTarget.Value.Y, world, trace))
                {
                    agent.RecordAction(ActionType.Move, currentTick, "Moving to forage target");
                    return;
                }
            }
        }

        // D25b: Before spiral search, try hunting/harvesting if hungry and foraging for food
        if (agent.Stage == DevelopmentStage.Adult && agent.Hunger < 80f
            && agent.ModeCommit.ForageTargetResource.HasValue
            && ModeTransitionManager.IsFoodResource(agent.ModeCommit.ForageTargetResource.Value))
        {
            // Prefer carcasses (free food, no combat)
            var carcasses = agent.GetRememberedCarcasses(currentTick);
            if (carcasses.Count > 0)
            {
                var best = carcasses.OrderBy(c => Math.Abs(c.X - agent.X) + Math.Abs(c.Y - agent.Y)).First();
                var scored = new ScoredAction { Action = ActionType.Harvest, Score = 1f,
                    TargetTile = (best.X, best.Y), TargetResource = ResourceType.Meat };
                if (TryDispatchHarvest(agent, scored, world, bus, currentTick, trace))
                {
                    agent.RecordAction(ActionType.Harvest, currentTick, "Harvesting carcass (forage)");
                    return;
                }
            }

            // Try hunting nearby animals
            var huntable = agent.GetRememberedHuntableAnimals(currentTick);
            if (huntable.Count > 0)
            {
                var nearest = huntable.OrderBy(h => Math.Abs(h.X - agent.X) + Math.Abs(h.Y - agent.Y)).First();
                int dist = Math.Abs(nearest.X - agent.X) + Math.Abs(nearest.Y - agent.Y);
                if (dist <= SimConfig.HuntMaxDistanceFromHome)
                {
                    var scored = new ScoredAction { Action = ActionType.Hunt, Score = 1f,
                        TargetTile = (nearest.X, nearest.Y), TargetResource = ResourceType.Meat };
                    if (TryDispatchHunt(agent, scored, world, bus, currentTick, trace))
                    {
                        agent.RecordAction(ActionType.Hunt, currentTick, "Hunting animal (forage)");
                        return;
                    }
                }
            }
        }

        // Fix 3c: Nothing to gather and no target found — spiral outward from home to find food
        StartForageSearch(agent, world, bus, currentTick, trace);
        // Only record Explore if StartForageSearch actually started a move
        if (agent.IsBusy)
            agent.RecordAction(ActionType.Explore, currentTick, "Searching for food (forage spiral)");
        else
        {
            agent.CurrentAction = ActionType.Idle;
            agent.RecordAction(ActionType.Idle, currentTick, "Idle (no forage target found)");
        }
    }

    /// <summary>Fix 3c: Forage-specific search — spirals outward from home, bounded by ForageMaxRange.
    /// Unlike Explore mode's novelty-driven wander, this is a targeted food search.</summary>
    private void StartForageSearch(Agent agent, World world, EventBus bus, int currentTick, Action<string>? trace)
    {
        int anchorX = agent.X, anchorY = agent.Y;
        if (agent.HomeTile.HasValue)
        {
            anchorX = agent.HomeTile.Value.X;
            anchorY = agent.HomeTile.Value.Y;
        }

        // Search expanding rings from agent's current position, biased toward home anchor
        var candidates = new List<(int x, int y, float score)>();

        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;

                int newX = agent.X + dx;
                int newY = agent.Y + dy;

                if (!world.IsInBounds(newX, newY)) continue;

                var tile = world.GetTile(newX, newY);
                if (float.IsPositiveInfinity(tile.MovementCostMultiplier)) continue;

                float score = 1.0f;

                // Strong bias toward tiles with visible food
                foreach (var food in new[] { ResourceType.Berries, ResourceType.Grain, ResourceType.Meat, ResourceType.Fish })
                {
                    if (tile.Resources.TryGetValue(food, out int amt) && amt > 0)
                    {
                        score += 10.0f; // Immediate food is highly desirable
                        break;
                    }
                }

                // Prefer food-rich biomes (forest/plains) over barren ones
                switch (tile.Biome)
                {
                    case BiomeType.Plains:
                    case BiomeType.Forest:
                        score += 2.0f;
                        break;
                    case BiomeType.Mountain:
                    case BiomeType.Desert:
                        score = Math.Max(0.1f, score - 1.0f);
                        break;
                }

                // Home distance penalty: don't drift beyond ForageMaxRange
                int distFromHome = agent.HomeTile.HasValue
                    ? Math.Max(Math.Abs(newX - anchorX), Math.Abs(newY - anchorY))
                    : 0;
                if (distFromHome > SimConfig.ForageMaxRange)
                {
                    continue; // D15 Fix: Hard block — don't forage-search beyond max range
                }
                else
                {
                    // Slight preference toward home direction when far out
                    int currentHomeDist = agent.HomeTile.HasValue
                        ? Math.Max(Math.Abs(agent.X - anchorX), Math.Abs(agent.Y - anchorY))
                        : 0;
                    if (distFromHome < currentHomeDist && currentHomeDist > SimConfig.HomeGatherRadius)
                        score += 0.5f; // Small pull toward home when distant
                }

                // Novelty: avoid recently-visited tiles
                bool recentlyVisited = agent.Memory.Any(m =>
                    m.X == newX && m.Y == newY
                    && currentTick - m.TickObserved <= 30);
                if (!recentlyVisited)
                    score += 1.5f; // Stronger novelty preference for searching

                // Food memory bias: if moving toward remembered food, strong boost
                var rememberedFood = agent.GetRememberedFood(currentTick);
                foreach (var foodMem in rememberedFood)
                {
                    int currentDist = Math.Max(Math.Abs(foodMem.X - agent.X), Math.Abs(foodMem.Y - agent.Y));
                    int newDist = Math.Max(Math.Abs(foodMem.X - newX), Math.Abs(foodMem.Y - newY));
                    if (newDist < currentDist)
                    {
                        score += 4.0f;
                        break;
                    }
                }

                candidates.Add((newX, newY, score));
            }
        }

        if (candidates.Count == 0)
        {
            // Completely stuck — rest in place
            agent.PendingAction = ActionType.Rest;
            agent.ActionProgress = 0f;
            agent.ActionDurationTicks = 5;
            agent.CurrentAction = ActionType.Rest;
            return;
        }

        // Pick best candidate (with slight randomness to avoid deterministic oscillation)
        candidates.Sort((a, b) => b.score.CompareTo(a.score));
        // Among top candidates within 80% of best score, pick randomly
        float bestScore = candidates[0].score;
        float threshold = bestScore * 0.8f;
        var topCandidates = candidates.Where(c => c.score >= threshold).ToList();
        var rng = new Random(currentTick + agent.Id);
        var chosen = topCandidates[rng.Next(topCandidates.Count)];

        // D20 Fix 2: Set a GatherFoodAt goal in the chosen direction, extended 3 tiles.
        // This commits the agent to a search direction for multiple ticks instead of
        // re-evaluating every tick (which causes goalless Move oscillation).
        // Uses GatherFoodAt (not SeekFood) because SeekFood is exempt from hunger interrupts
        // and was designed for "walking home to eat", not wilderness food searching.
        int dirX = chosen.x - agent.X;
        int dirY = chosen.y - agent.Y;
        int searchDist = 3;
        int goalX = agent.X + dirX * searchDist;
        int goalY = agent.Y + dirY * searchDist;
        // Clamp to world bounds and ForageMaxRange
        goalX = Math.Clamp(goalX, 2, world.Width - 3);
        goalY = Math.Clamp(goalY, 2, world.Height - 3);
        // D24 Fix 2: If computed goal is on impassable tile (water), fall back to chosen candidate
        var goalTile = world.GetTile(goalX, goalY);
        if (float.IsPositiveInfinity(goalTile.MovementCostMultiplier))
        {
            goalX = chosen.x;
            goalY = chosen.y;
        }
        if (agent.HomeTile.HasValue)
        {
            int distFromHome = Math.Max(
                Math.Abs(goalX - agent.HomeTile.Value.X),
                Math.Abs(goalY - agent.HomeTile.Value.Y));
            if (distFromHome > SimConfig.ForageMaxRange)
            {
                // Reduce search distance to stay within range
                goalX = chosen.x;
                goalY = chosen.y;
            }
        }

        agent.ModeCommit.ForageTargetTile = (goalX, goalY);
        agent.CurrentGoal = GoalType.GatherFoodAt;
        agent.GoalTarget = (goalX, goalY);
        agent.GoalStartTick = currentTick;

        if (TryStartMove(agent, chosen.x, chosen.y, world, trace))
        {
            trace?.Invoke($"[TRACE Agent {agent.Id}] FORAGE SEARCH: moving to ({chosen.x},{chosen.y}) toward search goal ({goalX},{goalY}) score={chosen.score:F1}");
        }
        else
        {
            // Move failed — clear the search goal and idle
            agent.ModeCommit.ForageTargetTile = null;
            agent.ClearGoal();
            agent.CurrentAction = ActionType.Idle;
        }
    }

    /// <summary>v1.8: Build mode — sustained construction at committed build site.</summary>
    private void DecideBuild(Agent agent, World world, EventBus bus, int currentTick, Action<string>? trace)
    {
        if (!agent.ModeCommit.BuildTargetTile.HasValue || agent.ModeCommit.BuildRecipeId == null)
        {
            // No committed project — shouldn't be in Build mode, transition Home
            agent.TransitionMode(BehaviorMode.Home, currentTick);
            return;
        }

        var (bx, by) = agent.ModeCommit.BuildTargetTile.Value;
        var currentTile = world.GetTile(agent.X, agent.Y);

        // Eat if moderately hungry
        if (agent.Hunger <= 60f && agent.FoodInInventory() > 0)
        {
            if (TryEatOrCook(agent, currentTile, bus, currentTick, trace))
                return;
        }

        // At build site — build
        if (agent.X == bx && agent.Y == by)
        {
            var buildTile = world.GetTile(bx, by);
            var recipeId = agent.ModeCommit.BuildRecipeId;

            // Directive: Withdraw materials from home material storage if needed
            if (buildTile.HomeTotalMaterials > 0)
            {
                int neededWood = 0, neededStone = 0;
                if (recipeId == "lean_to") { neededWood = SimConfig.ShelterWoodCost; neededStone = SimConfig.ShelterStoneCost; }
                else if (recipeId == "granary" || recipeId == "reinforced_shelter") { neededWood = SimConfig.GranaryWoodCost; neededStone = SimConfig.GranaryStoneCost; }
                else if (recipeId == "campfire") { neededWood = 2; neededStone = 1; }
                else if (recipeId == "animal_pen") { neededWood = 5; neededStone = 2; }

                int haveWood = agent.Inventory.GetValueOrDefault(ResourceType.Wood, 0);
                int haveStone = agent.Inventory.GetValueOrDefault(ResourceType.Stone, 0);

                if (haveWood < neededWood)
                {
                    int withdrawn = buildTile.WithdrawMaterialFromHome(ResourceType.Wood, neededWood - haveWood);
                    // Direct add — original v1.8 code had no capacity check for build material withdrawal
                    if (withdrawn > 0)
                        agent.Inventory[ResourceType.Wood] = agent.Inventory.GetValueOrDefault(ResourceType.Wood, 0) + withdrawn;
                }
                if (haveStone < neededStone)
                {
                    int withdrawn = buildTile.WithdrawMaterialFromHome(ResourceType.Stone, neededStone - haveStone);
                    // Direct add — original v1.8 code had no capacity check for build material withdrawal
                    if (withdrawn > 0)
                        agent.Inventory[ResourceType.Stone] = agent.Inventory.GetValueOrDefault(ResourceType.Stone, 0) + withdrawn;
                }
            }

            int w = agent.Inventory.GetValueOrDefault(ResourceType.Wood, 0);
            int s = agent.Inventory.GetValueOrDefault(ResourceType.Stone, 0);

            if (recipeId == "lean_to" && !buildTile.HasShelter)
            {
                if (w >= SimConfig.ShelterWoodCost && s >= SimConfig.ShelterStoneCost)
                {
                    StartBuild(agent, buildTile, "lean_to", SimConfig.ShelterBuildTicks, trace);
                    LogDecision(currentTick, agent, "Build", $"lean_to at ({bx},{by})", 1f);
                    agent.RecordAction(ActionType.Build, currentTick, $"Building lean-to at ({bx},{by})");
                    return;
                }
            }
            else if (recipeId == "reinforced_shelter" && buildTile.HasShelter && !buildTile.Structures.Contains("improved_shelter"))
            {
                if (w >= SimConfig.GranaryWoodCost && s >= SimConfig.GranaryStoneCost)
                {
                    agent.Inventory[ResourceType.Wood] = w - SimConfig.GranaryWoodCost;
                    agent.Inventory[ResourceType.Stone] = s - SimConfig.GranaryStoneCost;
                    StartBuild(agent, buildTile, "improved_shelter", SimConfig.GranaryBuildTicks, trace);
                    LogDecision(currentTick, agent, "Build", $"improved_shelter at ({bx},{by})", 1f);
                    agent.RecordAction(ActionType.Build, currentTick, $"Upgrading shelter at ({bx},{by})");
                    return;
                }
            }
            else if (recipeId == "granary" && buildTile.HasShelter && !buildTile.HasGranary)
            {
                if (w >= SimConfig.GranaryWoodCost && s >= SimConfig.GranaryStoneCost)
                {
                    StartBuild(agent, buildTile, "granary", SimConfig.GranaryBuildTicks, trace);
                    LogDecision(currentTick, agent, "Build", $"granary at ({bx},{by})", 1f);
                    agent.RecordAction(ActionType.Build, currentTick, $"Building granary at ({bx},{by})");
                    return;
                }
            }
            else if (recipeId == "campfire" && !buildTile.Structures.Contains("campfire"))
            {
                if (w >= 2 && s >= 1)
                {
                    agent.Inventory[ResourceType.Wood] = w - 2;
                    agent.Inventory[ResourceType.Stone] = s - 1;
                    StartBuild(agent, buildTile, "campfire", SimConfig.ShelterBuildTicks / 2, trace);
                    LogDecision(currentTick, agent, "Build", $"campfire at ({bx},{by})", 1f);
                    agent.RecordAction(ActionType.Build, currentTick, $"Building campfire at ({bx},{by})");
                    return;
                }
            }
            else if (recipeId == "animal_pen" && !buildTile.Structures.Contains("animal_pen"))
            {
                if (w >= 5 && s >= 2)
                {
                    agent.Inventory[ResourceType.Wood] = w - 5;
                    agent.Inventory[ResourceType.Stone] = s - 2;
                    StartBuild(agent, buildTile, "animal_pen", SimConfig.PenBuildDuration, trace);
                    LogDecision(currentTick, agent, "Build", $"animal_pen at ({bx},{by})", 1f);
                    agent.RecordAction(ActionType.Build, currentTick, $"Building animal pen at ({bx},{by})");
                    return;
                }
            }

            // Can't build (maybe already built?) — ModeTransitionManager will handle exit next tick
            agent.CurrentAction = ActionType.Idle;
            return;
        }

        // Not at build site — move there
        if (TryStartMove(agent, bx, by, world, trace))
        {
            agent.RecordAction(ActionType.Move, currentTick, $"Moving to build site ({bx},{by})");
            return;
        }

        agent.CurrentAction = ActionType.Idle;
    }

    /// <summary>v1.8: Explore mode — committed direction scouting.</summary>
    private void DecideExplore(Agent agent, World world, EventBus bus, int currentTick, Action<string>? trace)
    {
        var currentTile = world.GetTile(agent.X, agent.Y);

        // Return-path validation: every 5 tiles of Chebyshev distance, verify we can get home
        if (agent.HomeTile.HasValue)
        {
            var (hx, hy) = agent.HomeTile.Value;
            int dist = Math.Max(Math.Abs(agent.X - hx), Math.Abs(agent.Y - hy));
            int milestone = (dist / 5) * 5;
            if (milestone > 0 && milestone > agent.LastReturnPathCheckDistance)
            {
                var returnPath = SimplePathfinder.FindPath(agent.X, agent.Y, hx, hy, world);
                if (returnPath == null)
                {
                    // No return path — abort explore, transition to Home mode, start heading back
                    agent.TransitionMode(BehaviorMode.Home, currentTick);
                    agent.CurrentGoal = GoalType.ReturnHome;
                    agent.GoalTarget = agent.HomeTile;
                    agent.GoalStartTick = currentTick;

                    // Greedy step: pick passable neighbor closest to home
                    int bestX = agent.X, bestY = agent.Y;
                    int bestDist = int.MaxValue;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            int nx = agent.X + dx;
                            int ny = agent.Y + dy;
                            if (!world.IsInBounds(nx, ny)) continue;
                            var tile = world.GetTile(nx, ny);
                            if (float.IsPositiveInfinity(tile.MovementCostMultiplier)) continue;
                            int d = Math.Max(Math.Abs(nx - hx), Math.Abs(ny - hy));
                            if (d < bestDist)
                            {
                                bestDist = d;
                                bestX = nx;
                                bestY = ny;
                            }
                        }
                    }
                    if (bestX != agent.X || bestY != agent.Y)
                    {
                        TryStartMove(agent, bestX, bestY, world, trace);
                    }
                    agent.RecordAction(ActionType.Move, currentTick, "Return path blocked — aborting explore");
                    return;
                }
                agent.LastReturnPathCheckDistance = milestone;
            }
        }

        // Post-Playtest Fix 3: Single rest covers remaining night — explorers rest wherever they are
        if (Agent.IsNightTime(currentTick))
        {
            agent.PendingAction = ActionType.Rest;
            agent.ActionProgress = 0f;
            agent.ActionDurationTicks = Math.Max(CalculateRemainingNightTicks(currentTick), 30);
            agent.CurrentAction = ActionType.Rest;
            agent.LastRestTick = currentTick;
            agent.RecordAction(ActionType.Rest, currentTick, "Resting while exploring (night)");
            return;
        }

        // Eat from inventory if getting hungry
        if (agent.Hunger <= 75f && agent.FoodInInventory() > 0)
        {
            if (agent.Eat())
            {
                agent.RecordAction(ActionType.Eat, currentTick, $"Ate food while exploring (hunger: {agent.Hunger:F0})");
                return;
            }
        }

        // Opportunistic gather — if on a tile with resources and have space, grab it (no detour)
        if (agent.HasInventorySpace())
        {
            foreach (var kvp in currentTile.Resources)
            {
                if (kvp.Value > 0 && TryStartGatherAction(agent, agent.X, agent.Y, kvp.Key, currentTick, trace))
                {
                    agent.RecordAction(ActionType.Gather, currentTick, $"Opportunistic gather {kvp.Key} (explore)");
                    return;
                }
            }
        }

        // Move in committed direction
        if (agent.ModeCommit.ExploreDirection.HasValue)
        {
            var dir = agent.ModeCommit.ExploreDirection.Value;
            int targetX = agent.X + dir.Dx;
            int targetY = agent.Y + dir.Dy;

            // Bug 2 fix: Detect when committed direction leads into water.
            // The direction was validated 10 tiles out at trip start, but the agent
            // may hit water earlier along the path. If the committed direction is
            // blocked by water for 3+ consecutive ticks, abort explore — the trip
            // is pointless, the agent would just oscillate along the water edge.
            bool committedBlockedByWater = false;
            if (!world.IsInBounds(targetX, targetY)
                || world.GetTile(targetX, targetY).Biome == BiomeType.Water)
            {
                committedBlockedByWater = true;
            }

            if (!committedBlockedByWater
                && !float.IsPositiveInfinity(world.GetTile(targetX, targetY).MovementCostMultiplier))
            {
                if (TryStartMove(agent, targetX, targetY, world, trace))
                {
                    agent.CurrentAction = ActionType.Explore;
                    agent.ExploreStuckTicks = 0;
                    agent.ExploreWaterBlockedTicks = 0;
                    agent.RecordAction(ActionType.Explore, currentTick, "Exploring (committed direction)");
                    return;
                }
            }

            // Bug 2 fix: If committed direction is water-blocked, track consecutive occurrences
            if (committedBlockedByWater)
            {
                agent.ExploreWaterBlockedTicks++;
                if (agent.ExploreWaterBlockedTicks >= 3)
                {
                    trace?.Invoke($"[TRACE Agent {agent.Id}] EXPLORE-WATER-BLOCKED: committed direction ({dir.Dx},{dir.Dy}) blocked by water {agent.ExploreWaterBlockedTicks} ticks — aborting explore");
                    agent.ExploreWaterBlockedTicks = 0;
                    agent.TransitionMode(BehaviorMode.Home, currentTick);
                    if (agent.HomeTile.HasValue)
                    {
                        agent.CurrentGoal = GoalType.ReturnHome;
                        agent.GoalTarget = agent.HomeTile;
                        agent.GoalStartTick = currentTick;
                    }
                    agent.RecordAction(ActionType.Idle, currentTick, "Explore direction blocked by water — returning home");
                    return;
                }
            }

            // Direction blocked — try adjacent directions
            (int, int)[] alternates = { (dir.Dx, 0), (0, dir.Dy), (-dir.Dy, dir.Dx), (dir.Dy, -dir.Dx) };
            foreach (var (adx, ady) in alternates)
            {
                if (adx == 0 && ady == 0) continue;
                int ax = agent.X + adx, ay = agent.Y + ady;
                if (world.IsInBounds(ax, ay)
                    && !float.IsPositiveInfinity(world.GetTile(ax, ay).MovementCostMultiplier)
                    && TryStartMove(agent, ax, ay, world, trace))
                {
                    agent.CurrentAction = ActionType.Explore;
                    agent.ExploreStuckTicks = 0;
                    agent.RecordAction(ActionType.Explore, currentTick, "Exploring (alternate direction)");
                    return;
                }
            }
        }

        // Fallback: use weighted explore
        StartExplore(agent, world, bus, currentTick, trace);

        // Fix 5e: Stuck detection — if agent couldn't move at all, increment stuck counter
        if (agent.CurrentAction == ActionType.Idle)
        {
            agent.ExploreStuckTicks++;

            if (agent.ExploreStuckTicks >= 3)
            {
                agent.BlacklistPosition(agent.X, agent.Y, currentTick + 5000);
                trace?.Invoke($"[TRACE Agent {agent.Id}] EXPLORE-STUCK: blacklisted ({agent.X},{agent.Y}) after {agent.ExploreStuckTicks} stuck ticks");

                // Force transition back to Home — agent is trapped
                agent.TransitionMode(BehaviorMode.Home, currentTick);
                if (agent.HomeTile.HasValue)
                {
                    agent.CurrentGoal = GoalType.ReturnHome;
                    agent.GoalTarget = agent.HomeTile;
                    agent.GoalStartTick = currentTick;
                }
                agent.ExploreStuckTicks = 0;
                agent.RecordAction(ActionType.Idle, currentTick, "Explore stuck — returning home");
                return;
            }
        }
        else
        {
            agent.ExploreStuckTicks = 0;
            agent.RecordAction(ActionType.Explore, currentTick, "Exploring (fallback)");
        }
    }

    /// <summary>v1.8: Caretaker mode — proactive child feeding, radius-constrained decisions.</summary>
    private void DecideCaretaker(Agent agent, World world, EventBus bus, int currentTick,
        List<Agent>? allAgents, Action<string>? trace)
    {
        var currentTile = world.GetTile(agent.X, agent.Y);
        int food = agent.FoodInInventory();
        int homeX = agent.HomeTile?.X ?? agent.X;
        int homeY = agent.HomeTile?.Y ?? agent.Y;
        int maxRange = SimConfig.CaretakerForageRange;

        // ── SAFETY TETHER: if somehow outside radius, go home (goal-based, walks all the way) ──
        if (agent.HomeTile.HasValue)
        {
            int distFromHome = Math.Abs(agent.X - homeX) + Math.Abs(agent.Y - homeY);
            if (distFromHome > maxRange)
            {
                agent.CurrentGoal = GoalType.ReturnHome;
                agent.GoalTarget = agent.HomeTile;
                agent.GoalStartTick = currentTick;
                if (TryStartMove(agent, homeX, homeY, world, trace))
                {
                    LogDecision(currentTick, agent, "Move", "return-home(caretaker tether)", 100f);
                    agent.RecordAction(ActionType.Move, currentTick,
                        $"Returning home (caretaker tether, dist={distFromHome})");
                    return;
                }
            }
        }

        // ── PROACTIVE CHILD FEEDING: at/near home with food, feed child if below 70 hunger ──
        if (allAgents != null && agent.HomeTile.HasValue
            && agent.X == homeX && agent.Y == homeY)
        {
            if (food > 0)
            {
                foreach (var kvp in agent.Relationships)
                {
                    if (kvp.Value != RelationshipType.Child) continue;
                    var child = allAgents.FirstOrDefault(a => a.Id == kvp.Key);
                    if (child == null || !child.IsAlive) continue;
                    // Fix 3: Broadened from same-tile to adjacent-tile (Manhattan dist <= 1)
                    int childDist = Math.Abs(child.X - agent.X) + Math.Abs(child.Y - agent.Y);
                    if (child.Hunger < 70f && childDist <= 1)
                    {
                        TryFeedNearbyChild(agent, world, bus, currentTick, trace);
                        food = agent.FoodInInventory();
                        break;
                    }
                }
            }

            // Deposit food to home storage for infant
            // When child is hungry: deposit aggressively (keep only 1 for self)
            // Normal: deposit surplus (keep 2 for self)
            bool childHungryForDeposit = false;
            if (allAgents != null)
            {
                foreach (var kvp in agent.Relationships)
                {
                    if (kvp.Value != RelationshipType.Child) continue;
                    var child = allAgents.FirstOrDefault(a => a.Id == kvp.Key);
                    if (child != null && child.IsAlive && child.Stage != DevelopmentStage.Adult
                        && child.Hunger < 50f)
                    {
                        childHungryForDeposit = true;
                        break;
                    }
                }
            }
            int depositThreshold = childHungryForDeposit ? 1 : SimConfig.DepositHomeFoodThreshold;
            int keepForSelf = childHungryForDeposit ? 1 : 2;
            if (food > depositThreshold && currentTile.HasHomeStorage
                && currentTile.HomeTotalFood < currentTile.HomeStorageCapacity)
            {
                int toDeposit = food - keepForSelf;
                int totalDeposited = 0;
                foreach (var res in agent.Inventory.Keys.ToList())
                {
                    if (!ModeTransitionManager.IsFoodResource(res)) continue;
                    int amt = Math.Min(agent.Inventory[res], toDeposit);
                    if (amt > 0)
                    {
                        int deposited = currentTile.DepositToHome(res, amt);
                        if (deposited > 0)
                        {
                            agent.Inventory[res] -= deposited;
                            if (agent.Inventory[res] <= 0) agent.Inventory.Remove(res);
                            toDeposit -= deposited;
                            totalDeposited += deposited;
                        }
                    }
                    if (toDeposit <= 0) break;
                }
                if (totalDeposited > 0)
                {
                    agent.RecordAction(ActionType.DepositHome, currentTick, $"Deposited {totalDeposited} food for child");
                }
            }
        }

        // ── RUSH HOME: if child is hungry (< 70) and we're carrying food to deliver ──
        if (allAgents != null && agent.HomeTile.HasValue
            && (agent.X != homeX || agent.Y != homeY)
            && agent.FoodInInventory() >= 3)
        {
            foreach (var kvp in agent.Relationships)
            {
                if (kvp.Value != RelationshipType.Child) continue;
                var child = allAgents.FirstOrDefault(a => a.Id == kvp.Key);
                if (child == null || !child.IsAlive || child.Hunger >= 70f) continue;

                agent.CurrentGoal = GoalType.ReturnHome;
                agent.GoalTarget = agent.HomeTile;
                agent.GoalStartTick = currentTick;
                if (TryStartMove(agent, homeX, homeY, world, trace))
                {
                    LogDecision(currentTick, agent, "Move", "rush-home(child hungry)", 95f);
                    agent.RecordAction(ActionType.Move, currentTick,
                        $"Rushing home to feed child (child hunger={child.Hunger:F0})");
                    return;
                }
            }
        }

        // ── Post-Playtest Fix 3: Single night rest covers remaining night ──
        if (Agent.IsNightTime(currentTick) && agent.Hunger > 40f)
        {
            // If at home (or no home), rest immediately
            bool atHome = !agent.HomeTile.HasValue
                || (agent.X == homeX && agent.Y == homeY);
            if (atHome)
            {
                LogDecision(currentTick, agent, "Rest", "night(caretaker)", 75f);
                agent.PendingAction = ActionType.Rest;
                agent.ActionProgress = 0f;
                agent.ActionDurationTicks = Math.Max(CalculateRemainingNightTicks(currentTick), 30);
                agent.CurrentAction = ActionType.Rest;
                agent.LastRestTick = currentTick;
                agent.RecordAction(ActionType.Rest, currentTick, "Resting (caretaker, night)");
                return;
            }
            // Away from home — go home first, rest there
            agent.CurrentGoal = GoalType.ReturnHome;
            agent.GoalTarget = agent.HomeTile;
            agent.GoalStartTick = currentTick;
            if (TryStartMove(agent, homeX, homeY, world, trace))
            {
                agent.RecordAction(ActionType.Move, currentTick, "Returning home to rest (caretaker)");
                return;
            }
        }

        // ── CARETAKER EAT: parents sacrifice comfort for children but not survival ──
        // Check if ANY dependent child is hungry, regardless of distance (parent knows their child)
        bool childNeedsFoodAnywhere = false;
        if (allAgents != null)
        {
            foreach (var kvp in agent.Relationships)
            {
                if (kvp.Value != RelationshipType.Child) continue;
                var child = allAgents.FirstOrDefault(a => a.Id == kvp.Key);
                if (child != null && child.IsAlive && child.Stage != DevelopmentStage.Adult
                    && child.Hunger < 50f)
                {
                    childNeedsFoodAnywhere = true;
                    break;
                }
            }
        }

        if (childNeedsFoodAnywhere)
        {
            // Child is hungry — parent only eats if in survival danger (hunger < 30)
            // Otherwise save the food to bring home
            if (agent.Hunger < 30f && food > 0)
            {
                if (TryEatOrCook(agent, currentTile, bus, currentTick, trace))
                    return;
            }
            // Don't withdraw from home storage either — leave it for the child
        }
        else
        {
            // No hungry child — normal eat logic
            bool caretakerChildHungrier = HasHungrierChild(agent, allAgents);
            if (agent.Hunger <= 60f && food > 0 && !caretakerChildHungrier)
            {
                if (TryEatOrCook(agent, currentTile, bus, currentTick, trace))
                    return;
            }
            if (agent.Hunger <= 60f && food <= 0 && !caretakerChildHungrier
                && agent.HomeTile.HasValue && agent.X == homeX && agent.Y == homeY)
            {
                if (agent.TryEatFromHomeStorage(currentTile))
                {
                    agent.RecordAction(ActionType.Eat, currentTick, $"Ate from home storage (hunger: {agent.Hunger:F0})");
                    return;
                }
            }
        }

        // ── Directive #7 Fix 4: Check if child is fed (hunger > 60) to unlock productive actions ──
        bool childFed = true; // Default true if no children
        if (allAgents != null)
        {
            foreach (var kvp in agent.Relationships)
            {
                if (kvp.Value != RelationshipType.Child) continue;
                var child = allAgents.FirstOrDefault(a => a.Id == kvp.Key);
                if (child != null && child.IsAlive && child.Hunger < 60f)
                {
                    childFed = false;
                    break;
                }
            }
        }

        // ── RADIUS-CONSTRAINED SCORING: score home actions, filter out-of-range targets ──
        var scoredActions = UtilityScorer.ScoreHomeActions(agent, world, currentTick, random,
            allAgents, _currentKnowledgeSystem, _currentSettlements, trace);

        // Directive #7 Fix 4: When child is fed, ensure productive actions have minimum scores
        // so they compete with Socialize (which scores 0.005 on cooldown)
        if (childFed)
        {
            for (int i = 0; i < scoredActions.Count; i++)
            {
                var sa = scoredActions[i];
                bool isProductive = sa.Action == ActionType.Experiment
                    || sa.Action == ActionType.Build
                    || sa.Action == ActionType.Craft;
                if (isProductive)
                {
                    // Ensure minimum score of 0.10 at 60% of Home mode base
                    float minScore = 0.10f;
                    // Build at home gets higher minimum
                    if (sa.Action == ActionType.Build && sa.TargetTile.HasValue
                        && agent.HomeTile.HasValue
                        && Math.Max(Math.Abs(sa.TargetTile.Value.X - homeX),
                            Math.Abs(sa.TargetTile.Value.Y - homeY)) <= 1)
                        minScore = 0.15f;

                    if (sa.Score > 0 && sa.Score < minScore)
                    {
                        sa.Score = minScore;
                        scoredActions[i] = sa;
                    }
                }

                // D25d: Boost FeedPen in Caretaker — animals are dependents too
                if (sa.Action == ActionType.FeedPen && sa.Score > 0)
                {
                    float boosted = Math.Max(sa.Score, SimConfig.FeedPenCaretakerScore);
                    sa.Score = boosted;
                    scoredActions[i] = sa;
                }
            }

            // D25d: If no FeedPen entry but agent has any food and a hungry pen exists, inject one
            // This covers agents with berries/meat but no grain (scorer is grain-only for determinism)
            if (agent.Knowledge.Contains("animal_domestication")
                && !scoredActions.Any(s => s.Action == ActionType.FeedPen)
                && agent.FoodInInventory() > 0)
            {
                foreach (var pen in world.Pens)
                {
                    if (!pen.IsActive || pen.AnimalCount == 0) continue;
                    float ratio = (float)pen.FoodStore / pen.MaxFoodStore;
                    if (ratio >= 0.50f) continue;
                    int dist = Math.Max(Math.Abs(pen.TileX - homeX), Math.Abs(pen.TileY - homeY));
                    if (dist <= SimConfig.PenMaxDistFromHome)
                    {
                        scoredActions.Add(new ScoredAction
                        {
                            Action = ActionType.FeedPen,
                            Score = SimConfig.FeedPenCaretakerScore,
                            TargetTile = (pen.TileX, pen.TileY)
                        });
                        break;
                    }
                }
            }
        }
        else
        {
            // Child hungry — suppress productive actions so caretaker focuses on feeding
            for (int i = scoredActions.Count - 1; i >= 0; i--)
            {
                var sa = scoredActions[i];
                if (sa.Action == ActionType.Experiment
                    || sa.Action == ActionType.Build
                    || sa.Action == ActionType.Craft)
                    scoredActions.RemoveAt(i);
            }
        }

        foreach (var candidate in scoredActions)
        {
            // Block any action targeting a tile outside caretaker radius
            if (candidate.TargetTile.HasValue && agent.HomeTile.HasValue)
            {
                int targetDist = Math.Abs(candidate.TargetTile.Value.X - homeX)
                    + Math.Abs(candidate.TargetTile.Value.Y - homeY);
                if (targetDist > maxRange)
                    continue; // Skip — outside caretaker radius
            }

            // Build triggers Build mode (same as DecideHome) — only at home in Caretaker
            if (candidate.Action == ActionType.Build && candidate.TargetTile.HasValue && candidate.TargetRecipeId != null)
            {
                // Caretaker: only allow building at home or adjacent tiles
                if (agent.HomeTile.HasValue)
                {
                    int buildDist = Math.Max(
                        Math.Abs(candidate.TargetTile.Value.X - homeX),
                        Math.Abs(candidate.TargetTile.Value.Y - homeY));
                    if (buildDist > 1)
                        continue; // Skip builds far from home
                }
                ModeTransitionManager.TransitionToBuild(agent, currentTick,
                    candidate.TargetRecipeId, candidate.TargetTile.Value);
                DecideBuild(agent, world, bus, currentTick, trace);
                return;
            }

            // Gather on remote tile (> 2 tiles) → Forage (range already checked above)
            if (candidate.Action == ActionType.Gather && candidate.TargetTile.HasValue
                && Math.Max(Math.Abs(candidate.TargetTile.Value.X - agent.X),
                    Math.Abs(candidate.TargetTile.Value.Y - agent.Y)) > 2)
            {
                var res = candidate.TargetResource ?? ResourceType.Berries;
                bool isFood = ModeTransitionManager.IsFoodResource(res);
                agent.TransitionMode(BehaviorMode.Forage, currentTick);
                agent.ModeCommit.ForageTargetResource = res;
                agent.ModeCommit.ForageTargetTile = candidate.TargetTile;
                agent.ModeCommit.ForageReturnFoodThreshold = SimConfig.ForageReturnFoodCaretaker;
                agent.CurrentGoal = isFood ? GoalType.GatherFoodAt : GoalType.GatherResourceAt;
                agent.GoalTarget = candidate.TargetTile;
                agent.GoalResource = res;
                agent.GoalStartTick = currentTick;
                trace?.Invoke($"[TRACE Agent {agent.Id}] CARETAKER→FORAGE: {res} at ({candidate.TargetTile.Value.X},{candidate.TargetTile.Value.Y})");
                DecideForage(agent, world, bus, currentTick, trace);
                return;
            }

            if (TryDispatchUtilityAction(agent, candidate, world, bus, currentTick, trace))
            {
                LogScoredDecision(currentTick, agent, candidate, scoredActions);
                UpdateDampening(agent, candidate.Action);
                return;
            }
        }

        // Fallback: idle
        agent.CurrentAction = ActionType.Idle;
        agent.RecordAction(ActionType.Idle, currentTick, "Idle (caretaker)");
    }

    /// <summary>v1.8: Child behavior (unchanged from previous system).</summary>
    private void DecideChild(Agent agent, World world, EventBus bus, int currentTick, Action<string>? trace)
    {
        // Phase 1b equivalent for children: distance safety check
        if (agent.HomeTile.HasValue)
        {
            int distFromHome = Math.Max(
                Math.Abs(agent.X - agent.HomeTile.Value.X),
                Math.Abs(agent.Y - agent.HomeTile.Value.Y));
            int childLeash = SimConfig.HomeModeMoveLeashChild; // 10 tiles
            if (distFromHome > childLeash)
            {
                trace?.Invoke($"[TRACE Agent {agent.Id}] CHILD-SAFETY-DISTANCE: {distFromHome} tiles from home, forcing return");
                agent.ClearGoal();
                agent.ClearPendingAction();

                var path = SimplePathfinder.FindPath(agent.X, agent.Y,
                    agent.HomeTile.Value.X, agent.HomeTile.Value.Y, world);
                if (path != null && path.Count > 0)
                {
                    agent.WaypointPath = path;
                    agent.WaypointIndex = 0;
                    agent.CurrentGoal = GoalType.ReturnHome;
                    agent.GoalTarget = agent.HomeTile;
                    agent.GoalStartTick = currentTick;
                    var wp = path[0];
                    TryStartMove(agent, wp.X, wp.Y, world, trace);
                    agent.RecordAction(ActionType.Move, currentTick,
                        $"Child safety return home (dist={distFromHome}, path={path.Count} steps)");
                    LogDecision(currentTick, agent, "Move", "child-safety-return-home", 90f);
                }
                else
                {
                    // No A* path — try greedy 1-tile step instead of teleporting
                    trace?.Invoke($"[TRACE Agent {agent.Id}] CHILD-NO-PATH: A* failed, trying greedy step home");
                    TryGreedyStepToward(agent, agent.HomeTile.Value.X, agent.HomeTile.Value.Y, world, trace);
                    agent.RecordAction(ActionType.Move, currentTick,
                        $"Child greedy step (no A* path, dist={distFromHome})");
                }
                agent.LastDecisionTick = currentTick;
                return;
            }
        }

        // Mini Phase 3 for children: continue ReturnHome goal via waypoint path
        // Children don't go through the adult TryAdvanceGoal pipeline, so we advance
        // waypoints here to ensure they actually walk HOME, not just 1 step then re-explore.
        if (agent.CurrentGoal == GoalType.ReturnHome && agent.HomeTile.HasValue)
        {
            var (hx, hy) = agent.HomeTile.Value;
            int distHome = Math.Max(Math.Abs(agent.X - hx), Math.Abs(agent.Y - hy));
            if (distHome <= 1)
            {
                // Arrived home — clear goal
                agent.ClearGoal();
                trace?.Invoke($"[TRACE Agent {agent.Id}] CHILD-RETURN-COMPLETE: arrived home at ({agent.X},{agent.Y})");
            }
            else if (agent.WaypointPath != null && agent.WaypointIndex < agent.WaypointPath.Count)
            {
                // Advance to next waypoint
                var wp = agent.WaypointPath[agent.WaypointIndex];
                agent.WaypointIndex++;
                TryStartMove(agent, wp.X, wp.Y, world, trace);
                agent.RecordAction(ActionType.Move, currentTick,
                    $"Child returning home (dist={distHome}, wp={agent.WaypointIndex}/{agent.WaypointPath.Count})");
                return;
            }
            else
            {
                // Waypoints exhausted but not home — recompute path
                var path = SimplePathfinder.FindPath(agent.X, agent.Y, hx, hy, world);
                if (path != null && path.Count > 0)
                {
                    agent.WaypointPath = path;
                    agent.WaypointIndex = 0;
                    var wp = path[0];
                    agent.WaypointIndex++;
                    TryStartMove(agent, wp.X, wp.Y, world, trace);
                    agent.RecordAction(ActionType.Move, currentTick,
                        $"Child returning home (repathed, dist={distHome}, path={path.Count})");
                    return;
                }
                else
                {
                    // No A* path — try greedy 1-tile step instead of teleporting
                    TryGreedyStepToward(agent, hx, hy, world, trace);
                    agent.ClearGoal();
                    agent.RecordAction(ActionType.Move, currentTick,
                        $"Child greedy step (no A* path during return, dist={distHome})");
                    // Fall through to normal child behavior
                }
            }
        }

        var currentTile = world.GetTile(agent.X, agent.Y);
        int food = agent.FoodInInventory();

        if (agent.Stage == DevelopmentStage.Infant)
        {
            if (agent.Hunger <= 60f)
            {
                // Eat from home storage
                if (agent.HomeTile.HasValue
                    && agent.X == agent.HomeTile.Value.X && agent.Y == agent.HomeTile.Value.Y
                    && agent.TryEatFromHomeStorage(currentTile))
                {
                    agent.RecordAction(ActionType.Eat, currentTick, $"Infant ate from home storage (hunger: {agent.Hunger:F0})");
                    return;
                }
                // Eat from granary
                if (currentTile.HasGranary && currentTile.GranaryTotalFood > 0)
                {
                    var (wType, wAmount) = currentTile.WithdrawAnyFood(1);
                    if (wAmount > 0)
                    {
                        agent.Hunger = Math.Min(100f, agent.Hunger + SimConfig.FoodRestoreRaw);
                        agent.RecordAction(ActionType.Eat, currentTick, $"Infant ate from granary (hunger: {agent.Hunger:F0})");
                        return;
                    }
                }
            }
            agent.CurrentAction = ActionType.GrowingUp;
            agent.RecordAction(ActionType.GrowingUp, currentTick, "Infant growing up");
            return;
        }
        else // Youth — Post-Playtest Fix 2+3: Proper youth action set with multi-tick gather
        {
            bool atHome = agent.HomeTile.HasValue
                && agent.X == agent.HomeTile.Value.X && agent.Y == agent.HomeTile.Value.Y;
            int homeX = agent.HomeTile?.X ?? agent.X;
            int homeY = agent.HomeTile?.Y ?? agent.Y;

            // Fix 3: Youth goal continuation — if youth has an active GatherFoodAt/GatherResourceAt
            // goal, continue moving toward it before re-evaluating. Without this, youth re-decide
            // every tick and never reach distant resources.
            if (agent.CurrentGoal == GoalType.GatherFoodAt || agent.CurrentGoal == GoalType.GatherResourceAt)
            {
                if (agent.GoalTarget.HasValue)
                {
                    var (gx, gy) = agent.GoalTarget.Value;
                    int goalDist = Math.Max(Math.Abs(agent.X - gx), Math.Abs(agent.Y - gy));
                    if (goalDist <= 0)
                    {
                        // Arrived at gather target — start multi-tick gather
                        var goalTile = world.GetTile(gx, gy);
                        if (agent.GoalResource.HasValue)
                        {
                            var res = agent.GoalResource.Value;
                            if (goalTile.Resources.TryGetValue(res, out int amt) && amt > 0 && agent.HasInventorySpace()
                                && TryStartGatherAction(agent, gx, gy, res, currentTick, trace, checkNonFoodGuard: false))
                            {
                                agent.RecordAction(ActionType.Gather, currentTick, $"Youth gathering {res} at ({gx},{gy})");
                                trace?.Invoke($"[TRACE Agent {agent.Id}] YOUTH-GATHER-ARRIVED: started {res} at ({gx},{gy})");
                                agent.ClearGoal();
                                return;
                            }
                        }
                        // Resource gone — try any food on this tile
                        if (TryStartGatherFood(agent, goalTile, world, bus, currentTick, trace))
                        {
                            agent.RecordAction(ActionType.Gather, currentTick, "Youth gathering food (goal fallback)");
                            agent.ClearGoal();
                            return;
                        }
                        // Nothing here — clear goal and fall through
                        agent.ClearGoal();
                    }
                    else
                    {
                        // Still en route — continue moving toward gather target
                        if (TryStartMove(agent, gx, gy, world, trace))
                        {
                            agent.RecordAction(ActionType.Move, currentTick,
                                $"Youth moving to gather at ({gx},{gy}), dist={goalDist}");
                            return;
                        }
                        // Can't move — clear goal and fall through
                        agent.ClearGoal();
                    }
                }
                else
                {
                    agent.ClearGoal();
                }
            }

            // 1. Night rest — same Phase 1 override as adults (75.0 priority)
            if (Agent.IsNightTime(currentTick) && agent.Hunger > 40f)
            {
                if (atHome || !agent.HomeTile.HasValue)
                {
                    int remainingNight = CalculateRemainingNightTicks(currentTick);
                    agent.PendingAction = ActionType.GrowingUp;
                    agent.ActionProgress = 0f;
                    agent.ActionDurationTicks = Math.Max(remainingNight, 30);
                    agent.CurrentAction = ActionType.GrowingUp;
                    agent.LastRestTick = currentTick;
                    agent.RecordAction(ActionType.GrowingUp, currentTick, "Youth resting for the night");
                    return;
                }
                // Not at home — head home to sleep
                if (TryStartMove(agent, homeX, homeY, world, trace))
                {
                    agent.RecordAction(ActionType.Move, currentTick, "Youth heading home to sleep");
                    return;
                }
            }

            // 2. Eat — from home storage, inventory, granary, or emergency forage
            if (agent.Hunger <= 60f)
            {
                if (atHome && agent.TryEatFromHomeStorage(currentTile))
                {
                    agent.RecordAction(ActionType.Eat, currentTick, $"Youth ate from home storage (hunger: {agent.Hunger:F0})");
                    return;
                }
                if (food > 0 && agent.Eat())
                {
                    agent.RecordAction(ActionType.Eat, currentTick, $"Youth ate food (hunger: {agent.Hunger:F0})");
                    return;
                }
                if (currentTile.HasGranary && currentTile.GranaryTotalFood > 0)
                {
                    var (wType, wAmount) = currentTile.WithdrawAnyFood(1);
                    if (wAmount > 0)
                    {
                        agent.Hunger = Math.Min(100f, agent.Hunger + SimConfig.FoodRestoreRaw);
                        agent.RecordAction(ActionType.Eat, currentTick, $"Youth ate from granary (hunger: {agent.Hunger:F0})");
                        return;
                    }
                }
            }

            // 3. Deposit home — if at home with surplus food and storage has room
            // Fix 4: Same threshold as adults (5+ total items) to prevent 1-item deposit spam.
            int youthFood = agent.FoodInInventory();
            if (atHome && youthFood >= 5 && currentTile.HasHomeStorage
                && currentTile.HomeTotalFood < currentTile.HomeStorageCapacity)
            {
                StartDepositHome(agent, currentTile, currentTick, trace);
                agent.RecordAction(ActionType.DepositHome, currentTick, "Youth depositing food at home");
                return;
            }

            // 4. Gather — within 8 tiles of home (Fix 3: expanded from 5, parents deplete nearby)
            //    Uses multi-tick gather (like adults) and goal-continuation for distant tiles.
            if (agent.HasInventorySpace())
            {
                // Find gatherable food/wood within 8 tiles of home
                (int bestX, int bestY, ResourceType bestRes)? bestGather = null;
                int bestDist = int.MaxValue;
                int youthGatherRadius = 8;
                for (int dx = -youthGatherRadius; dx <= youthGatherRadius; dx++)
                {
                    for (int dy = -youthGatherRadius; dy <= youthGatherRadius; dy++)
                    {
                        int gx = homeX + dx, gy = homeY + dy;
                        if (!world.IsInBounds(gx, gy)) continue;
                        var gTile = world.GetTile(gx, gy);
                        foreach (var kvp in gTile.Resources)
                        {
                            if (kvp.Value > 0 && (IsEdibleResource(kvp.Key) || kvp.Key == ResourceType.Wood))
                            {
                                int dist = Math.Max(Math.Abs(gx - agent.X), Math.Abs(gy - agent.Y));
                                if (dist < bestDist)
                                {
                                    bestDist = dist;
                                    bestGather = (gx, gy, kvp.Key);
                                }
                            }
                        }
                    }
                }

                if (bestGather.HasValue)
                {
                    var (gx, gy, res) = bestGather.Value;
                    if (gx == agent.X && gy == agent.Y)
                    {
                        // Fix 3: Multi-tick gather from current tile (not instant)
                        if (TryStartGatherAction(agent, gx, gy, res, currentTick, trace, checkNonFoodGuard: false))
                        {
                            agent.RecordAction(ActionType.Gather, currentTick, $"Youth gathering {res}");
                            trace?.Invoke($"[TRACE Agent {agent.Id}] YOUTH-GATHER: {res} at ({gx},{gy})");
                            return;
                        }
                        // Guard blocked — fall through
                    }
                    else
                    {
                        // Fix 3: Set a goal so the youth persists movement across ticks
                        // instead of re-evaluating and picking Follow Parent every tick
                        GoalType gType = IsEdibleResource(res) ? GoalType.GatherFoodAt : GoalType.GatherResourceAt;
                        agent.CurrentGoal = gType;
                        agent.GoalTarget = (gx, gy);
                        agent.GoalResource = res;
                        agent.GoalStartTick = currentTick;
                        if (TryStartMove(agent, gx, gy, world, trace))
                        {
                            agent.RecordAction(ActionType.Move, currentTick, $"Youth moving to gather {res} at ({gx},{gy})");
                            trace?.Invoke($"[TRACE Agent {agent.Id}] YOUTH-GATHER-GOAL: set {gType} for {res} at ({gx},{gy})");
                            return;
                        }
                        else
                        {
                            agent.ClearGoal(); // Can't move — clear stale goal
                        }
                    }
                }
            }

            // 5. Return home if away — higher priority than follow parent
            if (!atHome && agent.HomeTile.HasValue)
            {
                if (TryStartMove(agent, homeX, homeY, world, trace))
                {
                    agent.RecordAction(ActionType.Move, currentTick, "Youth returning home");
                    return;
                }
            }

            // 6. Follow parent — lowest priority fallback, only when parent is near home
            // Fix 3: Only follow parents who are within 5 tiles of home (don't chase foraging parents).
            // Also reduced priority — youth should gather, not shadow mom.
            if (agent.HomeTile.HasValue)
            {
                Agent? nearestParent = null;
                int nearestParentDist = int.MaxValue;
                foreach (var kvp in agent.Relationships)
                {
                    if (kvp.Value != RelationshipType.Parent) continue;
                    var parent = _currentAllAgents?.FirstOrDefault(a => a.Id == kvp.Key && a.IsAlive);
                    if (parent == null) continue;
                    // Only consider parents who are near home (within 5 tiles)
                    int parentHomeDist = Math.Max(Math.Abs(parent.X - homeX), Math.Abs(parent.Y - homeY));
                    if (parentHomeDist > 5) continue;
                    int dist = Math.Max(Math.Abs(parent.X - agent.X), Math.Abs(parent.Y - agent.Y));
                    if (dist < nearestParentDist)
                    {
                        nearestParentDist = dist;
                        nearestParent = parent;
                    }
                }

                if (nearestParent != null && nearestParentDist > 1 && nearestParentDist <= 5)
                {
                    if (TryStartMove(agent, nearestParent.X, nearestParent.Y, world, trace))
                    {
                        agent.RecordAction(ActionType.Move, currentTick, $"Youth following parent {nearestParent.Name}");
                        return;
                    }
                }
            }

            agent.CurrentAction = ActionType.GrowingUp;
            agent.RecordAction(ActionType.GrowingUp, currentTick, "Youth growing up");
        }
    }

    // ═════════════════════════════════════════════════════════════════
    // v1.8 Mode Decision Helpers
    // ═════════════════════════════════════════════════════════════════

    /// <summary>Try to eat or cook food from inventory.</summary>
    private bool TryEatOrCook(Agent agent, Tile currentTile, EventBus bus, int currentTick, Action<string>? trace)
    {
        if (agent.Knowledge.Contains("cooking") && agent.HasCookableFood())
        {
            bool hasFireSource = false;
            if (agent.HomeTile.HasValue && agent.X == agent.HomeTile.Value.X && agent.Y == agent.HomeTile.Value.Y
                && agent.Knowledge.Contains("fire"))
                hasFireSource = true;
            if (currentTile.Structures.Contains("campfire") || currentTile.Structures.Contains("hearth"))
                hasFireSource = true;

            if (hasFireSource)
            {
                StartCook(agent, currentTick, trace);
                agent.RecordAction(ActionType.Cook, currentTick, "Cooking food");
                return true;
            }
        }

        if (agent.Eat())
        {
            agent.RecordAction(ActionType.Eat, currentTick, $"Ate food (hunger: {agent.Hunger:F0})");
            bus.Emit(currentTick, $"{agent.Name} ate food (Hunger: {agent.Hunger:F0})", EventType.Action, agentId: agent.Id);
            return true;
        }
        return false;
    }

    /// <summary>Fix 3: Returns true if agent has a nearby child whose hunger is lower than the agent's.
    /// Used to suppress parent self-eat so the child gets fed first.</summary>
    private static bool HasHungrierChild(Agent agent, List<Agent>? allAgents)
    {
        if (allAgents == null) return false;
        foreach (var kvp in agent.Relationships)
        {
            if (kvp.Value != RelationshipType.Child) continue;
            var child = allAgents.FirstOrDefault(a => a.Id == kvp.Key);
            if (child == null || !child.IsAlive) continue;
            if (child.Stage == DevelopmentStage.Adult) continue;
            int dist = Math.Abs(child.X - agent.X) + Math.Abs(child.Y - agent.Y);
            if (dist <= 2 && child.Hunger < agent.Hunger && child.Hunger < 70f)
                return true;
        }
        return false;
    }

    /// <summary>Try to feed a nearby hungry child (P1.5 logic).</summary>
    private void TryFeedNearbyChild(Agent agent, World world, EventBus bus, int currentTick, Action<string>? trace)
    {
        int food = agent.FoodInInventory();
        if (food <= 0) return;

        var nearbyForFeeding = GetNearbyAgents(agent, world);
        Agent? hungriestChild = null;
        float lowestHunger = 70f; // Fix B2: Aligned with Caretaker proactive threshold (was 40)
        bool foundOwnChild = false;

        foreach (var other in nearbyForFeeding)
        {
            if (other.IsAlive && other.Stage != DevelopmentStage.Adult && other.Hunger < lowestHunger)
            {
                bool isOwnChild = agent.Relationships.TryGetValue(other.Id, out var rel) && rel == RelationshipType.Child;
                if (foundOwnChild && !isOwnChild) continue;
                if (isOwnChild && !foundOwnChild)
                {
                    foundOwnChild = true;
                    lowestHunger = other.Hunger;
                }
                if (other.Hunger < lowestHunger || (isOwnChild && !foundOwnChild))
                {
                    hungriestChild = other;
                    lowestHunger = other.Hunger;
                }
            }
        }

        if (hungriestChild != null)
        {
            ResourceType[] foodTypes = { ResourceType.PreservedFood, ResourceType.Berries, ResourceType.Grain, ResourceType.Meat, ResourceType.Fish };
            foreach (var foodType in foodTypes)
            {
                if (agent.Inventory.TryGetValue(foodType, out int amt) && amt > 0)
                {
                    agent.Inventory[foodType]--;
                    if (agent.Inventory[foodType] <= 0) agent.Inventory.Remove(foodType);

                    int restore = foodType == ResourceType.PreservedFood
                        ? SimConfig.FoodRestorePreserved
                        : agent.Knowledge.Contains("cooking") ? SimConfig.FoodRestoreCooked : SimConfig.FoodRestoreRaw;
                    hungriestChild.Hunger = Math.Min(100f, hungriestChild.Hunger + restore);

                    trace?.Invoke($"[TRACE Agent {agent.Id}] Feed-Child: fed Agent {hungriestChild.Id} (hunger: {hungriestChild.Hunger:F0})");
                    bus.Emit(currentTick,
                        $"{agent.Name} fed child {hungriestChild.Name} (hunger: {hungriestChild.Hunger:F0})",
                        EventType.Action, agentId: agent.Id, secondaryId: hungriestChild.Id);
                    agent.RecordAction(ActionType.Eat, currentTick, $"Fed child {hungriestChild.Name}");
                    break;
                }
            }
        }
    }

    /// <summary>Log a scored utility decision with runner-up.</summary>
    private void LogScoredDecision(int currentTick, Agent agent, ScoredAction candidate, List<ScoredAction> all)
    {
        if (Logger == null) return;
        // Directive #11 Fix 4: Only log viable runner-ups (score <= chosen).
        // Actions with higher scores that failed dispatch are not real contenders.
        var runnerUp = all.FirstOrDefault(s => s.Action != candidate.Action && s.Score <= candidate.Score);
        string runnerUpAction = (runnerUp.Action == default && runnerUp.Score == 0)
            ? "None" : runnerUp.Action.ToString();
        float runnerUpScore = (runnerUp.Action == default && runnerUp.Score == 0)
            ? 0f : runnerUp.Score;
        string target = candidate.TargetTile.HasValue
            ? $"tile({candidate.TargetTile.Value.X},{candidate.TargetTile.Value.Y})"
            : candidate.TargetRecipeId ?? candidate.TargetResource?.ToString() ?? "";
        LogDecision(currentTick, agent, candidate.Action.ToString(), target,
            candidate.Score, runnerUpAction, runnerUpScore);
    }

    /// <summary>Update action dampening state.</summary>
    private static void UpdateDampening(Agent agent, ActionType action)
    {
        if (agent.LastChosenUtilityAction == action)
            agent.ConsecutiveSameActionTicks++;
        else
        {
            agent.LastChosenUtilityAction = action;
            agent.ConsecutiveSameActionTicks = 1;
        }
    }

    // ── GDD v1.7.1: Utility Action Dispatch ─────────────────────────────

    /// <summary>
    /// Attempts to execute a scored utility action. Returns true if the action was started.
    /// Routes to existing Start* methods. Falls through on failure (agent can't actually do this now).
    /// </summary>
    private bool TryDispatchUtilityAction(Agent agent, ScoredAction scored, World world, EventBus bus, int currentTick, Action<string>? trace)
    {
        var currentTile = world.GetTile(agent.X, agent.Y);

        switch (scored.Action)
        {
            case ActionType.Gather:
                return TryDispatchGather(agent, scored, world, bus, currentTick, trace);

            case ActionType.TendFarm:
                return TryDispatchTendFarm(agent, scored, world, currentTick, trace);

            // GDD v1.8: Teach removed — knowledge is communal within settlements

            case ActionType.Build:
                return TryDispatchBuild(agent, scored, world, bus, currentTick, trace);

            case ActionType.Socialize:
                return TryDispatchSocialize(agent, scored, world, bus, currentTick, trace);

            case ActionType.Reproduce:
                return TryDispatchReproduce(agent, scored, world, bus, currentTick, trace);

            case ActionType.Experiment:
                return TryDispatchExperiment(agent, scored, world, bus, currentTick, trace);

            case ActionType.ReturnHome:
                return TryDispatchReturnHome(agent, scored, world, currentTick, trace);

            case ActionType.Deposit:
                return TryDispatchDeposit(agent, scored, world, currentTick, trace);

            case ActionType.Withdraw:
                return TryDispatchWithdraw(agent, scored, world, currentTick, trace);

            case ActionType.Preserve:
                if (agent.Knowledge.Contains("food_preservation") && agent.FoodInInventory() >= 2)
                {
                    StartPreserve(agent, currentTick, trace);
                    agent.RecordAction(ActionType.Preserve, currentTick, "Preserving food");
                    return true;
                }
                return false;

            case ActionType.DepositHome:
                return TryDispatchDepositHome(agent, scored, world, currentTick, trace);

            case ActionType.ShareFood:
                return TryDispatchShareFood(agent, scored, world, bus, currentTick, trace);

            case ActionType.Explore:
                StartExplore(agent, world, bus, currentTick, trace);
                agent.RecordAction(ActionType.Explore, currentTick, "Exploring");
                return true;

            // Directive #5 Fix 4: Rest was scored by ScoreIdleRest but never dispatched
            case ActionType.Rest:
            {
                agent.PendingAction = ActionType.Rest;
                agent.ActionProgress = 0f;
                agent.ActionDurationTicks = SimConfig.NightRestDuration / 2; // Shorter idle rest vs night rest
                agent.CurrentAction = ActionType.Rest;
                agent.LastRestTick = currentTick;
                agent.RecordAction(ActionType.Rest, currentTick, "Idle resting for health recovery");
                trace?.Invoke($"[TRACE Agent {agent.Id}] UTILITY-Rest: idle rest for health recovery");
                return true;
            }

            case ActionType.ClearLand:
                return TryDispatchClearLand(agent, scored, world, bus, currentTick, trace);

            case ActionType.TendAnimals:
                return TryDispatchTendAnimals(agent, scored, world, bus, currentTick, trace);

            case ActionType.Hunt:
                return TryDispatchHunt(agent, scored, world, bus, currentTick, trace);
            case ActionType.Harvest:
                return TryDispatchHarvest(agent, scored, world, bus, currentTick, trace);

            case ActionType.SetTrap:
                return TryDispatchSetTrap(agent, scored, world, bus, currentTick, trace);

            case ActionType.Tame:
                return TryDispatchTame(agent, scored, world, bus, currentTick, trace);

            case ActionType.FeedPen:
                return TryDispatchFeedPen(agent, scored, world, bus, currentTick, trace);
            case ActionType.PenAnimal:
                return TryDispatchPenAnimal(agent, scored, world, bus, currentTick, trace);
            case ActionType.Slaughter:
                return TryDispatchSlaughter(agent, scored, world, bus, currentTick, trace);

            default:
                return false;
        }
    }

    private bool TryDispatchGather(Agent agent, ScoredAction scored, World world, EventBus bus, int currentTick, Action<string>? trace)
    {
        var currentTile = world.GetTile(agent.X, agent.Y);

        // If target is current tile, start gathering
        if (scored.TargetTile.HasValue &&
            scored.TargetTile.Value.X == agent.X && scored.TargetTile.Value.Y == agent.Y)
        {
            // Try the specific resource first
            if (scored.TargetResource.HasValue)
            {
                var res = scored.TargetResource.Value;
                if (currentTile.Resources.TryGetValue(res, out int amt) && amt > 0 && agent.HasInventorySpace()
                    && TryStartGatherAction(agent, currentTile.X, currentTile.Y, res, currentTick, trace, checkNonFoodGuard: false))
                {
                    agent.RecordAction(ActionType.Gather, currentTick, $"Gathering {res}");
                    trace?.Invoke($"[TRACE Agent {agent.Id}] UTILITY-Gather: {res} at ({agent.X},{agent.Y})");
                    return true;
                }
            }

            // Fall back to any gatherable resource on this tile
            if (TryStartGatherFood(agent, currentTile, world, bus, currentTick, trace))
            {
                agent.RecordAction(ActionType.Gather, currentTick, "Gathering food");
                return true;
            }
            if (TryStartGatherAnyResource(agent, currentTile, world, bus, currentTick, trace))
            {
                agent.RecordAction(ActionType.Gather, currentTick, "Gathering resources");
                return true;
            }
            return false;
        }

        // Target is a different tile — set goal and move toward it
        if (scored.TargetTile.HasValue)
        {
            bool isFood = scored.TargetResource.HasValue &&
                (scored.TargetResource.Value == ResourceType.Berries || scored.TargetResource.Value == ResourceType.Grain
                || scored.TargetResource.Value == ResourceType.Meat || scored.TargetResource.Value == ResourceType.Fish);
            agent.CurrentGoal = isFood ? GoalType.GatherFoodAt : GoalType.GatherResourceAt;
            agent.GoalTarget = scored.TargetTile;
            agent.GoalResource = scored.TargetResource;
            agent.GoalStartTick = currentTick;

            if (TryStartMove(agent, scored.TargetTile.Value.X, scored.TargetTile.Value.Y, world, trace))
            {
                agent.RecordAction(ActionType.Move, currentTick,
                    $"Moving to gather {scored.TargetResource} at ({scored.TargetTile.Value.X},{scored.TargetTile.Value.Y})");
                return true;
            }
            agent.ClearGoal();
        }
        return false;
    }

    private bool TryDispatchTendFarm(Agent agent, ScoredAction scored, World world, int currentTick, Action<string>? trace)
    {
        if (!scored.TargetTile.HasValue) return false;

        var (tx, ty) = scored.TargetTile.Value;

        if (tx == agent.X && ty == agent.Y)
        {
            var tile = world.GetTile(tx, ty);

            // Fix 5A: Enforce farm placement constraints before creating new farm
            if (!tile.HasFarm)
            {
                // Never place farm on home tile
                if (agent.HomeTile.HasValue && tx == agent.HomeTile.Value.X && ty == agent.HomeTile.Value.Y)
                    return false;
                // Must be IsFarmable (Plains or cleared land — never forest/water/mountain)
                if (!tile.IsFarmable)
                    return false;
            }

            if (tile.IsFarmable || tile.HasFarm)
            {
                if (!tile.HasFarm)
                {
                    tile.Structures.Add("farm");
                    ClearNonGrainResources(tile);
                    // US-009: Add farm to settlement and recalculate territory
                    if (agent.SettlementId != null && _currentSettlements != null)
                    {
                        var settlement = _currentSettlements.FirstOrDefault(s => s.Id == agent.SettlementId);
                        if (settlement != null)
                        {
                            settlement.Structures.Add((tx, ty, "farm"));
                            settlement.RecalculateTerritory(world.Width, world.Height);
                        }
                    }
                }
                StartTendFarm(agent, tx, ty, trace);
                agent.RecordAction(ActionType.TendFarm, currentTick, $"Tending farm at ({tx},{ty})");
                return true;
            }
            return false;
        }

        // Move toward farm
        if (TryStartMove(agent, tx, ty, world, trace))
        {
            agent.RecordAction(ActionType.Move, currentTick, $"Moving to farm at ({tx},{ty})");
            return true;
        }
        return false;
    }

    // GDD v1.8: TryDispatchTeach REMOVED — knowledge is communal within settlements.

    private bool TryDispatchBuild(Agent agent, ScoredAction scored, World world, EventBus bus, int currentTick, Action<string>? trace)
    {
        if (!scored.TargetTile.HasValue || scored.TargetRecipeId == null) return false;

        var (tx, ty) = scored.TargetTile.Value;
        var tile = world.GetTile(tx, ty);

        // Not at build site — set a goal to move there first
        if (agent.X != tx || agent.Y != ty)
        {
            agent.CurrentGoal = GoalType.BuildAtTile;
            agent.GoalTarget = (tx, ty);
            agent.GoalRecipeId = scored.TargetRecipeId;
            agent.GoalStartTick = currentTick;

            if (TryStartMove(agent, tx, ty, world, trace))
            {
                agent.RecordAction(ActionType.Move, currentTick, $"Moving to build site at ({tx},{ty})");
                trace?.Invoke($"[TRACE Agent {agent.Id}] UTILITY-Build: moving to build site ({tx},{ty})");
                return true;
            }
            agent.ClearGoal();
            return false;
        }

        int woodHeld = agent.Inventory.GetValueOrDefault(ResourceType.Wood, 0);
        int stoneHeld = agent.Inventory.GetValueOrDefault(ResourceType.Stone, 0);

        if (scored.TargetRecipeId == "lean_to")
        {
            if (woodHeld >= SimConfig.ShelterWoodCost && stoneHeld >= SimConfig.ShelterStoneCost && !tile.HasShelter)
            {
                StartBuild(agent, tile, "lean_to", SimConfig.ShelterBuildTicks, trace);
                agent.RecordAction(ActionType.Build, currentTick, $"Building lean-to at ({tx},{ty})");
                return true;
            }
        }
        else if (scored.TargetRecipeId == "reinforced_shelter")
        {
            // Directive #5 Fix 2: Shelter upgrade — requires 5 wood, 3 stone
            if (woodHeld >= SimConfig.GranaryWoodCost && stoneHeld >= SimConfig.GranaryStoneCost
                && tile.HasShelter && !tile.Structures.Contains("improved_shelter"))
            {
                // Consume materials
                agent.Inventory[ResourceType.Wood] = woodHeld - SimConfig.GranaryWoodCost;
                agent.Inventory[ResourceType.Stone] = stoneHeld - SimConfig.GranaryStoneCost;
                StartBuild(agent, tile, "improved_shelter", SimConfig.GranaryBuildTicks, trace);
                agent.RecordAction(ActionType.Build, currentTick, $"Upgrading shelter at ({tx},{ty})");
                return true;
            }
        }
        else if (scored.TargetRecipeId == "granary")
        {
            if (woodHeld >= SimConfig.GranaryWoodCost && stoneHeld >= SimConfig.GranaryStoneCost
                && tile.HasShelter && !tile.HasGranary)
            {
                StartBuild(agent, tile, "granary", SimConfig.GranaryBuildTicks, trace);
                agent.RecordAction(ActionType.Build, currentTick, $"Building granary at ({tx},{ty})");
                return true;
            }
        }
        else if (scored.TargetRecipeId == "campfire")
        {
            // Directive #5 Fix 2: Campfire — 2 wood, 1 stone
            if (woodHeld >= 2 && stoneHeld >= 1 && !tile.Structures.Contains("campfire"))
            {
                agent.Inventory[ResourceType.Wood] = woodHeld - 2;
                agent.Inventory[ResourceType.Stone] = stoneHeld - 1;
                StartBuild(agent, tile, "campfire", SimConfig.ShelterBuildTicks / 2, trace);
                agent.RecordAction(ActionType.Build, currentTick, $"Building campfire at ({tx},{ty})");
                return true;
            }
        }
        else if (scored.TargetRecipeId == "animal_pen")
        {
            // D25d: Animal pen — 5 wood, 2 stone
            if (woodHeld >= 5 && stoneHeld >= 2 && !tile.Structures.Contains("animal_pen"))
            {
                agent.Inventory[ResourceType.Wood] = woodHeld - 5;
                agent.Inventory[ResourceType.Stone] = stoneHeld - 2;
                StartBuild(agent, tile, "animal_pen", SimConfig.PenBuildDuration, trace);
                agent.RecordAction(ActionType.Build, currentTick, $"Building animal pen at ({tx},{ty})");
                return true;
            }
        }
        return false;
    }

    private bool TryDispatchSocialize(Agent agent, ScoredAction scored, World world, EventBus bus, int currentTick, Action<string>? trace)
    {
        if (!scored.TargetAgentId.HasValue) return false;

        var target = world.GetAgentById(scored.TargetAgentId.Value);
        if (target == null || !target.IsAlive) return false;

        // Directive Fix 1: Socialize completes when within 2 tiles (proximity completion).
        // No pursuit — if target is beyond 2 tiles, Socialize fails and agent re-evaluates.
        int dist = Math.Max(Math.Abs(target.X - agent.X), Math.Abs(target.Y - agent.Y));
        if (dist <= 2)
        {
            IncrementSocialBond(agent, target);
            agent.CurrentAction = ActionType.Socialize;
            // Directive #5 Fix 1: Record socialize completion for cooldown + diminishing returns
            agent.LastSocializedTick = currentTick;
            agent.SocializeCountToday++;
            // D13: Track per-partner socialize count for daily cap
            agent.SocializePartnerCountToday.TryGetValue(target.Id, out int pCount);
            agent.SocializePartnerCountToday[target.Id] = pCount + 1;
            agent.RecordAction(ActionType.Socialize, currentTick, $"Socializing with {target.Name}");
            Logger?.LogCompletion(currentTick, agent, "Socialize", "completed",
                $"bonded with {target.Name}", 1);
            trace?.Invoke($"[TRACE Agent {agent.Id}] UTILITY-Socialize: bonding with Agent {target.Id} (dist={dist}, bonds={agent.SocialBonds.GetValueOrDefault(target.Id, 0)})");
            return true;
        }

        // Target beyond 2 tiles — don't pursue. Let action fall through so
        // agent picks something productive. Socialize only works with nearby agents.
        trace?.Invoke($"[TRACE Agent {agent.Id}] UTILITY-Socialize: target Agent {target.Id} too far (dist={dist}), skipping");
        return false;
    }

    private bool TryDispatchReproduce(Agent agent, ScoredAction scored, World world, EventBus bus, int currentTick, Action<string>? trace)
    {
        if (!scored.TargetAgentId.HasValue) return false;

        var partner = world.GetAgentById(scored.TargetAgentId.Value);
        if (partner == null || !partner.IsAlive || !partner.CanReproduce()) return false;

        var currentTile = world.GetTile(agent.X, agent.Y);
        var livingNames = _currentAllAgents?.Where(a => a.IsAlive).Select(a => a.Name);
        var child = agent.Reproduce(partner, world, currentTile, random, _currentKnowledgeSystem, _currentSettlements, livingNames);
        if (child != null)
        {
            trace?.Invoke($"[TRACE Agent {agent.Id}] UTILITY-Reproduce: partner={partner.Id}, child={child.Id}");
            bus.Emit(currentTick,
                $"{agent.Name} and {partner.Name} reproduced! {child.Name} born at ({child.X},{child.Y})",
                EventType.Birth, agentId: child.Id, secondaryId: agent.Id);
            _pendingChildren.Add(child);
            agent.RecordAction(ActionType.Reproduce, currentTick, $"Reproduced with {partner.Name}");
            return true;
        }
        else
        {
            trace?.Invoke($"[TRACE Agent {agent.Id}] UTILITY-Reproduce: ATTEMPTED but failed RNG (partner={partner.Id})");
        }
        return false;
    }

    private bool TryDispatchExperiment(Agent agent, ScoredAction scored, World world, EventBus bus, int currentTick, Action<string>? trace)
    {
        if (scored.TargetRecipeId == null) return false;

        var recipe = RecipeRegistry.AllRecipes.FirstOrDefault(r => r.Id == scored.TargetRecipeId);
        if (recipe == null) return false;

        // Verify agent still has resources
        foreach (var kvp in recipe.RequiredResources)
        {
            if (!agent.Inventory.TryGetValue(kvp.Key, out int amt) || amt < kvp.Value)
                return false;
        }

        StartExperiment(agent, recipe, world, bus, currentTick, trace);
        agent.RecordAction(ActionType.Experiment, currentTick, $"Experimenting: {recipe.Name}");
        return true;
    }

    private bool TryDispatchReturnHome(Agent agent, ScoredAction scored, World world, int currentTick, Action<string>? trace)
    {
        if (!agent.HomeTile.HasValue) return false;

        var home = agent.HomeTile.Value;
        agent.CurrentGoal = GoalType.ReturnHome;
        agent.GoalTarget = (home.X, home.Y);
        agent.GoalStartTick = currentTick;

        // Initialize recovery distance tracking
        agent.RecoveryStartDistanceToHome = Math.Max(
            Math.Abs(agent.X - home.X), Math.Abs(agent.Y - home.Y));

        if (TryStartMoveEscalating(agent, home.X, home.Y, world, currentTick, trace))
        {
            agent.RecordAction(ActionType.Move, currentTick, $"Returning home to ({home.X},{home.Y})");
            trace?.Invoke($"[TRACE Agent {agent.Id}] UTILITY-ReturnHome: moving toward ({home.X},{home.Y}), stage={agent.StuckRecoveryStage}");
            return true;
        }
        agent.ClearGoal();
        return false;
    }

    private bool TryDispatchDeposit(Agent agent, ScoredAction scored, World world, int currentTick, Action<string>? trace)
    {
        if (!scored.TargetTile.HasValue) return false;

        var (tx, ty) = scored.TargetTile.Value;

        if (tx == agent.X && ty == agent.Y)
        {
            var tile = world.GetTile(tx, ty);
            if (tile.HasGranary)
            {
                StartDeposit(agent, tile, currentTick, trace);
                agent.RecordAction(ActionType.Deposit, currentTick, "Depositing food in granary");
                return true;
            }
            return false;
        }

        if (TryStartMove(agent, tx, ty, world, trace))
        {
            agent.RecordAction(ActionType.Move, currentTick, $"Moving to granary at ({tx},{ty})");
            return true;
        }
        return false;
    }

    // ── GDD v1.8 Section 7: DepositHome Dispatch ─────────────────────

    private bool TryDispatchDepositHome(Agent agent, ScoredAction scored, World world, int currentTick, Action<string>? trace)
    {
        if (!agent.HomeTile.HasValue) return false;

        // Must be at home tile
        if (agent.X != agent.HomeTile.Value.X || agent.Y != agent.HomeTile.Value.Y)
        {
            // Move toward home
            if (TryStartMove(agent, agent.HomeTile.Value.X, agent.HomeTile.Value.Y, world, trace))
            {
                agent.RecordAction(ActionType.Move, currentTick, "Moving home to deposit food");
                return true;
            }
            return false;
        }

        var tile = world.GetTile(agent.X, agent.Y);
        // D21 Fix 1: Check both food and material storage capacity (was only checking food)
        bool canDepositFood = tile.HasHomeStorage && tile.HomeTotalFood < tile.HomeStorageCapacity;
        bool canDepositMats = tile.HomeTotalMaterials < Tile.MaterialStorageCapacity;
        if (canDepositFood || canDepositMats)
        {
            StartDepositHome(agent, tile, currentTick, trace);
            agent.RecordAction(ActionType.DepositHome, currentTick, "Depositing resources at home");
            return true;
        }
        return false;
    }

    /// <summary>GDD v1.8 Section 7: Starts depositing food into home shelter storage.</summary>
    private void StartDepositHome(Agent agent, Tile homeTile, int currentTick, Action<string>? trace)
    {
        agent.PendingAction = ActionType.DepositHome;
        agent.ActionProgress = 0f;
        agent.ActionDurationTicks = SimConfig.DepositDuration;
        agent.ActionTarget = (homeTile.X, homeTile.Y);
        agent.CurrentAction = ActionType.DepositHome;

        trace?.Invoke($"[TRACE Agent {agent.Id}] StartDepositHome: at ({homeTile.X},{homeTile.Y}), duration={SimConfig.DepositDuration}");
    }

    private bool TryDispatchWithdraw(Agent agent, ScoredAction scored, World world, int currentTick, Action<string>? trace)
    {
        if (!scored.TargetTile.HasValue) return false;

        var (tx, ty) = scored.TargetTile.Value;

        if (tx == agent.X && ty == agent.Y)
        {
            var tile = world.GetTile(tx, ty);
            if (tile.HasGranary && tile.GranaryTotalFood > 0)
            {
                StartWithdraw(agent, tile, currentTick, trace);
                agent.RecordAction(ActionType.Withdraw, currentTick, "Withdrawing food from granary");
                return true;
            }
            return false;
        }

        if (TryStartMove(agent, tx, ty, world, trace))
        {
            agent.RecordAction(ActionType.Move, currentTick, $"Moving to granary for food");
            return true;
        }
        return false;
    }

    // ── GDD v1.7.2: ShareFood Dispatch ──────────────────────────────────

    private bool TryDispatchShareFood(Agent agent, ScoredAction scored, World world, EventBus bus, int currentTick, Action<string>? trace)
    {
        if (!scored.TargetAgentId.HasValue) return false;

        int food = agent.FoodInInventory();
        if (food <= 0) return false;

        var target = world.GetAgentById(scored.TargetAgentId.Value);
        if (target == null || !target.IsAlive) return false;

        // Must be adjacent or on same tile
        int dist = Math.Max(Math.Abs(target.X - agent.X), Math.Abs(target.Y - agent.Y));
        if (dist > 1) return false;

        // Give food: remove from agent inventory, restore target hunger
        ResourceType[] foodTypes = { ResourceType.PreservedFood, ResourceType.Berries, ResourceType.Grain, ResourceType.Meat, ResourceType.Fish };
        foreach (var ft in foodTypes)
        {
            if (agent.Inventory.TryGetValue(ft, out int amt) && amt > 0)
            {
                agent.Inventory[ft] = amt - 1;
                if (agent.Inventory[ft] <= 0) agent.Inventory.Remove(ft);

                int restore = ft == ResourceType.PreservedFood ? SimConfig.FoodRestorePreserved : SimConfig.FoodRestoreRaw;
                target.Hunger = Math.Min(100f, target.Hunger + restore);

                agent.CurrentAction = ActionType.ShareFood;
                IncrementSocialBond(agent, target);

                trace?.Invoke($"[TRACE Agent {agent.Id}] SHARE-FOOD: Gave {ft} to {target.Name} (hunger: {target.Hunger:F0})");
                bus.Emit(currentTick, $"{agent.Name} shared food with {target.Name}", EventType.Action, agentId: agent.Id);
                agent.RecordAction(ActionType.ShareFood, currentTick, $"Shared {ft} with {target.Name}");
                return true;
            }
        }
        return false;
    }

    // ── GDD v1.7.1: Social Bond Helper ──────────────────────────────────

    /// <summary>Increments social bond between two agents (bidirectional).</summary>
    private static void IncrementSocialBond(Agent a, Agent b)
    {
        a.SocialBonds.TryGetValue(b.Id, out int abCount);
        a.SocialBonds[b.Id] = abCount + 1;

        b.SocialBonds.TryGetValue(a.Id, out int baCount);
        b.SocialBonds[a.Id] = baCount + 1;
    }

    // ── Interrupt Check ─────────────────────────────────────────────────

    /// <summary>
    /// Critical needs that interrupt any multi-tick action:
    /// - hunger &lt;= 20 and has food -> interrupt to eat
    /// - hunger &lt;= threshold and has food -> interrupt to eat
    /// - hunger &lt;= threshold and no food -> interrupt to seek food (but NOT if already gathering/eating)
    /// - health &lt;= 30 and hunger &gt; threshold -> interrupt to rest
    /// GDD v1.7.2 FIX: Don't interrupt Gather/Eat/Cook actions when starving with no food —
    /// the agent is already working on the food problem. Interrupting causes an infinite
    /// cancel-restart loop where the agent never finishes gathering.
    /// </summary>
    private static bool ShouldInterrupt(Agent agent)
    {
        // D18.2 Fix 3: ABSOLUTE starvation override — belt to Fix 2's suspenders.
        // An agent with food must NEVER starve. This overrides everything, including
        // ReturnHome protection. The only exception is if already eating/cooking.
        if (agent.Hunger <= SimConfig.InterruptHungerThreshold && agent.FoodInInventory() > 0)
        {
            if (agent.PendingAction == ActionType.Eat || agent.PendingAction == ActionType.Cook)
                return false;
            return true;  // Starvation with food overrides ALL actions, no exceptions
        }

        // Starving with NO food -> only interrupt if not already gathering/seeking food
        if (agent.Hunger <= SimConfig.InterruptHungerThreshold && agent.FoodInInventory() == 0)
        {
            // Don't interrupt Gather — agent is trying to get food, let it finish
            if (agent.PendingAction == ActionType.Gather)
                return false;
            // Don't interrupt Eat/Cook — shouldn't happen with 0 food but be safe
            if (agent.PendingAction == ActionType.Eat || agent.PendingAction == ActionType.Cook)
                return false;
            // Directive Fix 3: Protected moves toward food in Urgent mode.
            // When starving and moving toward a food source, let the move complete
            // to prevent the interrupt→re-evaluate→move→interrupt deadlock that
            // causes agents to die in place while pathfinding to visible food.
            if (agent.PendingAction == ActionType.Move)
            {
                if (agent.CurrentGoal == GoalType.SeekFood
                    || agent.CurrentGoal == GoalType.GatherFoodAt
                    || agent.CurrentGoal == GoalType.ReturnHome)
                    return false;
                // Urgent mode: any move gets protection — agent is in survival mode
                if (agent.CurrentMode == BehaviorMode.Urgent)
                    return false;
            }
            return true;
        }

        // Low health -> interrupt to rest (unless starving)
        if (agent.Health <= 30 && agent.Hunger > SimConfig.InterruptHungerThreshold)
            return true;

        return false;
    }

    // ── Goal Advancement ─────────────────────────────────────────────

    /// <summary>
    /// Validates and advances the agent's current goal by one step.
    /// Returns true if the goal is still active and the agent took an action.
    /// Returns false if the goal should be cleared (completed, invalid, or stale).
    /// </summary>
    private bool TryAdvanceGoal(Agent agent, World world, EventBus bus, int currentTick, Action<string>? trace)
    {
        bool isGatherGoal = agent.CurrentGoal == GoalType.GatherFoodAt || agent.CurrentGoal == GoalType.GatherResourceAt;

        if (!agent.CurrentGoal.HasValue || !agent.GoalTarget.HasValue)
        {
            if (isGatherGoal) _dbgAdvFail_NoGoalTarget++;
            return false;
        }

        var (gx, gy) = agent.GoalTarget.Value;

        // D14 Fix 2: Tighter distance leash for Home/Caretaker modes during goal continuation
        // Prevents agents silently walking 30-60 tiles from home during Move goal continuation
        // NOTE: Runs BEFORE stale goal timer so 60-tick expiry can't bypass leash checks
        if ((agent.CurrentMode == BehaviorMode.Home || agent.CurrentMode == BehaviorMode.Caretaker)
            && agent.HomeTile.HasValue)
        {
            var (hx, hy) = agent.HomeTile.Value;
            int distFromHome = Math.Max(Math.Abs(agent.X - hx), Math.Abs(agent.Y - hy));
            int leashLimit = (agent.Stage == DevelopmentStage.Infant || agent.Stage == DevelopmentStage.Youth)
                ? SimConfig.HomeModeMoveLeashChild
                : SimConfig.HomeModeMoveLeash;
            if (distFromHome > leashLimit)
            {
                trace?.Invoke($"[TRACE Agent {agent.Id}] HOME-CARETAKER-LEASH: {agent.CurrentGoal} aborted at dist {distFromHome} (limit {leashLimit}) from home");
                agent.ClearGoal();
                if (isGatherGoal) _dbgAdvFail_HomeCaretakerLeash++;
                return false; // Falls through to Phase 4 (fresh decision re-evaluation)
            }
        }

        // Directive #8 Fix 1: Distance leash during goal execution
        // D15 Fix: Forage uses ForageMaxRange; other non-Explore modes use HardMoveCeiling
        if (agent.CurrentMode != BehaviorMode.Explore && agent.HomeTile.HasValue)
        {
            var (hx, hy) = agent.HomeTile.Value;
            int distFromHome = Math.Max(Math.Abs(agent.X - hx), Math.Abs(agent.Y - hy));
            int leashDist = agent.CurrentMode == BehaviorMode.Forage
                ? SimConfig.ForageMaxRange : SimConfig.HardMoveCeiling;
            if (distFromHome > leashDist)
            {
                trace?.Invoke($"[TRACE Agent {agent.Id}] GOAL-LEASH: {agent.CurrentGoal} aborted at dist {distFromHome} (limit {leashDist}) from home");
                agent.ClearGoal();
                if (isGatherGoal) _dbgAdvFail_DistanceLeash++;
                return false; // Falls through to Phase 4 (fresh decision)
            }
        }

        // Critical interrupt: hunger overrides non-food goals
        if (agent.Hunger <= SimConfig.InterruptHungerThreshold
            && agent.CurrentGoal != GoalType.SeekFood
            && agent.CurrentGoal != GoalType.GatherFoodAt)
        {
            trace?.Invoke($"[TRACE Agent {agent.Id}] GOAL-INTERRUPTED: hunger {agent.Hunger:F0} overrides {agent.CurrentGoal}");
            if (isGatherGoal) _dbgAdvFail_HungerInterrupt++;
            agent.ClearGoal(); // Clear so Phase 3 retry doesn't re-enter interrupted goal
            return false;
        }

        // Directive #7 Fix 5: Distance-scaled stale goal timer.
        // Base 60 ticks for short goals, scaled up for longer distances.
        // Each tile of distance adds ~8 ticks (accounts for terrain cost + move processing).
        // Recovery agents get extended timeout to allow escalating pathfinding.
        int distToTarget = Math.Max(Math.Abs(agent.X - gx), Math.Abs(agent.Y - gy));
        int distanceBasedLimit = Math.Max(60, distToTarget * 8);
        int staleLimit = agent.StuckRecoveryStage > 1 ? 500 : distanceBasedLimit;
        if (currentTick - agent.GoalStartTick > staleLimit)
        {
            trace?.Invoke($"[TRACE Agent {agent.Id}] GOAL-STALE: {agent.CurrentGoal} started {currentTick - agent.GoalStartTick} ticks ago (limit={staleLimit})");
            if (isGatherGoal) _dbgAdvFail_StaleGoal++;
            agent.ClearGoal(); // Clear so Phase 3 retry doesn't loop on stale goals
            return false;
        }

        // Move chain checkpoint: every 5 consecutive moves, check whether the goal should be abandoned.
        // Previously this blindly set ForceReevaluation=true, destroying any goal targeting >5 tiles away.
        // Now we only abandon for specific reasons: survival emergency, depleted target, or stuck agent.
        agent.ConsecutiveMoveCount++;
        if (agent.ConsecutiveMoveCount >= 5)
        {
            agent.ConsecutiveMoveCount = 0;

            // Safety check: distance leash (mode-aware limit) — unchanged
            if (agent.CurrentMode != BehaviorMode.Explore && agent.HomeTile.HasValue)
            {
                var (hx2, hy2) = agent.HomeTile.Value;
                int chainDist = Math.Max(Math.Abs(agent.X - hx2), Math.Abs(agent.Y - hy2));
                int chainLimit = agent.CurrentMode == BehaviorMode.Forage
                    ? SimConfig.ForageMaxRange : SimConfig.HardMoveCeiling;
                if (chainDist > chainLimit)
                {
                    trace?.Invoke($"[TRACE Agent {agent.Id}] MOVE-CHAIN-LEASH: dist={chainDist} (limit {chainLimit}), clearing goal");
                    agent.ClearGoal();
                    if (isGatherGoal) _dbgAdvFail_MoveChainLeash++;
                    return false;
                }
            }

            // Smart abandonment: only abandon the goal for specific, justified reasons
            bool shouldAbandonGoal = false;
            string abandonReason = "";

            // Reason 1: Survival emergency — starving and not already seeking food
            if (agent.Hunger < 30
                && agent.CurrentGoal != GoalType.GatherFoodAt
                && agent.CurrentGoal != GoalType.SeekFood)
            {
                shouldAbandonGoal = true;
                abandonReason = $"survival-hunger={agent.Hunger:F0}";
            }

            // Reason 1b: Health emergency
            if (agent.Health < 30)
            {
                shouldAbandonGoal = true;
                abandonReason = $"survival-health={agent.Health:F0}";
            }

            // Reason 2: Goal target became depleted/impossible
            // Goal Commitment Fix: When target is depleted, try to find a nearby alternative
            // within 3 tiles of the original target before abandoning the goal entirely.
            if (!shouldAbandonGoal
                && (agent.CurrentGoal == GoalType.GatherFoodAt || agent.CurrentGoal == GoalType.GatherResourceAt)
                && agent.GoalTarget.HasValue)
            {
                var (tx, ty) = agent.GoalTarget.Value;
                if (world.IsInBounds(tx, ty))
                {
                    var targetTile = world.GetTile(tx, ty);
                    bool targetStillValid = GoalTargetHasRelevantResource(agent, targetTile);
                    if (!targetStillValid)
                    {
                        // Try to find nearby alternative before abandoning
                        bool redirected = TryRedirectToNearbyResource(agent, world, tx, ty, trace);
                        if (!redirected)
                        {
                            shouldAbandonGoal = true;
                            abandonReason = $"target-depleted at ({tx},{ty}), no nearby alternative";
                        }
                        else
                        {
                            trace?.Invoke($"[TRACE Agent {agent.Id}] MOVE-CHAIN-CHECKPOINT: target depleted, redirected to ({agent.GoalTarget?.X},{agent.GoalTarget?.Y})");
                            // Goal was redirected — don't abandon, continue
                        }
                    }
                }
                else
                {
                    shouldAbandonGoal = true;
                    abandonReason = $"target-out-of-bounds ({tx},{ty})";
                }
            }

            // Reason 3: Agent is stuck (position hasn't changed despite movement attempts)
            if (!shouldAbandonGoal && agent.IsStuck)
            {
                shouldAbandonGoal = true;
                abandonReason = $"stuck at ({agent.X},{agent.Y})";
            }

            if (shouldAbandonGoal)
            {
                agent.ForceReevaluation = true;
                trace?.Invoke($"[TRACE Agent {agent.Id}] MOVE-CHAIN-CHECKPOINT: abandoning {agent.CurrentGoal} — {abandonReason}");
                if (isGatherGoal) _dbgAdvFail_MoveChainAbandon++;
                return false; // Falls through to Phase 4
            }
            else
            {
                // Goal is still valid — continue walking toward target
                trace?.Invoke($"[TRACE Agent {agent.Id}] MOVE-CHAIN-CHECKPOINT: 5 moves, goal {agent.CurrentGoal} still valid, continuing");
                // Do NOT set ForceReevaluation — let the agent keep going
            }
        }

        // Already at target?
        if (agent.X == gx && agent.Y == gy)
        {
            agent.WaypointPath = null; // Clear waypoints on arrival
            bool execResult = TryExecuteGoalAtTarget(agent, world, bus, currentTick, trace);
            if (!execResult && isGatherGoal) _dbgAdvFail_ExecAtTarget++;
            return execResult;
        }

        // Directive #7 Fix 3: If agent has a waypoint path, follow waypoints instead of greedy stepping
        if (agent.WaypointPath != null && agent.WaypointIndex < agent.WaypointPath.Count)
        {
            var wp = agent.WaypointPath[agent.WaypointIndex];
            // Check if we've reached the current waypoint
            if (agent.X == wp.X && agent.Y == wp.Y)
            {
                agent.WaypointIndex++;
                if (agent.WaypointIndex >= agent.WaypointPath.Count)
                {
                    agent.WaypointPath = null;
                    // At final waypoint (home) — goal complete
                    if (agent.X == gx && agent.Y == gy)
                        return TryExecuteGoalAtTarget(agent, world, bus, currentTick, trace);
                }
                else
                {
                    wp = agent.WaypointPath[agent.WaypointIndex];
                }
            }

            if (agent.WaypointPath != null && agent.WaypointIndex < agent.WaypointPath.Count)
            {
                if (TryStartMove(agent, wp.X, wp.Y, world, trace))
                {
                    agent.RecordAction(ActionType.Move, currentTick,
                        $"Following waypoint {agent.WaypointIndex}/{agent.WaypointPath.Count} toward {agent.CurrentGoal}");
                    return true;
                }
                // Waypoint blocked — recompute path (D20: respect agent blacklist)
                var newPath = SimplePathfinder.FindPath(agent.X, agent.Y, gx, gy, world,
                    avoidTiles: agent.GetBlacklistedTileSet(currentTick) is var bl1 && bl1.Count > 0 ? bl1 : null);
                if (newPath != null && newPath.Count > 0)
                {
                    agent.WaypointPath = newPath;
                    agent.WaypointIndex = 0;
                    wp = newPath[0];
                    if (TryStartMove(agent, wp.X, wp.Y, world, trace))
                    {
                        agent.RecordAction(ActionType.Move, currentTick,
                            $"Recomputed path toward {agent.CurrentGoal} ({newPath.Count} steps)");
                        return true;
                    }
                }
                // Still can't move — give up
                trace?.Invoke($"[TRACE Agent {agent.Id}] GOAL-BLOCKED: waypoint path failed to ({gx},{gy})");
                if (isGatherGoal) _dbgAdvFail_WaypointBlocked++;
                return false;
            }
        }

        // Move toward target — use escalating pathfinding for ALL goal types
        // (not just ReturnHome; broadened so stuck agents seeking food, resources, etc. also escalate)
        bool moved = TryStartMoveEscalating(agent, gx, gy, world, currentTick, trace);

        if (moved)
        {
            agent.RecordAction(ActionType.Move, currentTick,
                $"Moving toward {agent.CurrentGoal} at ({gx},{gy})" +
                (agent.StuckRecoveryStage > 1 ? $" [stage {agent.StuckRecoveryStage}]" : ""));
            return true;
        }

        // Can't move toward target — goal is blocked.
        // Increment MoveFailCount so subsequent attempts escalate to A* pathfinding.
        // Without this, gather goals that fail to move at Stage 1 get cleared and
        // re-created from scratch, never escalating past greedy movement.
        agent.MoveFailCount++;
        trace?.Invoke($"[TRACE Agent {agent.Id}] GOAL-BLOCKED: can't move toward ({gx},{gy}), recovery stage={agent.StuckRecoveryStage}, moveFailCount={agent.MoveFailCount}");
        if (isGatherGoal) _dbgAdvFail_MoveEscFailed++;

        // Fix 5e: Blacklist position during Explore to prevent repeated stuck cycles
        if (agent.CurrentMode == BehaviorMode.Explore)
        {
            agent.ExploreStuckTicks++;
            if (agent.ExploreStuckTicks >= 3)
            {
                agent.BlacklistPosition(agent.X, agent.Y, currentTick + 5000);
                trace?.Invoke($"[TRACE Agent {agent.Id}] EXPLORE-STUCK: blacklisted ({agent.X},{agent.Y}) via goal-blocked after {agent.ExploreStuckTicks} ticks");
            }
        }

        return false;
    }

    /// <summary>
    /// Executes the terminal action of a goal when the agent has arrived at the target.
    /// Returns false to signal the goal is complete and should be cleared.
    /// </summary>
    private bool TryExecuteGoalAtTarget(Agent agent, World world, EventBus bus, int currentTick, Action<string>? trace)
    {
        var currentTile = world.GetTile(agent.X, agent.Y);

        switch (agent.CurrentGoal)
        {
            case GoalType.GatherFoodAt:
            case GoalType.GatherResourceAt:
                // Arrived at resource tile — try to start gathering
                if (agent.GoalResource.HasValue)
                {
                    var res = agent.GoalResource.Value;
                    // D21 Hotfix 2: Block non-food gather if carrying 9+ non-food items
                    bool isNonFood = !ModeTransitionManager.IsFoodResource(res);
                    if (isNonFood)
                    {
                        int nfTerm = agent.Inventory.GetValueOrDefault(ResourceType.Stone, 0)
                                   + agent.Inventory.GetValueOrDefault(ResourceType.Ore, 0)
                                   + agent.Inventory.GetValueOrDefault(ResourceType.Wood, 0)
                                   + agent.Inventory.GetValueOrDefault(ResourceType.Hide, 0)
                                   + agent.Inventory.GetValueOrDefault(ResourceType.Bone, 0);
                        if (nfTerm >= 9)
                        {
                            agent.ClearGoal();
                            return false;
                        }
                    }
                    if (currentTile.Resources.TryGetValue(res, out int amt) && amt > 0 && agent.HasInventorySpace()
                        && TryStartGatherAction(agent, agent.X, agent.Y, res, currentTick, trace))
                    {
                        agent.RecordAction(ActionType.Gather, currentTick, $"Gathering {res} (goal arrived)");
                        trace?.Invoke($"[TRACE Agent {agent.Id}] GOAL-TERMINAL: started gathering {res} at ({agent.X},{agent.Y})");
                        agent.ClearGoal();
                        return true;
                    }
                    // Resource is gone — clear stale memory
                    agent.ClearStaleMemory(agent.X, agent.Y, res);
                }
                // Also try any available food (for GatherFoodAt)
                if (agent.CurrentGoal == GoalType.GatherFoodAt
                    && TryStartGatherFood(agent, currentTile, world, bus, currentTick, trace))
                {
                    agent.RecordAction(ActionType.Gather, currentTick, "Gathering food (goal fallback)");
                    agent.ClearGoal();
                    return true;
                }
                // For GatherResourceAt: try any non-food resource at the tile
                if (agent.CurrentGoal == GoalType.GatherResourceAt && agent.HasInventorySpace())
                {
                    foreach (var kvp in currentTile.Resources)
                    {
                        if (kvp.Value > 0 && !ModeTransitionManager.IsFoodResource(kvp.Key)
                            && TryStartGatherAction(agent, agent.X, agent.Y, kvp.Key, currentTick, trace, checkNonFoodGuard: false))
                        {
                            agent.RecordAction(ActionType.Gather, currentTick, $"Gathering {kvp.Key} (resource goal fallback)");
                            agent.ClearGoal();
                            return true;
                        }
                    }
                }
                // Goal Commitment Fix: Resource depleted at target — use shared redirect
                // to search nearby tiles (5-tile radius) for same or equivalent resource.
                // This prevents agents from picking a new distant target every tick when local
                // resources exist within a few tiles of the original target.
                _dbgExecAtTargetRedirectAttempted++;
                if (TryRedirectToNearbyResource(agent, world, agent.X, agent.Y, trace))
                {
                    // Redirect found a nearby resource — start moving toward it
                    var newTarget = agent.GoalTarget!.Value;
                    if (agent.X == newTarget.X && agent.Y == newTarget.Y)
                    {
                        // Already at the redirected tile — try gathering immediately
                        var rdTile = world.GetTile(newTarget.X, newTarget.Y);
                        if (agent.GoalResource.HasValue
                            && rdTile.Resources.TryGetValue(agent.GoalResource.Value, out int rdAmt) && rdAmt > 0
                            && agent.HasInventorySpace()
                            && TryStartGatherAction(agent, newTarget.X, newTarget.Y, agent.GoalResource.Value, currentTick, trace, checkNonFoodGuard: false))
                        {
                            agent.RecordAction(ActionType.Gather, currentTick, $"Gathering {agent.GoalResource.Value} (redirect, same tile)");
                            agent.ClearGoal();
                            return true;
                        }
                    }
                    if (TryStartMove(agent, newTarget.X, newTarget.Y, world, trace))
                    {
                        agent.RecordAction(ActionType.Move, currentTick,
                            $"Redirecting to nearby {agent.GoalResource} at ({newTarget.X},{newTarget.Y})");
                        return true;
                    }
                }
                agent.ClearGoal(); // Resource gone, no nearby alternative — clear so Phase 3 doesn't retry
                return false;

            case GoalType.ReturnHome:
                // Arrived home — goal complete, let utility scorer decide next
                trace?.Invoke($"[TRACE Agent {agent.Id}] GOAL-TERMINAL: arrived home at ({agent.X},{agent.Y})");
                agent.ClearGoal();
                return false; // Fall through to full decision at home

            case GoalType.BuildAtTile:
                // Arrived at build site — start building directly
                trace?.Invoke($"[TRACE Agent {agent.Id}] GOAL-TERMINAL: arrived at build site ({agent.X},{agent.Y})");
                string? recipeId = agent.GoalRecipeId;
                agent.ClearGoal();
                if (recipeId == "lean_to" && !currentTile.HasShelter)
                {
                    int w = agent.Inventory.GetValueOrDefault(ResourceType.Wood, 0);
                    int s = agent.Inventory.GetValueOrDefault(ResourceType.Stone, 0);
                    if (w >= SimConfig.ShelterWoodCost && s >= SimConfig.ShelterStoneCost)
                    {
                        StartBuild(agent, currentTile, "lean_to", SimConfig.ShelterBuildTicks, trace);
                        agent.RecordAction(ActionType.Build, currentTick, $"Building lean-to at ({agent.X},{agent.Y})");
                        return true;
                    }
                }
                else if (recipeId == "granary" && currentTile.HasShelter && !currentTile.HasGranary)
                {
                    int w = agent.Inventory.GetValueOrDefault(ResourceType.Wood, 0);
                    int s = agent.Inventory.GetValueOrDefault(ResourceType.Stone, 0);
                    if (w >= SimConfig.GranaryWoodCost && s >= SimConfig.GranaryStoneCost)
                    {
                        StartBuild(agent, currentTile, "granary", SimConfig.GranaryBuildTicks, trace);
                        agent.RecordAction(ActionType.Build, currentTick, $"Building granary at ({agent.X},{agent.Y})");
                        return true;
                    }
                }
                return false; // Fall through to utility scorer

            case GoalType.SeekFood:
                // Arrived at food target — try gathering from tile
                if (TryStartGatherFood(agent, currentTile, world, bus, currentTick, trace))
                {
                    agent.RecordAction(ActionType.Gather, currentTick, "Gathering food (seek goal arrived)");
                    agent.ClearGoal();
                    return true;
                }
                // Try eating from home storage (SeekFood targets home when storage has food)
                if (agent.TryEatFromHomeStorage(currentTile))
                {
                    bus.Emit(currentTick, $"{agent.Name} ate from home storage (Hunger: {agent.Hunger:F0})", EventType.Action, agentId: agent.Id);
                    agent.RecordAction(ActionType.Eat, currentTick, $"Ate from home storage (hunger: {agent.Hunger:F0})");
                    trace?.Invoke($"[TRACE Agent {agent.Id}] GOAL-TERMINAL: ate from home storage at ({agent.X},{agent.Y})");
                    agent.ClearGoal();
                    return true;
                }
                // No food here — clear stale memory for this location
                if (agent.GoalResource.HasValue)
                    agent.ClearStaleMemory(agent.X, agent.Y, agent.GoalResource.Value);
                trace?.Invoke($"[TRACE Agent {agent.Id}] GOAL-FAILED: no food at seek target ({agent.X},{agent.Y})");
                return false; // Goal failed, will re-evaluate

            case GoalType.Explore:
                // Arrived at explore target — re-evaluate
                trace?.Invoke($"[TRACE Agent {agent.Id}] GOAL-TERMINAL: arrived at explore target ({agent.X},{agent.Y})");
                agent.ClearGoal();
                return false;

            case GoalType.HuntAnimal:
            {
                // Arrived at animal's last known position — look for a huntable animal nearby
                // Allow fleeing animals — pursuit logic handles chasing them
                var animalsHere = world.GetAnimalsAt(agent.X, agent.Y);
                var prey = animalsHere.FirstOrDefault(a =>
                    a.IsAlive && a.Species != AnimalSpecies.Boar && a.Species != AnimalSpecies.Wolf);
                if (prey == null)
                {
                    // Check adjacent tiles for the animal (may have moved)
                    for (int dx = -1; dx <= 1 && prey == null; dx++)
                        for (int dy = -1; dy <= 1 && prey == null; dy++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            var nearby = world.GetAnimalsAt(agent.X + dx, agent.Y + dy);
                            prey = nearby.FirstOrDefault(a =>
                                a.IsAlive && a.Species != AnimalSpecies.Boar && a.Species != AnimalSpecies.Wolf);
                        }
                }
                // Also check 2-tile radius for recently-fled animals
                if (prey == null)
                {
                    for (int dx = -2; dx <= 2 && prey == null; dx++)
                        for (int dy = -2; dy <= 2 && prey == null; dy++)
                        {
                            if (Math.Abs(dx) <= 1 && Math.Abs(dy) <= 1) continue; // Already checked
                            int nx = agent.X + dx, ny = agent.Y + dy;
                            if (!world.IsInBounds(nx, ny)) continue;
                            var nearby = world.GetAnimalsAt(nx, ny);
                            prey = nearby.FirstOrDefault(a =>
                                a.IsAlive && a.Species != AnimalSpecies.Boar && a.Species != AnimalSpecies.Wolf);
                        }
                }
                if (prey != null)
                {
                    // Start pursuit
                    agent.HuntTargetAnimalId = prey.Id;
                    agent.HuntPursuitTicks = 0;
                    agent.PendingAction = ActionType.Hunt;
                    agent.ActionProgress = 0f;
                    agent.ActionDurationTicks = 1f; // Re-evaluated each tick during pursuit
                    agent.ActionTarget = (prey.X, prey.Y);
                    agent.CurrentAction = ActionType.Hunt;
                    trace?.Invoke($"[TRACE Agent {agent.Id}] GOAL-TERMINAL: starting hunt pursuit of {prey.Species} #{prey.Id}");
                    agent.ClearGoal();
                    return true;
                }
                // Animal not here — clear goal
                trace?.Invoke($"[TRACE Agent {agent.Id}] GOAL-FAILED: no huntable animal at ({agent.X},{agent.Y})");
                agent.ClearGoal();
                return false;
            }

            case GoalType.HarvestCarcass:
            {
                // Arrived at carcass position — start harvesting
                var carcass = world.Carcasses.FirstOrDefault(c => c.IsActive && c.X == agent.X && c.Y == agent.Y);
                if (carcass != null)
                {
                    agent.PendingAction = ActionType.Harvest;
                    agent.ActionProgress = 0f;
                    agent.ActionDurationTicks = SimConfig.HarvestDuration;
                    agent.ActionTarget = (agent.X, agent.Y);
                    agent.CurrentAction = ActionType.Harvest;
                    trace?.Invoke($"[TRACE Agent {agent.Id}] GOAL-TERMINAL: butchering {carcass.Species} carcass at ({agent.X},{agent.Y})");
                    agent.ClearGoal();
                    return true;
                }
                // Carcass gone — clear stale memory
                agent.ClearStaleMemory(agent.X, agent.Y, ResourceType.Meat);
                trace?.Invoke($"[TRACE Agent {agent.Id}] GOAL-FAILED: no carcass at ({agent.X},{agent.Y})");
                agent.ClearGoal();
                return false;
            }

            case GoalType.SetTrapAt:
            {
                // Arrived at trap placement tile — start placing trap
                agent.PendingAction = ActionType.SetTrap;
                agent.ActionProgress = 0f;
                agent.ActionDurationTicks = SimConfig.TrapPlacementTicks;
                agent.ActionTarget = (agent.X, agent.Y);
                agent.CurrentAction = ActionType.SetTrap;
                trace?.Invoke($"[TRACE Agent {agent.Id}] GOAL-TERMINAL: placing trap at ({agent.X},{agent.Y})");
                agent.ClearGoal();
                return true;
            }

            default:
                return false;
        }
    }

    // ── Action Start Methods ────────────────────────────────────────────

    private void StartRest(Agent agent, Action<string>? trace)
    {
        agent.PendingAction = ActionType.Rest;
        agent.ActionProgress = 0f;
        agent.ActionDurationTicks = SimConfig.RestDuration;
        agent.CurrentAction = ActionType.Rest;
        trace?.Invoke($"[TRACE Agent {agent.Id}] StartRest: duration={SimConfig.RestDuration}");
    }

    /// <summary>v1.8 Corrections: Starts a float-based move toward the target (one step at a time).
    /// Movement duration is fractional ticks per tile based on terrain.
    /// Tries diagonal first, then falls back to horizontal/vertical if blocked.</summary>
    private bool TryStartMove(Agent agent, int targetX, int targetY, World world, Action<string>? trace)
    {
        // Calculate preferred step (diagonal via Chebyshev)
        int dx = targetX > agent.X ? 1 : targetX < agent.X ? -1 : 0;
        int dy = targetY > agent.Y ? 1 : targetY < agent.Y ? -1 : 0;

        if (dx == 0 && dy == 0)
            return false;

        // Try directions in priority order: diagonal, then each axis separately
        // This prevents getting stuck on impassable diagonal tiles
        (int, int)[] candidates;
        if (dx != 0 && dy != 0)
            candidates = new[] { (dx, dy), (dx, 0), (0, dy) };
        else
            candidates = new[] { (dx, dy) };

        foreach (var (cdx, cdy) in candidates)
        {
            int moveX = agent.X + cdx;
            int moveY = agent.Y + cdy;

            if (!world.IsInBounds(moveX, moveY))
                continue;

            // Directive #10 Fix 5: Avoid moving into world edge buffer (2 tiles)
            // Allow moving FROM edge toward center, but not INTO edge zone
            bool agentInEdge = agent.X < 2 || agent.Y < 2
                || agent.X >= world.Width - 2 || agent.Y >= world.Height - 2;
            bool targetInEdge = moveX < 2 || moveY < 2
                || moveX >= world.Width - 2 || moveY >= world.Height - 2;
            if (targetInEdge && !agentInEdge)
                continue;

            var moveTile = world.GetTile(moveX, moveY);
            if (float.IsPositiveInfinity(moveTile.MovementCostMultiplier))
                continue;

            float duration = Agent.GetMoveDuration(moveTile.Biome);

            agent.PendingAction = ActionType.Move;
            agent.ActionProgress = 0f;
            agent.ActionDurationTicks = duration;
            agent.ActionTarget = (moveX, moveY);
            agent.CurrentAction = ActionType.Move;
            agent.SetMoveInterpolation(moveX, moveY);

            trace?.Invoke($"[TRACE Agent {agent.Id}] StartMove: ({agent.X},{agent.Y})->({moveX},{moveY}), duration={duration:F3}");
            return true;
        }

        // All directions blocked
        return false;
    }

    /// <summary>
    /// Fix 1: 4-stage escalating pathfinding for stuck agents. Uses agent.MoveFailCount to determine
    /// which recovery stage to use:
    /// Stage 1 (failures 1-3): Normal A* with standard 2000 node budget.
    /// Stage 2 (failures 4-10): A* with doubled 4000 node budget.
    /// Stage 3 (failures 11-20): Greedy single-tile steps toward target.
    /// Stage 4 (failures 21+): Random walk to reposition, then reset to Stage 1.
    /// </summary>
    private bool TryStartMoveEscalating(Agent agent, int targetX, int targetY, World world, int currentTick, Action<string>? trace)
    {
        // Initialize recovery tracking distance if needed
        if (agent.HomeTile.HasValue && agent.RecoveryStartDistanceToHome == 0)
        {
            agent.RecoveryStartDistanceToHome = Math.Max(
                Math.Abs(agent.X - agent.HomeTile.Value.X),
                Math.Abs(agent.Y - agent.HomeTile.Value.Y));
        }

        // Also track distance to goal target (for all goal types, not just ReturnHome)
        if (agent.RecoveryStartDistanceToGoal == 0f)
        {
            agent.RecoveryStartDistanceToGoal = Math.Max(
                Math.Abs(agent.X - targetX),
                Math.Abs(agent.Y - targetY));
        }

        int stage = agent.StuckRecoveryStage;

        switch (stage)
        {
            case 1:
                // Stage 1: Try anti-oscillation move first (avoids recently-visited tiles),
                // falling back to standard TryStartMove if anti-oscillation fails
                if (TryStartMoveAntiOscillation(agent, targetX, targetY, world, trace))
                    return true;
                return TryStartMove(agent, targetX, targetY, world, trace);

            case 2:
            {
                // Try standard move first
                if (TryStartMove(agent, targetX, targetY, world, trace))
                    return true;

                // Standard move failed — try A* with doubled node budget (D20: respect agent blacklist)
                var avoidS2 = agent.GetBlacklistedTileSet(currentTick);
                var path = SimplePathfinder.FindPath(agent.X, agent.Y, targetX, targetY, world,
                    maxNodes: 4000, avoidTiles: avoidS2.Count > 0 ? avoidS2 : null);
                if (path != null && path.Count > 0)
                {
                    var nextStep = path[0];
                    trace?.Invoke($"[TRACE Agent {agent.Id}] ESCALATE-S2: A* (4000 budget) found path ({path.Count} steps), stepping to ({nextStep.X},{nextStep.Y})");
                    return TryStartMoveTo(agent, nextStep.X, nextStep.Y, world, trace);
                }

                trace?.Invoke($"[TRACE Agent {agent.Id}] ESCALATE-S2: A* (4000 budget) found no path from ({agent.X},{agent.Y}) to ({targetX},{targetY})");
                agent.MoveFailCount++; // Count this as a failure to escalate
                return false;
            }

            case 3:
            {
                // Greedy single-tile steps toward home
                trace?.Invoke($"[TRACE Agent {agent.Id}] ESCALATE-S3: greedy step toward ({targetX},{targetY}) from ({agent.X},{agent.Y})");
                return TryGreedyStepToward(agent, targetX, targetY, world, trace);
            }

            case 4:
            {
                // Stage 4: Emergency — behavior differs by goal type
                if (agent.CurrentGoal == GoalType.ReturnHome && agent.HomeTile.HasValue)
                {
                    // ReturnHome: random walk to reposition, then reset
                    agent.RandomWalkTickCount++;
                    int walkBudget = 10 + (agent.MoveFailCount % 10); // 10-20 ticks of random walk

                    if (agent.RandomWalkTickCount >= walkBudget)
                    {
                        // Reset to Stage 1 from new position
                        trace?.Invoke($"[TRACE Agent {agent.Id}] ESCALATE-S4: random walk complete ({agent.RandomWalkTickCount} ticks), resetting to Stage 1 from ({agent.X},{agent.Y})");
                        agent.MoveFailCount = 0;
                        agent.RandomWalkTickCount = 0;
                        if (agent.HomeTile.HasValue)
                        {
                            agent.RecoveryStartDistanceToHome = Math.Max(
                                Math.Abs(agent.X - agent.HomeTile.Value.X),
                                Math.Abs(agent.Y - agent.HomeTile.Value.Y));
                        }
                        return TryStartMove(agent, targetX, targetY, world, trace);
                    }

                    trace?.Invoke($"[TRACE Agent {agent.Id}] ESCALATE-S4: random walk tick {agent.RandomWalkTickCount}/{walkBudget}");
                    return TryRandomWalkStep(agent, world, trace);
                }
                else
                {
                    // Non-ReturnHome goals: abandon after 21+ failures
                    // Don't teleport — just clear the goal and let normal scoring pick a new action
                    trace?.Invoke($"[TRACE Agent {agent.Id}] ESCALATE-S4: abandoning {agent.CurrentGoal} after {agent.MoveFailCount} failures (non-ReturnHome)");
                    agent.ClearGoal();
                    return false;
                }
            }

            default:
                return TryStartMove(agent, targetX, targetY, world, trace);
        }
    }

    /// <summary>
    /// Directly starts a move to an adjacent tile (no target calculation, just move to the exact tile).
    /// Used by A* pathfinding to follow waypoints.
    /// </summary>
    private bool TryStartMoveTo(Agent agent, int moveX, int moveY, World world, Action<string>? trace)
    {
        if (!world.IsInBounds(moveX, moveY))
            return false;

        var moveTile = world.GetTile(moveX, moveY);
        if (float.IsPositiveInfinity(moveTile.MovementCostMultiplier))
            return false;

        float duration = Agent.GetMoveDuration(moveTile.Biome);
        agent.PendingAction = ActionType.Move;
        agent.ActionProgress = 0f;
        agent.ActionDurationTicks = duration;
        agent.ActionTarget = (moveX, moveY);
        agent.CurrentAction = ActionType.Move;
        agent.SetMoveInterpolation(moveX, moveY);

        trace?.Invoke($"[TRACE Agent {agent.Id}] StartMoveTo: ({agent.X},{agent.Y})->({moveX},{moveY}), duration={duration:F3}");
        return true;
    }

    /// <summary>
    /// Stage 3: Greedy single-tile step toward target. Picks the best adjacent walkable tile
    /// closest to home direction. If direct step is blocked, tries the two diagonal alternatives.
    /// </summary>
    private bool TryGreedyStepToward(Agent agent, int targetX, int targetY, World world, Action<string>? trace)
    {
        int dx = targetX > agent.X ? 1 : targetX < agent.X ? -1 : 0;
        int dy = targetY > agent.Y ? 1 : targetY < agent.Y ? -1 : 0;

        if (dx == 0 && dy == 0)
            return false;

        // Build a list of candidate tiles sorted by closeness to target
        var candidates = new List<(int x, int y, float dist)>();

        // All 8 neighbors
        for (int cdx = -1; cdx <= 1; cdx++)
        {
            for (int cdy = -1; cdy <= 1; cdy++)
            {
                if (cdx == 0 && cdy == 0) continue;
                int nx = agent.X + cdx;
                int ny = agent.Y + cdy;
                if (!world.IsInBounds(nx, ny)) continue;

                var tile = world.GetTile(nx, ny);
                if (float.IsPositiveInfinity(tile.MovementCostMultiplier)) continue;

                // Chebyshev distance to target
                float dist = Math.Max(Math.Abs(targetX - nx), Math.Abs(targetY - ny));
                candidates.Add((nx, ny, dist));
            }
        }

        // Sort by distance to target (closest first)
        candidates.Sort((a, b) => a.dist.CompareTo(b.dist));

        foreach (var c in candidates)
        {
            if (TryStartMoveTo(agent, c.x, c.y, world, trace))
            {
                trace?.Invoke($"[TRACE Agent {agent.Id}] GREEDY-STEP: moving to ({c.x},{c.y}), dist to target = {c.dist}");
                return true;
            }
        }

        // All directions blocked — increment failure count
        agent.MoveFailCount++;
        return false;
    }

    /// <summary>
    /// Stage 4: Random walk — pick a random walkable adjacent tile and move there.
    /// Used to reposition the agent when all directed approaches have failed.
    /// </summary>
    private bool TryRandomWalkStep(Agent agent, World world, Action<string>? trace)
    {
        var walkable = new List<(int x, int y)>();

        for (int cdx = -1; cdx <= 1; cdx++)
        {
            for (int cdy = -1; cdy <= 1; cdy++)
            {
                if (cdx == 0 && cdy == 0) continue;
                int nx = agent.X + cdx;
                int ny = agent.Y + cdy;
                if (!world.IsInBounds(nx, ny)) continue;

                var tile = world.GetTile(nx, ny);
                if (!float.IsPositiveInfinity(tile.MovementCostMultiplier))
                    walkable.Add((nx, ny));
            }
        }

        if (walkable.Count == 0)
            return false;

        var chosen = walkable[random.Next(walkable.Count)];
        trace?.Invoke($"[TRACE Agent {agent.Id}] RANDOM-WALK: ({agent.X},{agent.Y}) -> ({chosen.x},{chosen.y})");
        return TryStartMoveTo(agent, chosen.x, chosen.y, world, trace);
    }

    /// <summary>Directive #7 Fix 3: Anti-oscillation move — avoids tiles visited in the last 5 ticks.
    /// Falls back to standard TryStartMove if all non-recent directions are blocked.</summary>
    private bool TryStartMoveAntiOscillation(Agent agent, int targetX, int targetY, World world, Action<string>? trace)
    {
        int dx = targetX > agent.X ? 1 : targetX < agent.X ? -1 : 0;
        int dy = targetY > agent.Y ? 1 : targetY < agent.Y ? -1 : 0;

        if (dx == 0 && dy == 0)
            return false;

        // Build candidate list: primary direction + alternates
        var candidates = new List<(int dx, int dy)>();
        if (dx != 0 && dy != 0)
        {
            candidates.Add((dx, dy));
            candidates.Add((dx, 0));
            candidates.Add((0, dy));
            // Add perpendicular directions as escape routes
            candidates.Add((0, -dy));
            candidates.Add((-dx, 0));
        }
        else
        {
            candidates.Add((dx, dy));
            // Add perpendicular and opposite as escape routes
            if (dx != 0)
            {
                candidates.Add((dx, 1));
                candidates.Add((dx, -1));
                candidates.Add((0, 1));
                candidates.Add((0, -1));
            }
            else
            {
                candidates.Add((1, dy));
                candidates.Add((-1, dy));
                candidates.Add((1, 0));
                candidates.Add((-1, 0));
            }
        }

        // First pass: try candidates that aren't in recent positions
        bool agentInEdge = agent.X < 2 || agent.Y < 2
            || agent.X >= world.Width - 2 || agent.Y >= world.Height - 2;
        foreach (var (cdx, cdy) in candidates)
        {
            int moveX = agent.X + cdx;
            int moveY = agent.Y + cdy;

            if (!world.IsInBounds(moveX, moveY))
                continue;

            // Directive #10 Fix 5: Avoid moving into world edge buffer
            bool targetInEdge = moveX < 2 || moveY < 2
                || moveX >= world.Width - 2 || moveY >= world.Height - 2;
            if (targetInEdge && !agentInEdge)
                continue;

            var moveTile = world.GetTile(moveX, moveY);
            if (float.IsPositiveInfinity(moveTile.MovementCostMultiplier))
                continue;

            // Skip recently visited tiles to break oscillation
            if (agent.IsPositionRecent(moveX, moveY))
                continue;

            float duration = Agent.GetMoveDuration(moveTile.Biome);
            agent.PendingAction = ActionType.Move;
            agent.ActionProgress = 0f;
            agent.ActionDurationTicks = duration;
            agent.ActionTarget = (moveX, moveY);
            agent.CurrentAction = ActionType.Move;
            agent.SetMoveInterpolation(moveX, moveY);
            trace?.Invoke($"[TRACE Agent {agent.Id}] AntiOscMove: ({agent.X},{agent.Y})->({moveX},{moveY})");
            return true;
        }

        // Second pass: fall back to any passable tile (even if recently visited)
        return TryStartMove(agent, targetX, targetY, world, trace);
    }

    /// <summary>
    /// D22 Fix 2: Centralized gather dispatch. Wraps the common pattern:
    /// set PendingAction, ActionProgress, ActionDurationTicks, ActionTarget, ActionTargetResource, CurrentAction.
    /// Checks non-food inventory guard (nonFoodCount >= 9 blocks non-food gathers).
    /// Returns true if gather started, false if blocked.
    /// </summary>
    private bool TryStartGatherAction(Agent agent, int tileX, int tileY, ResourceType resource, int currentTick, Action<string>? trace, bool checkNonFoodGuard = true)
    {
        // D21 Hotfix 2: Non-food inventory guard — block non-food gathers when carrying 9+ non-food
        // Only applied to paths that had this guard before centralization (checkNonFoodGuard=true).
        if (checkNonFoodGuard && !ModeTransitionManager.IsFoodResource(resource))
        {
            int nonFoodCount = agent.Inventory.GetValueOrDefault(ResourceType.Stone, 0)
                             + agent.Inventory.GetValueOrDefault(ResourceType.Ore, 0)
                             + agent.Inventory.GetValueOrDefault(ResourceType.Wood, 0)
                             + agent.Inventory.GetValueOrDefault(ResourceType.Hide, 0)
                             + agent.Inventory.GetValueOrDefault(ResourceType.Bone, 0);
            if (nonFoodCount >= SimConfig.NonFoodInventoryHardCap) return false;
        }

        int duration = agent.GetGatherDuration();
        agent.PendingAction = ActionType.Gather;
        agent.ActionProgress = 0f;
        agent.ActionDurationTicks = duration;
        agent.ActionTarget = (tileX, tileY);
        agent.ActionTargetResource = resource;
        agent.CurrentAction = ActionType.Gather;
        return true;
    }

    private bool TryStartGatherFood(Agent agent, Tile tile, World world, EventBus bus, int currentTick, Action<string>? trace)
    {
        ResourceType[] foodTypes = { ResourceType.Berries, ResourceType.Grain, ResourceType.Meat, ResourceType.Fish };

        foreach (var food in foodTypes)
        {
            if (tile.Resources.TryGetValue(food, out int amount) && amount > 0 && agent.HasInventorySpace()
                && TryStartGatherAction(agent, tile.X, tile.Y, food, currentTick, trace))
            {
                trace?.Invoke($"[TRACE Agent {agent.Id}] StartGather: {food} at ({tile.X},{tile.Y})");
                return true;
            }
        }
        return false;
    }

    private static bool IsEdibleResource(ResourceType type)
    {
        return type == ResourceType.Berries || type == ResourceType.Grain
            || type == ResourceType.Meat || type == ResourceType.Fish
            || type == ResourceType.PreservedFood;
    }

    /// <summary>
    /// Checks whether the target tile still has resources relevant to the agent's current goal.
    /// For GatherFoodAt: tile must have food. For GatherResourceAt: tile must have the specific
    /// GoalResource, or if no GoalResource is set, any non-food resource.
    /// Used by the move-chain checkpoint to detect depleted targets without blindly abandoning goals.
    /// </summary>
    private static bool GoalTargetHasRelevantResource(Agent agent, Tile targetTile)
    {
        if (agent.CurrentGoal == GoalType.GatherFoodAt)
        {
            return targetTile.TotalFood() > 0;
        }

        if (agent.CurrentGoal == GoalType.GatherResourceAt)
        {
            // If we know the specific resource we're after, check for that
            if (agent.GoalResource.HasValue)
            {
                return targetTile.Resources.TryGetValue(agent.GoalResource.Value, out int amt) && amt > 0;
            }

            // No specific resource tracked — check for any non-food resource
            foreach (var kvp in targetTile.Resources)
            {
                if (!ModeTransitionManager.IsFoodResource(kvp.Key) && kvp.Value > 0)
                    return true;
            }
            return false;
        }

        // For non-gather goals (ReturnHome, BuildAtTile, Explore, SeekFood), target validity
        // isn't about resource presence — return true so we don't falsely abandon
        return true;
    }

    /// <summary>
    /// Goal Commitment Fix: When a gather goal's target tile is depleted, search nearby tiles
    /// (within 3 tiles of the original target) for the same or equivalent resource and redirect
    /// the goal there. Returns true if successfully redirected, false if no alternative found.
    /// </summary>
    private static bool TryRedirectToNearbyResource(Agent agent, World world, int targetX, int targetY, Action<string>? trace, int searchRadius = 5)
    {
        if (!agent.GoalResource.HasValue || !agent.HasInventorySpace())
            return false;

        var searchRes = agent.GoalResource.Value;
        bool isFood = agent.CurrentGoal == GoalType.GatherFoodAt;
        (int x, int y, ResourceType res)? best = null;
        int bestDist = int.MaxValue;

        for (int dx = -searchRadius; dx <= searchRadius; dx++)
        {
            for (int dy = -searchRadius; dy <= searchRadius; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                int sx = targetX + dx, sy = targetY + dy;
                if (!world.IsInBounds(sx, sy)) continue;
                var tile = world.GetTile(sx, sy);
                if (float.IsPositiveInfinity(tile.MovementCostMultiplier)) continue;

                // Check home distance constraint for Forage mode
                if (agent.CurrentMode == BehaviorMode.Forage && agent.HomeTile.HasValue)
                {
                    int homeDist = Math.Max(Math.Abs(sx - agent.HomeTile.Value.X),
                                            Math.Abs(sy - agent.HomeTile.Value.Y));
                    if (homeDist > SimConfig.ForageMaxRange) continue;
                }

                int dist = Math.Max(Math.Abs(sx - agent.X), Math.Abs(sy - agent.Y));

                // Same resource type
                if (tile.Resources.TryGetValue(searchRes, out int amt) && amt > 0)
                {
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        best = (sx, sy, searchRes);
                    }
                }
                // For food goals, also accept alternate food types
                else if (isFood)
                {
                    foreach (var foodType in new[] { ResourceType.Berries, ResourceType.Grain, ResourceType.Meat, ResourceType.Fish })
                    {
                        if (tile.Resources.TryGetValue(foodType, out int fAmt) && fAmt > 0)
                        {
                            if (dist < bestDist)
                            {
                                bestDist = dist;
                                best = (sx, sy, foodType);
                            }
                            break;
                        }
                    }
                }
            }
        }

        if (best.HasValue)
        {
            agent.GoalTarget = (best.Value.x, best.Value.y);
            agent.GoalResource = best.Value.res;
            agent.ModeCommit.ForageTargetTile = (best.Value.x, best.Value.y);
            trace?.Invoke($"[TRACE Agent {agent.Id}] GOAL-REDIRECT: target depleted, redirected to {best.Value.res} at ({best.Value.x},{best.Value.y}) dist={bestDist}");
            _dbgRedirectSuccess++;
            return true;
        }

        _dbgRedirectFailed++;
        return false;
    }

    /// <summary>Post-Playtest Fix 3: Calculate remaining ticks until dawn.
    /// Night spans ticks 360-480 and 0-120 within a 480-tick day.</summary>
    private static int CalculateRemainingNightTicks(int currentTick)
    {
        int hourOfDay = currentTick % SimConfig.TicksPerSimDay;
        if (hourOfDay >= SimConfig.NightStartHour) // evening portion
            return (SimConfig.TicksPerSimDay - hourOfDay) + SimConfig.NightEndHour;
        else if (hourOfDay < SimConfig.NightEndHour) // 0-120 morning portion
            return SimConfig.NightEndHour - hourOfDay;
        else
            return 0; // Not night
    }

    private bool TryStartGatherAnyResource(Agent agent, Tile tile, World world, EventBus bus, int currentTick, Action<string>? trace)
    {
        if (!agent.HasInventorySpace()) return false;

        // Prefer non-food resources (Wood, Stone, Ore) so agents accumulate crafting materials.
        // Only fall back to food if no non-food resources are available on this tile.
        ResourceType? target = null;

        // D21 Hotfix 2: Check non-food inventory count to avoid crowding
        int nonFoodCount = agent.Inventory.GetValueOrDefault(ResourceType.Stone, 0)
                         + agent.Inventory.GetValueOrDefault(ResourceType.Ore, 0)
                         + agent.Inventory.GetValueOrDefault(ResourceType.Wood, 0)
                         + agent.Inventory.GetValueOrDefault(ResourceType.Hide, 0)
                         + agent.Inventory.GetValueOrDefault(ResourceType.Bone, 0);

        // First pass: look for non-food resources (skip if carrying 9+ non-food)
        if (nonFoodCount < 9)
        {
            foreach (var kvp in tile.Resources)
            {
                if (kvp.Value > 0 && !IsEdibleResource(kvp.Key))
                {
                    target = kvp.Key;
                    break;
                }
            }
        }

        // Second pass: if no non-food found AND agent has fewer than 8 food items, gather food
        if (target == null && agent.FoodInInventory() < 8)
        {
            foreach (var kvp in tile.Resources)
            {
                if (kvp.Value > 0 && IsEdibleResource(kvp.Key))
                {
                    target = kvp.Key;
                    break;
                }
            }
        }

        if (target == null) return false;

        if (!TryStartGatherAction(agent, tile.X, tile.Y, target.Value, currentTick, trace))
            return false;

        trace?.Invoke($"[TRACE Agent {agent.Id}] StartGather: {target.Value} at ({tile.X},{tile.Y})");
        return true;
    }

    // GDD v1.8: StartTeach REMOVED — knowledge is communal within settlements.

    private void StartExperiment(Agent agent, Recipe recipe, World world, EventBus bus, int currentTick, Action<string>? trace)
    {
        // Consume resources immediately (GDD: resources consumed on attempt start)
        foreach (var kvp in recipe.RequiredResources)
        {
            agent.Inventory[kvp.Key] -= kvp.Value;
            if (agent.Inventory[kvp.Key] <= 0)
                agent.Inventory.Remove(kvp.Key);
        }

        // Calculate duration: base + 6 per prerequisite tier
        int duration = SimConfig.ExperimentDurationBase;
        duration += recipe.Tier * 6;

        agent.PendingAction = ActionType.Experiment;
        agent.ActionProgress = 0f;
        agent.ActionDurationTicks = duration;
        agent.ActionTargetRecipe = recipe.Id;
        agent.CurrentAction = ActionType.Experiment;

        trace?.Invoke($"[TRACE Agent {agent.Id}] StartExperiment: '{recipe.Name}' (tier {recipe.Tier}), duration={duration}");
    }

    private void StartBuild(Agent agent, Tile tile, string structureId, int totalDuration, Action<string>? trace)
    {
        // Apply build speed multiplier from tools
        int duration = Math.Max(1, (int)(totalDuration / agent.GetBuildSpeedMultiplier()));

        // Consume resources at start
        if (structureId == "lean_to")
        {
            agent.Inventory[ResourceType.Wood] -= SimConfig.ShelterWoodCost;
            if (agent.Inventory[ResourceType.Wood] <= 0) agent.Inventory.Remove(ResourceType.Wood);
            if (agent.Inventory.ContainsKey(ResourceType.Stone))
            {
                agent.Inventory[ResourceType.Stone] -= SimConfig.ShelterStoneCost;
                if (agent.Inventory[ResourceType.Stone] <= 0) agent.Inventory.Remove(ResourceType.Stone);
            }
        }
        else if (structureId == "granary")
        {
            agent.Inventory[ResourceType.Wood] -= SimConfig.GranaryWoodCost;
            if (agent.Inventory[ResourceType.Wood] <= 0) agent.Inventory.Remove(ResourceType.Wood);
            agent.Inventory[ResourceType.Stone] -= SimConfig.GranaryStoneCost;
            if (agent.Inventory[ResourceType.Stone] <= 0) agent.Inventory.Remove(ResourceType.Stone);
        }

        // Initialize or continue build progress
        if (!tile.BuildProgress.ContainsKey(structureId))
            tile.BuildProgress[structureId] = 0;

        agent.PendingAction = ActionType.Build;
        agent.ActionProgress = 0f;
        agent.ActionDurationTicks = duration;
        agent.ActionTarget = (tile.X, tile.Y);
        agent.ActionTargetRecipe = structureId;
        agent.CurrentAction = ActionType.Build;

        trace?.Invoke($"[TRACE Agent {agent.Id}] StartBuild: '{structureId}' at ({tile.X},{tile.Y}), duration={duration}");
    }

    // ── GDD v1.7: New Action Start Methods ─────────────────────────────

    /// <summary>GDD v1.7: Starts a visible 2-tick Cook action. Food consumed on completion.</summary>
    private void StartCook(Agent agent, int currentTick, Action<string>? trace)
    {
        agent.PendingAction = ActionType.Cook;
        agent.ActionProgress = 0f;
        agent.ActionDurationTicks = SimConfig.CookDuration;
        agent.CurrentAction = ActionType.Cook;

        trace?.Invoke($"[TRACE Agent {agent.Id}] StartCook: duration={SimConfig.CookDuration}");
    }

    /// <summary>GDD v1.7: Starts food preservation (3 ticks). Consumes 2 food → 1 PreservedFood.</summary>
    private void StartPreserve(Agent agent, int currentTick, Action<string>? trace)
    {
        agent.PendingAction = ActionType.Preserve;
        agent.ActionProgress = 0f;
        agent.ActionDurationTicks = SimConfig.PreserveFoodDuration;
        agent.CurrentAction = ActionType.Preserve;

        trace?.Invoke($"[TRACE Agent {agent.Id}] StartPreserve: duration={SimConfig.PreserveFoodDuration}");
    }

    /// <summary>v1.8 Corrections: Starts depositing food into a granary (~2-5 min).</summary>
    private void StartDeposit(Agent agent, Tile granaryTile, int currentTick, Action<string>? trace)
    {
        agent.PendingAction = ActionType.Deposit;
        agent.ActionProgress = 0f;
        agent.ActionDurationTicks = SimConfig.DepositDuration;
        agent.ActionTarget = (granaryTile.X, granaryTile.Y);
        agent.CurrentAction = ActionType.Deposit;

        trace?.Invoke($"[TRACE Agent {agent.Id}] StartDeposit: at ({granaryTile.X},{granaryTile.Y}), duration={SimConfig.DepositDuration}");
    }

    /// <summary>v1.8 Corrections: Starts withdrawing food from a granary (~2-5 min).</summary>
    private void StartWithdraw(Agent agent, Tile granaryTile, int currentTick, Action<string>? trace)
    {
        agent.PendingAction = ActionType.Withdraw;
        agent.ActionProgress = 0f;
        agent.ActionDurationTicks = SimConfig.DepositDuration;
        agent.ActionTarget = (granaryTile.X, granaryTile.Y);
        agent.CurrentAction = ActionType.Withdraw;

        trace?.Invoke($"[TRACE Agent {agent.Id}] StartWithdraw: at ({granaryTile.X},{granaryTile.Y}), duration={SimConfig.DepositDuration}");
    }

    private void StartTendFarm(Agent agent, int x, int y, Action<string>? trace)
    {
        agent.PendingAction = ActionType.TendFarm;
        agent.ActionProgress = 0f;
        agent.ActionDurationTicks = SimConfig.TendFarmDuration;
        agent.ActionTarget = (x, y);
        agent.CurrentAction = ActionType.TendFarm;

        trace?.Invoke($"[TRACE Agent {agent.Id}] StartTendFarm: at ({x},{y}), duration={SimConfig.TendFarmDuration}");
    }

    /// <summary>
    /// GDD v1.7: Weighted explore — prefers directions toward food, good biomes, and unexplored areas.
    /// Falls back to pure random if all weights are equal.
    /// </summary>
    private void StartExplore(Agent agent, World world, EventBus bus, int currentTick, Action<string>? trace)
    {
        // Build list of candidate directions with weights
        var candidates = new List<(int dx, int dy, float weight)>();

        // Get remembered food locations for directional bias
        var rememberedFood = agent.GetRememberedFood(currentTick);

        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;

                int newX = agent.X + dx;
                int newY = agent.Y + dy;

                if (!world.IsInBounds(newX, newY)) continue;

                var tile = world.GetTile(newX, newY);
                if (float.IsPositiveInfinity(tile.MovementCostMultiplier)) continue;

                float weight = 1.0f; // Base weight

                // Biome preference: plains/forest are desirable, mountain/desert less so
                switch (tile.Biome)
                {
                    case BiomeType.Plains:
                    case BiomeType.Forest:
                        weight += 2.0f;
                        break;
                    case BiomeType.Mountain:
                    case BiomeType.Desert:
                        weight = Math.Max(0.1f, weight - 2.0f);
                        break;
                    default:
                        weight += 1.0f; // Coast, water proximity
                        break;
                }

                // Food memory bias: if this direction moves toward remembered food, boost
                foreach (var foodMem in rememberedFood)
                {
                    int currentDist = Math.Max(Math.Abs(foodMem.X - agent.X), Math.Abs(foodMem.Y - agent.Y));
                    int newDist = Math.Max(Math.Abs(foodMem.X - newX), Math.Abs(foodMem.Y - newY));
                    if (newDist < currentDist)
                    {
                        weight += 3.0f;
                        break; // One food source bonus is enough
                    }
                }

                // Novelty bias: avoid tiles we've been on recently (check memory for recent visits)
                bool recentlyVisited = agent.Memory.Any(m =>
                    m.X == newX && m.Y == newY
                    && currentTick - m.TickObserved <= 50);
                if (!recentlyVisited)
                    weight += 1.0f;

                // Social cohesion bias: prefer directions toward recently-seen agents
                var rememberedAgents = agent.GetRememberedAgents(currentTick);
                foreach (var agentMem in rememberedAgents)
                {
                    int curDist = Math.Max(Math.Abs(agentMem.X - agent.X), Math.Abs(agentMem.Y - agent.Y));
                    int newDist = Math.Max(Math.Abs(agentMem.X - newX), Math.Abs(agentMem.Y - newY));
                    if (newDist < curDist)
                    {
                        weight += 2.0f;
                        break;
                    }
                }

                // Home bias: prefer directions toward known shelters
                var rememberedShelters = agent.GetRememberedShelters(currentTick);
                foreach (var shelterMem in rememberedShelters)
                {
                    int curDist = Math.Max(Math.Abs(shelterMem.X - agent.X), Math.Abs(shelterMem.Y - agent.Y));
                    int newDist = Math.Max(Math.Abs(shelterMem.X - newX), Math.Abs(shelterMem.Y - newY));
                    if (newDist < curDist)
                    {
                        weight += 1.5f;
                        break;
                    }
                }

                candidates.Add((dx, dy, weight));
            }
        }

        if (candidates.Count == 0)
        {
            agent.CurrentAction = ActionType.Idle;
            return;
        }

        // Anti-backtrack: penalize the direction we just came from (prevents oscillation)
        if (agent.ActionTarget.HasValue)
        {
            int prevDx = agent.ActionTarget.Value.X - agent.X;
            int prevDy = agent.ActionTarget.Value.Y - agent.Y;
            // The direction we came FROM is opposite to our last move target
            // If we moved TO target, we came FROM the opposite direction
            // So the "backtrack" direction is moving back toward where we came from
            int backDx = -Math.Sign(prevDx);
            int backDy = -Math.Sign(prevDy);
            if (backDx != 0 || backDy != 0)
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    var c = candidates[i];
                    if (c.dx == backDx && c.dy == backDy)
                    {
                        candidates[i] = (c.dx, c.dy, c.weight * 0.2f); // Heavy penalty for backtracking
                    }
                }
            }
        }

        // Weighted random selection
        float totalWeight = 0;
        foreach (var c in candidates) totalWeight += c.weight;

        float roll = (float)(random.NextDouble() * totalWeight);
        float cumulative = 0;
        (int dx, int dy, float weight) chosen = candidates[0];

        foreach (var c in candidates)
        {
            cumulative += c.weight;
            if (roll <= cumulative)
            {
                chosen = c;
                break;
            }
        }

        int targetX = agent.X + chosen.dx;
        int targetY = agent.Y + chosen.dy;

        if (TryStartMove(agent, targetX, targetY, world, trace))
        {
            agent.CurrentAction = ActionType.Explore;
            return;
        }

        // Fallback: try any passable direction
        foreach (var c in candidates)
        {
            if (TryStartMove(agent, agent.X + c.dx, agent.Y + c.dy, world, trace))
            {
                agent.CurrentAction = ActionType.Explore;
                return;
            }
        }

        agent.CurrentAction = ActionType.Idle;
    }

    // ── Action Completion ───────────────────────────────────────────────

    private void CompleteAction(Agent agent, World world, EventBus bus, int currentTick, Action<string>? trace)
    {
        var action = agent.PendingAction!.Value;
        int duration = (int)agent.ActionDurationTicks;

        // Directive #10 Fix 1: Reset consecutive move count on non-Move completions
        if (action != ActionType.Move)
        {
            agent.ConsecutiveMoveCount = 0;
            // D18.2: Only reset MoveFailCount if agent has actually moved since goal was set.
            // Otherwise, stuck agents who eat (Fix 2 critical eat) would lose their
            // escalation progress and never advance past Stage 1.
            if (agent.X != agent.GoalSetX || agent.Y != agent.GoalSetY)
            {
                agent.MoveFailCount = 0; // Directive #11 Fix 1: Reset move fail counter on non-Move action
            }
            agent.RandomWalkTickCount = 0;
        }

        switch (action)
        {
            case ActionType.Move:
                CompleteMove(agent, world, bus, currentTick, trace);
                break;
            case ActionType.Gather:
                CompleteGather(agent, world, bus, currentTick, trace);
                break;
            case ActionType.Rest:
            case ActionType.GrowingUp:
                CompleteRest(agent, world, trace);
                break;
            // GDD v1.8: Teach case REMOVED — knowledge is communal
            case ActionType.Experiment:
                CompleteExperiment(agent, world, bus, currentTick, trace);
                break;
            case ActionType.Build:
                CompleteBuild(agent, world, bus, currentTick, trace);
                break;
            case ActionType.TendFarm:
                CompleteTendFarm(agent, world, bus, currentTick, trace);
                break;
            // GDD v1.7: new action completions
            case ActionType.Cook:
                CompleteCook(agent, bus, currentTick, trace);
                break;
            case ActionType.Preserve:
                CompletePreserve(agent, world, bus, currentTick, trace);
                break;
            case ActionType.Deposit:
                CompleteDeposit(agent, world, bus, currentTick, trace);
                break;
            case ActionType.Withdraw:
                CompleteWithdraw(agent, world, bus, currentTick, trace);
                break;
            case ActionType.DepositHome:
                CompleteDepositHome(agent, world, bus, currentTick, trace);
                break;
            // Directive #5 Fix 3b-3c: New action completions
            case ActionType.ClearLand:
                CompleteClearLand(agent, world, bus, currentTick, trace);
                break;
            case ActionType.TendAnimals:
                CompleteTendAnimals(agent, world, bus, currentTick, trace);
                break;
            case ActionType.Harvest:
                CompleteHarvest(agent, world, bus, currentTick, trace);
                break;
            case ActionType.SetTrap:
                CompleteSetTrap(agent, world, bus, currentTick, trace);
                break;
            // D25d: Slaughter completion (2 tick action)
            case ActionType.Slaughter:
                CompleteSlaughter(agent, world, bus, currentTick, trace);
                break;
        }

        // Log the completion — use last recorded action detail if available
        var lastRecord = agent.GetLastActions(1).FirstOrDefault();
        string completionDetail = (lastRecord.Tick == currentTick) ? lastRecord.Detail : "";
        // Directive #11 Fix 1: Log "failure" when Move failed, "success" otherwise
        string result = (action == ActionType.Move && !_lastMoveSucceeded) ? "failure" : "success";
        Logger?.LogCompletion(currentTick, agent, action.ToString(), result, completionDetail, duration);
        _lastMoveSucceeded = true; // Reset for next action

        agent.ForceActivePerception = true; // GDD v1.6.2: force active scan after action completion
        agent.ClearPendingAction();
    }

    /// <summary>Tracks whether the last CompleteMove succeeded. Used by CompleteAction for logging.</summary>
    private bool _lastMoveSucceeded = true;

    private void CompleteMove(Agent agent, World world, EventBus bus, int currentTick, Action<string>? trace)
    {
        if (!agent.ActionTarget.HasValue) return;

        var (tx, ty) = agent.ActionTarget.Value;

        // Hard distance guard: reject moves that would INCREASE distance beyond mode limit from home.
        // Always allow moves that decrease distance (i.e., moves toward home) even if already past the ceiling.
        // Mode-aware limits: Explore=ExploreMaxRange, Forage=ForageMaxRange, others=HardMoveCeiling.
        if (agent.HomeTile.HasValue)
        {
            var (hx, hy) = agent.HomeTile.Value;
            int currentDist = Math.Max(Math.Abs(agent.X - hx), Math.Abs(agent.Y - hy));
            int newDist = Math.Max(Math.Abs(tx - hx), Math.Abs(ty - hy));
            int moveLimit = agent.CurrentMode == BehaviorMode.Explore ? SimConfig.ExploreMaxRange
                : agent.CurrentMode == BehaviorMode.Forage ? SimConfig.ForageMaxRange
                : SimConfig.HardMoveCeiling;
            if (newDist > moveLimit && newDist > currentDist)
            {
                // D18.2: Count this as a move failure so the escalating pathfinder can engage.
                // Without this, the Hard Distance Ceiling greedy step picks further-from-home
                // neighbors, the guard rejects them, but MoveFailCount never increments —
                // the agent is stuck in an infinite loop with MoveFailCount=0.
                agent.MoveFailCount++;
                trace?.Invoke($"[TRACE Agent {agent.Id}] MOVE-DISTANCE-GUARD: rejecting move to ({tx},{ty}), dist={newDist} exceeds limit={moveLimit}, mfc={agent.MoveFailCount}");
                agent.ClearGoal();
                _lastMoveSucceeded = false;
                return;
            }
        }

        bool moved = agent.MoveTo(tx, ty, world);

        if (!moved)
        {
            agent.MoveFailCount++;
            _lastMoveSucceeded = false;
            trace?.Invoke($"[TRACE Agent {agent.Id}] CompleteMove: MoveTo FAILED at ({agent.X},{agent.Y}) target ({tx},{ty}), fail count: {agent.MoveFailCount}, stage: {agent.StuckRecoveryStage}");

            // After 3 consecutive failures, clear goal and blacklist the target tile
            if (agent.MoveFailCount >= 3)
            {
                agent.ClearGoal();
                // D12 Fix 1: Blacklist the target tile for 2000 ticks
                agent.BlacklistTile(tx, ty, currentTick + 2000);
                // Also blacklist agent's current stuck position
                agent.BlacklistTile(agent.X, agent.Y, currentTick + 2000);
                trace?.Invoke($"[TRACE Agent {agent.Id}] CompleteMove: goal cleared + blacklisted ({tx},{ty}) and ({agent.X},{agent.Y}) after {agent.MoveFailCount} move failures");
            }

            // After 20 consecutive failures, emergency recovery
            if (agent.MoveFailCount >= 20)
            {
                if (agent.CurrentGoal == GoalType.ReturnHome && agent.HomeTile.HasValue)
                {
                    // ReturnHome: try A* with higher budget before teleporting (D20: respect blacklist)
                    int homeX = agent.HomeTile.Value.X;
                    int homeY = agent.HomeTile.Value.Y;
                    var avoidEmerg = agent.GetBlacklistedTileSet(currentTick);
                    var path = SimplePathfinder.FindPath(agent.X, agent.Y, homeX, homeY, world,
                        maxNodes: 8000, avoidTiles: avoidEmerg.Count > 0 ? avoidEmerg : null);
                    if (path != null && path.Count > 0)
                    {
                        agent.WaypointPath = path;
                        agent.WaypointIndex = 0;
                        agent.CurrentGoal = GoalType.ReturnHome;
                        agent.GoalTarget = (homeX, homeY);
                        agent.GoalStartTick = currentTick;
                        agent.MoveFailCount = 0;
                        trace?.Invoke($"[TRACE Agent {agent.Id}] CompleteMove: found A* path with extended budget ({path.Count} steps) from ({agent.X},{agent.Y}) to home");
                        bus?.Emit(currentTick, $"UNSTUCK: {agent.Name} found path home from ({agent.X},{agent.Y}), {path.Count} steps",
                            EventType.Info, agent.Id);
                    }
                    else
                    {
                        // Truly unreachable — teleport as absolute last resort
                        int oldX = agent.X, oldY = agent.Y;
                        agent.X = homeX;
                        agent.Y = homeY;
                        world.UpdateAgentPosition(agent, oldX, oldY, homeX, homeY);
                        agent.ClearGoal();
                        agent.MoveFailCount = 0;
                        trace?.Invoke($"[TRACE Agent {agent.Id}] CompleteMove: EMERGENCY TELEPORT from ({oldX},{oldY}) to home ({homeX},{homeY}) — no path even with extended budget");
                        bus?.Emit(currentTick, $"EMERGENCY TELEPORT: {agent.Name} from ({oldX},{oldY}) to home ({homeX},{homeY}) — truly unreachable",
                            EventType.Info, agent.Id);
                    }
                }
                else
                {
                    // Non-ReturnHome goals: abandon after 20+ failures (don't teleport)
                    trace?.Invoke($"[TRACE Agent {agent.Id}] CompleteMove: abandoning {agent.CurrentGoal} after {agent.MoveFailCount} failures");
                    agent.ClearGoal();
                }
            }
            return;
        }

        // Successful move — check if we moved closer to goal target to reset recovery
        // Track progress toward the current goal target (any goal, not just ReturnHome)
        if (agent.CurrentGoal.HasValue && agent.GoalTarget.HasValue)
        {
            var (gx, gy) = agent.GoalTarget.Value;
            float currentDist = Math.Max(Math.Abs(agent.X - gx), Math.Abs(agent.Y - gy));

            if (agent.MoveFailCount > 0)
            {
                // We're in escalation — check if we made progress toward goal
                if (currentDist < agent.RecoveryStartDistanceToGoal - 0.5f)
                {
                    // Made progress toward goal — reset escalation
                    int oldStage = agent.StuckRecoveryStage;
                    agent.MoveFailCount = 0;
                    agent.RandomWalkTickCount = 0;
                    agent.RecoveryStartDistanceToGoal = currentDist;
                    agent.RecoveryStartDistanceToHome = agent.HomeTile.HasValue
                        ? Math.Max(Math.Abs(agent.X - agent.HomeTile.Value.X), Math.Abs(agent.Y - agent.HomeTile.Value.Y))
                        : 0;
                    if (oldStage > 1)
                        trace?.Invoke($"[TRACE Agent {agent.Id}] CompleteMove: progress toward {agent.CurrentGoal} (dist={currentDist:F1}), recovery stage reset from {oldStage} to 1");
                }
                else
                {
                    // D18.2: Not closer to goal — DON'T reset MoveFailCount.
                    // The agent can move physically but isn't making navigation progress.
                    // Preserving MoveFailCount lets the escalating pathfinder advance
                    // through stages (A*, greedy stepping, emergency recovery) instead
                    // of being stuck at Stage 1 forever.
                    trace?.Invoke($"[TRACE Agent {agent.Id}] CompleteMove: moved but no progress toward {agent.CurrentGoal} (dist={currentDist:F1}), keeping MoveFailCount={agent.MoveFailCount}");
                    agent.RandomWalkTickCount = 0;
                }
            }
            else
            {
                // Not in escalation — just reset
                agent.MoveFailCount = 0;
                agent.RandomWalkTickCount = 0;
            }

            // Record current distance for future progress checks
            if (agent.MoveFailCount > 0 && agent.RecoveryStartDistanceToGoal == 0f)
            {
                agent.RecoveryStartDistanceToGoal = currentDist;
            }
        }
        else if (agent.HomeTile.HasValue)
        {
            // No goal target but have home — check home distance
            int newDist = Math.Max(
                Math.Abs(agent.X - agent.HomeTile.Value.X),
                Math.Abs(agent.Y - agent.HomeTile.Value.Y));
            if (newDist < agent.RecoveryStartDistanceToHome)
            {
                int oldStage = agent.StuckRecoveryStage;
                agent.MoveFailCount = 0;
                agent.RandomWalkTickCount = 0;
                agent.RecoveryStartDistanceToHome = newDist;
                if (oldStage > 1)
                    trace?.Invoke($"[TRACE Agent {agent.Id}] CompleteMove: progress toward home (dist={newDist}), recovery stage reset from {oldStage} to 1");
            }
            else
            {
                // D18.2: Not closer to home — preserve MoveFailCount if in escalation.
                if (agent.MoveFailCount == 0)
                {
                    agent.MoveFailCount = 0;
                    agent.RandomWalkTickCount = 0;
                }
                else
                {
                    trace?.Invoke($"[TRACE Agent {agent.Id}] CompleteMove: moved but no progress toward home (dist={newDist}), keeping MoveFailCount={agent.MoveFailCount}");
                    agent.RandomWalkTickCount = 0;
                }
            }
        }
        else
        {
            // No home, no goal target — reset on any successful move
            agent.MoveFailCount = 0;
            agent.RandomWalkTickCount = 0;
        }
        _lastMoveSucceeded = true;
        trace?.Invoke($"[TRACE Agent {agent.Id}] CompleteMove: arrived at ({tx},{ty})");

        // D21 Fix 3: Opportunistic pickup — passive resource collection during movement.
        // When an agent moves into a tile with Stone/Ore/Wood, there's a 25% chance to
        // pick up 1 unit. Represents casually pocketing interesting objects while walking.
        // Infants (age < ChildInfantAge) cannot; Youth and Adults can.
        if (agent.Stage != DevelopmentStage.Infant && agent.HasInventorySpace()
            && agent.Hunger >= SimConfig.NonFoodPickupHungerGate  // D21 Hotfix: hungry agents don't stop for rocks
            && (agent.Inventory.GetValueOrDefault(ResourceType.Stone, 0)
              + agent.Inventory.GetValueOrDefault(ResourceType.Ore, 0)
              + agent.Inventory.GetValueOrDefault(ResourceType.Wood, 0)
              + agent.Inventory.GetValueOrDefault(ResourceType.Hide, 0)
              + agent.Inventory.GetValueOrDefault(ResourceType.Bone, 0)) <= SimConfig.NonFoodPickupCap)  // D21 Hotfix: cap non-food items
        {
            var arrivalTile = world.GetTile(tx, ty);
            // Eligible resource types: Stone, Ore, Wood (NOT food)
            var eligible = new List<ResourceType>();
            if (arrivalTile.Resources.TryGetValue(ResourceType.Stone, out int stoneAmt) && stoneAmt > 0)
                eligible.Add(ResourceType.Stone);
            if (arrivalTile.Resources.TryGetValue(ResourceType.Ore, out int oreAmt) && oreAmt > 0)
                eligible.Add(ResourceType.Ore);
            if (arrivalTile.Resources.TryGetValue(ResourceType.Wood, out int woodAmt) && woodAmt > 0)
                eligible.Add(ResourceType.Wood);

            if (eligible.Count > 0)
            {
                // Deterministic RNG: seed from tick + agent ID for reproducibility
                int pickupSeed = currentTick * 31 + agent.Id * 7919;
                var pickupRng = new Random(pickupSeed);
                if (pickupRng.NextDouble() < SimConfig.OPPORTUNISTIC_PICKUP_CHANCE)
                {
                    // Pick one eligible type at random (deterministic)
                    var picked = eligible[pickupRng.Next(eligible.Count)];
                    // D22: Route through TryAddToInventory. Outer guards are stricter (<=5, hunger>=50)
                    // than TryAddToInventory defaults (>=9, hunger<50), so inner guards never trigger.
                    if (agent.TryAddToInventory(picked, 1))
                    {
                        arrivalTile.Resources[picked]--;
                        if (arrivalTile.Resources[picked] <= 0)
                            arrivalTile.Resources.Remove(picked);
                        trace?.Invoke($"[TRACE Agent {agent.Id}] picked up 1 {picked} while moving through ({tx},{ty})");
                    }
                }
            }
        }

        // GDD v1.8 Section 5: Explorer return — transfer geographic discoveries to settlement lore
        // when agent arrives at their home tile with pending discoveries.
        if (agent.HomeTile.HasValue
            && tx == agent.HomeTile.Value.X && ty == agent.HomeTile.Value.Y
            && agent.PendingGeographicDiscoveries.Count > 0
            && _currentKnowledgeSystem != null && _currentSettlements != null)
        {
            foreach (var discovery in agent.PendingGeographicDiscoveries)
            {
                _currentKnowledgeSystem.AddGeographicKnowledge(
                    agent, discovery.X, discovery.Y, discovery.FeatureType,
                    discovery.Resource, _currentSettlements, bus, currentTick);
            }
            agent.ClearPendingGeographicDiscoveries();
            trace?.Invoke($"[TRACE Agent {agent.Id}] CompleteMove: transferred geographic discoveries to settlement");
        }
    }

    private void CompleteGather(Agent agent, World world, EventBus bus, int currentTick, Action<string>? trace)
    {
        if (!agent.ActionTarget.HasValue || !agent.ActionTargetResource.HasValue) return;

        var (tx, ty) = agent.ActionTarget.Value;
        var tile = world.GetTile(tx, ty);
        var resource = agent.ActionTargetResource.Value;

        int gathered = agent.GatherFrom(tile, resource, world: world);
        if (gathered > 0)
        {
            // Track gathering on tile for overgrazing
            tile.TicksSinceLastGathered = 0;

            // GDD v1.7.1: Co-gathering builds social bonds
            var coLocated = world.GetAgentsAt(tx, ty);
            foreach (var other in coLocated)
            {
                if (other.Id != agent.Id && other.IsAlive)
                    IncrementSocialBond(agent, other);
            }

            // D12 Fix 2: Track gather count during Forage mode
            if (agent.CurrentMode == BehaviorMode.Forage)
                agent.ForageGatherCount++;

            bus.Emit(currentTick,
                $"{agent.Name} gathered {gathered} {resource} at ({tx},{ty})",
                EventType.Action, agentId: agent.Id);
            trace?.Invoke($"[TRACE Agent {agent.Id}] CompleteGather: {gathered} {resource} at ({tx},{ty})");
        }
        else
        {
            // GDD v1.8 Section 5: Resource was gone — clear stale memory immediately.
            // "Acting on stale memory and finding the resource gone clears the memory."
            agent.ClearStaleMemory(tx, ty, resource);
            trace?.Invoke($"[TRACE Agent {agent.Id}] CompleteGather: stale memory cleared for {resource} at ({tx},{ty})");

            // Also mark in settlement geographic lore as depleted (if applicable)
            if (_currentSettlements != null)
                _currentKnowledgeSystem?.MarkResourceDepleted(tx, ty, _currentSettlements);
        }
    }

    private void CompleteRest(Agent agent, World world, Action<string>? trace)
    {
        trace?.Invoke($"[TRACE Agent {agent.Id}] CompleteRest: finished resting (health={agent.Health})");

        // Fix B1: Clear stale goal on rest completion so the agent re-evaluates
        // conditions (e.g., child now hungry) instead of blindly resuming pre-rest activity.
        agent.ClearGoal();

        // GDD v1.7.1: Adopt HomeTile if agent has no home and rests at a sheltered tile
        if (!agent.HomeTile.HasValue)
        {
            var tile = world.GetTile(agent.X, agent.Y);
            if (tile.HasShelter)
            {
                agent.HomeTile = (agent.X, agent.Y);
                trace?.Invoke($"[TRACE Agent {agent.Id}] CompleteRest: adopted HomeTile ({agent.X},{agent.Y})");
            }
        }

        // GDD v1.7.1: Resting near another agent builds bonds
        var coLocated = world.GetAgentsAt(agent.X, agent.Y);
        foreach (var other in coLocated)
        {
            if (other.Id != agent.Id && other.IsAlive)
                IncrementSocialBond(agent, other);
        }
    }

    // GDD v1.8: CompleteTeach REMOVED — knowledge is communal within settlements.
    // Knowledge propagation is handled by the SettlementKnowledge system in Simulation.cs.

    private void CompleteExperiment(Agent agent, World world, EventBus bus, int currentTick, Action<string>? trace)
    {
        if (agent.ActionTargetRecipe == null) return;

        var recipe = RecipeRegistry.AllRecipes.FirstOrDefault(r => r.Id == agent.ActionTargetRecipe);
        if (recipe == null) return;

        // Calculate success chance: base + familiarity + collaboration + tool
        float chance = recipe.BaseChance;
        chance += agent.GetFamiliarityBonus(recipe.Id);

        // GDD v1.8: Collaboration bonus from adjacent agents, capped at 10%
        var adjacent = world.GetAdjacentAgents(agent.X, agent.Y);
        var samePos = world.GetAgentsAt(agent.X, agent.Y).Where(a => a.Id != agent.Id);
        int knowledgeableNeighbors = 0;
        foreach (var other in adjacent.Concat(samePos))
        {
            if (other.IsAlive && other.Knowledge.Count > 0)
                knowledgeableNeighbors++;
        }
        float collaborationBonus = Math.Min(
            knowledgeableNeighbors * SimConfig.CollaborationBonusPerAgent,
            SimConfig.CollaborationBonusCap);
        chance += collaborationBonus;

        // Tool bonus — having any basic tool aids experimentation
        if (agent.Knowledge.Contains("stone_knife") || agent.Knowledge.Contains("crude_axe"))
            chance += SimConfig.ToolDiscoveryBonus;

        // Monument bonus: +5% experiment bonus if within 3 tiles of a monument
        // (Phase 4 wiring — checks once monuments are buildable)

        if (random.NextDouble() < chance)
        {
            agent.LearnDiscovery(recipe.Output);
            trace?.Invoke($"[TRACE Agent {agent.Id}] CompleteExperiment: SUCCESS '{recipe.Name}' (chance={chance:P1})");
            bus.Emit(currentTick,
                $"{agent.Name} discovered '{recipe.Name}'! ({recipe.Output})",
                EventType.Discovery, agentId: agent.Id, recipeId: recipe.Id);

            // GDD v1.8: Notify communal knowledge system of discovery
            OnDiscoveryCallback?.Invoke(agent, recipe.Output);
        }
        else
        {
            agent.RecordFailedAttempt(recipe.Id);
            trace?.Invoke($"[TRACE Agent {agent.Id}] CompleteExperiment: FAIL '{recipe.Name}' (chance={chance:P1})");
            bus.Emit(currentTick,
                $"{agent.Name} failed to discover '{recipe.Name}'",
                EventType.Action, agentId: agent.Id);
        }
    }

    private void CompleteBuild(Agent agent, World world, EventBus bus, int currentTick, Action<string>? trace)
    {
        if (!agent.ActionTarget.HasValue || agent.ActionTargetRecipe == null) return;

        var (tx, ty) = agent.ActionTarget.Value;
        var tile = world.GetTile(tx, ty);
        var structureId = agent.ActionTargetRecipe;

        // Complete the structure
        // US-008: Shelter on same tile replaces old shelter in tile structures
        bool isShelter = structureId == "lean_to" || structureId == "shelter" || structureId == "improved_shelter";
        if (isShelter)
        {
            tile.Structures.RemoveAll(s => s == "lean_to" || s == "shelter" || s == "improved_shelter");
        }
        tile.Structures.Add(structureId);
        tile.BuildProgress.Remove(structureId);

        // GDD v1.7.1: Set HomeTile when building a shelter (first shelter or closer to current pos)
        if (isShelter)
        {
            agent.HomeTile = (tx, ty);
            trace?.Invoke($"[TRACE Agent {agent.Id}] CompleteBuild: HomeTile set to ({tx},{ty})");
        }

        // US-007: Settlement founding on shelter build
        if (isShelter && _currentSettlements != null)
        {
            if (agent.SettlementId == null)
            {
                // Found a new settlement
                // Use a separate RNG seeded from tile position + tick to avoid cascading the AgentAI random (anti-pattern #11)
                var nameRng = new Random(tx * 31 + ty * 17 + currentTick);
                var settlement = new Settlement
                {
                    Name = SettlementNameGenerator.Generate(nameRng),
                    CenterTile = (tx, ty),
                    FoundedTick = currentTick,
                    ShelterCount = 1,
                };
                settlement.Members.Add(agent.Id);
                settlement.Structures.Add((tx, ty, structureId));
                settlement.RecalculateShelterQuality();
                agent.SettlementId = settlement.Id;

                // Add nearby agents within SpawnClusterRadius
                if (_currentAllAgents != null)
                {
                    foreach (var other in _currentAllAgents)
                    {
                        if (other.Id == agent.Id || !other.IsAlive) continue;
                        int dist = Math.Max(Math.Abs(other.X - tx), Math.Abs(other.Y - ty));
                        if (dist <= SimConfig.SpawnClusterRadius && other.SettlementId == null)
                        {
                            settlement.Members.Add(other.Id);
                            other.SettlementId = settlement.Id;
                            trace?.Invoke($"[TRACE Agent {other.Id}] Joined settlement '{settlement.Name}' (nearby at founding)");
                        }
                    }
                }

                // US-009: Calculate initial territory
                settlement.RecalculateTerritory(world.Width, world.Height);
                _currentSettlements.Add(settlement);
                trace?.Invoke($"[TRACE Agent {agent.Id}] Founded settlement '{settlement.Name}' at ({tx},{ty}) with {settlement.Members.Count} members");
                bus.Emit(currentTick,
                    $"Settlement '{settlement.Name}' founded at ({tx},{ty}) with {settlement.Members.Count} members!",
                    EventType.Discovery, agentId: agent.Id);
            }
            else
            {
                // Add structure to existing settlement
                var existing = _currentSettlements.FirstOrDefault(s => s.Id == agent.SettlementId);
                if (existing != null)
                {
                    // US-008: Shelter on same tile replaces old one
                    if (isShelter)
                    {
                        existing.Structures.RemoveAll(s => s.X == tx && s.Y == ty
                            && Settlement.StructureToShelterTier(s.Type) != ShelterTier.None);
                    }
                    existing.Structures.Add((tx, ty, structureId));
                    existing.ShelterCount = existing.Structures.Count(s =>
                        Settlement.StructureToShelterTier(s.Type) != ShelterTier.None);
                    existing.RecalculateShelterQuality();
                    // US-009: Recalculate territory on structure change
                    existing.RecalculateTerritory(world.Width, world.Height);
                    trace?.Invoke($"[TRACE Agent {agent.Id}] Added {structureId} to settlement '{existing.Name}' (shelter quality: {existing.ShelterQuality}, {existing.ShelterCount} shelters)");
                }
            }
        }

        // D25d: When animal_pen completes, create a Pen entity
        if (structureId == "animal_pen")
        {
            // Default capacity = small animal pen (can hold 5 Rabbit/Pig or 3 large animals)
            var pen = new Pen(tx, ty, SimConfig.PenCapacitySmall, agent.Id);
            world.Pens.Add(pen);
            trace?.Invoke($"[TRACE Agent {agent.Id}] CompleteBuild: created Pen {pen.Id} at ({tx},{ty}) capacity={pen.Capacity}");
        }

        trace?.Invoke($"[TRACE Agent {agent.Id}] CompleteBuild: '{structureId}' at ({tx},{ty})");
        bus.Emit(currentTick,
            $"{agent.Name} completed a {structureId} at ({tx},{ty})!",
            EventType.Discovery, agentId: agent.Id);
    }

    private void CompleteTendFarm(Agent agent, World world, EventBus bus, int currentTick, Action<string>? trace)
    {
        if (!agent.ActionTarget.HasValue) return;

        var (tx, ty) = agent.ActionTarget.Value;
        var tile = world.GetTile(tx, ty);

        tile.LastTendedTick = currentTick;

        // Fix 5A: Only add farm structure if placement constraints are met
        if (!tile.HasFarm)
        {
            // Never on home tile
            bool isHomeTile = agent.HomeTile.HasValue
                && tx == agent.HomeTile.Value.X && ty == agent.HomeTile.Value.Y;
            if (!isHomeTile && tile.IsFarmable && !tile.HasFarm)
            {
                tile.Structures.Add("farm");
                ClearNonGrainResources(tile);
                // US-009: Add farm to settlement and recalculate territory
                if (agent.SettlementId != null && _currentSettlements != null)
                {
                    var settlement = _currentSettlements.FirstOrDefault(s => s.Id == agent.SettlementId);
                    if (settlement != null)
                    {
                        settlement.Structures.Add((tx, ty, "farm"));
                        settlement.RecalculateTerritory(world.Width, world.Height);
                    }
                }
            }
        }

        trace?.Invoke($"[TRACE Agent {agent.Id}] CompleteTendFarm: tended farm at ({tx},{ty})");
        bus.Emit(currentTick,
            $"{agent.Name} tended farm at ({tx},{ty})",
            EventType.Action, agentId: agent.Id);
    }

    /// <summary>Removes non-grain resources and their regen caps from a newly created farm tile.</summary>
    private static void ClearNonGrainResources(Tile tile)
    {
        var toRemove = tile.Resources.Keys.Where(r => r != ResourceType.Grain).ToList();
        foreach (var r in toRemove)
        {
            tile.Resources.Remove(r);
            tile.RegenCap.Remove(r);
        }
    }

    // ── Directive #5 Fix 3b: ClearLand ─────────────────────────────────

    private bool TryDispatchClearLand(Agent agent, ScoredAction scored, World world, EventBus bus, int currentTick, Action<string>? trace)
    {
        if (!scored.TargetTile.HasValue) return false;
        if (!agent.Knowledge.Contains("land_clearing")) return false;

        var (tx, ty) = scored.TargetTile.Value;

        // Move to target if not adjacent
        int dist = Math.Max(Math.Abs(tx - agent.X), Math.Abs(ty - agent.Y));
        if (dist > 1)
        {
            if (TryStartMove(agent, tx, ty, world, trace))
            {
                agent.RecordAction(ActionType.Move, currentTick, $"Moving to clear land at ({tx},{ty})");
                return true;
            }
            return false;
        }

        // Start clearing (40 ticks ≈ half a day)
        agent.PendingAction = ActionType.ClearLand;
        agent.ActionProgress = 0f;
        agent.ActionDurationTicks = 40;
        agent.ActionTarget = (tx, ty);
        agent.CurrentAction = ActionType.ClearLand;
        agent.RecordAction(ActionType.ClearLand, currentTick, $"Clearing land at ({tx},{ty})");
        trace?.Invoke($"[TRACE Agent {agent.Id}] ClearLand: started at ({tx},{ty}), duration=40");
        return true;
    }

    private void CompleteClearLand(Agent agent, World world, EventBus bus, int currentTick, Action<string>? trace)
    {
        if (!agent.ActionTarget.HasValue) return;
        var (tx, ty) = agent.ActionTarget.Value;
        var tile = world.GetTile(tx, ty);

        // Clear the land: mark as cleared, make farmable
        tile.Structures.Add("cleared");
        // Remove some wood (the cleared trees) and add a small amount to agent inventory
        int woodCleared = Math.Min(tile.Resources.GetValueOrDefault(ResourceType.Wood, 0), 5);
        if (woodCleared > 0)
        {
            tile.Resources[ResourceType.Wood] = tile.Resources.GetValueOrDefault(ResourceType.Wood, 0) - woodCleared;
            // D22: enforceNonFoodGuards=false — CompleteClearLand has no non-food guards currently.
            // Pre-limit to available space (same as original canCarry logic).
            int canCarry = Math.Min(woodCleared, SimConfig.InventoryCapacity - agent.InventoryCount());
            if (canCarry > 0)
                agent.TryAddToInventory(ResourceType.Wood, canCarry, enforceNonFoodGuards: false);
        }

        trace?.Invoke($"[TRACE Agent {agent.Id}] CompleteClearLand: cleared ({tx},{ty}), got {woodCleared} wood");
        bus.Emit(currentTick,
            $"{agent.Name} cleared land at ({tx},{ty})",
            EventType.Action, agentId: agent.Id);
    }

    // ── Directive #5 Fix 3c: TendAnimals ─────────────────────────────

    private bool TryDispatchTendAnimals(Agent agent, ScoredAction scored, World world, EventBus bus, int currentTick, Action<string>? trace)
    {
        if (!scored.TargetTile.HasValue) return false;
        if (!agent.Knowledge.Contains("animal_domestication")) return false;

        var (tx, ty) = scored.TargetTile.Value;
        var tile = world.GetTile(tx, ty);
        if (!tile.Structures.Contains("animal_pen")) return false;

        // Move to pen if not there
        if (agent.X != tx || agent.Y != ty)
        {
            if (TryStartMove(agent, tx, ty, world, trace))
            {
                agent.RecordAction(ActionType.Move, currentTick, $"Moving to animal pen at ({tx},{ty})");
                return true;
            }
            return false;
        }

        // Start tending (25 ticks)
        agent.PendingAction = ActionType.TendAnimals;
        agent.ActionProgress = 0f;
        agent.ActionDurationTicks = 25;
        agent.ActionTarget = (tx, ty);
        agent.CurrentAction = ActionType.TendAnimals;
        agent.RecordAction(ActionType.TendAnimals, currentTick, $"Tending animals at ({tx},{ty})");
        trace?.Invoke($"[TRACE Agent {agent.Id}] TendAnimals: started at ({tx},{ty}), duration=25");
        return true;
    }

    private void CompleteTendAnimals(Agent agent, World world, EventBus bus, int currentTick, Action<string>? trace)
    {
        if (!agent.ActionTarget.HasValue) return;
        var (tx, ty) = agent.ActionTarget.Value;

        // Produce food: add 2 meat/animals to agent inventory
        // D22: Animals is food → non-food guards not triggered. Pre-limit to available space.
        int canCarry = Math.Min(2, SimConfig.InventoryCapacity - agent.InventoryCount());
        if (canCarry > 0)
            agent.TryAddToInventory(ResourceType.Meat, canCarry);

        trace?.Invoke($"[TRACE Agent {agent.Id}] CompleteTendAnimals: produced {canCarry} meat at ({tx},{ty})");
        bus.Emit(currentTick,
            $"{agent.Name} tended animals at ({tx},{ty}), produced {canCarry} meat",
            EventType.Action, agentId: agent.Id);
    }

    // ── D25b: Hunt & Harvest dispatch/completion ──────────────────────

    private bool TryDispatchHunt(Agent agent, ScoredAction scored, World world,
        EventBus bus, int currentTick, Action<string>? trace)
    {
        if (!scored.TargetTile.HasValue) return false;
        var (tx, ty) = scored.TargetTile.Value;

        // Find the actual animal at or near the target (may have moved or started fleeing)
        var animalsAtTarget = world.GetAnimalsAt(tx, ty);
        Animal? target = animalsAtTarget.FirstOrDefault(a =>
            a.IsAlive && a.Species != AnimalSpecies.Boar && a.Species != AnimalSpecies.Wolf);
        // Also check adjacent tiles if not found at exact position
        if (target == null)
        {
            for (int ddx = -1; ddx <= 1 && target == null; ddx++)
                for (int ddy = -1; ddy <= 1 && target == null; ddy++)
                {
                    if (ddx == 0 && ddy == 0) continue;
                    var nearby = world.GetAnimalsAt(tx + ddx, ty + ddy);
                    target = nearby.FirstOrDefault(a =>
                        a.IsAlive && a.Species != AnimalSpecies.Boar && a.Species != AnimalSpecies.Wolf);
                }
        }

        if (target != null && Math.Max(Math.Abs(target.X - agent.X), Math.Abs(target.Y - agent.Y)) <= 1)
        {
            // Adjacent or same tile — start pursuit
            agent.HuntTargetAnimalId = target.Id;
            agent.HuntPursuitTicks = 0;
            agent.PendingAction = ActionType.Hunt;
            agent.ActionProgress = 0f;
            agent.ActionDurationTicks = 1f; // Re-evaluated each tick during pursuit
            agent.ActionTarget = (tx, ty);
            agent.CurrentAction = ActionType.Hunt;
            trace?.Invoke($"[TRACE Agent {agent.Id}] Hunt: starting pursuit of {target.Species} #{target.Id} at ({tx},{ty})");
            return true;
        }

        // Not adjacent — set goal to move there
        agent.CurrentGoal = GoalType.HuntAnimal;
        agent.GoalTarget = (tx, ty);
        agent.GoalStartTick = currentTick;
        agent.HuntTargetAnimalId = null; // Will find specific animal when we arrive
        TryStartMove(agent, tx, ty, world, trace);
        trace?.Invoke($"[TRACE Agent {agent.Id}] Hunt: moving toward animal at ({tx},{ty})");
        return true;
    }

    private bool TryDispatchHarvest(Agent agent, ScoredAction scored, World world,
        EventBus bus, int currentTick, Action<string>? trace)
    {
        if (!scored.TargetTile.HasValue) return false;
        var (tx, ty) = scored.TargetTile.Value;

        if (agent.X == tx && agent.Y == ty)
        {
            // On the carcass tile — start harvesting
            var carcass = world.Carcasses.FirstOrDefault(c => c.IsActive && c.X == tx && c.Y == ty);
            if (carcass == null)
            {
                agent.ClearStaleMemory(tx, ty, ResourceType.Meat); // No carcass here anymore
                return false;
            }

            agent.PendingAction = ActionType.Harvest;
            agent.ActionProgress = 0f;
            agent.ActionDurationTicks = SimConfig.HarvestDuration;
            agent.ActionTarget = (tx, ty);
            agent.CurrentAction = ActionType.Harvest;
            trace?.Invoke($"[TRACE Agent {agent.Id}] Harvest: butchering {carcass.Species} carcass at ({tx},{ty})");
            return true;
        }

        // Not at tile — set goal to move there
        agent.CurrentGoal = GoalType.HarvestCarcass;
        agent.GoalTarget = (tx, ty);
        agent.GoalStartTick = currentTick;
        TryStartMove(agent, tx, ty, world, trace);
        trace?.Invoke($"[TRACE Agent {agent.Id}] Harvest: moving toward carcass at ({tx},{ty})");
        return true;
    }

    private void CompleteHarvest(Agent agent, World world, EventBus bus,
        int currentTick, Action<string>? trace)
    {
        if (!agent.ActionTarget.HasValue) return;
        var (tx, ty) = agent.ActionTarget.Value;

        var carcass = world.Carcasses.FirstOrDefault(c => c.IsActive && c.X == tx && c.Y == ty);
        if (carcass == null)
        {
            trace?.Invoke($"[TRACE Agent {agent.Id}] Harvest: carcass gone");
            return;
        }

        int meatHarvested = 0, hideHarvested = 0, boneHarvested = 0;

        // Harvest meat first (food priority)
        if (carcass.MeatYield > 0)
        {
            int added = 0;
            for (int i = 0; i < carcass.MeatYield; i++)
            {
                if (agent.TryAddToInventory(ResourceType.Meat, 1, enforceNonFoodGuards: false))
                    added++;
                else break;
            }
            carcass.MeatYield -= added;
            meatHarvested = added;
        }

        // Harvest hide (non-food, uses guards)
        if (carcass.HideYield > 0)
        {
            int added = 0;
            for (int i = 0; i < carcass.HideYield; i++)
            {
                if (agent.TryAddToInventory(ResourceType.Hide, 1, enforceNonFoodGuards: true))
                    added++;
                else break;
            }
            carcass.HideYield -= added;
            hideHarvested = added;
        }

        // Harvest bone (non-food, uses guards)
        if (carcass.BoneYield > 0)
        {
            int added = 0;
            for (int i = 0; i < carcass.BoneYield; i++)
            {
                if (agent.TryAddToInventory(ResourceType.Bone, 1, enforceNonFoodGuards: true))
                    added++;
                else break;
            }
            carcass.BoneYield -= added;
            boneHarvested = added;
        }

        // Mark carcass as consumed only if ALL yields are depleted
        if (carcass.MeatYield <= 0 && carcass.HideYield <= 0 && carcass.BoneYield <= 0)
            carcass.DecayTicksRemaining = 0;

        var parts = new List<string>();
        if (meatHarvested > 0) parts.Add($"{meatHarvested} meat");
        if (hideHarvested > 0) parts.Add($"{hideHarvested} hide");
        if (boneHarvested > 0) parts.Add($"{boneHarvested} bone");
        string yieldStr = parts.Count > 0 ? string.Join(", ", parts) : "nothing (full inventory)";

        agent.RecordAction(ActionType.Harvest, currentTick, $"Harvested {yieldStr} from {carcass.Species}");
        bus.Emit(currentTick,
            $"{agent.Name} harvested {yieldStr} from a {carcass.Species} carcass",
            EventType.Action, agentId: agent.Id);

        trace?.Invoke($"[TRACE Agent {agent.Id}] Harvest complete: {yieldStr} from {carcass.Species}");
    }

    // ── D25d: Tame dispatch ────────────────────────────────────────────

    private bool TryDispatchTame(Agent agent, ScoredAction scored, World world,
        EventBus bus, int currentTick, Action<string>? trace)
    {
        if (!scored.TargetAgentId.HasValue) return false;
        int animalId = scored.TargetAgentId.Value;

        // Find the target animal by ID
        var animal = world.Animals.FirstOrDefault(a => a.Id == animalId);
        if (animal == null || !animal.IsAlive || animal.IsDomesticated) return false;

        int dist = Math.Max(Math.Abs(animal.X - agent.X), Math.Abs(animal.Y - agent.Y));

        if (dist > 1)
        {
            // Not adjacent — move toward animal
            agent.CurrentGoal = GoalType.TameAnimal;
            agent.GoalTarget = (animal.X, animal.Y);
            agent.GoalStartTick = currentTick;
            agent.TameTargetAnimalId = animal.Id;
            TryStartMove(agent, animal.X, animal.Y, world, trace);
            trace?.Invoke($"[TRACE Agent {agent.Id}] Tame: moving toward {animal.Species} #{animal.Id} at ({animal.X},{animal.Y})");
            return true;
        }

        // Adjacent — attempt tame offering
        // Check offering interval cooldown
        if (currentTick - animal.LastTameOfferingTick < SimConfig.TameOfferingInterval)
        {
            // Wait for cooldown — keep goal active
            agent.PendingAction = ActionType.Tame;
            agent.CurrentAction = ActionType.Tame;
            agent.TameTargetAnimalId = animal.Id;
            trace?.Invoke($"[TRACE Agent {agent.Id}] Tame: waiting for offering cooldown on {animal.Species} #{animal.Id}");
            return true;
        }

        // Remove 1 food from inventory (prefer Berries > Grain > Meat)
        bool removedFood = false;
        foreach (var foodType in new[] { ResourceType.Berries, ResourceType.Grain, ResourceType.Meat })
        {
            if (agent.Inventory.TryGetValue(foodType, out int amt) && amt > 0)
            {
                agent.Inventory[foodType] = amt - 1;
                if (agent.Inventory[foodType] <= 0)
                    agent.Inventory.Remove(foodType);
                removedFood = true;
                break;
            }
        }
        if (!removedFood) return false; // No food to offer

        // Apply offering
        animal.TameProgress++;
        animal.LastTameOfferingTick = currentTick;
        animal.TameTargetAgentId = agent.Id;
        agent.TameTargetAnimalId = animal.Id;

        bus.Emit(currentTick,
            $"{agent.Name} offers food to {animal.Species}",
            EventType.Action, agentId: agent.Id);
        trace?.Invoke($"[TRACE Agent {agent.Id}] Tame: offered food to {animal.Species} #{animal.Id} (progress {animal.TameProgress}/{GetTameThreshold(animal.Species)})");

        // Check if taming is complete
        if (animal.TameProgress >= GetTameThreshold(animal.Species))
        {
            CompleteTame(agent, animal, world, bus, currentTick, trace);
            return true;
        }

        // Species-specific flee behavior after offering
        // Rabbit: stays near (no flee during taming)
        // Cow: slow flee (handled by AnimalAI, agent re-approaches next tick)
        // Sheep: full flee (whole flock bolts together)
        // Deer: full flee (agent must re-approach)
        // Boar: stays near trap

        agent.PendingAction = ActionType.Tame;
        agent.CurrentAction = ActionType.Tame;
        agent.CurrentGoal = GoalType.TameAnimal;
        agent.GoalTarget = (animal.X, animal.Y);
        agent.GoalStartTick = currentTick;
        agent.RecordAction(ActionType.Tame, currentTick, $"Taming {animal.Species} (progress {animal.TameProgress}/{GetTameThreshold(animal.Species)})");
        return true;
    }

    private void CompleteTame(Agent agent, Animal animal, World world,
        EventBus bus, int currentTick, Action<string>? trace)
    {
        animal.IsDomesticated = true;
        animal.OwnerAgentId = agent.Id;
        animal.State = AnimalState.Domesticated;
        animal.TerritoryCenter = agent.HomeTile ?? (agent.X, agent.Y);
        // Note: TerritoryRadius is readonly — we don't change it
        animal.TameTargetAgentId = null;
        agent.TameTargetAnimalId = null;

        // D25d Fix 6: Wolf pup → Dog transformation
        if (animal.Species == AnimalSpecies.Wolf && animal.IsPup)
        {
            animal.IsDog = true;
            animal.IsPup = false;
            animal.Health = SimConfig.DogHealth;

            // Give the dog a name
            var livingNames = _currentAllAgents?.Where(a => a.IsAlive).Select(a => a.Name) ?? Enumerable.Empty<string>();
            string dogName = NameGenerator.Generate(random, random.Next(2) == 0, livingNames);
            agent.RecordAction(ActionType.Tame, currentTick, $"Tamed a wolf pup and named it {dogName}!");
            bus.Emit(currentTick,
                $"{agent.Name} tamed a wolf pup and named it {dogName}!",
                EventType.Discovery, agentId: agent.Id);
            trace?.Invoke($"[TRACE Agent {agent.Id}] Tame COMPLETE: Wolf pup #{animal.Id} → Dog '{dogName}'!");
        }
        else
        {
            agent.RecordAction(ActionType.Tame, currentTick, $"Tamed a {animal.Species}!");
            bus.Emit(currentTick,
                $"{agent.Name} tamed a {animal.Species}!",
                EventType.Discovery, agentId: agent.Id);
            trace?.Invoke($"[TRACE Agent {agent.Id}] Tame COMPLETE: {animal.Species} #{animal.Id} is now domesticated!");
        }
    }

    private static int GetTameThreshold(AnimalSpecies species) => species switch
    {
        AnimalSpecies.Rabbit => SimConfig.TameThresholdRabbit,
        AnimalSpecies.Cow => SimConfig.TameThresholdCow,
        AnimalSpecies.Sheep => SimConfig.TameThresholdSheep,
        AnimalSpecies.Deer => SimConfig.TameThresholdDeer,
        AnimalSpecies.Boar => SimConfig.TameThresholdBoar,
        AnimalSpecies.Wolf => SimConfig.TameThresholdWolf,
        _ => 99
    };

    // ── D25d Fix 3b: FeedPen Dispatch ─────────────────────────────────
    private bool TryDispatchFeedPen(Agent agent, ScoredAction scored, World world,
        EventBus bus, int currentTick, Action<string>? trace)
    {
        if (!scored.TargetTile.HasValue) return false;
        var (tx, ty) = scored.TargetTile.Value;

        // Find pen at target tile
        var pen = world.Pens.FirstOrDefault(p => p.IsActive && p.TileX == tx && p.TileY == ty);
        if (pen == null || pen.AnimalCount == 0 || pen.FoodStore >= pen.MaxFoodStore) return false;

        // Ensure agent has food — withdraw from home storage if needed (before moving)
        int foodAvailable = agent.FoodInInventory();
        if (foodAvailable <= 0 && agent.HomeTile.HasValue)
        {
            var homeTile = world.GetTile(agent.HomeTile.Value.X, agent.HomeTile.Value.Y);
            int distToHome = Math.Abs(agent.X - agent.HomeTile.Value.X)
                           + Math.Abs(agent.Y - agent.HomeTile.Value.Y);
            if (distToHome <= 1 && homeTile.HasHomeStorage)
            {
                // Withdraw any food type from home storage
                int needed = pen.MaxFoodStore - pen.FoodStore;
                foreach (var foodType in new[] { ResourceType.Grain, ResourceType.Berries, ResourceType.Meat, ResourceType.PreservedFood })
                {
                    if (needed <= 0) break;
                    int withdrawn = homeTile.WithdrawFromHome(foodType, needed);
                    if (withdrawn > 0)
                    {
                        agent.Inventory[foodType] = agent.Inventory.GetValueOrDefault(foodType) + withdrawn;
                        needed -= withdrawn;
                    }
                }
                foodAvailable = agent.FoodInInventory();
            }
            else if (distToHome > 1)
            {
                // Not near home — go home first to pick up food
                TryStartMove(agent, agent.HomeTile.Value.X, agent.HomeTile.Value.Y, world, trace);
                trace?.Invoke($"[TRACE Agent {agent.Id}] FeedPen: going home first to get food");
                return true;
            }
        }

        if (foodAvailable <= 0) return false;

        if (agent.X != tx || agent.Y != ty)
        {
            TryStartMove(agent, tx, ty, world, trace);
            trace?.Invoke($"[TRACE Agent {agent.Id}] FeedPen: moving to pen at ({tx},{ty}) with {foodAvailable} food");
            return true;
        }

        // At pen — transfer food from inventory to pen store
        int spaceInPen = pen.MaxFoodStore - pen.FoodStore;
        int totalTransferred = 0;
        foreach (var foodType in new[] { ResourceType.Grain, ResourceType.Berries, ResourceType.Meat, ResourceType.PreservedFood })
        {
            if (spaceInPen <= 0) break;
            int have = agent.Inventory.GetValueOrDefault(foodType);
            if (have <= 0) continue;
            int transfer = Math.Min(have, spaceInPen);
            agent.Inventory[foodType] -= transfer;
            if (agent.Inventory[foodType] <= 0) agent.Inventory.Remove(foodType);
            pen.FoodStore += transfer;
            spaceInPen -= transfer;
            totalTransferred += transfer;
        }

        if (totalTransferred <= 0) return false;

        agent.PendingAction = ActionType.FeedPen;
        agent.CurrentAction = ActionType.FeedPen;
        agent.ActionProgress = 0f;
        agent.ActionDurationTicks = 1f;
        agent.RecordAction(ActionType.FeedPen, currentTick, $"Fed {totalTransferred} food to pen");
        bus.Emit(currentTick, $"{agent.Name} fed {totalTransferred} food to the animal pen",
            EventType.Action, agentId: agent.Id);
        trace?.Invoke($"[TRACE Agent {agent.Id}] FeedPen: transferred {totalTransferred} food to pen at ({tx},{ty})");
        return true;
    }

    // ── D25d Fix 3c: PenAnimal Dispatch ───────────────────────────────
    private bool TryDispatchPenAnimal(Agent agent, ScoredAction scored, World world,
        EventBus bus, int currentTick, Action<string>? trace)
    {
        if (!scored.TargetTile.HasValue || !scored.TargetAgentId.HasValue) return false;
        var (tx, ty) = scored.TargetTile.Value;
        int animalId = scored.TargetAgentId.Value;

        var animal = world.Animals.FirstOrDefault(a => a.Id == animalId && a.IsAlive && a.IsDomesticated);
        if (animal == null) return false;

        var pen = world.Pens.FirstOrDefault(p => p.IsActive && p.TileX == tx && p.TileY == ty && !p.IsFull);
        if (pen == null) return false;

        if (agent.X != tx || agent.Y != ty)
        {
            // Move to pen tile — domesticated animal follows owner
            TryStartMove(agent, tx, ty, world, trace);
            trace?.Invoke($"[TRACE Agent {agent.Id}] PenAnimal: moving to pen at ({tx},{ty}) with {animal.Species} #{animal.Id}");
            return true;
        }

        // At pen tile with following animal — pen it
        animal.PenId = pen.Id;
        animal.X = tx;
        animal.Y = ty;
        pen.AnimalIds.Add(animal.Id);

        agent.PendingAction = ActionType.PenAnimal;
        agent.CurrentAction = ActionType.PenAnimal;
        agent.ActionProgress = 0f;
        agent.ActionDurationTicks = 1f;
        agent.RecordAction(ActionType.PenAnimal, currentTick, $"Penned {animal.Species}");
        bus.Emit(currentTick, $"{agent.Name} penned a {animal.Species}",
            EventType.Action, agentId: agent.Id);
        trace?.Invoke($"[TRACE Agent {agent.Id}] PenAnimal: penned {animal.Species} #{animal.Id} in pen #{pen.Id}");
        return true;
    }

    // ── D25d Fix 5b: Slaughter Dispatch ───────────────────────────────
    private bool TryDispatchSlaughter(Agent agent, ScoredAction scored, World world,
        EventBus bus, int currentTick, Action<string>? trace)
    {
        if (!scored.TargetTile.HasValue || !scored.TargetAgentId.HasValue) return false;
        var (tx, ty) = scored.TargetTile.Value;
        int animalId = scored.TargetAgentId.Value;

        var animal = world.Animals.FirstOrDefault(a => a.Id == animalId && a.IsAlive);
        if (animal == null) return false;

        // Verify animal is penned
        if (!animal.PenId.HasValue) return false;
        var pen = world.Pens.FirstOrDefault(p => p.Id == animal.PenId.Value && p.IsActive);
        if (pen == null) return false;

        // Verify breeding pair preserved (3+ same species)
        int sameSpeciesCount = pen.CountSpecies(animal.Species, world.Animals);
        if (sameSpeciesCount < SimConfig.SlaughterBreedingPairMin + 1) return false;

        if (agent.X != tx || agent.Y != ty)
        {
            // Move to pen tile
            TryStartMove(agent, tx, ty, world, trace);
            trace?.Invoke($"[TRACE Agent {agent.Id}] Slaughter: moving to pen at ({tx},{ty})");
            return true;
        }

        // At pen tile — start slaughter action (multi-tick)
        agent.TameTargetAnimalId = animalId; // Reuse for slaughter target tracking
        agent.ActionTarget = (tx, ty);
        agent.PendingAction = ActionType.Slaughter;
        agent.CurrentAction = ActionType.Slaughter;
        agent.ActionProgress = 0f;
        agent.ActionDurationTicks = SimConfig.SlaughterDuration;
        agent.RecordAction(ActionType.Slaughter, currentTick, $"Slaughtering {animal.Species}");
        trace?.Invoke($"[TRACE Agent {agent.Id}] Slaughter: started slaughtering {animal.Species} #{animal.Id} ({SimConfig.SlaughterDuration} ticks)");
        return true;
    }

    // ── D25d Fix 5c: CompleteSlaughter ────────────────────────────────
    private void CompleteSlaughter(Agent agent, World world, EventBus bus,
        int currentTick, Action<string>? trace)
    {
        if (!agent.ActionTarget.HasValue || !agent.TameTargetAnimalId.HasValue) return;
        var animal = world.Animals.FirstOrDefault(a => a.Id == agent.TameTargetAnimalId.Value && a.IsAlive);
        if (animal == null) { agent.TameTargetAnimalId = null; return; }

        var config = Animal.SpeciesConfig[animal.Species];

        // Kill the animal
        animal.Die();

        // Remove from pen
        if (animal.PenId.HasValue)
        {
            var pen = world.Pens.FirstOrDefault(p => p.Id == animal.PenId.Value);
            pen?.AnimalIds.Remove(animal.Id);
        }

        // Add yields to inventory
        if (config.MeatYield > 0) agent.TryAddToInventory(ResourceType.Meat, config.MeatYield);
        if (config.HideYield > 0) agent.TryAddToInventory(ResourceType.Hide, config.HideYield);
        if (config.BoneYield > 0) agent.TryAddToInventory(ResourceType.Bone, config.BoneYield);

        agent.TameTargetAnimalId = null;

        bus.Emit(currentTick, $"{agent.Name} slaughtered a penned {animal.Species} for meat",
            EventType.Action, agentId: agent.Id);
        trace?.Invoke($"[TRACE Agent {agent.Id}] Slaughter COMPLETE: {animal.Species} #{animal.Id} → {config.MeatYield} meat, {config.HideYield} hide, {config.BoneYield} bone");
    }

    private bool TryDispatchSetTrap(Agent agent, ScoredAction scored, World world,
        EventBus bus, int currentTick, Action<string>? trace)
    {
        if (!scored.TargetTile.HasValue) return false;
        var (tx, ty) = scored.TargetTile.Value;

        if (agent.X == tx && agent.Y == ty)
        {
            // On the target tile — start placing trap
            agent.PendingAction = ActionType.SetTrap;
            agent.ActionProgress = 0f;
            agent.ActionDurationTicks = SimConfig.TrapPlacementTicks;
            agent.ActionTarget = (tx, ty);
            agent.CurrentAction = ActionType.SetTrap;
            trace?.Invoke($"[TRACE Agent {agent.Id}] SetTrap: placing trap at ({tx},{ty})");
            return true;
        }

        // Not at tile — set goal to move there
        agent.CurrentGoal = GoalType.SetTrapAt;
        agent.GoalTarget = (tx, ty);
        agent.GoalStartTick = currentTick;
        if (TryStartMove(agent, tx, ty, world, trace))
        {
            agent.RecordAction(ActionType.Move, currentTick, $"Moving to set trap at ({tx},{ty})");
            trace?.Invoke($"[TRACE Agent {agent.Id}] SetTrap: moving toward ({tx},{ty})");
            return true;
        }
        agent.ClearGoal();
        return false;
    }

    private void CompleteSetTrap(Agent agent, World world, EventBus bus,
        int currentTick, Action<string>? trace)
    {
        if (!agent.ActionTarget.HasValue) return;
        var (tx, ty) = agent.ActionTarget.Value;

        var trap = new Trap
        {
            X = tx,
            Y = ty,
            PlacedByAgentId = agent.Id,
            TickPlaced = currentTick
        };
        world.Traps.Add(trap);

        agent.RecordAction(ActionType.SetTrap, currentTick, $"Placed trap at ({tx},{ty})");
        bus.Emit(currentTick,
            $"{agent.Name} placed a trap at ({tx},{ty})",
            EventType.Action, agentId: agent.Id);

        trace?.Invoke($"[TRACE Agent {agent.Id}] SetTrap complete at ({tx},{ty})");
    }

    private static float GetHuntSuccessChance(Agent agent, AnimalSpecies species, World? world = null)
    {
        bool hasHafted = agent.Knowledge.Contains("hafted_tools");
        bool hasRefined = agent.Knowledge.Contains("refined_tools");
        bool hasCrudeAxe = agent.Knowledge.Contains("crude_axe");
        bool hasKnife = agent.Knowledge.Contains("stone_knife");
        bool hasSpear = agent.Knowledge.Contains("spear");
        bool hasBow = agent.Knowledge.Contains("bow");

        float baseChance = species switch
        {
            AnimalSpecies.Rabbit => hasHafted ? 0.70f : hasRefined ? 0.60f : (hasKnife || hasCrudeAxe) ? 0.55f : 0.35f,
            AnimalSpecies.Deer => hasHafted ? 0.50f : hasRefined ? 0.40f : (hasKnife || hasCrudeAxe) ? 0.35f : 0.10f,
            AnimalSpecies.Cow => hasHafted ? 0.60f : hasRefined ? 0.50f : (hasKnife || hasCrudeAxe) ? 0.45f : 0.15f,
            AnimalSpecies.Sheep => hasHafted ? 0.65f : hasRefined ? 0.55f : (hasKnife || hasCrudeAxe) ? 0.50f : 0.20f,
            AnimalSpecies.Boar => hasSpear ? 0.50f : 0f,  // D25c: Requires spear
            AnimalSpecies.Wolf => hasBow ? 0.35f : 0f,    // D25c: Requires bow
            _ => 0f
        };

        // D25c: Spear +15%, Bow +25% bonus to all prey (additive)
        if (hasSpear && baseChance > 0) baseChance += 0.15f;
        if (hasBow && baseChance > 0) baseChance += 0.25f;

        // D25d: Dog companion hunt bonus
        if (world != null && baseChance > 0 && agent.HasDogCompanion(world.Animals))
        {
            // Check if dog is within 3 tiles of the agent
            foreach (var animal in world.Animals)
            {
                if (!animal.IsAlive || !animal.IsDog || animal.OwnerAgentId != agent.Id) continue;
                int dist = Math.Max(Math.Abs(animal.X - agent.X), Math.Abs(animal.Y - agent.Y));
                if (dist <= 3)
                {
                    baseChance += SimConfig.DogHuntBonus;
                    break;
                }
            }
        }

        return Math.Min(1f, baseChance);
    }

    // ── D25c: Combat helpers ──────────────────────────────────────────

    /// <summary>D25c: Check if agent is near a dangerous animal that should initiate involuntary combat.</summary>
    private void CheckInvoluntaryCombat(Agent agent, World world, int currentTick, Action<string>? trace)
    {
        // Only agents with weapons can enter involuntary combat.
        // Unarmed agents don't perceive danger from Boar/Wolf (haven't learned to fear them yet).
        // This preserves baseline RNG determinism until agents discover spear/bow.
        bool hasSpear = agent.Knowledge.Contains("spear");
        bool hasBow = agent.Knowledge.Contains("bow");
        if (!hasSpear && !hasBow) return;

        // US-009: Territory deterrent — no involuntary combat within settlement territory
        if (Settlement.IsInAnyTerritory(_currentSettlements, agent.X, agent.Y)) return;

        // Home deterrent (fallback for agents without settlement territory)
        if (agent.HomeTile.HasValue)
        {
            int distFromHome = Math.Max(Math.Abs(agent.X - agent.HomeTile.Value.X), Math.Abs(agent.Y - agent.HomeTile.Value.Y));
            if (distFromHome <= SimConfig.StructureDeterrentRange + 2) return;
        }

        foreach (var animal in world.Animals)
        {
            if (!animal.IsAlive) continue;
            if (animal.AggressiveCooldown > 0) continue;
            if (animal.AggressiveTargetAgentId.HasValue) continue;

            // Only trigger for species the agent can fight
            if (animal.Species == AnimalSpecies.Boar && !hasSpear) continue;
            if (animal.Species == AnimalSpecies.Wolf && !hasBow) continue;
            if (animal.Species != AnimalSpecies.Boar && animal.Species != AnimalSpecies.Wolf) continue;

            int dist = Math.Max(Math.Abs(agent.X - animal.X), Math.Abs(agent.Y - animal.Y));
            int aggroRange = animal.Species == AnimalSpecies.Boar ? SimConfig.BoarChargeRange : SimConfig.WolfAggroRange;
            if (animal.Species == AnimalSpecies.Wolf)
            {
                int timeOfDay = currentTick % SimConfig.TicksPerSimDay;
                bool isNight = timeOfDay >= SimConfig.NightStartHour || timeOfDay < SimConfig.NightEndHour;
                if (isNight) aggroRange += SimConfig.WolfNightDetectionBonus;
            }

            if (dist <= aggroRange)
            {
                agent.CombatTargetAnimalId = animal.Id;
                agent.CombatTicksRemaining = 30;
                agent.PendingAction = ActionType.Combat;
                agent.CurrentAction = ActionType.Combat;
                animal.AggressiveTargetAgentId = agent.Id;

                trace?.Invoke($"[TRACE Agent {agent.Id}] INVOLUNTARY COMBAT: {animal.Species} at ({animal.X},{animal.Y})");

                if (animal.Species == AnimalSpecies.Wolf)
                {
                    foreach (var packmate in world.Animals)
                    {
                        if (packmate.Id == animal.Id || !packmate.IsAlive) continue;
                        if (packmate.HerdId != animal.HerdId || packmate.Species != AnimalSpecies.Wolf) continue;
                        if (packmate.AggressiveTargetAgentId.HasValue) continue;
                        int pmDist = Math.Max(Math.Abs(packmate.X - animal.X), Math.Abs(packmate.Y - animal.Y));
                        if (pmDist <= SimConfig.WolfPackConvergeRange)
                            packmate.AggressiveTargetAgentId = agent.Id;
                    }
                }
                return;
            }
        }
    }

    private static int GetAgentMeleeDamage(Agent agent)
    {
        if (agent.Knowledge.Contains("spear")) return SimConfig.DamageSpearMelee;
        if (agent.Knowledge.Contains("hafted_tools")) return SimConfig.DamageHaftedTools;
        if (agent.Knowledge.Contains("crude_axe")) return SimConfig.DamageCrudeAxe;
        if (agent.Knowledge.Contains("stone_knife")) return SimConfig.DamageStoneKnife;
        return SimConfig.DamageBareHands;
    }

    private static int GetAnimalDamage(Animal animal, World world)
    {
        if (animal.Species == AnimalSpecies.Boar) return SimConfig.BoarDamagePerTick;
        if (animal.Species == AnimalSpecies.Wolf)
        {
            // Count other wolves in combat with same target
            int packInCombat = 0;
            foreach (var a in world.Animals)
            {
                if (a.Id == animal.Id || !a.IsAlive) continue;
                if (a.Species == AnimalSpecies.Wolf && a.HerdId == animal.HerdId
                    && a.AggressiveTargetAgentId == animal.AggressiveTargetAgentId)
                    packInCombat++;
            }
            return SimConfig.WolfSingleDamagePerTick + packInCombat * SimConfig.WolfPackExtraDamagePerTick;
        }
        return 0;
    }

    private static float GetCombatFleeChance(Animal animal, World world)
    {
        if (animal.Species == AnimalSpecies.Boar) return SimConfig.BoarFleeSuccessChance;
        if (animal.Species == AnimalSpecies.Wolf)
        {
            int packInCombat = 0;
            foreach (var a in world.Animals)
            {
                if (a.Id == animal.Id || !a.IsAlive) continue;
                if (a.Species == AnimalSpecies.Wolf && a.HerdId == animal.HerdId
                    && a.AggressiveTargetAgentId.HasValue)
                    packInCombat++;
            }
            return packInCombat > 0 ? SimConfig.WolfPackFleeSuccessChance : SimConfig.WolfSingleFleeSuccessChance;
        }
        return 0.5f;
    }

    /// <summary>D25c: Clear aggression tags on an animal and its pack mates after combat ends.</summary>
    private static void ClearAnimalAggroTags(Animal animal, World world)
    {
        animal.AggressiveTargetAgentId = null;
        animal.AggressiveCooldown = 30;
        // Clear pack mates targeting the same agent
        if (animal.Species == AnimalSpecies.Wolf)
        {
            foreach (var a in world.Animals)
            {
                if (a.Id == animal.Id || !a.IsAlive) continue;
                if (a.Species == AnimalSpecies.Wolf && a.HerdId == animal.HerdId)
                {
                    a.AggressiveTargetAgentId = null;
                    a.AggressiveCooldown = 30;
                }
            }
        }
    }

    // ── GDD v1.7: New Action Completions ──────────────────────────────

    private void CompleteCook(Agent agent, EventBus bus, int currentTick, Action<string>? trace)
    {
        // Consume one cookable food item and restore cooked hunger value
        if (agent.Eat(cooked: true))
        {
            trace?.Invoke($"[TRACE Agent {agent.Id}] CompleteCook: ate cooked food (hunger={agent.Hunger:F0})");
            bus.Emit(currentTick, $"{agent.Name} cooked and ate food (Hunger: {agent.Hunger:F0})", EventType.Action, agentId: agent.Id);
            agent.RecordAction(ActionType.Eat, currentTick, $"Ate cooked food (hunger: {agent.Hunger:F0})");
        }
        else
        {
            trace?.Invoke($"[TRACE Agent {agent.Id}] CompleteCook: no food to eat after cooking!");
        }
    }

    private void CompletePreserve(Agent agent, World world, EventBus bus, int currentTick, Action<string>? trace)
    {
        // Consume 2 food items → produce 1 PreservedFood
        ResourceType[] foodTypes = { ResourceType.Meat, ResourceType.Fish, ResourceType.Grain, ResourceType.Berries };
        int consumed = 0;

        foreach (var food in foodTypes)
        {
            if (consumed >= 2) break;
            if (agent.Inventory.TryGetValue(food, out int amt) && amt > 0)
            {
                int toConsume = Math.Min(amt, 2 - consumed);
                agent.Inventory[food] -= toConsume;
                if (agent.Inventory[food] <= 0)
                    agent.Inventory.Remove(food);
                consumed += toConsume;
            }
        }

        if (consumed >= 2)
        {
            // Deposit preserved food to home storage if at home, otherwise carry in inventory
            bool depositedToHome = false;
            if (agent.HomeTile.HasValue
                && agent.X == agent.HomeTile.Value.X && agent.Y == agent.HomeTile.Value.Y)
            {
                var homeTile = world.GetTile(agent.X, agent.Y);
                if (homeTile.HasHomeStorage)
                {
                    int deposited = homeTile.DepositToHome(ResourceType.PreservedFood, 1);
                    if (deposited > 0)
                    {
                        depositedToHome = true;
                        trace?.Invoke($"[TRACE Agent {agent.Id}] CompletePreserve: deposited 1 PreservedFood to home storage");
                    }
                }
            }

            if (!depositedToHome)
            {
                // Direct add — original v1.8 code had no capacity check for preserve completion
                if (!agent.Inventory.ContainsKey(ResourceType.PreservedFood))
                    agent.Inventory[ResourceType.PreservedFood] = 0;
                agent.Inventory[ResourceType.PreservedFood]++;
                trace?.Invoke($"[TRACE Agent {agent.Id}] CompletePreserve: created 1 PreservedFood in inventory");
            }

            string dest = depositedToHome ? "home storage" : "inventory";
            bus.Emit(currentTick, $"{agent.Name} preserved food → {dest}", EventType.Action, agentId: agent.Id);
            agent.RecordAction(ActionType.Preserve, currentTick, $"Preserved 1 food → {dest}");
        }
    }

    private void CompleteDeposit(Agent agent, World world, EventBus bus, int currentTick, Action<string>? trace)
    {
        if (!agent.ActionTarget.HasValue) return;

        var (tx, ty) = agent.ActionTarget.Value;
        var tile = world.GetTile(tx, ty);

        if (!tile.HasGranary) return;

        // Deposit excess food — keep at least 2 food for personal use
        ResourceType[] foodTypes = { ResourceType.Berries, ResourceType.Grain, ResourceType.Meat, ResourceType.Fish, ResourceType.PreservedFood };
        int totalDeposited = 0;
        int personalFood = agent.FoodInInventory();

        foreach (var food in foodTypes)
        {
            if (personalFood <= 2) break; // Keep some for yourself
            if (agent.Inventory.TryGetValue(food, out int amt) && amt > 0)
            {
                int toDeposit = Math.Min(amt, personalFood - 2); // Don't go below 2 personal food
                int deposited = tile.DepositToGranary(food, toDeposit);
                if (deposited > 0)
                {
                    agent.Inventory[food] -= deposited;
                    if (agent.Inventory[food] <= 0)
                        agent.Inventory.Remove(food);
                    totalDeposited += deposited;
                    personalFood -= deposited;
                }
            }
        }

        if (totalDeposited > 0)
        {
            trace?.Invoke($"[TRACE Agent {agent.Id}] CompleteDeposit: deposited {totalDeposited} food to granary at ({tx},{ty})");
            bus.Emit(currentTick, $"{agent.Name} deposited {totalDeposited} food in granary at ({tx},{ty})", EventType.Action, agentId: agent.Id);
        }
    }

    private void CompleteWithdraw(Agent agent, World world, EventBus bus, int currentTick, Action<string>? trace)
    {
        if (!agent.ActionTarget.HasValue) return;

        var (tx, ty) = agent.ActionTarget.Value;
        var tile = world.GetTile(tx, ty);

        if (!tile.HasGranary) return;

        // Withdraw food and add to inventory
        // D22: Route through TryAddToInventory. Food type → non-food guards not triggered.
        var (foodType, amount) = tile.WithdrawAnyFood(2); // Take up to 2 items
        if (amount > 0 && agent.TryAddToInventory(foodType, amount))
        {
            trace?.Invoke($"[TRACE Agent {agent.Id}] CompleteWithdraw: took {amount} {foodType} from granary at ({tx},{ty})");
            bus.Emit(currentTick, $"{agent.Name} withdrew {amount} {foodType} from granary at ({tx},{ty})", EventType.Action, agentId: agent.Id);
        }
    }

    // ── GDD v1.8 Section 7: DepositHome Completion ────────────────────
    // Fix 4 investigation: CompleteDepositHome previously allowed no-op completions.
    // If the agent had food <= 2 (after the keep-2 logic) or storage was full,
    // the action would complete silently, then the agent would immediately re-score
    // DepositHome and loop for 20+ consecutive ticks depositing nothing.
    // Fix: guard against zero-transfer completions and log as no-op.

    private void CompleteDepositHome(Agent agent, World world, EventBus bus, int currentTick, Action<string>? trace)
    {
        if (!agent.ActionTarget.HasValue) return;

        var (tx, ty) = agent.ActionTarget.Value;
        var tile = world.GetTile(tx, ty);

        if (!tile.HasHomeStorage) return;

        int inventoryBefore = agent.InventoryCount();

        // Deposit excess food — keep at least 2 food for personal use
        ResourceType[] foodTypes = { ResourceType.PreservedFood, ResourceType.Berries, ResourceType.Grain, ResourceType.Meat, ResourceType.Fish };
        int totalDeposited = 0;
        int personalFood = agent.FoodInInventory();

        foreach (var food in foodTypes)
        {
            if (personalFood <= 2) break; // Keep some for yourself
            if (agent.Inventory.TryGetValue(food, out int amt) && amt > 0)
            {
                int toDeposit = Math.Min(amt, personalFood - 2);
                int deposited = tile.DepositToHome(food, toDeposit);
                if (deposited > 0)
                {
                    agent.Inventory[food] -= deposited;
                    if (agent.Inventory[food] <= 0)
                        agent.Inventory.Remove(food);
                    totalDeposited += deposited;
                    personalFood -= deposited;
                }
            }
        }

        // Directive: Also deposit materials (wood, stone) into material storage pile
        ResourceType[] materialTypes = { ResourceType.Wood, ResourceType.Stone };
        int totalMaterialDeposited = 0;
        foreach (var mat in materialTypes)
        {
            if (agent.Inventory.TryGetValue(mat, out int matAmt) && matAmt > 0)
            {
                // Keep 3 of each material for immediate needs, deposit the rest
                int toDeposit = Math.Max(0, matAmt - 3);
                if (toDeposit > 0)
                {
                    int deposited = tile.DepositMaterialToHome(mat, toDeposit);
                    if (deposited > 0)
                    {
                        agent.Inventory[mat] -= deposited;
                        if (agent.Inventory[mat] <= 0) agent.Inventory.Remove(mat);
                        totalMaterialDeposited += deposited;
                    }
                }
            }
        }

        int inventoryAfter = agent.InventoryCount();

        if ((totalDeposited > 0 || totalMaterialDeposited > 0) && inventoryAfter < inventoryBefore)
        {
            // Directive #10 Fix 4: Record deposit tick for cooldown
            agent.LastDepositTick = currentTick;

            trace?.Invoke($"[TRACE Agent {agent.Id}] CompleteDepositHome: deposited {totalDeposited} food + {totalMaterialDeposited} materials at home ({tx},{ty})");
            if (totalDeposited > 0)
                bus.Emit(currentTick, $"{agent.Name} stored {totalDeposited} food at home ({tx},{ty})", EventType.Action, agentId: agent.Id);
            if (totalMaterialDeposited > 0)
                bus.Emit(currentTick, $"{agent.Name} stored {totalMaterialDeposited} materials at home ({tx},{ty})", EventType.Action, agentId: agent.Id);
        }
        else
        {
            // Nothing was actually deposited — don't count as success.
            trace?.Invoke($"[TRACE Agent {agent.Id}] CompleteDepositHome: NO-OP, nothing deposited at ({tx},{ty}), food={personalFood}");
        }
    }

    // ── Granary Helper Methods (GDD v1.7) ───────────────────────────────

    /// <summary>Finds a nearby granary tile that has food stored, using agent memory.</summary>
    private Tile? FindNearbyGranaryWithFood(Agent agent, World world, int currentTick)
    {
        // Check current tile first
        var currentTile = world.GetTile(agent.X, agent.Y);
        if (currentTile.HasGranary && currentTile.GranaryTotalFood > 0)
            return currentTile;

        // Check memory for granary structures
        var granaryMemories = agent.Memory.Where(m =>
            m.Type == MemoryType.Structure
            && m.StructureId == "granary"
            && currentTick - m.TickObserved <= SimConfig.MemoryDecayTicks
        ).ToList();

        Tile? best = null;
        int bestDist = int.MaxValue;

        foreach (var mem in granaryMemories)
        {
            var tile = world.GetTile(mem.X, mem.Y);
            if (tile.HasGranary && tile.GranaryTotalFood > 0)
            {
                int dist = Math.Max(Math.Abs(mem.X - agent.X), Math.Abs(mem.Y - agent.Y));
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = tile;
                }
            }
        }

        return best;
    }

    /// <summary>Finds a nearby granary tile with space for deposits, using agent memory.</summary>
    private Tile? FindNearbyGranaryWithSpace(Agent agent, World world, int currentTick)
    {
        // Check current tile first
        var currentTile = world.GetTile(agent.X, agent.Y);
        if (currentTile.HasGranary && currentTile.GranaryTotalFood < SimConfig.GranaryCapacity)
            return currentTile;

        var granaryMemories = agent.Memory.Where(m =>
            m.Type == MemoryType.Structure
            && m.StructureId == "granary"
            && currentTick - m.TickObserved <= SimConfig.MemoryDecayTicks
        ).ToList();

        Tile? best = null;
        int bestDist = int.MaxValue;

        foreach (var mem in granaryMemories)
        {
            var tile = world.GetTile(mem.X, mem.Y);
            if (tile.HasGranary && tile.GranaryTotalFood < SimConfig.GranaryCapacity)
            {
                int dist = Math.Max(Math.Abs(mem.X - agent.X), Math.Abs(mem.Y - agent.Y));
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = tile;
                }
            }
        }

        return best;
    }

    // ── Pending children from reproduction this tick ──
    private readonly List<Agent> _pendingChildren = new();

    /// <summary>Returns and clears children born during DecideAction calls this tick.</summary>
    public List<Agent> FlushPendingChildren()
    {
        var children = new List<Agent>(_pendingChildren);
        _pendingChildren.Clear();
        return children;
    }

    // ── Helper Methods ─────────────────────────────────────────────────

    /// <summary>
    /// Starts a multi-tick move toward food using perception memory, then fallback to local scan.
    /// Sets a SeekFood goal so the agent commits to the food target (prevents oscillation).
    /// Validates stale memories when food is within perception range.
    /// </summary>
    private bool TryStartMoveTowardsFood(Agent agent, World world, EventBus bus, int currentTick, Action<string>? trace)
    {
        // First: check perception memory for remembered food
        var rememberedFood = agent.GetRememberedFood(currentTick);
        MemoryEntry? validTarget = null;

        while (rememberedFood.Count > 0)
        {
            var nearest = GetNearest(agent, rememberedFood);
            if (nearest == null) break;

            // Skip if food is at our current position (can't move to self)
            if (nearest.X == agent.X && nearest.Y == agent.Y)
            {
                // We're AT the location but there's no gatherable food (or we'd have gathered it)
                if (nearest.Resource.HasValue)
                    agent.ClearStaleMemory(nearest.X, nearest.Y, nearest.Resource.Value);
                rememberedFood.Remove(nearest);
                continue;
            }

            // Validate: if food is within immediate perception range, check it still exists
            int dist = Math.Max(Math.Abs(nearest.X - agent.X), Math.Abs(nearest.Y - agent.Y));
            if (dist <= SimConfig.PerceptionImmediateRadius)
            {
                var memTile = world.GetTile(nearest.X, nearest.Y);
                if (memTile.TotalFood() <= 0)
                {
                    // Memory is stale — food was consumed
                    if (nearest.Resource.HasValue)
                        agent.ClearStaleMemory(nearest.X, nearest.Y, nearest.Resource.Value);
                    rememberedFood.Remove(nearest);
                    continue;
                }
            }

            validTarget = nearest;
            break;
        }

        if (validTarget != null)
        {
            // Set SeekFood goal for committed multi-tile movement
            agent.CurrentGoal = GoalType.SeekFood;
            agent.GoalTarget = (validTarget.X, validTarget.Y);
            agent.GoalResource = validTarget.Resource;
            agent.GoalStartTick = currentTick;

            if (TryStartMove(agent, validTarget.X, validTarget.Y, world, trace))
            {
                bus.Emit(currentTick,
                    $"{agent.Name} seeking food at ({validTarget.X},{validTarget.Y})",
                    EventType.Movement, agentId: agent.Id);
                return true;
            }
            agent.ClearGoal();
        }

        // Fallback: scan local area for actual food on tiles
        int searchRadius = SimConfig.ViabilityScanRadius;
        (int x, int y)? nearestFood = null;
        int nearestDistance = int.MaxValue;

        for (int dx = -searchRadius; dx <= searchRadius; dx++)
        {
            for (int dy = -searchRadius; dy <= searchRadius; dy++)
            {
                if (dx == 0 && dy == 0) continue; // Skip current tile

                int checkX = agent.X + dx;
                int checkY = agent.Y + dy;

                if (!world.IsInBounds(checkX, checkY)) continue;

                var tile = world.GetTile(checkX, checkY);
                if (tile.TotalFood() > 0)
                {
                    int distance = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    if (distance < nearestDistance)
                    {
                        nearestDistance = distance;
                        nearestFood = (checkX, checkY);
                    }
                }
            }
        }

        if (nearestFood.HasValue)
        {
            // Set SeekFood goal for committed multi-tile movement
            agent.CurrentGoal = GoalType.SeekFood;
            agent.GoalTarget = nearestFood.Value;
            agent.GoalStartTick = currentTick;

            if (TryStartMove(agent, nearestFood.Value.x, nearestFood.Value.y, world, trace))
            {
                bus.Emit(currentTick,
                    $"{agent.Name} seeking food (scan)",
                    EventType.Movement, agentId: agent.Id);
                return true;
            }
            agent.ClearGoal();
        }

        return false;
    }

    /// <summary>Gets all agents adjacent to or on the same tile as the given agent.</summary>
    private static List<Agent> GetNearbyAgents(Agent agent, World world)
    {
        var adjacentAgents = world.GetAdjacentAgents(agent.X, agent.Y);
        var samePos = world.GetAgentsAt(agent.X, agent.Y).Where(a => a.Id != agent.Id).ToList();
        var nearby = new List<Agent>();
        nearby.AddRange(adjacentAgents);
        nearby.AddRange(samePos);
        return nearby;
    }

    /// <summary>Returns the closest memory entry to the agent (Chebyshev distance).</summary>
    private static MemoryEntry? GetNearest(Agent agent, List<MemoryEntry> entries)
    {
        MemoryEntry? best = null;
        int bestDist = int.MaxValue;

        foreach (var entry in entries)
        {
            int dist = Math.Max(Math.Abs(entry.X - agent.X), Math.Abs(entry.Y - agent.Y));
            if (dist < bestDist)
            {
                bestDist = dist;
                best = entry;
            }
        }

        return best;
    }

    // ═════════════════════════════════════════════════════════════════
    // Goal Preservation on Mode Transitions
    // ═════════════════════════════════════════════════════════════════

    /// <summary>
    /// Determines whether a goal should survive a mode transition rather than being cleared.
    /// This prevents the #1 cause of goal abandonment: agents walking toward food/resources
    /// lose their goal because the mode label flipped (e.g., Forage↔Home oscillation).
    ///
    /// Preservation rules by goal type:
    ///   GatherFoodAt:     PRESERVE on Forage↔Home, Any→Caretaker (if carrying food). CLEAR on Any→Urgent.
    ///   GatherResourceAt: PRESERVE on Forage↔Home. CLEAR on Any→Urgent, Any→Caretaker.
    ///   ReturnHome:       PRESERVE when transitioning TO Home or Caretaker. CLEAR otherwise.
    ///   BuildAtTile:      CLEAR on all transitions (mode-specific).
    ///   SeekFood:         PRESERVE on ALL transitions (always relevant).
    ///   Explore:          CLEAR on all transitions (mode-specific).
    /// </summary>
    private static bool ShouldPreserveGoal(GoalType goal, BehaviorMode oldMode, BehaviorMode newMode, Agent agent)
    {
        // SeekFood survives ALL transitions — finding food is always relevant
        if (goal == GoalType.SeekFood)
            return true;

        // Any→Urgent: CLEAR everything except SeekFood (handled above)
        // Survival emergencies must always get a fresh decision
        if (newMode == BehaviorMode.Urgent)
            return false;

        // BuildAtTile and Explore: CLEAR on all transitions (mode-specific goals)
        if (goal == GoalType.BuildAtTile || goal == GoalType.Explore)
            return false;

        // GatherFoodAt: PRESERVE on Forage↔Home transitions UNLESS the exit is legitimate
        // (commitment met, inventory full, or survival emergency). Legitimate exits mean the
        // agent completed their forage trip — they should deposit food at home, not keep gathering.
        // Goal Commitment Fix: Also preserve across Build transitions (agent gathering materials to build)
        if (goal == GoalType.GatherFoodAt)
        {
            // Home→Forage: always preserve (agent just entered Forage with this goal)
            if (oldMode == BehaviorMode.Home && newMode == BehaviorMode.Forage)
                return true;

            // Forage→Home: only preserve if the exit was NOT legitimate
            // (i.e., mode oscillation — agent hasn't completed their forage commitment yet)
            if (oldMode == BehaviorMode.Forage && newMode == BehaviorMode.Home)
            {
                bool commitmentMet = agent.ForageGatherCount >= 3;
                bool inventoryFull = !agent.HasInventorySpace();
                bool starving = agent.Hunger < 30;
                // If commitment not met and not full and not starving, this is oscillation — preserve
                return !commitmentMet && !inventoryFull && !starving;
            }

            if (newMode == BehaviorMode.Caretaker && agent.FoodInInventory() > 0)
                return true;

            // Any→Build or Build→Forage: preserve food gathering goals
            // Agent was gathering food and got pulled into Build mode (or vice versa)
            if (newMode == BehaviorMode.Build || oldMode == BehaviorMode.Build)
                return true;

            return false;
        }

        // GatherResourceAt: same logic as GatherFoodAt for Forage↔Home
        // Goal Commitment Fix: Also preserve across Build transitions (agent gathering materials to build)
        if (goal == GoalType.GatherResourceAt)
        {
            if (oldMode == BehaviorMode.Home && newMode == BehaviorMode.Forage)
                return true;

            if (oldMode == BehaviorMode.Forage && newMode == BehaviorMode.Home)
            {
                bool commitmentMet = agent.ForageGatherCount >= 3;
                bool inventoryFull = !agent.HasInventorySpace();
                bool starving = agent.Hunger < 30;

                // Fix 1C: For non-food resource goals (stone/wood/ore), don't let
                // inventory-full clear the goal. The inventory may be full of food
                // but the agent hasn't obtained the target material yet.
                // Preserve the goal if the agent hasn't reached the target tile.
                if (agent.GoalResource.HasValue
                    && !ModeTransitionManager.IsFoodResource(agent.GoalResource.Value))
                {
                    // Only clear if starving (genuine emergency)
                    // or if agent is AT the target (goal was reachable, inventory truly can't hold more)
                    bool atTarget = agent.GoalTarget.HasValue
                        && agent.X == agent.GoalTarget.Value.X
                        && agent.Y == agent.GoalTarget.Value.Y;
                    if (starving || (inventoryFull && atTarget))
                        return false; // Clear goal — genuine exit
                    return true; // Preserve goal — not done yet
                }

                return !commitmentMet && !inventoryFull && !starving;
            }

            // Any→Build or Build→Forage: preserve resource gathering goals
            // Agent was gathering wood/stone for building and mode transition happened
            if (newMode == BehaviorMode.Build || oldMode == BehaviorMode.Build)
                return true;

            // Caretaker: preserve resource goals if agent is en route
            if (newMode == BehaviorMode.Caretaker)
                return true;

            return false;
        }

        // ReturnHome: PRESERVE when transitioning TO Home or Caretaker
        if (goal == GoalType.ReturnHome)
        {
            return newMode == BehaviorMode.Home || newMode == BehaviorMode.Caretaker;
        }

        // Default: CLEAR unknown goals on transitions (safe fallback)
        return false;
    }
}
