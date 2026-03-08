namespace CivSim.Core;

/// <summary>
/// v1.8 Behavioral Modes: Evaluates mode transitions each tick.
/// Called from AgentAI.DecideAction BEFORE mode-specific decision logic.
/// All transitions have hysteresis (entry/exit threshold gaps) to prevent oscillation.
/// </summary>
public static class ModeTransitionManager
{
    private static readonly ResourceType[] FoodTypes =
        { ResourceType.Berries, ResourceType.Grain, ResourceType.Meat, ResourceType.Fish };

    /// <summary>
    /// Evaluates whether the agent should transition to a different mode.
    /// Modifies agent.CurrentMode in place. Returns true if a transition occurred.
    /// </summary>
    public static bool EvaluateTransitions(Agent agent, World world, int currentTick,
        List<Agent>? allAgents = null)
    {
        // D15 Fix: Suppress non-Urgent mode transitions when agent is actively returning home.
        // Mode oscillation (Home↔Forage) was clearing ReturnHome goals and preventing agents
        // from actually walking home, causing indefinite drift at the leash boundary.
        if (agent.CurrentGoal == GoalType.ReturnHome && agent.HomeTile.HasValue)
        {
            int distHome = Math.Max(
                Math.Abs(agent.X - agent.HomeTile.Value.X),
                Math.Abs(agent.Y - agent.HomeTile.Value.Y));
            if (distHome > 2)
            {
                // Still allow Urgent entry — starvation overrides everything
                if (agent.CurrentMode != BehaviorMode.Urgent && ShouldEnterUrgent(agent))
                {
                    agent.TransitionMode(BehaviorMode.Urgent, currentTick);
                    return true;
                }
                return false; // Suppress all other transitions until agent reaches home
            }
        }

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

        // Goal Commitment Fix: Suppress non-emergency mode exits at night.
        // Transitioning to Forage at night causes immediate Forage->Home thrashing
        // because DecideForage's night rest check transitions back to Home,
        // destroying the gather goal in the process.
        if (Agent.IsNightTime(currentTick))
            return false;

        // D12 Fix 2: Forage re-entry cooldown — 50 ticks after entering Home mode.
        // Gives the agent time to deposit food, eat, rest, socialize, or experiment.
        bool forageCooldownActive = (currentTick - agent.ModeEntryTick) < 50;

        // ── Food low → Forage ──
        // Normal case: must be above ForageEntryHunger (well-fed enough for a trip).
        // Emergency case: if agent has NO food at all (personal + home = 0), skip the
        // hunger gate — they must forage or they'll starve in the dead zone between
        // Urgent exit (40) and normal Forage entry (55).
        // Directive #10 Fix 3b: Also boost entry when local food within HomeGatherRadius is depleted.
        if (ShouldForageForFood(agent, world) && !forageCooldownActive)
        {
            bool hasNoFoodAnywhere = agent.FoodInInventory() == 0
                && GetHomeFoodCount(agent, world) == 0;

            // Fix 3b: Local food scarcity check — fewer than 3 food sources nearby
            bool localFoodScarce = false;
            if (agent.HomeTile.HasValue)
            {
                int localFoodCount = CountFoodWithinRadius(agent, world,
                    agent.HomeTile.Value.X, agent.HomeTile.Value.Y, SimConfig.HomeGatherRadius);
                localFoodScarce = localFoodCount < 3;
            }

            if (agent.Hunger > SimConfig.ForageEntryHunger || hasNoFoodAnywhere || localFoodScarce)
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

        // ── Fix B6: Resume saved build project if materials now sufficient ──
        if (agent.SavedBuildRecipeId != null && agent.SavedBuildTargetTile.HasValue
            && agent.Hunger > SimConfig.ForageEntryHunger)
        {
            bool hasMaterials = false;
            if (agent.SavedBuildRecipeId == "lean_to")
            {
                hasMaterials = agent.Inventory.GetValueOrDefault(ResourceType.Wood, 0) >= SimConfig.ShelterWoodCost
                    && agent.Inventory.GetValueOrDefault(ResourceType.Stone, 0) >= SimConfig.ShelterStoneCost;
            }
            else if (agent.SavedBuildRecipeId == "granary")
            {
                hasMaterials = agent.Inventory.GetValueOrDefault(ResourceType.Wood, 0) >= SimConfig.GranaryWoodCost
                    && agent.Inventory.GetValueOrDefault(ResourceType.Stone, 0) >= SimConfig.GranaryStoneCost;
            }

            if (hasMaterials)
            {
                var recipeId = agent.SavedBuildRecipeId;
                var targetTile = agent.SavedBuildTargetTile.Value;
                agent.SavedBuildRecipeId = null;
                agent.SavedBuildTargetTile = null;
                TransitionToBuild(agent, currentTick, recipeId, targetTile);
                return true;
            }
        }

        // ── Fix 2: Forced shelter rebuild — exposed + knows lean_to + has materials → Build ──
        if (agent.IsExposed && agent.Knowledge.Contains("lean_to"))
        {
            int wood = agent.Inventory.GetValueOrDefault(ResourceType.Wood, 0);
            int stone = agent.Inventory.GetValueOrDefault(ResourceType.Stone, 0);
            if (wood >= SimConfig.ShelterWoodCost && stone >= SimConfig.ShelterStoneCost)
            {
                // Build at HomeTile if set, otherwise current position
                var buildTarget = agent.HomeTile ?? (agent.X, agent.Y);
                TransitionToBuild(agent, currentTick, "lean_to", buildTarget);
                return true;
            }
        }

        // ── Inventory bootstrapping → Forage for experiment materials ──
        // Exposed agents who lack resources for shelter recipes need targeted foraging.
        // Uses GetExperimentMaterialNeed to find the specific missing resource, regardless
        // of total inventory size (an agent with 20 food but 0 wood still needs wood).
        // Post-shelter agents use normal forage cycles for experiment materials.
        if (!forageCooldownActive && agent.IsExposed && agent.Hunger > SimConfig.ForageEntryHunger)
        {
            var experimentMaterial = GetExperimentMaterialNeed(agent);
            if (experimentMaterial.HasValue)
            {
                var target = FindBestResourceTarget(agent, world, currentTick, experimentMaterial.Value);
                TransitionToForage(agent, currentTick,
                    experimentMaterial.Value, target, SimConfig.ForageReturnFoodDefault);
                return true;
            }
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

        // An adult with zero personal food and no home food should forage.
        // This prevents newly matured adults (who inherit no inventory) from sitting idle.
        // But if home has food, they can eat from storage first — don't force unnecessary forage trips.

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

    /// <summary>
    /// Checks if the agent could experiment but lacks resources to do so.
    /// Returns the first resource type needed for an available-if-stocked recipe.
    /// This prevents newly matured adults from sitting idle when they know recipes
    /// but have empty inventories (youth stage doesn't gather into inventory).
    /// </summary>
    private static ResourceType? GetExperimentMaterialNeed(Agent agent)
    {
        foreach (var recipe in RecipeRegistry.AllRecipes)
        {
            // Skip already known or innate
            if (agent.Knowledge.Contains(recipe.Output)) continue;
            if (recipe.BaseChance <= 0f) continue;

            // Check knowledge prerequisites
            bool hasKnowledge = true;
            foreach (var req in recipe.RequiredKnowledge)
            {
                if (!agent.Knowledge.Contains(req)) { hasKnowledge = false; break; }
            }
            if (!hasKnowledge) continue;

            // This recipe is knowledge-eligible — check which resources are missing
            foreach (var kvp in recipe.RequiredResources)
            {
                int held = agent.Inventory.GetValueOrDefault(kvp.Key, 0);
                if (held < kvp.Value)
                    return kvp.Key; // Need this resource
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
        // D24 Fix 3B: Waive dwell time for newly matured adults (let them explore immediately)
        if (!agent.IsNewAdult && currentTick - agent.ModeEntryTick < SimConfig.ExploreMinHomeDwell)
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

        // Directive #8 Fix 5: Surplus-ready check — well-stocked settlement lowers food-carry requirement
        var homeTile = world.GetTile(agent.HomeTile.Value.X, agent.HomeTile.Value.Y);
        int homeFood = homeTile.HomeTotalFood;
        if (homeFood > 15)
            foodThreshold = Math.Max(foodThreshold - 1, isExplorer ? 2 : 3); // Well-stocked: lighter pack OK

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
        // D12 Fix 2: Decision-count-based Forage commitment.
        // Must complete 5 gathers before exit is evaluated (except emergencies).
        int gathers = agent.ForageGatherCount;
        int foodHeld = agent.FoodInInventory();

        // ── Always-allowed exits (survival overrides — bypass gather commitment) ──

        // Urgent hunger override — starving agents can always leave
        if (agent.Hunger < SimConfig.UrgentEntryHunger)
        {
            TransitionToHome(agent, currentTick);
            return true;
        }
        // Inventory full — can't carry more, go home
        // Fix 1B: Skip this exit if agent is on a non-food gather trip (stone/wood/ore).
        // Inventory may be full of food/wood but the agent hasn't reached the material target yet.
        if (!agent.HasInventorySpace())
        {
            bool onNonFoodGatherTrip = agent.CurrentGoal == GoalType.GatherResourceAt
                && agent.GoalResource.HasValue
                && !IsFoodResource(agent.GoalResource.Value);
            if (!onNonFoodGatherTrip)
            {
                TransitionToHome(agent, currentTick);
                return true;
            }
        }

        // D13: Removed hunger safety valve (Hunger < ForageExitHunger) that was bypassing
        // the Forage commitment gate. The Urgent mode transition at hunger < 30 handles
        // real emergencies. Moderate hunger should NOT abort a foraging trip.

        // Duration exceeded safety valve — prevents indefinite Forage when stuck
        if (currentTick - agent.ModeEntryTick > SimConfig.ForageMaxDuration)
        {
            TransitionToHome(agent, currentTick);
            return true;
        }

        // ── Commitment phase: must complete 3+ gathers before evaluating exit ──
        // Lowered from 5 to 3: with goal preservation (Parts 1-2), agents now
        // reliably reach targets. Shorter commitment = more responsive to conditions.
        if (gathers < 3)
            return false; // Still committed — keep gathering

        // ── Post-commitment: successful trip or extended trip ──

        // Carrying 6+ food = successful trip, head home
        if (foodHeld >= 6)
        {
            TransitionToHome(agent, currentTick);
            return true;
        }

        // 10+ gathers without enough food = area depleted, head home with what you have
        if (gathers >= 10)
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
                // Fix B6: Clear saved build state — project is complete
                agent.SavedBuildRecipeId = null;
                agent.SavedBuildTargetTile = null;
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

        // Out of materials — save build commitment, then forage for them
        if (agent.ModeCommit.BuildRecipeId == "lean_to")
        {
            int wood = agent.Inventory.GetValueOrDefault(ResourceType.Wood, 0);
            int stone = agent.Inventory.GetValueOrDefault(ResourceType.Stone, 0);
            if (wood < SimConfig.ShelterWoodCost || stone < SimConfig.ShelterStoneCost)
            {
                // Fix B6: Save build project before ModeCommit is cleared by TransitionToForage
                agent.SavedBuildRecipeId = agent.ModeCommit.BuildRecipeId;
                agent.SavedBuildTargetTile = agent.ModeCommit.BuildTargetTile;
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
                // Fix B6: Save build project before ModeCommit is cleared by TransitionToForage
                agent.SavedBuildRecipeId = agent.ModeCommit.BuildRecipeId;
                agent.SavedBuildTargetTile = agent.ModeCommit.BuildTargetTile;
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
        // D24 Fix 1C: Budget waste detection — return early if not making progress
        if (agent.ExploreStartPosition.HasValue)
        {
            int ticksInExplore = currentTick - agent.ModeEntryTick;
            int halfBudget = agent.ModeCommit.ExploreBudget / 2;
            if (ticksInExplore > halfBudget)
            {
                int distFromStart = Math.Max(
                    Math.Abs(agent.X - agent.ExploreStartPosition.Value.X),
                    Math.Abs(agent.Y - agent.ExploreStartPosition.Value.Y));
                if (distFromStart <= SimConfig.ExploreBudgetWasteRadius)
                {
                    RecordExploreDirection(agent, currentTick);
                    TransitionToHome(agent, currentTick);
                    return true;
                }
            }
        }

        // Tick budget expired
        if (currentTick - agent.ModeEntryTick >= agent.ModeCommit.ExploreBudget)
        {
            RecordExploreDirection(agent, currentTick);
            TransitionToHome(agent, currentTick);
            return true;
        }

        // Found something significant
        if (agent.PendingGeographicDiscoveries.Count > 0)
        {
            RecordExploreDirection(agent, currentTick);
            TransitionToHome(agent, currentTick);
            return true;
        }

        // Hunger getting low
        if (agent.Hunger < SimConfig.ExploreExitHunger)
        {
            RecordExploreDirection(agent, currentTick);
            TransitionToHome(agent, currentTick);
            return true;
        }

        // Health getting low
        if (agent.Health < SimConfig.ExploreExitHealth)
        {
            RecordExploreDirection(agent, currentTick);
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
        // Directive #6 Fix 3 Layer 3: Caretaker distance escape — if agent is more than
        // 15 tiles from home in Caretaker mode, force transition to Home with ReturnHome goal.
        // The Caretaker tether is 8 tiles; being 15+ away is already a bug state.
        if (agent.HomeTile.HasValue)
        {
            int homeDist = Math.Max(
                Math.Abs(agent.X - agent.HomeTile.Value.X),
                Math.Abs(agent.Y - agent.HomeTile.Value.Y));
            if (homeDist > SimConfig.CaretakerMTMEscapeDist)
            {
                agent.TransitionMode(BehaviorMode.Home, currentTick);
                SetReturnHomeGoalIfNeeded(agent, currentTick);
                return true;
            }
        }

        // Directive #6 Fix 1: Caretaker-Forage hysteresis.
        // Only transition to Forage when food is BEYOND the tether radius.
        // Food within CaretakerForageRange (8 tiles) is gathered locally in Caretaker mode
        // without a mode transition — this prevents the rapid Caretaker↔Forage oscillation.
        if (agent.CurrentGoal != GoalType.ReturnHome
            && ShouldForageForFood(agent, world)
            && agent.Hunger > SimConfig.ForageEntryHunger)
        {
            var target = FindBestFoodTarget(agent, world, currentTick);

            if (target.tile.HasValue && agent.HomeTile.HasValue)
            {
                int dist = Math.Max(
                    Math.Abs(target.tile.Value.X - agent.HomeTile.Value.X),
                    Math.Abs(target.tile.Value.Y - agent.HomeTile.Value.Y));
                // Within tether: stay in Caretaker, gather locally (no mode transition)
                if (dist <= SimConfig.CaretakerForageRange)
                    return false;
                // Beyond tether: transition to Forage for a real trip
                TransitionToForage(agent, currentTick,
                    target.resource ?? ResourceType.Berries,
                    target.tile, SimConfig.ForageReturnFoodCaretaker);
                return true;
            }
            // No known food tile — don't transition, let Caretaker decision handle it
        }

        // Materials need: same hysteresis — only transition if beyond tether
        var materialNeed = GetMaterialNeed(agent, world);
        if (materialNeed.HasValue && agent.Hunger > SimConfig.ForageEntryHunger)
        {
            var target = FindBestResourceTarget(agent, world, currentTick, materialNeed.Value);

            if (target.HasValue && agent.HomeTile.HasValue)
            {
                int dist = Math.Max(
                    Math.Abs(target.Value.X - agent.HomeTile.Value.X),
                    Math.Abs(target.Value.Y - agent.HomeTile.Value.Y));
                // Within tether: stay in Caretaker, gather locally
                if (dist <= SimConfig.CaretakerForageRange)
                    return false;
                // Beyond tether: transition to Forage
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
        agent.ForageModeEntryTick = currentTick;
        agent.ForageGatherCount = 0; // D12 Fix 2: Reset gather counter on Forage entry
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
        agent.LastReturnPathCheckDistance = 0;

        bool isExplorer = agent.Traits.Contains(PersonalityTrait.Explorer);
        agent.ModeCommit.ExploreBudget = isExplorer
            ? SimConfig.ExploreBudgetExplorer
            : SimConfig.ExploreBudgetDefault;

        // Pick direction: prefer least-recently-visited quadrant
        // Simple approach: pick from 8 directions, weight toward unexplored
        var dir = PickExploreDirection(agent, world, currentTick);
        agent.ModeCommit.ExploreDirection = dir;

        // D24 Fix 1C: Record start position for budget waste detection
        agent.ExploreStartPosition = (agent.X, agent.Y);

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

        // Fix 3c: Check memory for nearest food, bounded by ForageMaxRange from home
        var remembered = agent.GetRememberedFood(currentTick);
        if (remembered.Count > 0)
        {
            MemoryEntry? best = null;
            int bestDist = int.MaxValue;
            foreach (var mem in remembered)
            {
                // Fix 5: Skip edge buffer tiles
                if (IsInEdgeBuffer(mem.X, mem.Y, world))
                    continue;
                // D12 Fix 1: Skip blacklisted tiles
                if (agent.IsTileBlacklisted(mem.X, mem.Y, currentTick))
                    continue;
                // D24 Fix 2A: Skip impassable tiles (e.g., water with fish)
                if (float.IsPositiveInfinity(world.GetTile(mem.X, mem.Y).MovementCostMultiplier))
                    continue;

                // Fix 3c: Skip targets beyond ForageMaxRange from home
                if (agent.HomeTile.HasValue)
                {
                    int homeDistTarget = Math.Max(
                        Math.Abs(mem.X - agent.HomeTile.Value.X),
                        Math.Abs(mem.Y - agent.HomeTile.Value.Y));
                    if (homeDistTarget > SimConfig.ForageMaxRange)
                        continue; // Too far from home for a forage trip

                    int dist = Math.Max(Math.Abs(mem.X - agent.X), Math.Abs(mem.Y - agent.Y));
                    // Weight: prefer closer to home when similar distance
                    dist += homeDistTarget / 3;

                    if (dist < bestDist) { bestDist = dist; best = mem; }
                }
                else
                {
                    int dist = Math.Max(Math.Abs(mem.X - agent.X), Math.Abs(mem.Y - agent.Y));
                    if (dist < bestDist) { bestDist = dist; best = mem; }
                }
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
            && !IsInEdgeBuffer(m.X, m.Y, world) // Fix 5: skip edge tiles
            && !agent.IsTileBlacklisted(m.X, m.Y, currentTick) // D12 Fix 1
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

    private static (int Dx, int Dy) PickExploreDirection(Agent agent, World world, int currentTick)
    {
        // 8 cardinal/diagonal directions
        (int Dx, int Dy)[] directions =
        {
            (1, 0), (-1, 0), (0, 1), (0, -1),
            (1, 1), (1, -1), (-1, 1), (-1, -1)
        };

        int homeX = agent.HomeTile?.X ?? agent.X;
        int homeY = agent.HomeTile?.Y ?? agent.Y;

        // Score all valid directions
        var candidates = new List<(int Dx, int Dy, int MemCount)>();
        int maxMemCount = 0;

        foreach (var dir in directions)
        {
            int targetX = Math.Clamp(agent.X + dir.Dx * 10, 0, world.Width - 1);
            int targetY = Math.Clamp(agent.Y + dir.Dy * 10, 0, world.Height - 1);

            // Fix 5a: Skip targets within 3 tiles of map edge
            if (targetX < 3 || targetY < 3
                || targetX >= world.Width - 3 || targetY >= world.Height - 3)
                continue;

            // Fix 5b: Skip targets on Water biome
            var targetTile = world.GetTile(targetX, targetY);
            if (targetTile.Biome == BiomeType.Water)
                continue;

            // Bug 2 fix: Also check the first 3 tiles along the direction for water.
            // The target 10 tiles away may be land, but the path goes through water.
            // Without this check, the agent starts exploring and immediately hits water.
            bool earlyWater = false;
            for (int step = 1; step <= 3; step++)
            {
                int checkX = Math.Clamp(agent.X + dir.Dx * step, 0, world.Width - 1);
                int checkY = Math.Clamp(agent.Y + dir.Dy * step, 0, world.Height - 1);
                if (world.GetTile(checkX, checkY).Biome == BiomeType.Water)
                {
                    earlyWater = true;
                    break;
                }
            }
            if (earlyWater) continue;

            // Fix 5c: Skip targets near blacklisted positions (within 3 tiles)
            bool nearBlacklist = false;
            foreach (var kvp in agent.ExploreBlacklist)
            {
                if (!agent.IsPositionBlacklisted(kvp.Key.Item1, kvp.Key.Item2, currentTick))
                    continue;
                int dist = Math.Max(Math.Abs(kvp.Key.Item1 - targetX),
                                    Math.Abs(kvp.Key.Item2 - targetY));
                if (dist <= 3) { nearBlacklist = true; break; }
            }
            if (nearBlacklist) continue;

            // Fix 5d: Verify return path exists from target back to home
            var returnPath = SimplePathfinder.FindPath(targetX, targetY, homeX, homeY, world, maxNodes: 4000);
            if (returnPath == null) continue;

            // Direction is valid — score by memory count
            int memCount = 0;
            foreach (var mem in agent.Memory)
            {
                int relX = mem.X - agent.X;
                int relY = mem.Y - agent.Y;
                if (relX * dir.Dx + relY * dir.Dy > 0)
                    memCount++;
            }

            candidates.Add((dir.Dx, dir.Dy, memCount));
            if (memCount > maxMemCount) maxMemCount = memCount;
        }

        if (candidates.Count == 0)
        {
            // No valid direction — fall back toward home
            if (agent.HomeTile.HasValue)
            {
                int dx = Math.Sign(homeX - agent.X);
                int dy = Math.Sign(homeY - agent.Y);
                if (dx != 0 || dy != 0)
                    return (dx, dy);
            }
            return (1, 0); // absolute fallback
        }

        // D24 Fix 1A: Convert to weights (lower memCount = higher weight)
        // D24 Fix 1B: Apply recent direction cooldown penalty
        bool directionsDecayed = (currentTick - agent.LastExploreTripEndTick) > SimConfig.ExploreDirectionDecayTicks;

        var weighted = new List<(int Dx, int Dy, float Weight)>();
        bool anyCooldownApplied = false;
        foreach (var c in candidates)
        {
            float weight = maxMemCount + 1 - c.MemCount; // Invert: fewer memories = higher weight
            if (weight < 1f) weight = 1f;

            // Pre-D25b fix: Hard block most recent direction (prevents consecutive repeats)
            if (!directionsDecayed && agent.RecentExploreDirections.Count > 0
                && agent.RecentExploreDirections[agent.RecentExploreDirections.Count - 1] == (c.Dx, c.Dy)
                && candidates.Count > 1)
            {
                anyCooldownApplied = true;
                continue; // Completely exclude most recent direction
            }

            // D24 Fix 1B: Penalize other recently explored directions
            if (!directionsDecayed && agent.RecentExploreDirections.Contains((c.Dx, c.Dy)))
            {
                weight *= SimConfig.ExploreRecentDirectionPenalty;
                anyCooldownApplied = true;
            }

            weighted.Add((c.Dx, c.Dy, weight));
        }

        // Sort by weight descending
        weighted.Sort((a, b) => b.Weight.CompareTo(a.Weight));

        // D24 Fix 1A: First trip (no cooldown history) stays deterministic to preserve RNG cascade.
        // With decay=2000 ticks (>> 100 tick dwell), cooldowns persist between trips,
        // so weighted random kicks in from the 2nd trip onward naturally.
        if (!anyCooldownApplied)
            return (weighted[0].Dx, weighted[0].Dy);
        int topN = Math.Min(SimConfig.ExploreTopNDirections, weighted.Count);
        var topCandidates = weighted.GetRange(0, topN);

        var rng = new Random(agent.Id * 31 + currentTick / 100);
        float totalWeight = 0f;
        foreach (var tc in topCandidates) totalWeight += tc.Weight;

        float roll = (float)(rng.NextDouble() * totalWeight);
        float cumulative = 0f;
        foreach (var tc in topCandidates)
        {
            cumulative += tc.Weight;
            if (roll <= cumulative)
                return (tc.Dx, tc.Dy);
        }

        return (topCandidates[0].Dx, topCandidates[0].Dy);
    }

    /// <summary>D24 Fix 1B: Records the explore direction for cooldown on future trips.</summary>
    private static void RecordExploreDirection(Agent agent, int currentTick)
    {
        var dir = agent.ModeCommit.ExploreDirection;
        if (dir.HasValue && dir.Value != (0, 0))
        {
            agent.RecentExploreDirections.Add(dir.Value);
            while (agent.RecentExploreDirections.Count > SimConfig.ExploreRecentDirectionDepth)
                agent.RecentExploreDirections.RemoveAt(0);
            agent.LastExploreTripEndTick = currentTick;
        }
    }

    public static bool IsFoodResource(ResourceType type)
    {
        return type == ResourceType.Berries || type == ResourceType.Grain
            || type == ResourceType.Meat || type == ResourceType.Fish;
    }

    /// <summary>Directive #10 Fix 3b: Count food sources (tiles with any food resource > 0) within radius.</summary>
    private static int CountFoodWithinRadius(Agent agent, World world, int cx, int cy, int radius)
    {
        int count = 0;
        for (int dx = -radius; dx <= radius; dx++)
        {
            for (int dy = -radius; dy <= radius; dy++)
            {
                int tx = cx + dx, ty = cy + dy;
                if (!world.IsInBounds(tx, ty)) continue;
                var tile = world.GetTile(tx, ty);
                foreach (var food in FoodTypes)
                {
                    if (tile.Resources.TryGetValue(food, out int amt) && amt > 0)
                    {
                        count++;
                        break; // Count this tile once even if it has multiple food types
                    }
                }
            }
        }
        return count;
    }

    private static int GetHomeFoodCount(Agent agent, World world)
    {
        if (!agent.HomeTile.HasValue || !world.IsInBounds(agent.HomeTile.Value.X, agent.HomeTile.Value.Y))
            return 0;
        var homeTile = world.GetTile(agent.HomeTile.Value.X, agent.HomeTile.Value.Y);
        return homeTile.HasHomeStorage ? homeTile.HomeTotalFood : 0;
    }

    /// <summary>Directive #10 Fix 5: Returns true if the tile is within the 2-tile world edge buffer.</summary>
    private static bool IsInEdgeBuffer(int x, int y, World world)
    {
        return x < 2 || y < 2 || x >= world.Width - 2 || y >= world.Height - 2;
    }
}
