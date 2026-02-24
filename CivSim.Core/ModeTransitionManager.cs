namespace CivSim.Core;

/// <summary>
/// v1.8 Behavioral Modes: Evaluates mode transitions each tick.
/// Called from AgentAI.DecideAction BEFORE mode-specific decision logic.
/// All transitions have hysteresis (entry/exit threshold gaps) to prevent oscillation.
/// </summary>
public static class ModeTransitionManager
{
    private static readonly ResourceType[] FoodTypes =
        { ResourceType.Berries, ResourceType.Grain, ResourceType.Animals, ResourceType.Fish };

    /// <summary>
    /// Evaluates whether the agent should transition to a different mode.
    /// Modifies agent.CurrentMode in place. Returns true if a transition occurred.
    /// </summary>
    public static bool EvaluateTransitions(Agent agent, World world, int currentTick,
        List<Agent>? allAgents = null)
    {
        // ── 1. URGENT: checked first, overrides everything ──────────
        if (agent.CurrentMode != BehaviorMode.Urgent)
        {
            if (ShouldEnterUrgent(agent))
            {
                agent.TransitionMode(BehaviorMode.Urgent, currentTick);
                return true;
            }
        }
        else
        {
            if (ShouldExitUrgent(agent))
            {
                var target = agent.PreviousMode ?? BehaviorMode.Home;
                agent.TransitionMode(target, currentTick);
                agent.PreviousMode = null;
                // If returning to Home and not at home, set return goal
                if (target == BehaviorMode.Home)
                    SetReturnHomeGoalIfNeeded(agent, currentTick);
                return true;
            }
            return false; // Stay in Urgent — no other transitions
        }

        // ── 2. CARETAKER: one parent caretakes, other provides ──────
        // Only one parent enters Caretaker at a time. The other stays in Home
        // mode as the "provider" — foraging, experimenting, building for the family.
        bool hasDependents = HasYoungDependents(agent, allAgents);
        if (hasDependents && agent.CurrentMode != BehaviorMode.Caretaker)
        {
            // Only enter Caretaker if no other parent is already caretaking
            if (!OtherParentIsCaretaking(agent, allAgents))
            {
                agent.TransitionMode(BehaviorMode.Caretaker, currentTick);
                SetReturnHomeGoalIfNeeded(agent, currentTick);
                return true;
            }
            // Otherwise: stay in current mode as the provider parent
        }
        if (!hasDependents && agent.CurrentMode == BehaviorMode.Caretaker)
        {
            agent.TransitionMode(BehaviorMode.Home, currentTick);
            SetReturnHomeGoalIfNeeded(agent, currentTick);
            return true;
        }

        // ── 3. Mode-specific exit checks ────────────────────────────
        return agent.CurrentMode switch
        {
            BehaviorMode.Home => EvaluateHomeExits(agent, world, currentTick),
            BehaviorMode.Forage => EvaluateForageExits(agent, world, currentTick),
            BehaviorMode.Build => EvaluateBuildExits(agent, world, currentTick),
            BehaviorMode.Explore => EvaluateExploreExits(agent, world, currentTick),
            BehaviorMode.Caretaker => EvaluateCaretakerExits(agent, world, currentTick, allAgents),
            _ => false
        };
    }

    // ═════════════════════════════════════════════════════════════════
    // Urgent transitions
    // ═════════════════════════════════════════════════════════════════

    private static bool ShouldEnterUrgent(Agent agent)
    {
        return agent.Hunger < SimConfig.UrgentEntryHunger
            || agent.Health < SimConfig.UrgentEntryHealth;
    }

    private static bool ShouldExitUrgent(Agent agent)
    {
        return agent.Hunger > SimConfig.UrgentExitHunger
            && agent.Health > SimConfig.UrgentExitHealth;
    }

    // ═════════════════════════════════════════════════════════════════
    // Caretaker detection
    // ═════════════════════════════════════════════════════════════════

