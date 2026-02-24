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

    public AgentAI(Random random)
    {
        this.random = random;
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

        // ── v1.8: Mode transition evaluation (BEFORE goal continuation) ──
        // Mode transitions must always run so the agent can respond to changing
        // conditions (hunger, child needs, forage return thresholds) even mid-goal.
        bool modeChanged = ModeTransitionManager.EvaluateTransitions(agent, world, currentTick, allAgents);
        if (modeChanged)
        {
            // Mode changed — clear any stale goal from the previous mode
            agent.ClearGoal();
            trace?.Invoke($"[TRACE Agent {agent.Id}] MODE-CHANGE: cleared goal, re-evaluating");
            // Fall through to mode-specific decision below
        }

        // ── Goal continuation (prevents decision thrashing) ─────────
        // If agent has an active goal and is not mid-action, advance toward it
        // instead of re-running full utility scoring.
        if (!modeChanged && agent.CurrentGoal.HasValue && !agent.IsBusy)
        {
            if (TryAdvanceGoal(agent, world, bus, currentTick, trace))
                return; // Goal still valid, action taken

            // Goal invalid or completed — clear and fall through to full decision
            agent.ClearGoal();
            trace?.Invoke($"[TRACE Agent {agent.Id}] GOAL-CLEARED: falling through to full decision");
        }

        // ── v1.8: Mode-specific decision routing ──────────────────────
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
            if (currentTile.HasHomeStorage && currentTile.HomeTotalFood > 0)
            {
                var (wType, wAmount) = currentTile.WithdrawAnyFoodFromHome(1);
                if (wAmount > 0)
                {
                    int restore = wType == ResourceType.PreservedFood ? SimConfig.FoodRestorePreserved : SimConfig.FoodRestoreRaw;
                    agent.Hunger = Math.Min(100f, agent.Hunger + restore);
                    bus.Emit(currentTick, $"{agent.Name} ate from home storage (Hunger: {agent.Hunger:F0})", EventType.Action, agentId: agent.Id);
                    agent.RecordAction(ActionType.Eat, currentTick, $"Ate from home storage (hunger: {agent.Hunger:F0})");
                    return;
                }
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

        // Rush home to feed starving child
        if (allAgents != null && agent.HomeTile.HasValue)
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

        // Night rest — but NOT if exposed and able to build shelter
        bool canBuildShelter = agent.IsExposed
            && agent.Knowledge.Contains("lean_to")
            && agent.Inventory.GetValueOrDefault(ResourceType.Wood, 0) >= SimConfig.ShelterWoodCost;

        if (agent.NeedsRest(currentTick) && agent.Hunger > 40f && !canBuildShelter)
        {
            LogDecision(currentTick, agent, "Rest", "night", 75f);

            if (currentTile.HasShelter || (agent.HomeTile.HasValue && agent.X == agent.HomeTile.Value.X && agent.Y == agent.HomeTile.Value.Y))
            {
                agent.PendingAction = ActionType.Rest;
                agent.ActionProgress = 0f;
                agent.ActionDurationTicks = SimConfig.NightRestDuration;
                agent.CurrentAction = ActionType.Rest;
                agent.LastRestTick = currentTick;
                agent.RecordAction(ActionType.Rest, currentTick, "Resting for the night");
                return;
            }
            else if (agent.HomeTile.HasValue)
            {
                if (TryStartMove(agent, agent.HomeTile.Value.X, agent.HomeTile.Value.Y, world, trace))
                {
                    agent.RecordAction(ActionType.Move, currentTick, "Heading home to sleep");
                    return;
                }
            }
            // No home — rest in the open
            agent.PendingAction = ActionType.Rest;
            agent.ActionProgress = 0f;
            agent.ActionDurationTicks = SimConfig.NightRestDuration;
            agent.CurrentAction = ActionType.Rest;
            agent.LastRestTick = currentTick;
            agent.RecordAction(ActionType.Rest, currentTick, "Resting in the open (night)");
            return;
        }

        // Eat from inventory if moderately hungry
        if (agent.Hunger <= 60f && food > 0)
        {
            if (TryEatOrCook(agent, currentTile, bus, currentTick, trace))
                return;
        }

        // Eat from home storage if moderately hungry and at home
        if (agent.Hunger <= 60f && food <= 0
            && agent.HomeTile.HasValue
            && agent.X == agent.HomeTile.Value.X && agent.Y == agent.HomeTile.Value.Y)
        {
            if (currentTile.HasHomeStorage && currentTile.HomeTotalFood > 0)
            {
                var (wType, wAmount) = currentTile.WithdrawAnyFoodFromHome(1);
                if (wAmount > 0)
                {
                    int restore = wType == ResourceType.PreservedFood ? SimConfig.FoodRestorePreserved : SimConfig.FoodRestoreRaw;
                    agent.Hunger = Math.Min(100f, agent.Hunger + restore);
                    agent.RecordAction(ActionType.Eat, currentTick, $"Ate from home storage (hunger: {agent.Hunger:F0})");
                    return;
                }
            }
        }

        // Score Home-mode actions via utility scorer
        var scoredActions = UtilityScorer.ScoreHomeActions(agent, world, currentTick, random,
            allAgents, _currentKnowledgeSystem, _currentSettlements);

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
            if (candidate.Action == ActionType.Gather && candidate.TargetTile.HasValue
                && Math.Max(Math.Abs(candidate.TargetTile.Value.X - agent.X),
                    Math.Abs(candidate.TargetTile.Value.Y - agent.Y)) > 2)
            {
                var res = candidate.TargetResource ?? ResourceType.Berries;
                bool isFood = ModeTransitionManager.IsFoodResource(res);
                int returnThreshold = isFood ? SimConfig.ForageReturnFoodDefault : SimConfig.ForageReturnFoodDefault;
                agent.TransitionMode(BehaviorMode.Forage, currentTick);
                agent.ModeCommit.ForageTargetResource = res;
                agent.ModeCommit.ForageTargetTile = candidate.TargetTile;
                agent.ModeCommit.ForageReturnFoodThreshold = returnThreshold;
                agent.CurrentGoal = isFood ? GoalType.GatherFoodAt : GoalType.GatherResourceAt;
                agent.GoalTarget = candidate.TargetTile;
                agent.GoalResource = res;
                agent.GoalStartTick = currentTick;
                trace?.Invoke($"[TRACE Agent {agent.Id}] HOME→FORAGE: {res} at ({candidate.TargetTile.Value.X},{candidate.TargetTile.Value.Y})");
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

        // Fallback: idle at home
        agent.CurrentAction = ActionType.Idle;
        agent.RecordAction(ActionType.Idle, currentTick, "Idle at home");
    }

    /// <summary>v1.8: Forage mode — committed gather trip, return when threshold met.</summary>
    private void DecideForage(Agent agent, World world, EventBus bus, int currentTick, Action<string>? trace)
    {
        var currentTile = world.GetTile(agent.X, agent.Y);

        // Night rest — foragers rest wherever they are
        if (agent.NeedsRest(currentTick))
        {
            agent.PendingAction = ActionType.Rest;
            agent.ActionProgress = 0f;
            agent.ActionDurationTicks = SimConfig.NightRestDuration;
            agent.CurrentAction = ActionType.Rest;
            agent.LastRestTick = currentTick;
            agent.RecordAction(ActionType.Rest, currentTick, "Resting while foraging (night)");
            return;
        }

        // Emergency eat from inventory
        if (agent.Hunger <= 60f && agent.FoodInInventory() > 0)
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
                    if (currentTile.Resources.TryGetValue(res, out int amt) && amt > 0)
                    {
                        int duration = agent.GetGatherDuration();
                        agent.PendingAction = ActionType.Gather;
                        agent.ActionProgress = 0f;
                        agent.ActionDurationTicks = duration;
                        agent.ActionTarget = (tx, ty);
                        agent.ActionTargetResource = res;
                        agent.CurrentAction = ActionType.Gather;
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

        // Opportunistic gather: if on a tile with the committed resource, grab it
        if (agent.ModeCommit.ForageTargetResource.HasValue && agent.HasInventorySpace())
        {
            var res = agent.ModeCommit.ForageTargetResource.Value;
            if (currentTile.Resources.TryGetValue(res, out int curAmt) && curAmt > 0)
            {
                int duration = agent.GetGatherDuration();
                agent.PendingAction = ActionType.Gather;
                agent.ActionProgress = 0f;
                agent.ActionDurationTicks = duration;
                agent.ActionTarget = (agent.X, agent.Y);
                agent.ActionTargetResource = res;
                agent.CurrentAction = ActionType.Gather;
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
                    // Prefer food closer to home (not just closer to agent) to prevent drift
                    var best = remembered.OrderBy(m =>
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
            else
            {
                var memories = agent.Memory.Where(m =>
                    m.Type == MemoryType.Resource && m.Resource == res && m.Quantity > 0
                    && currentTick - m.TickObserved <= SimConfig.MemoryDecayTicks).ToList();
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

        // Nothing to gather and no target found — explore to find resources
        StartExplore(agent, world, bus, currentTick, trace);
        agent.RecordAction(ActionType.Explore, currentTick, "Exploring for resources (forage)");
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

            if (recipeId == "lean_to" && !buildTile.HasShelter)
            {
                int w = agent.Inventory.GetValueOrDefault(ResourceType.Wood, 0);
                int s = agent.Inventory.GetValueOrDefault(ResourceType.Stone, 0);
                if (w >= SimConfig.ShelterWoodCost && s >= SimConfig.ShelterStoneCost)
                {
                    StartBuild(agent, buildTile, "lean_to", SimConfig.ShelterBuildTicks, trace);
                    LogDecision(currentTick, agent, "Build", $"lean_to at ({bx},{by})", 1f);
                    agent.RecordAction(ActionType.Build, currentTick, $"Building lean-to at ({bx},{by})");
                    return;
                }
            }
            else if (recipeId == "granary" && buildTile.HasShelter && !buildTile.HasGranary)
            {
                int w = agent.Inventory.GetValueOrDefault(ResourceType.Wood, 0);
                int s = agent.Inventory.GetValueOrDefault(ResourceType.Stone, 0);
                if (w >= SimConfig.GranaryWoodCost && s >= SimConfig.GranaryStoneCost)
                {
                    StartBuild(agent, buildTile, "granary", SimConfig.GranaryBuildTicks, trace);
                    LogDecision(currentTick, agent, "Build", $"granary at ({bx},{by})", 1f);
                    agent.RecordAction(ActionType.Build, currentTick, $"Building granary at ({bx},{by})");
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

        // Night rest — explorers rest wherever they are
        if (agent.NeedsRest(currentTick))
        {
            agent.PendingAction = ActionType.Rest;
            agent.ActionProgress = 0f;
            agent.ActionDurationTicks = SimConfig.NightRestDuration;
            agent.CurrentAction = ActionType.Rest;
            agent.LastRestTick = currentTick;
            agent.RecordAction(ActionType.Rest, currentTick, "Resting while exploring (night)");
            return;
        }

        // Eat from inventory if getting hungry
        if (agent.Hunger <= 60f && agent.FoodInInventory() > 0)
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
                if (kvp.Value > 0)
                {
                    int duration = agent.GetGatherDuration();
                    agent.PendingAction = ActionType.Gather;
                    agent.ActionProgress = 0f;
                    agent.ActionDurationTicks = duration;
                    agent.ActionTarget = (agent.X, agent.Y);
                    agent.ActionTargetResource = kvp.Key;
                    agent.CurrentAction = ActionType.Gather;
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

            if (world.IsInBounds(targetX, targetY)
                && !float.IsPositiveInfinity(world.GetTile(targetX, targetY).MovementCostMultiplier))
            {
                if (TryStartMove(agent, targetX, targetY, world, trace))
                {
                    agent.CurrentAction = ActionType.Explore;
                    agent.RecordAction(ActionType.Explore, currentTick, "Exploring (committed direction)");
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
                    agent.RecordAction(ActionType.Explore, currentTick, "Exploring (alternate direction)");
                    return;
                }
            }
        }

        // Fallback: use weighted explore
        StartExplore(agent, world, bus, currentTick, trace);
        agent.RecordAction(ActionType.Explore, currentTick, "Exploring (fallback)");
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

        // ── PROACTIVE CHILD FEEDING: at home with food, feed child if below 70 hunger ──
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
                    if (child.Hunger < 70f && child.X == agent.X && child.Y == agent.Y)
                    {
                        TryFeedNearbyChild(agent, world, bus, currentTick, trace);
                        food = agent.FoodInInventory();
                        break;
                    }
                }
            }

            // Deposit surplus food to home storage so infant can self-feed
            if (food > 2 && currentTile.HasHomeStorage)
            {
                int toDeposit = food - 2; // Keep 2 for self
                foreach (var res in agent.Inventory.Keys.ToList())
                {
                    if (!ModeTransitionManager.IsFoodResource(res)) continue;
                    int amt = Math.Min(agent.Inventory[res], toDeposit);
                    if (amt > 0)
                    {
                        agent.Inventory[res] -= amt;
                        if (agent.Inventory[res] <= 0) agent.Inventory.Remove(res);
                        currentTile.DepositToHome(res, amt);
                        toDeposit -= amt;
                        agent.RecordAction(ActionType.DepositHome, currentTick, $"Deposited {amt} {res} for child");
                    }
                    if (toDeposit <= 0) break;
                }
            }
        }

        // ── RUSH HOME: if child is hungry (< 70) and we're not at home ──
        if (allAgents != null && agent.HomeTile.HasValue
            && (agent.X != homeX || agent.Y != homeY))
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

        // ── NIGHT REST ──
        if (agent.NeedsRest(currentTick) && agent.Hunger > 40f)
        {
            LogDecision(currentTick, agent, "Rest", "night(caretaker)", 75f);
            agent.PendingAction = ActionType.Rest;
            agent.ActionProgress = 0f;
            agent.ActionDurationTicks = SimConfig.NightRestDuration;
            agent.CurrentAction = ActionType.Rest;
            agent.LastRestTick = currentTick;
            agent.RecordAction(ActionType.Rest, currentTick, "Resting (caretaker, night)");
            return;
        }

        // ── EAT if moderately hungry ──
        if (agent.Hunger <= 60f && food > 0)
        {
            if (TryEatOrCook(agent, currentTile, bus, currentTick, trace))
                return;
        }
        if (agent.Hunger <= 60f && food <= 0
            && agent.HomeTile.HasValue && agent.X == homeX && agent.Y == homeY)
        {
            if (currentTile.HasHomeStorage && currentTile.HomeTotalFood > 0)
            {
                var (wType, wAmount) = currentTile.WithdrawAnyFoodFromHome(1);
                if (wAmount > 0)
                {
                    int restore = wType == ResourceType.PreservedFood ? SimConfig.FoodRestorePreserved : SimConfig.FoodRestoreRaw;
                    agent.Hunger = Math.Min(100f, agent.Hunger + restore);
                    agent.RecordAction(ActionType.Eat, currentTick, $"Ate from home storage (hunger: {agent.Hunger:F0})");
                    return;
                }
            }
        }

        // ── RADIUS-CONSTRAINED SCORING: score home actions, filter out-of-range targets ──
        var scoredActions = UtilityScorer.ScoreHomeActions(agent, world, currentTick, random,
            allAgents, _currentKnowledgeSystem, _currentSettlements);

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

            // Build triggers Build mode (same as DecideHome)
            if (candidate.Action == ActionType.Build && candidate.TargetTile.HasValue && candidate.TargetRecipeId != null)
            {
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
        var currentTile = world.GetTile(agent.X, agent.Y);
        int food = agent.FoodInInventory();

        if (agent.Stage == DevelopmentStage.Infant)
        {
            if (agent.Hunger <= 60f)
            {
                // Eat from home storage
                if (agent.HomeTile.HasValue
                    && agent.X == agent.HomeTile.Value.X && agent.Y == agent.HomeTile.Value.Y
                    && currentTile.HasHomeStorage && currentTile.HomeTotalFood > 0)
                {
                    var (wType, wAmount) = currentTile.WithdrawAnyFoodFromHome(1);
                    if (wAmount > 0)
                    {
                        int restore = wType == ResourceType.PreservedFood ? SimConfig.FoodRestorePreserved : SimConfig.FoodRestoreRaw;
                        agent.Hunger = Math.Min(100f, agent.Hunger + restore);
                        agent.RecordAction(ActionType.Eat, currentTick, $"Infant ate from home storage (hunger: {agent.Hunger:F0})");
                        return;
                    }
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
            agent.CurrentAction = ActionType.Idle;
            agent.RecordAction(ActionType.Idle, currentTick, "Infant waiting");
            return;
        }
        else // Youth
        {
            if (agent.Hunger <= 60f)
            {
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
                foreach (var kvp in currentTile.Resources.ToList())
                {
                    if (kvp.Value > 0 && IsEdibleResource(kvp.Key))
                    {
                        currentTile.Resources[kvp.Key] = Math.Max(0, kvp.Value - 1);
                        agent.Hunger = Math.Min(100f, agent.Hunger + SimConfig.FoodRestoreRaw * 0.5f);
                        agent.RecordAction(ActionType.Gather, currentTick, $"Youth foraged {kvp.Key}");
                        return;
                    }
                }
            }
            StartExplore(agent, world, bus, currentTick, trace);
            agent.RecordAction(ActionType.Explore, currentTick, "Exploring (youth)");
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

    /// <summary>Try to feed a nearby hungry child (P1.5 logic).</summary>
    private void TryFeedNearbyChild(Agent agent, World world, EventBus bus, int currentTick, Action<string>? trace)
    {
        int food = agent.FoodInInventory();
        if (food <= 0) return;

        var nearbyForFeeding = GetNearbyAgents(agent, world);
        Agent? hungriestChild = null;
        float lowestHunger = 40f;
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
            ResourceType[] foodTypes = { ResourceType.PreservedFood, ResourceType.Berries, ResourceType.Grain, ResourceType.Animals, ResourceType.Fish };
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
        var runnerUp = all.FirstOrDefault(s => s.Action != candidate.Action);
        string target = candidate.TargetTile.HasValue
            ? $"tile({candidate.TargetTile.Value.X},{candidate.TargetTile.Value.Y})"
            : candidate.TargetRecipeId ?? candidate.TargetResource?.ToString() ?? "";
        LogDecision(currentTick, agent, candidate.Action.ToString(), target,
            candidate.Score, runnerUp.Action.ToString(), runnerUp.Score);
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
                if (currentTile.Resources.TryGetValue(res, out int amt) && amt > 0 && agent.HasInventorySpace())
                {
                    int duration = agent.GetGatherDuration();
                    agent.PendingAction = ActionType.Gather;
                    agent.ActionProgress = 0f;
                    agent.ActionDurationTicks = duration;
                    agent.ActionTarget = (currentTile.X, currentTile.Y);
                    agent.ActionTargetResource = res;
                    agent.CurrentAction = ActionType.Gather;
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
                || scored.TargetResource.Value == ResourceType.Animals || scored.TargetResource.Value == ResourceType.Fish);
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
            if (tile.IsFarmable)
            {
                if (!tile.HasFarm) tile.Structures.Add("farm");
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

        if (scored.TargetRecipeId == "lean_to")
        {
            int woodHeld = agent.Inventory.GetValueOrDefault(ResourceType.Wood, 0);
            int stoneHeld = agent.Inventory.GetValueOrDefault(ResourceType.Stone, 0);
            if (woodHeld >= SimConfig.ShelterWoodCost && stoneHeld >= SimConfig.ShelterStoneCost && !tile.HasShelter)
            {
                StartBuild(agent, tile, "lean_to", SimConfig.ShelterBuildTicks, trace);
                agent.RecordAction(ActionType.Build, currentTick, $"Building lean-to at ({tx},{ty})");
                return true;
            }
        }
        else if (scored.TargetRecipeId == "granary")
        {
            int woodHeld = agent.Inventory.GetValueOrDefault(ResourceType.Wood, 0);
            int stoneHeld = agent.Inventory.GetValueOrDefault(ResourceType.Stone, 0);
            if (woodHeld >= SimConfig.GranaryWoodCost && stoneHeld >= SimConfig.GranaryStoneCost
                && tile.HasShelter && !tile.HasGranary)
            {
                StartBuild(agent, tile, "granary", SimConfig.GranaryBuildTicks, trace);
                agent.RecordAction(ActionType.Build, currentTick, $"Building granary at ({tx},{ty})");
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

        // If adjacent, increment bond and idle (social interaction)
        int dist = Math.Max(Math.Abs(target.X - agent.X), Math.Abs(target.Y - agent.Y));
        if (dist <= 1)
        {
            IncrementSocialBond(agent, target);
            agent.CurrentAction = ActionType.Socialize;
            agent.RecordAction(ActionType.Socialize, currentTick, $"Socializing with {target.Name}");
            trace?.Invoke($"[TRACE Agent {agent.Id}] UTILITY-Socialize: bonding with Agent {target.Id} (bonds={agent.SocialBonds.GetValueOrDefault(target.Id, 0)})");
            return true;
        }

        // Only the "visitor" (higher ID) walks toward the "host" (lower ID).
        // This prevents both agents from walking toward each other and crossing
        // every tick, which produces oscillation.
        if (agent.Id > target.Id)
        {
            if (TryStartMove(agent, target.X, target.Y, world, trace))
            {
                agent.RecordAction(ActionType.Move, currentTick, $"Moving toward {target.Name} to socialize");
                return true;
            }
        }
        else
        {
            // Host — don't show Socialize, just let the action fall through
            // so the agent can do something productive while waiting.
            // The visitor will come to us; next tick we'll re-score and if
            // the visitor is adjacent we'll bond then.
            return false;
        }
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

        if (TryStartMove(agent, home.X, home.Y, world, trace))
        {
            agent.RecordAction(ActionType.Move, currentTick, $"Returning home to ({home.X},{home.Y})");
            trace?.Invoke($"[TRACE Agent {agent.Id}] UTILITY-ReturnHome: moving toward ({home.X},{home.Y})");
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
        if (tile.HasHomeStorage && tile.HomeTotalFood < tile.HomeStorageCapacity)
        {
            StartDepositHome(agent, tile, currentTick, trace);
            agent.RecordAction(ActionType.DepositHome, currentTick, "Depositing food at home");
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
        ResourceType[] foodTypes = { ResourceType.PreservedFood, ResourceType.Berries, ResourceType.Grain, ResourceType.Animals, ResourceType.Fish };
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
        // Starving AND has food in inventory -> interrupt to eat immediately
        if (agent.Hunger <= SimConfig.InterruptHungerThreshold && agent.FoodInInventory() > 0)
        {
            // Don't interrupt eating or cooking — already consuming food
            if (agent.PendingAction == ActionType.Eat || agent.PendingAction == ActionType.Cook)
                return false;
            return true;
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
            // Don't interrupt Move when agent has a SeekFood goal — they're walking toward food
            if (agent.PendingAction == ActionType.Move && agent.CurrentGoal == GoalType.SeekFood)
                return false;
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
        if (!agent.CurrentGoal.HasValue || !agent.GoalTarget.HasValue)
            return false;

        var (gx, gy) = agent.GoalTarget.Value;

        // Stale goal check: goals older than 200 ticks (~7 sim-hours) are cleared
        if (currentTick - agent.GoalStartTick > 200)
        {
            trace?.Invoke($"[TRACE Agent {agent.Id}] GOAL-STALE: {agent.CurrentGoal} started {currentTick - agent.GoalStartTick} ticks ago");
            return false;
        }

        // Critical interrupt: hunger overrides non-food goals
        if (agent.Hunger <= SimConfig.InterruptHungerThreshold
            && agent.CurrentGoal != GoalType.SeekFood
            && agent.CurrentGoal != GoalType.GatherFoodAt)
        {
            trace?.Invoke($"[TRACE Agent {agent.Id}] GOAL-INTERRUPTED: hunger {agent.Hunger:F0} overrides {agent.CurrentGoal}");
            return false;
        }

        // Already at target?
        if (agent.X == gx && agent.Y == gy)
            return TryExecuteGoalAtTarget(agent, world, bus, currentTick, trace);

        // Not at target — move one step toward it
        if (TryStartMove(agent, gx, gy, world, trace))
        {
            agent.RecordAction(ActionType.Move, currentTick,
                $"Moving toward {agent.CurrentGoal} at ({gx},{gy})");
            return true;
        }

        // Can't move toward target — goal is blocked
        trace?.Invoke($"[TRACE Agent {agent.Id}] GOAL-BLOCKED: can't move toward ({gx},{gy})");
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
                    if (currentTile.Resources.TryGetValue(res, out int amt) && amt > 0 && agent.HasInventorySpace())
                    {
                        int duration = agent.GetGatherDuration();
                        agent.PendingAction = ActionType.Gather;
                        agent.ActionProgress = 0f;
                        agent.ActionDurationTicks = duration;
                        agent.ActionTarget = (agent.X, agent.Y);
                        agent.ActionTargetResource = res;
                        agent.CurrentAction = ActionType.Gather;
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
                return false; // Resource gone, goal failed

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
                if (currentTile.HasHomeStorage && currentTile.HomeTotalFood > 0)
                {
                    var (wType, wAmount) = currentTile.WithdrawAnyFoodFromHome(1);
                    if (wAmount > 0)
                    {
                        int restore = wType == ResourceType.PreservedFood ? SimConfig.FoodRestorePreserved : SimConfig.FoodRestoreRaw;
                        agent.Hunger = Math.Min(100f, agent.Hunger + restore);
                        bus.Emit(currentTick, $"{agent.Name} ate from home storage (Hunger: {agent.Hunger:F0})", EventType.Action, agentId: agent.Id);
                        agent.RecordAction(ActionType.Eat, currentTick, $"Ate from home storage (hunger: {agent.Hunger:F0})");
                        trace?.Invoke($"[TRACE Agent {agent.Id}] GOAL-TERMINAL: ate from home storage at ({agent.X},{agent.Y})");
                        agent.ClearGoal();
                        return true;
                    }
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

            var moveTile = world.GetTile(moveX, moveY);
            if (float.IsPositiveInfinity(moveTile.MovementCostMultiplier))
                continue;

            float duration = Agent.GetMoveDuration(moveTile.Biome);

            agent.PendingAction = ActionType.Move;
            agent.ActionProgress = 0f;
            agent.ActionDurationTicks = duration;
            agent.ActionTarget = (moveX, moveY);
            agent.CurrentAction = ActionType.Move;

            trace?.Invoke($"[TRACE Agent {agent.Id}] StartMove: ({agent.X},{agent.Y})->({moveX},{moveY}), duration={duration:F3}");
            return true;
        }

        // All directions blocked
        return false;
    }

    private bool TryStartGatherFood(Agent agent, Tile tile, World world, EventBus bus, int currentTick, Action<string>? trace)
    {
        ResourceType[] foodTypes = { ResourceType.Berries, ResourceType.Grain, ResourceType.Animals, ResourceType.Fish };

        foreach (var food in foodTypes)
        {
            if (tile.Resources.TryGetValue(food, out int amount) && amount > 0 && agent.HasInventorySpace())
            {
                int duration = agent.GetGatherDuration();

                agent.PendingAction = ActionType.Gather;
                agent.ActionProgress = 0f;
                agent.ActionDurationTicks = duration;
                agent.ActionTarget = (tile.X, tile.Y);
                agent.ActionTargetResource = food;
                agent.CurrentAction = ActionType.Gather;

                trace?.Invoke($"[TRACE Agent {agent.Id}] StartGather: {food} at ({tile.X},{tile.Y}), duration={duration}");
                return true;
            }
        }
        return false;
    }

    private static bool IsEdibleResource(ResourceType type)
    {
        return type == ResourceType.Berries || type == ResourceType.Grain
            || type == ResourceType.Animals || type == ResourceType.Fish
            || type == ResourceType.PreservedFood;
    }

    private bool TryStartGatherAnyResource(Agent agent, Tile tile, World world, EventBus bus, int currentTick, Action<string>? trace)
    {
        if (!agent.HasInventorySpace()) return false;

        // Prefer non-food resources (Wood, Stone, Ore) so agents accumulate crafting materials.
        // Only fall back to food if no non-food resources are available on this tile.
        ResourceType? target = null;

        // First pass: look for non-food resources
        foreach (var kvp in tile.Resources)
        {
            if (kvp.Value > 0 && !IsEdibleResource(kvp.Key))
            {
                target = kvp.Key;
                break;
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

        int duration = agent.GetGatherDuration();

        agent.PendingAction = ActionType.Gather;
        agent.ActionProgress = 0f;
        agent.ActionDurationTicks = duration;
        agent.ActionTarget = (tile.X, tile.Y);
        agent.ActionTargetResource = target.Value;
        agent.CurrentAction = ActionType.Gather;

        trace?.Invoke($"[TRACE Agent {agent.Id}] StartGather: {target.Value} at ({tile.X},{tile.Y}), duration={duration}");
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

        switch (action)
        {
            case ActionType.Move:
                CompleteMove(agent, world, bus, currentTick, trace);
                break;
            case ActionType.Gather:
                CompleteGather(agent, world, bus, currentTick, trace);
                break;
            case ActionType.Rest:
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
                CompletePreserve(agent, bus, currentTick, trace);
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
        }

        // Log the completion — use last recorded action detail if available
        var lastRecord = agent.GetLastActions(1).FirstOrDefault();
        string completionDetail = (lastRecord.Tick == currentTick) ? lastRecord.Detail : "";
        Logger?.LogCompletion(currentTick, agent, action.ToString(), "success", completionDetail, duration);

        agent.ForceActivePerception = true; // GDD v1.6.2: force active scan after action completion
        agent.ClearPendingAction();
    }

    private void CompleteMove(Agent agent, World world, EventBus bus, int currentTick, Action<string>? trace)
    {
        if (!agent.ActionTarget.HasValue) return;

        var (tx, ty) = agent.ActionTarget.Value;
        agent.MoveTo(tx, ty, world);

        trace?.Invoke($"[TRACE Agent {agent.Id}] CompleteMove: arrived at ({tx},{ty})");

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

        int gathered = agent.GatherFrom(tile, resource);
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
        tile.Structures.Add(structureId);
        tile.BuildProgress.Remove(structureId);

        // GDD v1.7.1: Set HomeTile when building a shelter (first shelter or closer to current pos)
        if (structureId == "lean_to" || structureId == "shelter" || structureId == "improved_shelter")
        {
            agent.HomeTile = (tx, ty);
            trace?.Invoke($"[TRACE Agent {agent.Id}] CompleteBuild: HomeTile set to ({tx},{ty})");
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

        // Ensure farm structure exists
        if (!tile.HasFarm)
            tile.Structures.Add("farm");

        trace?.Invoke($"[TRACE Agent {agent.Id}] CompleteTendFarm: tended farm at ({tx},{ty})");
        bus.Emit(currentTick,
            $"{agent.Name} tended farm at ({tx},{ty})",
            EventType.Action, agentId: agent.Id);
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

    private void CompletePreserve(Agent agent, EventBus bus, int currentTick, Action<string>? trace)
    {
        // Consume 2 food items → produce 1 PreservedFood
        ResourceType[] foodTypes = { ResourceType.Animals, ResourceType.Fish, ResourceType.Grain, ResourceType.Berries };
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
            if (!agent.Inventory.ContainsKey(ResourceType.PreservedFood))
                agent.Inventory[ResourceType.PreservedFood] = 0;
            agent.Inventory[ResourceType.PreservedFood]++;

            trace?.Invoke($"[TRACE Agent {agent.Id}] CompletePreserve: created 1 PreservedFood");
            bus.Emit(currentTick, $"{agent.Name} preserved food (now has {agent.Inventory[ResourceType.PreservedFood]} preserved)", EventType.Action, agentId: agent.Id);
            agent.RecordAction(ActionType.Preserve, currentTick, "Preserved 1 food");
        }
    }

    private void CompleteDeposit(Agent agent, World world, EventBus bus, int currentTick, Action<string>? trace)
    {
        if (!agent.ActionTarget.HasValue) return;

        var (tx, ty) = agent.ActionTarget.Value;
        var tile = world.GetTile(tx, ty);

        if (!tile.HasGranary) return;

        // Deposit excess food — keep at least 2 food for personal use
        ResourceType[] foodTypes = { ResourceType.Berries, ResourceType.Grain, ResourceType.Animals, ResourceType.Fish, ResourceType.PreservedFood };
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
        var (foodType, amount) = tile.WithdrawAnyFood(2); // Take up to 2 items
        if (amount > 0 && agent.HasInventorySpace(amount))
        {
            if (!agent.Inventory.ContainsKey(foodType))
                agent.Inventory[foodType] = 0;
            agent.Inventory[foodType] += amount;

            trace?.Invoke($"[TRACE Agent {agent.Id}] CompleteWithdraw: took {amount} {foodType} from granary at ({tx},{ty})");
            bus.Emit(currentTick, $"{agent.Name} withdrew {amount} {foodType} from granary at ({tx},{ty})", EventType.Action, agentId: agent.Id);
        }
    }

    // ── GDD v1.8 Section 7: DepositHome Completion ────────────────────

    private void CompleteDepositHome(Agent agent, World world, EventBus bus, int currentTick, Action<string>? trace)
    {
        if (!agent.ActionTarget.HasValue) return;

        var (tx, ty) = agent.ActionTarget.Value;
        var tile = world.GetTile(tx, ty);

        if (!tile.HasHomeStorage) return;

        // Deposit excess food — keep at least 2 food for personal use
        ResourceType[] foodTypes = { ResourceType.PreservedFood, ResourceType.Berries, ResourceType.Grain, ResourceType.Animals, ResourceType.Fish };
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

        if (totalDeposited > 0)
        {
            trace?.Invoke($"[TRACE Agent {agent.Id}] CompleteDepositHome: deposited {totalDeposited} food at home ({tx},{ty})");
            bus.Emit(currentTick, $"{agent.Name} stored {totalDeposited} food at home ({tx},{ty})", EventType.Action, agentId: agent.Id);
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
}