    private static bool HasYoungDependents(Agent agent, List<Agent>? allAgents)
    {
        if (allAgents == null) return false;

        foreach (var kvp in agent.Relationships)
        {
            if (kvp.Value != RelationshipType.Child) continue;
            var child = allAgents.FirstOrDefault(a => a.Id == kvp.Key);
            if (child != null && child.IsAlive && child.Age < SimConfig.CaretakerExitChildAge)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Returns true if another parent of the same children is already in Caretaker mode.
    /// This ensures only one parent caretakes while the other provides for the family.
    /// </summary>
    private static bool OtherParentIsCaretaking(Agent agent, List<Agent>? allAgents)
    {
        if (allAgents == null) return false;

        // Find the agent's spouse (co-parent)
        foreach (var kvp in agent.Relationships)
        {
            if (kvp.Value != RelationshipType.Spouse) continue;
            var spouse = allAgents.FirstOrDefault(a => a.Id == kvp.Key);
            if (spouse != null && spouse.IsAlive && spouse.CurrentMode == BehaviorMode.Caretaker)
                return true;
        }
        return false;
    }

    // ═════════════════════════════════════════════════════════════════
    // Home mode exits
    // ═════════════════════════════════════════════════════════════════

    private static bool EvaluateHomeExits(Agent agent, World world, int currentTick)
    {
        // Only evaluate exits if agent is actually at home (or has no home yet)
        // If agent is in Home mode but traveling back, let goal system handle it
        if (agent.CurrentGoal == GoalType.ReturnHome)
            return false;

        // ── Food low → Forage ──
        // Normal case: must be above ForageEntryHunger (well-fed enough for a trip).
        // Emergency case: if agent has NO food at all (personal + home = 0), skip the
        // hunger gate — they must forage or they'll starve in the dead zone between
        // Urgent exit (40) and normal Forage entry (55).
        if (ShouldForageForFood(agent, world))
        {
            bool hasNoFoodAnywhere = agent.FoodInInventory() == 0
                && GetHomeFoodCount(agent, world) == 0;
            if (agent.Hunger > SimConfig.ForageEntryHunger || hasNoFoodAnywhere)
            {
                var target = FindBestFoodTarget(agent, world, currentTick);
                TransitionToForage(agent, currentTick,
                    target.resource ?? ResourceType.Berries,
                    target.tile, SimConfig.ForageReturnFoodDefault);
                return true;
            }
        }

        // ── Materials needed for known recipe → Forage for materials ──
        var materialNeed = GetMaterialNeed(agent, world);
        if (materialNeed.HasValue && agent.Hunger > SimConfig.ForageEntryHunger)
        {
            var target = FindBestResourceTarget(agent, world, currentTick, materialNeed.Value);
            TransitionToForage(agent, currentTick,
                materialNeed.Value, target, SimConfig.ForageReturnFoodDefault);
            return true;
        }

        // ── Explore conditions ──
        if (CanEnterExplore(agent, world, currentTick))
        {
            TransitionToExplore(agent, world, currentTick);
            return true;
        }

        return false;
    }

    private static bool ShouldForageForFood(Agent agent, World world)
    {
        int personalFood = agent.FoodInInventory();
        if (personalFood >= SimConfig.ForageEntryFoodInventory) return false;

        int homeFood = 0;
        if (agent.HomeTile.HasValue && world.IsInBounds(agent.HomeTile.Value.X, agent.HomeTile.Value.Y))
        {
            var homeTile = world.GetTile(agent.HomeTile.Value.X, agent.HomeTile.Value.Y);
            if (homeTile.HasHomeStorage)
                homeFood = homeTile.HomeTotalFood;
        }

        return homeFood < SimConfig.ForageEntryHomeStorage;
    }

    private static ResourceType? GetMaterialNeed(Agent agent, World world)
    {
        // Check if agent knows lean_to but doesn't have shelter
        if (agent.Knowledge.Contains("lean_to"))
        {
            bool hasShelter = agent.HomeTile.HasValue
                && world.IsInBounds(agent.HomeTile.Value.X, agent.HomeTile.Value.Y)
                && world.GetTile(agent.HomeTile.Value.X, agent.HomeTile.Value.Y).HasShelter;

            if (!hasShelter)
            {
                int wood = agent.Inventory.GetValueOrDefault(ResourceType.Wood, 0);
                int stone = agent.Inventory.GetValueOrDefault(ResourceType.Stone, 0);
                if (wood < SimConfig.ShelterWoodCost) return ResourceType.Wood;
                if (stone < SimConfig.ShelterStoneCost) return ResourceType.Stone;
            }
        }

        // Check if agent knows granary but home doesn't have one
        if (agent.Knowledge.Contains("granary") && agent.HomeTile.HasValue)
        {
            var homeTile = world.GetTile(agent.HomeTile.Value.X, agent.HomeTile.Value.Y);
            if (homeTile.HasShelter && !homeTile.HasGranary)
            {
                int wood = agent.Inventory.GetValueOrDefault(ResourceType.Wood, 0);
                int stone = agent.Inventory.GetValueOrDefault(ResourceType.Stone, 0);
                if (wood < SimConfig.GranaryWoodCost) return ResourceType.Wood;
                if (stone < SimConfig.GranaryStoneCost) return ResourceType.Stone;
            }
        }

        return null;
    }

    private static bool CanEnterExplore(Agent agent, World world, int currentTick)
    {
        // Must have a home to return to
        if (!agent.HomeTile.HasValue) return false;

        // Must actually be at home — don't start an expedition from a random field
        if (agent.X != agent.HomeTile.Value.X || agent.Y != agent.HomeTile.Value.Y)
            return false;

        // Must have spent some time in Home mode first (settle, eat, deposit)
        if (currentTick - agent.ModeEntryTick < SimConfig.ExploreMinHomeDwell)
            return false;

        // Must be sheltered
        bool hasShelter = world.IsInBounds(agent.HomeTile.Value.X, agent.HomeTile.Value.Y)
            && world.GetTile(agent.HomeTile.Value.X, agent.HomeTile.Value.Y).HasShelter;
        if (!hasShelter) return false;

        // Check thresholds (Explorer trait lowers them)
        bool isExplorer = agent.Traits.Contains(PersonalityTrait.Explorer);

        float hungerThreshold = isExplorer ? SimConfig.ExploreEntryHungerExplorer : SimConfig.ExploreEntryHunger;
        int healthThreshold = isExplorer ? SimConfig.ExploreEntryHealthExplorer : SimConfig.ExploreEntryHealth;
        int foodThreshold = isExplorer ? SimConfig.ExploreEntryFoodExplorer : SimConfig.ExploreEntryFood;

        if (agent.Hunger < hungerThreshold) return false;
        if (agent.Health < healthThreshold) return false;
        if (agent.FoodInInventory() < foodThreshold) return false;

        // No dependents (Caretaker mode would have already been entered)
        // Agent in Caretaker can't reach this method (Caretaker has its own exits)

        return true;
    }

    // ═════════════════════════════════════════════════════════════════
    // Forage mode exits
    // ═════════════════════════════════════════════════════════════════

    private static bool EvaluateForageExits(Agent agent, World world, int currentTick)
    {
        // Return threshold met
        bool isFoodForage = agent.ModeCommit.ForageTargetResource.HasValue
            && IsFoodResource(agent.ModeCommit.ForageTargetResource.Value);

        if (isFoodForage && agent.FoodInInventory() >= agent.ModeCommit.ForageReturnFoodThreshold)
        {
            TransitionToHome(agent, currentTick);
            return true;
        }

        // Inventory full
        if (!agent.HasInventorySpace())
        {
            TransitionToHome(agent, currentTick);
            return true;
        }

        // Hunger low — head home (but NOT if we have no food anywhere — must keep foraging)
        if (agent.Hunger < SimConfig.ForageExitHunger)
        {
            bool hasNoFoodAnywhere = agent.FoodInInventory() == 0
                && GetHomeFoodCount(agent, world) == 0;
            if (!hasNoFoodAnywhere)
            {
                TransitionToHome(agent, currentTick);
                return true;
            }
        }

        // Duration exceeded safety valve
        if (currentTick - agent.ModeEntryTick > SimConfig.ForageMaxDuration)
        {
            TransitionToHome(agent, currentTick);
            return true;
        }

        return false;
    }

    // ═════════════════════════════════════════════════════════════════
    // Build mode exits
    // ═════════════════════════════════════════════════════════════════

    private static bool EvaluateBuildExits(Agent agent, World world, int currentTick)
    {
        // Project complete — check if the structure is built on the target tile
        if (agent.ModeCommit.BuildTargetTile.HasValue)
        {
            var buildTile = world.GetTile(
                agent.ModeCommit.BuildTargetTile.Value.X,
                agent.ModeCommit.BuildTargetTile.Value.Y);

            bool projectDone = false;
            if (agent.ModeCommit.BuildRecipeId == "lean_to" && buildTile.HasShelter)
                projectDone = true;
            else if (agent.ModeCommit.BuildRecipeId == "granary" && buildTile.HasGranary)
                projectDone = true;

            if (projectDone)
            {
                TransitionToHome(agent, currentTick);
                return true;
            }
        }

        // Night time — go home to rest, project persists on tile
        if (Agent.IsNightTime(currentTick))
        {
            TransitionToHome(agent, currentTick);
            return true;
        }

        // Hunger low — go home or forage
        if (agent.Hunger < SimConfig.ForageExitHunger)
        {
            TransitionToHome(agent, currentTick);
            return true;
        }

        // Out of materials — forage for them
        if (agent.ModeCommit.BuildRecipeId == "lean_to")
        {
            int wood = agent.Inventory.GetValueOrDefault(ResourceType.Wood, 0);
            int stone = agent.Inventory.GetValueOrDefault(ResourceType.Stone, 0);
            if (wood < SimConfig.ShelterWoodCost || stone < SimConfig.ShelterStoneCost)
            {
                ResourceType needed = wood < SimConfig.ShelterWoodCost
                    ? ResourceType.Wood : ResourceType.Stone;
                var target = FindBestResourceTarget(agent, world, currentTick, needed);
                TransitionToForage(agent, currentTick, needed, target, SimConfig.ForageReturnFoodDefault);
                return true;
            }
        }
        else if (agent.ModeCommit.BuildRecipeId == "granary")
        {
            int wood = agent.Inventory.GetValueOrDefault(ResourceType.Wood, 0);
            int stone = agent.Inventory.GetValueOrDefault(ResourceType.Stone, 0);
            if (wood < SimConfig.GranaryWoodCost || stone < SimConfig.GranaryStoneCost)
            {
                ResourceType needed = wood < SimConfig.GranaryWoodCost
                    ? ResourceType.Wood : ResourceType.Stone;
                var target = FindBestResourceTarget(agent, world, currentTick, needed);
                TransitionToForage(agent, currentTick, needed, target, SimConfig.ForageReturnFoodDefault);
                return true;
            }
        }

        return false;
    }

    // ═════════════════════════════════════════════════════════════════
    // Explore mode exits
    // ═════════════════════════════════════════════════════════════════

    private static bool EvaluateExploreExits(Agent agent, World world, int currentTick)
    {
        // Tick budget expired
        if (currentTick - agent.ModeEntryTick >= agent.ModeCommit.ExploreBudget)
        {
            TransitionToHome(agent, currentTick);
            return true;
        }

        // Found something significant
        if (agent.PendingGeographicDiscoveries.Count > 0)
        {
            TransitionToHome(agent, currentTick);
            return true;
        }

        // Hunger getting low
        if (agent.Hunger < SimConfig.ExploreExitHunger)
        {
            TransitionToHome(agent, currentTick);
            return true;
        }

        // Health getting low
        if (agent.Health < SimConfig.ExploreExitHealth)
        {
            TransitionToHome(agent, currentTick);
            return true;
        }

        return false;
    }

    // ═════════════════════════════════════════════════════════════════
    // Caretaker mode exits (delegates with tighter constraints)
    // ═════════════════════════════════════════════════════════════════

    private static bool EvaluateCaretakerExits(Agent agent, World world, int currentTick,
        List<Agent>? allAgents)
    {
        // Caretaker can enter a short-range Forage
        if (agent.CurrentGoal != GoalType.ReturnHome
            && ShouldForageForFood(agent, world)
            && agent.Hunger > SimConfig.ForageEntryHunger)
        {
            // Forage with Caretaker constraints: shorter return threshold, limited range
            var target = FindBestFoodTarget(agent, world, currentTick);

            // Range check: only forage nearby home
            if (target.tile.HasValue && agent.HomeTile.HasValue)
            {
                int dist = Math.Max(
                    Math.Abs(target.tile.Value.X - agent.HomeTile.Value.X),
                    Math.Abs(target.tile.Value.Y - agent.HomeTile.Value.Y));
                if (dist > SimConfig.CaretakerForageRange)
                    target.tile = null; // Too far, don't forage there
            }

            if (target.tile.HasValue)
            {
                TransitionToForage(agent, currentTick,
                    target.resource ?? ResourceType.Berries,
                    target.tile, SimConfig.ForageReturnFoodCaretaker);
                return true;
            }
        }

        // Materials need (same as Home, but doesn't enter Explore)
        var materialNeed = GetMaterialNeed(agent, world);
        if (materialNeed.HasValue && agent.Hunger > SimConfig.ForageEntryHunger)
        {
            var target = FindBestResourceTarget(agent, world, currentTick, materialNeed.Value);

            // Range check
            if (target.HasValue && agent.HomeTile.HasValue)
            {
                int dist = Math.Max(
                    Math.Abs(target.Value.X - agent.HomeTile.Value.X),
                    Math.Abs(target.Value.Y - agent.HomeTile.Value.Y));
                if (dist > SimConfig.CaretakerForageRange)
                    target = null;
            }

            if (target.HasValue)
            {
                TransitionToForage(agent, currentTick, materialNeed.Value, target,
                    SimConfig.ForageReturnFoodCaretaker);
                return true;
            }
        }

        // Cannot enter Explore — that's structural (blocked here)

        return false;
    }

    // ═════════════════════════════════════════════════════════════════
    // Transition helpers
    // ═════════════════════════════════════════════════════════════════

    private static void TransitionToHome(Agent agent, int currentTick)
    {
        agent.TransitionMode(BehaviorMode.Home, currentTick);
        SetReturnHomeGoalIfNeeded(agent, currentTick);
    }

    private static void TransitionToForage(Agent agent, int currentTick,
        ResourceType targetResource, (int X, int Y)? targetTile, int returnThreshold)
    {
        agent.TransitionMode(BehaviorMode.Forage, currentTick);
        agent.ModeCommit.ForageTargetResource = targetResource;
        agent.ModeCommit.ForageTargetTile = targetTile;
        agent.ModeCommit.ForageReturnFoodThreshold = returnThreshold;

        if (targetTile.HasValue)
        {
            agent.CurrentGoal = IsFoodResource(targetResource)
                ? GoalType.GatherFoodAt : GoalType.GatherResourceAt;
            agent.GoalTarget = targetTile;
            agent.GoalResource = targetResource;
            agent.GoalStartTick = currentTick;
        }
    }

    /// <summary>Enters Explore mode with a committed direction and tick budget.</summary>
    private static void TransitionToExplore(Agent agent, World world, int currentTick)
    {
        agent.TransitionMode(BehaviorMode.Explore, currentTick);

        bool isExplorer = agent.Traits.Contains(PersonalityTrait.Explorer);
        agent.ModeCommit.ExploreBudget = isExplorer
            ? SimConfig.ExploreBudgetExplorer
            : SimConfig.ExploreBudgetDefault;

        // Pick direction: prefer least-recently-visited quadrant
        // Simple approach: pick from 8 directions, weight toward unexplored
        var dir = PickExploreDirection(agent, world);
        agent.ModeCommit.ExploreDirection = dir;

        // Set explore goal
        agent.CurrentGoal = GoalType.Explore;
        agent.GoalStartTick = currentTick;
        if (agent.HomeTile.HasValue)
        {
            // Target tile is some distance in the chosen direction
            int targetX = agent.X + dir.Dx * 10;
            int targetY = agent.Y + dir.Dy * 10;
            targetX = Math.Clamp(targetX, 0, world.Width - 1);
            targetY = Math.Clamp(targetY, 0, world.Height - 1);
            agent.GoalTarget = (targetX, targetY);
        }
    }

    /// <summary>Enters Build mode with a committed project.</summary>
    public static void TransitionToBuild(Agent agent, int currentTick,
        string recipeId, (int X, int Y) targetTile)
    {
        agent.TransitionMode(BehaviorMode.Build, currentTick);
        agent.ModeCommit.BuildRecipeId = recipeId;
        agent.ModeCommit.BuildTargetTile = targetTile;

        // Set build goal if not already at tile
        if (agent.X != targetTile.X || agent.Y != targetTile.Y)
        {
            agent.CurrentGoal = GoalType.BuildAtTile;
            agent.GoalTarget = targetTile;
            agent.GoalRecipeId = recipeId;
            agent.GoalStartTick = currentTick;
        }
    }

    private static void SetReturnHomeGoalIfNeeded(Agent agent, int currentTick)
    {
        if (agent.HomeTile.HasValue
            && (agent.X != agent.HomeTile.Value.X || agent.Y != agent.HomeTile.Value.Y))
        {
            agent.CurrentGoal = GoalType.ReturnHome;
            agent.GoalTarget = agent.HomeTile;
            agent.GoalStartTick = currentTick;
        }
    }

    // ═════════════════════════════════════════════════════════════════
    // Target finding helpers
    // ═════════════════════════════════════════════════════════════════

    private static (ResourceType? resource, (int X, int Y)? tile) FindBestFoodTarget(
        Agent agent, World world, int currentTick)
    {
        // Check current tile first
        var currentTile = world.GetTile(agent.X, agent.Y);
        foreach (var food in FoodTypes)
        {
            if (currentTile.Resources.TryGetValue(food, out int amt) && amt > 0)
                return (food, (agent.X, agent.Y));
        }

        // Check memory for nearest food
        var remembered = agent.GetRememberedFood(currentTick);
        if (remembered.Count > 0)
        {
            MemoryEntry? best = null;
            int bestDist = int.MaxValue;
            foreach (var mem in remembered)
            {
                int dist = Math.Max(Math.Abs(mem.X - agent.X), Math.Abs(mem.Y - agent.Y));

                // Prefer food closer to home (structural home-pull)
                if (agent.HomeTile.HasValue)
                {
                    int homeDistTarget = Math.Max(
                        Math.Abs(mem.X - agent.HomeTile.Value.X),
                        Math.Abs(mem.Y - agent.HomeTile.Value.Y));
                    // Weight: prefer closer to home when similar distance
                    dist += homeDistTarget / 3;
                }

                if (dist < bestDist) { bestDist = dist; best = mem; }
            }
            if (best != null)
                return (best.Resource, (best.X, best.Y));
        }

        // No known food — forage will explore
        return (ResourceType.Berries, null);
    }

    private static (int X, int Y)? FindBestResourceTarget(
        Agent agent, World world, int currentTick, ResourceType resource)
    {
        // Check current tile
        var currentTile = world.GetTile(agent.X, agent.Y);
        if (currentTile.Resources.TryGetValue(resource, out int amt) && amt > 0)
            return (agent.X, agent.Y);

        // Check memory
        var memories = agent.Memory.Where(m =>
            m.Type == MemoryType.Resource
            && m.Resource == resource
            && m.Quantity > 0
            && currentTick - m.TickObserved <= SimConfig.MemoryDecayTicks
        ).ToList();

        if (memories.Count > 0)
        {
            MemoryEntry? best = null;
            int bestDist = int.MaxValue;
            foreach (var mem in memories)
            {
                int dist = Math.Max(Math.Abs(mem.X - agent.X), Math.Abs(mem.Y - agent.Y));
                if (dist < bestDist) { bestDist = dist; best = mem; }
            }
            if (best != null) return (best.X, best.Y);
        }

        return null;
    }

    private static (int Dx, int Dy) PickExploreDirection(Agent agent, World world)
    {
        // 8 cardinal/diagonal directions
        (int Dx, int Dy)[] directions =
        {
            (1, 0), (-1, 0), (0, 1), (0, -1),
            (1, 1), (1, -1), (-1, 1), (-1, -1)
        };

        // Score each direction by how "unexplored" it is (fewer memories = better)
        var best = directions[0];
        int bestScore = int.MaxValue;

        foreach (var dir in directions)
        {
            // Check how many memory entries exist in this direction (5-tile cone)
            int memCount = 0;
            foreach (var mem in agent.Memory)
            {
                int relX = mem.X - agent.X;
                int relY = mem.Y - agent.Y;
                // Dot product: positive means memory is in this direction
                if (relX * dir.Dx + relY * dir.Dy > 0)
                    memCount++;
            }

            // Also check: is the direction valid? (not heading off map edge)
            int testX = agent.X + dir.Dx * 8;
            int testY = agent.Y + dir.Dy * 8;
            if (!world.IsInBounds(Math.Clamp(testX, 0, world.Width - 1),
                                   Math.Clamp(testY, 0, world.Height - 1)))
                memCount += 100; // Penalty for edge

            if (memCount < bestScore)
            {
                bestScore = memCount;
                best = dir;
            }
        }

        return best;
    }

    public static bool IsFoodResource(ResourceType type)
    {
        return type == ResourceType.Berries || type == ResourceType.Grain
            || type == ResourceType.Animals || type == ResourceType.Fish;
    }

    private static int GetHomeFoodCount(Agent agent, World world)
    {
        if (!agent.HomeTile.HasValue || !world.IsInBounds(agent.HomeTile.Value.X, agent.HomeTile.Value.Y))
            return 0;
        var homeTile = world.GetTile(agent.HomeTile.Value.X, agent.HomeTile.Value.Y);
        return homeTile.HasHomeStorage ? homeTile.HomeTotalFood : 0;
    }
}
