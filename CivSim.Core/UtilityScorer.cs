namespace CivSim.Core;

/// <summary>
/// A scored candidate action returned by the utility system.
/// AgentAI dispatches the highest-scoring action that can actually execute.
/// </summary>
public struct ScoredAction
{
    public ActionType Action;
    public float Score;
    public (int X, int Y)? TargetTile;
    public int? TargetAgentId;
    public string? TargetRecipeId;
    public ResourceType? TargetResource;

    public ScoredAction(ActionType action, float score,
        (int, int)? targetTile = null, int? targetAgentId = null,
        string? targetRecipeId = null, ResourceType? targetResource = null)
    {
        Action = action;
        Score = score;
        TargetTile = targetTile;
        TargetAgentId = targetAgentId;
        TargetRecipeId = targetRecipeId;
        TargetResource = targetResource;
    }
}

/// <summary>
/// Pure utility scoring system. Replaces P4-P12 priority cascade.
/// Evaluates all growth actions and returns a sorted list (highest score first).
/// Trait multipliers and action dampening are applied after base scoring.
///
/// P1-P3 (survival) remain hard gates in AgentAI — this scorer handles
/// everything AFTER survival needs are met.
/// GDD v1.8: shelter_basic → lean_to migration in ScoreBuild.
/// </summary>
public static class UtilityScorer
{
    private static readonly ResourceType[] FoodTypes =
        { ResourceType.Berries, ResourceType.Grain, ResourceType.Meat, ResourceType.Fish };

    /// <summary>
    /// Scores all non-survival actions for the given agent. Returns sorted list (highest first).
    /// </summary>
    public static List<ScoredAction> ScoreAll(Agent agent, World world, int currentTick, Random random,
        List<Agent>? allAgents = null, SettlementKnowledge? knowledgeSystem = null, List<Settlement>? settlements = null,
        Action<string>? trace = null)
    {
        var results = new List<ScoredAction>();
        var currentTile = world.GetTile(agent.X, agent.Y);

        // Fix B3: Check if agent has a nearby hungry dependent
        bool hungryDependent = HasNearbyHungryDependent(agent, allAgents);

        // ── Score each action category ──────────────────────────────
        ScoreGatherFood(agent, world, currentTile, currentTick, results,
            hasHungryDependent: hungryDependent);
        ScoreGatherResource(agent, world, currentTile, currentTick, results, knowledgeSystem, settlements);
        ScoreTendFarm(agent, world, currentTile, currentTick, results);
        // GDD v1.8: Teach removed — knowledge propagation is communal within settlements
        ScoreBuild(agent, world, currentTile, results);
        ScoreSocialize(agent, world, currentTick, results);
        ScoreReproduce(agent, world, currentTile, results, allAgents);
        ScoreExperiment(agent, world, currentTick, random, results, knowledgeSystem, settlements, trace);
        ScoreReturnHome(agent, results);
        ScoreDepositGranary(agent, world, currentTile, currentTick, results);
        ScoreWithdrawGranary(agent, world, currentTile, currentTick, results);
        ScorePreserve(agent, results);
        ScoreDepositHome(agent, world, currentTile, currentTick, results);
        ScoreShareFood(agent, world, results);
        ScoreExplore(agent, world, currentTile, currentTick, results);
        ScoreIdleRest(agent, currentTile, currentTick, results);
        // Directive #5 Fix 3b-3c: New tech-action pipelines
        ScoreClearLand(agent, world, currentTile, currentTick, results);
        ScoreTendAnimals(agent, world, currentTile, currentTick, results);
        // D25b: Hunt and Harvest scoring (food acquisition via animal entities)
        ScoreHunt(agent, world, currentTile, currentTick, results);
        ScoreHarvest(agent, world, currentTile, currentTick, results);

        // ── Apply trait multipliers ─────────────────────────────────
        ApplyTraitMultipliers(agent, results);

        // ── Apply action dampening ──────────────────────────────────
        ApplyDampening(agent, results);

        // ── Fix 3A: Post-dampening content-agent Experiment floor ────
        // Dampening can push the 0.30 floor below competitive range.
        // Re-enforce after all multipliers when agent is content AND recipes exist.
        ApplyExperimentContentFloor(agent, world, currentTick, results, knowledgeSystem, settlements);

        // ── Fix 3B: Suppress Rest/Idle when eligible recipes exist ────
        SuppressIdleWhenRecipesAvailable(agent, world, currentTick, results, knowledgeSystem, settlements);

        // ── D19: Restlessness motivation multiplier (FINAL multiplicative factor) ──
        ApplyRestlessnessMultiplier(agent, results);

        // ── Systemic 4: Night suppression ────────────────────────────────
        if (Agent.IsNightTime(currentTick))
        {
            for (int i = 0; i < results.Count; i++)
            {
                var sa = results[i];
                if (sa.Action == ActionType.Explore || sa.Action == ActionType.Experiment)
                {
                    sa.Score *= 0.5f;
                    results[i] = sa;
                }
                else if (sa.Action == ActionType.ReturnHome)
                {
                    sa.Score += 0.3f;
                    results[i] = sa;
                }
            }

            // Directive #8 Fix 3: Night rest floor for at-home sheltered agents
            bool atHome = agent.HomeTile.HasValue && agent.X == agent.HomeTile.Value.X && agent.Y == agent.HomeTile.Value.Y;
            if (atHome && currentTile.HasShelter)
            {
                // Ensure Rest is in results with score >= 0.80
                bool hasRest = false;
                for (int i = 0; i < results.Count; i++)
                {
                    if (results[i].Action == ActionType.Rest)
                    {
                        hasRest = true;
                        if (results[i].Score < 0.80f)
                        {
                            var sa = results[i];
                            sa.Score = 0.80f;
                            results[i] = sa;
                        }
                        break;
                    }
                }
                if (!hasRest)
                {
                    results.Add(new ScoredAction(ActionType.Rest, 0.80f));
                }
            }
        }

        // ── Fix 4A: Daytime idle guard — Rest must not beat productive actions ──
        ApplyDaytimeIdleGuard(agent, currentTick, results);

        // ── D24 Fix 3: Apply new adult productivity boost ──
        if (agent.IsNewAdult)
            ApplyNewAdultBoost(results);

        // ── Sort descending by score, break ties randomly ───────────
        // Shuffle first for tie-breaking, then stable sort by score
        for (int i = results.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (results[i], results[j]) = (results[j], results[i]);
        }
        results.Sort((a, b) => b.Score.CompareTo(a.Score));

        // Remove zero/negative scores
        results.RemoveAll(s => s.Score <= 0f);

        return results;
    }

    /// <summary>
    /// Scores only the actions available in Home mode.
    /// Home actions: Experiment, Socialize, Reproduce, DepositGranary, WithdrawGranary,
    /// Preserve, DepositHome, ShareFood, IdleRest, TendFarm, GatherFood (local only).
    /// When exposed (no shelter): also Build and GatherResource (for materials).
    /// Excludes: ReturnHome, Explore (these are mode transitions, not scored).
    /// </summary>
    public static List<ScoredAction> ScoreHomeActions(Agent agent, World world, int currentTick, Random random,
        List<Agent>? allAgents = null, SettlementKnowledge? knowledgeSystem = null, List<Settlement>? settlements = null,
        Action<string>? trace = null)
    {
        var results = new List<ScoredAction>();
        var currentTile = world.GetTile(agent.X, agent.Y);

        // Fix B3: Check if agent has a nearby hungry dependent (child hunger < 70)
        bool hungryDependent = HasNearbyHungryDependent(agent, allAgents);

        // ── Local food gathering (at current tile only — trips are Forage mode) ──
        ScoreGatherFood(agent, world, currentTile, currentTick, results, localOnly: true,
            hasHungryDependent: hungryDependent);

        // ── Score Home-appropriate actions ────────────────────────────
        ScoreTendFarm(agent, world, currentTile, currentTick, results);
        ScoreBuild(agent, world, currentTile, results);
        ScoreSocialize(agent, world, currentTick, results);
        ScoreReproduce(agent, world, currentTile, results, allAgents);
        ScoreExperiment(agent, world, currentTick, random, results, knowledgeSystem, settlements, trace);
        ScoreDepositGranary(agent, world, currentTile, currentTick, results);
        ScoreWithdrawGranary(agent, world, currentTile, currentTick, results);
        ScorePreserve(agent, results);
        ScoreDepositHome(agent, world, currentTile, currentTick, results);
        ScoreShareFood(agent, world, results);
        ScoreIdleRest(agent, currentTile, currentTick, results);
        // Directive #5 Fix 3b-3c: New tech-action pipelines
        ScoreClearLand(agent, world, currentTile, currentTick, results);
        ScoreTendAnimals(agent, world, currentTile, currentTick, results);
        // D25d: Tame scoring (Home mode only)
        ScoreTame(agent, world, currentTile, currentTick, results);
        // D25d: Wolf pup taming (requires bow + animal_domestication)
        ScoreTameWolfPup(agent, world, currentTick, results);
        // D25d: Pen feeding, penning animals, slaughter
        ScoreFeedPen(agent, world, currentTile, currentTick, results);
        ScorePenAnimal(agent, world, currentTile, currentTick, results);
        ScoreSlaughter(agent, world, currentTile, currentTick, results);

        // Directive: Surplus drives — proactive behavior when content
        ScoreSurplusDrives(agent, world, currentTile, results, knowledgeSystem, settlements, allAgents);

        // When exposed, also score GatherResource (for building materials)
        if (agent.IsExposed)
            ScoreGatherResource(agent, world, currentTile, currentTick, results, knowledgeSystem, settlements, homeMode: true);

        // ── Apply trait multipliers ───────────────────────────────────
        ApplyTraitMultipliers(agent, results);

        // ── Apply action dampening ────────────────────────────────────
        ApplyDampening(agent, results);

        // ── Fix 3A: Post-dampening content-agent Experiment floor ────
        ApplyExperimentContentFloor(agent, world, currentTick, results, knowledgeSystem, settlements);

        // ── Fix 3B: Suppress Rest/Idle when eligible recipes exist ────
        SuppressIdleWhenRecipesAvailable(agent, world, currentTick, results, knowledgeSystem, settlements);

        // ── D19: Restlessness motivation multiplier (FINAL multiplicative factor) ──
        ApplyRestlessnessMultiplier(agent, results);

        // ── Directive Principle 1: Travel cost discount ──────────────────
        // Every action is evaluated as (Action + Location + Travel Cost).
        // Closer actions score higher. An action 10 tiles away scores ~half.
        // Only applied in Home/Caretaker modes — Forage/Build/Explore have
        // already committed to a destination at mode entry.
        for (int i = 0; i < results.Count; i++)
        {
            var sa = results[i];
            if (!sa.TargetTile.HasValue) continue;
            int travelDist = Math.Max(
                Math.Abs(sa.TargetTile.Value.X - agent.X),
                Math.Abs(sa.TargetTile.Value.Y - agent.Y));
            if (travelDist <= 0) continue;
            float travelDiscount = 1.0f / (1.0f + travelDist * 0.05f);
            sa.Score *= travelDiscount;
            results[i] = sa;
        }

        // ── Directive Fix 2: Home mode soft distance penalty ────────────
        // 350×350 scale: strong pull ~50 tiles, negligible ~150 tiles.
        // Tiles 0-50: no penalty. Tile 100: ~halved. Tile 150: ~33%.
        if (agent.HomeTile.HasValue)
        {
            int distFromHome = Math.Max(Math.Abs(agent.X - agent.HomeTile.Value.X),
                Math.Abs(agent.Y - agent.HomeTile.Value.Y));
            if (distFromHome > 50)
            {
                float homePenalty = 1.0f / (1.0f + (distFromHome - 50) * 0.02f);
                for (int i = 0; i < results.Count; i++)
                {
                    var sa = results[i];
                    // ReturnHome is exempt from penalty — it's how you get back
                    if (sa.Action == ActionType.ReturnHome) continue;
                    sa.Score *= homePenalty;
                    results[i] = sa;
                }
            }
        }

        // ── Night suppression for experiment ──────────────────────────
        if (Agent.IsNightTime(currentTick))
        {
            for (int i = 0; i < results.Count; i++)
            {
                var sa = results[i];
                if (sa.Action == ActionType.Experiment)
                {
                    sa.Score *= 0.5f;
                    results[i] = sa;
                }
            }

            // Directive #8 Fix 3: Night rest floor for at-home sheltered agents (Forage scorer)
            bool atHome = agent.HomeTile.HasValue && agent.X == agent.HomeTile.Value.X && agent.Y == agent.HomeTile.Value.Y;
            if (atHome && currentTile.HasShelter)
            {
                bool hasRest = false;
                for (int i = 0; i < results.Count; i++)
                {
                    if (results[i].Action == ActionType.Rest)
                    {
                        hasRest = true;
                        if (results[i].Score < 0.80f)
                        {
                            var sa = results[i];
                            sa.Score = 0.80f;
                            results[i] = sa;
                        }
                        break;
                    }
                }
                if (!hasRest)
                {
                    results.Add(new ScoredAction(ActionType.Rest, 0.80f));
                }
            }
        }

        // ── Fix 4A: Daytime idle guard — Rest must not beat productive actions ──
        ApplyDaytimeIdleGuard(agent, currentTick, results);

        // ── D24 Fix 3: Apply new adult productivity boost ──
        if (agent.IsNewAdult)
            ApplyNewAdultBoost(results);

        // ── Sort descending by score, break ties randomly ─────────────
        for (int i = results.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (results[i], results[j]) = (results[j], results[i]);
        }
        results.Sort((a, b) => b.Score.CompareTo(a.Score));

        results.RemoveAll(s => s.Score <= 0f);

        return results;
    }

    // ── Individual Scoring Methods ──────────────────────────────────────

    private static void ScoreGatherFood(Agent agent, World world, Tile currentTile,
        int currentTick, List<ScoredAction> results, bool localOnly = false,
        bool hasHungryDependent = false)
    {
        // Base: 0.6 × (1 − hunger/100) — higher score when MORE hungry (lower hunger value)
        float hungerNeed = 1f - (agent.Hunger / 100f);
        float baseScore = 0.6f * hungerNeed;

        // Food buffer bonus: if carrying little food, always maintain a small reserve regardless of hunger
        int foodInInv = agent.FoodInInventory();
        if (foodInInv < 2) baseScore = Math.Max(baseScore, 0.25f);

        // Fix B3: When parent has 0 food and a hungry dependent nearby, boost gather urgency.
        // The child's hunger should influence the parent's action selection even when the parent
        // isn't personally hungry — "get food because my child needs it."
        if (hasHungryDependent && foodInInv == 0)
            baseScore = Math.Max(baseScore, 0.45f);

        // D12 Fix 4: Caretaker food emergency — when family is starving, gather is critical.
        if (agent.CurrentMode == BehaviorMode.Caretaker && foodInInv == 0)
        {
            int homeFood = 0;
            if (agent.HomeTile.HasValue)
            {
                var homeTile = world.GetTile(agent.HomeTile.Value.X, agent.HomeTile.Value.Y);
                homeFood = homeTile.HomeTotalFood;
            }
            if (homeFood < 2)
                baseScore = Math.Max(baseScore, 0.70f); // High priority — family is starving
        }

        // Pantry target: when home storage is low, gather surplus for the settlement.
        // Fades as storage approaches 15 food. Only applies post-shelter (HasHomeStorage).
        if (agent.HomeTile.HasValue && !agent.IsExposed && agent.Hunger > 60)
        {
            if (world.IsInBounds(agent.HomeTile.Value.X, agent.HomeTile.Value.Y))
            {
                var ht = world.GetTile(agent.HomeTile.Value.X, agent.HomeTile.Value.Y);
                if (ht.HasHomeStorage)
                {
                    int homeFood = ht.HomeTotalFood;
                    int pantryTarget = 15;
                    if (homeFood < pantryTarget)
                    {
                        float surplusBonus = 0.15f * (1.0f - (homeFood / (float)pantryTarget));
                        baseScore += surplusBonus;
                    }
                }
            }
        }

        if (baseScore <= 0.01f) return; // Well-fed and well-stocked, skip

        // Check current tile for food — prefer gathering where you already are
        foreach (var food in FoodTypes)
        {
            if (currentTile.Resources.TryGetValue(food, out int amt) && amt > 0 && agent.HasInventorySpace())
            {
                // Bonus for staying at current tile (commitment — reduces oscillation)
                float stayBonus = (agent.LastChosenUtilityAction == ActionType.Gather) ? 1.2f : 1.0f;
                results.Add(new ScoredAction(ActionType.Gather, baseScore * stayBonus,
                    targetTile: (currentTile.X, currentTile.Y), targetResource: food));
                return; // Only add one gather-food action
            }
        }

        // localOnly: Home mode only gathers nearby (within 5 tiles). Remote food is Forage mode's job.
        // Directive #9 Fix 6: Increased from 2→5 to reduce Home↔Forage oscillation.
        // Spatial hierarchy: Home gather (5) < Caretaker gather (8) < Forage trip (15+) < Explore (20+).
        if (localOnly)
        {
            var rememberedNearby = agent.GetRememberedFood(currentTick);
            foreach (var mem in rememberedNearby)
            {
                int dist = Math.Max(Math.Abs(mem.X - agent.X), Math.Abs(mem.Y - agent.Y));
                if (dist < 1 || dist > 5) continue; // Skip same tile (handled above) and far tiles

                // D24 Fix 2A: Skip impassable tiles (e.g., water with fish)
                var memTile = world.GetTile(mem.X, mem.Y);
                if (float.IsPositiveInfinity(memTile.MovementCostMultiplier)) continue;

                // D12 Fix 6: Home-mode gather radius cap from HomeTile (not just agent position)
                if (agent.HomeTile.HasValue)
                {
                    int homeDist = Math.Max(Math.Abs(mem.X - agent.HomeTile.Value.X), Math.Abs(mem.Y - agent.HomeTile.Value.Y));
                    if (homeDist > SimConfig.HomeGatherRadius) continue;
                }
                if (!agent.HasInventorySpace()) break;
                // D12 Fix 1: Skip blacklisted tiles
                if (agent.IsTileBlacklisted(mem.X, mem.Y, currentTick)) continue;

                float distPenalty = 1f / (1f + dist * 0.3f);
                results.Add(new ScoredAction(ActionType.Gather, baseScore * distPenalty,
                    targetTile: (mem.X, mem.Y), targetResource: mem.Resource));
                return; // One nearby gather option is enough
            }
            return;
        }

        // Check memory for food sources (only used by ScoreAll / Forage context)
        var rememberedFood = agent.GetRememberedFood(currentTick);
        if (rememberedFood.Count > 0)
        {
            // Find nearest food memory
            MemoryEntry? nearest = null;
            int bestDist = int.MaxValue;
            foreach (var mem in rememberedFood)
            {
                int dist = Math.Max(Math.Abs(mem.X - agent.X), Math.Abs(mem.Y - agent.Y));
                // D12 Fix 1: Skip blacklisted tiles
                if (agent.IsTileBlacklisted(mem.X, mem.Y, currentTick)) continue;
                // D24 Fix 2A: Skip impassable tiles (e.g., water with fish)
                if (float.IsPositiveInfinity(world.GetTile(mem.X, mem.Y).MovementCostMultiplier)) continue;
                if (dist < bestDist) { bestDist = dist; nearest = mem; }
            }
            if (nearest != null && bestDist > 0)
            {
                // Reduce score by distance (further = less attractive)
                float distPenalty = 1f / (1f + bestDist * 0.15f);

                // Systemic 1: Home-pull — prefer gathering toward home
                float homeBonus = 1.0f;
                if (agent.HomeTile.HasValue)
                {
                    int agentHomeDist = Math.Max(Math.Abs(agent.HomeTile.Value.X - agent.X), Math.Abs(agent.HomeTile.Value.Y - agent.Y));
                    int targetHomeDist = Math.Max(Math.Abs(agent.HomeTile.Value.X - nearest.X), Math.Abs(agent.HomeTile.Value.Y - nearest.Y));
                    if (targetHomeDist <= agentHomeDist) // Target is closer to home or same distance
                        homeBonus = 1.3f;
                }

                results.Add(new ScoredAction(ActionType.Gather, baseScore * distPenalty * homeBonus,
                    targetTile: (nearest.X, nearest.Y), targetResource: nearest.Resource));
            }
        }
    }

    private static void ScoreGatherResource(Agent agent, World world, Tile currentTile,
        int currentTick, List<ScoredAction> results,
        SettlementKnowledge? knowledgeSystem = null, List<Settlement>? settlements = null,
        bool homeMode = false)
    {
        if (!agent.HasInventorySpace()) return;

        int capacity = SimConfig.InventoryCapacity;
        if (agent.Knowledge.Contains("improved_shelter")) capacity += 10;
        float invSpace = (float)(capacity - agent.InventoryCount()) / capacity;

        float baseScore = 0.2f * invSpace;

        // D21 Hotfix 2: Non-food inventory guard — reduce Gather score for non-food
        // targets when agent already carries many non-food items (Stone/Ore/Wood/Hide/Bone).
        // 0-5: 1.0x, 6-8: 0.5x, 9+: 0.0x. Prevents inventory crowding → starvation.
        int nonFoodCount = agent.Inventory.GetValueOrDefault(ResourceType.Stone, 0)
                         + agent.Inventory.GetValueOrDefault(ResourceType.Ore, 0)
                         + agent.Inventory.GetValueOrDefault(ResourceType.Wood, 0)
                         + agent.Inventory.GetValueOrDefault(ResourceType.Hide, 0)
                         + agent.Inventory.GetValueOrDefault(ResourceType.Bone, 0);
        float nonFoodMultiplier = nonFoodCount <= SimConfig.NonFoodGatherFullScoreCap ? 1.0f
                                : nonFoodCount <= SimConfig.NonFoodGatherReducedCap ? SimConfig.NonFoodGatherReducedMultiplier
                                : 0.0f;
        baseScore *= nonFoodMultiplier;

        // Exposure boost: exposed agents need building materials urgently
        // Two cases: (1) knows lean_to — gather materials to build, (2) doesn't know lean_to — gather wood to experiment
        // Safety gate: hunger must be > 60 — food always beats materials when hungry
        ResourceType? neededMaterial = null;
        if (agent.IsExposed && agent.Hunger > 60f)
        {
            if (agent.Knowledge.Contains("lean_to"))
            {
                // Case 1: knows the recipe, needs materials to build
                int woodHeld = agent.Inventory.GetValueOrDefault(ResourceType.Wood, 0);
                int stoneHeld = agent.Inventory.GetValueOrDefault(ResourceType.Stone, 0);
                bool needWood = woodHeld < SimConfig.ShelterWoodCost;
                bool needStone = stoneHeld < SimConfig.ShelterStoneCost;
                if (needWood || needStone)
                {
                    baseScore = Math.Max(baseScore, 0.5f);
                    if (needStone) neededMaterial = ResourceType.Stone;
                    else if (needWood) neededMaterial = ResourceType.Wood;
                }
            }
            else
            {
                // Case 2: doesn't know lean_to yet — need wood to experiment with
                int woodHeld = agent.Inventory.GetValueOrDefault(ResourceType.Wood, 0);
                if (woodHeld < 3) // lean_to requires 3 wood
                {
                    baseScore = Math.Max(baseScore, 0.35f);
                    neededMaterial = ResourceType.Wood;
                }
            }
        }

        if (baseScore <= 0.01f) return;

        // Check current tile for non-food resources (prefer needed material if set)
        foreach (var kvp in currentTile.Resources)
        {
            if (kvp.Value > 0 && !IsEdible(kvp.Key))
            {
                if (neededMaterial == null || kvp.Key == neededMaterial.Value)
                {
                    results.Add(new ScoredAction(ActionType.Gather, baseScore,
                        targetTile: (currentTile.X, currentTile.Y), targetResource: kvp.Key));
                    return;
                }
            }
        }

        // If needed material not found on current tile but boost is active, check memory for it specifically
        var nonFoodMemories = agent.Memory.Where(m =>
            m.Type == MemoryType.Resource
            && m.Quantity > 0
            && m.Resource.HasValue && !IsEdible(m.Resource.Value)
            && (neededMaterial == null || m.Resource.Value == neededMaterial.Value)
            && currentTick - m.TickObserved <= SimConfig.MemoryDecayTicks
        ).ToList();

        if (nonFoodMemories.Count > 0)
        {
            MemoryEntry? nearest = null;
            int bestDist = int.MaxValue;
            foreach (var mem in nonFoodMemories)
            {
                int dist = Math.Max(Math.Abs(mem.X - agent.X), Math.Abs(mem.Y - agent.Y));
                // D12 Fix 1: Skip blacklisted tiles
                if (agent.IsTileBlacklisted(mem.X, mem.Y, currentTick)) continue;
                // D24 Fix 2A: Skip impassable tiles (e.g., water with fish)
                if (float.IsPositiveInfinity(world.GetTile(mem.X, mem.Y).MovementCostMultiplier)) continue;
                // D12 Fix 6: Home-mode radius cap — only gather resources near home
                if (homeMode && agent.HomeTile.HasValue)
                {
                    int homeDist = Math.Max(Math.Abs(mem.X - agent.HomeTile.Value.X), Math.Abs(mem.Y - agent.HomeTile.Value.Y));
                    if (homeDist > SimConfig.HomeGatherRadius) continue;
                }
                if (dist < bestDist) { bestDist = dist; nearest = mem; }
            }
            if (nearest != null && bestDist > 0)
            {
                float distPenalty = 1f / (1f + bestDist * 0.15f);

                // Systemic 1: Home-pull — prefer gathering toward home
                float homeBonus = 1.0f;
                if (agent.HomeTile.HasValue)
                {
                    int agentHomeDist = Math.Max(Math.Abs(agent.HomeTile.Value.X - agent.X), Math.Abs(agent.HomeTile.Value.Y - agent.Y));
                    int targetHomeDist = Math.Max(Math.Abs(agent.HomeTile.Value.X - nearest.X), Math.Abs(agent.HomeTile.Value.Y - nearest.Y));
                    if (targetHomeDist <= agentHomeDist)
                        homeBonus = 1.3f;
                }

                results.Add(new ScoredAction(ActionType.Gather, baseScore * distPenalty * 0.8f * homeBonus,
                    targetTile: (nearest.X, nearest.Y), targetResource: nearest.Resource));
                return; // Found via transient memory, skip lore fallback
            }
        }

        // GDD v1.8 Section 5: Settlement lore fallback — when transient memory has no
        // non-food resource locations, check settlement geographic lore for known deposits.
        // This is how "grandpa's knowledge of the ore vein" guides future generations.
        if (knowledgeSystem != null && settlements != null)
        {
            ResourceType[] nonFoodTypes = { ResourceType.Ore, ResourceType.Stone, ResourceType.Wood };
            GeographicEntry? bestLore = null;
            int bestLoreDist = int.MaxValue;

            foreach (var resourceType in nonFoodTypes)
            {
                var loreEntries = knowledgeSystem.GetGeographicLoreByResource(agent, resourceType, settlements);
                foreach (var entry in loreEntries)
                {
                    int dist = Math.Max(Math.Abs(entry.X - agent.X), Math.Abs(entry.Y - agent.Y));
                    // D12 Fix 6: Home-mode radius cap — only target resources near home
                    if (homeMode && agent.HomeTile.HasValue)
                    {
                        int homeDist = Math.Max(Math.Abs(entry.X - agent.HomeTile.Value.X), Math.Abs(entry.Y - agent.HomeTile.Value.Y));
                        if (homeDist > SimConfig.HomeGatherRadius) continue;
                    }
                    if (dist < bestLoreDist)
                    {
                        bestLoreDist = dist;
                        bestLore = entry;
                    }
                }
            }

            if (bestLore != null && bestLoreDist > 0)
            {
                // Lore-based scoring: slightly lower than transient memory (0.7× modifier)
                // because agent hasn't personally verified the resource is still there
                float distPenalty = 1f / (1f + bestLoreDist * 0.15f);

                // Systemic 1: Home-pull — prefer gathering toward home
                float loreHomeBonus = 1.0f;
                if (agent.HomeTile.HasValue)
                {
                    int agentHomeDist = Math.Max(Math.Abs(agent.HomeTile.Value.X - agent.X), Math.Abs(agent.HomeTile.Value.Y - agent.Y));
                    int targetHomeDist = Math.Max(Math.Abs(agent.HomeTile.Value.X - bestLore.X), Math.Abs(agent.HomeTile.Value.Y - bestLore.Y));
                    if (targetHomeDist <= agentHomeDist)
                        loreHomeBonus = 1.3f;
                }

                results.Add(new ScoredAction(ActionType.Gather, baseScore * distPenalty * 0.7f * loreHomeBonus,
                    targetTile: (bestLore.X, bestLore.Y), targetResource: bestLore.Resource));
            }
        }
    }

    private static void ScoreTendFarm(Agent agent, World world, Tile currentTile,
        int currentTick, List<ScoredAction> results)
    {
        if (!agent.Knowledge.Contains("farming")) return;

        // Directive #5 Fix 3a: Replace food saturation gate with surplus-based scoring.
        // TendFarm scores based on home storage fill ratio, NOT global food abundance.
        // Farming at home is more efficient than foraging — it should score HIGHER than forage
        // when agent has farming knowledge, because that's the evolutionary advantage of agriculture.
        float homeStorageFill = 1.0f;
        if (agent.HomeTile.HasValue)
        {
            var homeTile = world.GetTile(agent.HomeTile.Value.X, agent.HomeTile.Value.Y);
            if (homeTile.HasHomeStorage)
            {
                homeStorageFill = (float)homeTile.HomeTotalFood / homeTile.HomeStorageCapacity;
            }
        }
        float baseScore = 0.30f * (1.0f - homeStorageFill); // Slightly higher than forage (0.24)
        if (baseScore < 0.01f) return;

        // ── Fix 5: Count existing farm tiles near home for cap enforcement ──
        int existingFarmCount = 0;
        bool hasGranaryNearHome = false;
        int homeX = agent.HomeTile?.X ?? agent.X;
        int homeY = agent.HomeTile?.Y ?? agent.Y;

        // Scan within a reasonable radius around home to count farms and check for granary
        int scanRadius = SimConfig.FarmPreferredHomeDistance + 3; // scan a bit beyond preferred range
        for (int dx = -scanRadius; dx <= scanRadius; dx++)
            for (int dy = -scanRadius; dy <= scanRadius; dy++)
            {
                int sx = homeX + dx, sy = homeY + dy;
                if (!world.IsInBounds(sx, sy)) continue;
                var scanTile = world.GetTile(sx, sy);
                if (scanTile.HasFarm) existingFarmCount++;
                if (scanTile.HasGranary) hasGranaryNearHome = true;
            }

        // ── Priority 1: Tend existing farms that need tending ──
        // Check current tile first (if it already has a farm)
        if (currentTile.HasFarm &&
            currentTick - currentTile.LastTendedTick >= SimConfig.FarmTendedGracePeriod)
        {
            results.Add(new ScoredAction(ActionType.TendFarm, baseScore,
                targetTile: (agent.X, agent.Y)));
            return;
        }

        // Check memory for existing farms that need tending
        var farmMemories = agent.Memory.Where(m =>
            m.Type == MemoryType.Structure
            && m.StructureId == "farm"
            && currentTick - m.TickObserved <= SimConfig.MemoryDecayTicks
        ).ToList();

        foreach (var mem in farmMemories)
        {
            if (mem.X == agent.X && mem.Y == agent.Y) continue;
            var farmTile = world.GetTile(mem.X, mem.Y);
            if (currentTick - farmTile.LastTendedTick >= SimConfig.FarmTendedGracePeriod)
            {
                int dist = Math.Max(Math.Abs(mem.X - agent.X), Math.Abs(mem.Y - agent.Y));
                float distPenalty = 1f / (1f + dist * 0.2f);
                results.Add(new ScoredAction(ActionType.TendFarm, baseScore * distPenalty,
                    targetTile: (mem.X, mem.Y)));
                return;
            }
        }

        // ── Priority 2: Create a new farm on best-scored candidate tile ──
        // Enforce farm cap: no new farms if at limit (unless granary is built)
        int farmCap = hasGranaryNearHome ? int.MaxValue : SimConfig.MaxFarmTilesPreGranary;
        if (existingFarmCount >= farmCap) return;

        // Score candidate tiles for new farm placement using placement formula
        var bestCandidate = ScoreFarmPlacementCandidates(agent, world, homeX, homeY);
        if (bestCandidate.HasValue)
        {
            int dist = Math.Max(Math.Abs(bestCandidate.Value.X - agent.X),
                                Math.Abs(bestCandidate.Value.Y - agent.Y));
            float distPenalty = 1f / (1f + dist * 0.2f);
            results.Add(new ScoredAction(ActionType.TendFarm, baseScore * distPenalty,
                targetTile: bestCandidate.Value));
        }
    }

    /// <summary>
    /// Fix 5B: Scores candidate tiles for new farm placement.
    /// Formula: (1.0 if adjacent to existing farm, else 0.3)
    ///        x (1.0 if within 3 tiles of home, else 0.1)
    ///        x (1.0 if plains/cleared, else 0.0 for forest/water/mountain)
    ///        x (0.0 if is home tile, else 1.0)
    /// Returns the highest-scoring valid tile, or null if none found.
    /// </summary>
    internal static (int X, int Y)? ScoreFarmPlacementCandidates(Agent agent, World world,
        int homeX, int homeY)
    {
        float bestScore = 0f;
        (int X, int Y)? bestTile = null;

        // Search within a reasonable radius around home
        int searchRadius = SimConfig.FarmPreferredHomeDistance + 2;
        for (int dx = -searchRadius; dx <= searchRadius; dx++)
            for (int dy = -searchRadius; dy <= searchRadius; dy++)
            {
                int tx = homeX + dx, ty = homeY + dy;
                if (!world.IsInBounds(tx, ty)) continue;

                var tile = world.GetTile(tx, ty);

                // Hard exclusions: home tile, already has farm, non-farmable biome
                if (tx == homeX && ty == homeY) continue; // never on home tile
                if (tile.HasFarm) continue; // already a farm
                if (!tile.IsFarmable) continue; // must be Plains or cleared land

                // Biome factor: only Plains and cleared land score > 0
                // IsFarmable already filters this, but be explicit about forest/water/mountain
                float biomeFactor;
                if (tile.Biome == BiomeType.Plains || tile.Structures.Contains("cleared"))
                    biomeFactor = 1.0f;
                else
                    biomeFactor = 0.0f; // Forest, Water, Mountain, Desert
                if (biomeFactor <= 0f) continue;

                // Home tile exclusion (already handled above, but explicit in formula)
                float homeFactor = 1.0f; // already excluded home tile above

                // Distance from home factor
                int homeDist = Math.Max(Math.Abs(tx - homeX), Math.Abs(ty - homeY));
                float distFactor = homeDist <= SimConfig.FarmPreferredHomeDistance ? 1.0f : 0.1f;

                // Adjacency to existing farm factor
                bool adjacentToFarm = false;
                for (int adx = -1; adx <= 1 && !adjacentToFarm; adx++)
                    for (int ady = -1; ady <= 1 && !adjacentToFarm; ady++)
                    {
                        if (adx == 0 && ady == 0) continue;
                        int ax = tx + adx, ay = ty + ady;
                        if (world.IsInBounds(ax, ay) && world.GetTile(ax, ay).HasFarm)
                            adjacentToFarm = true;
                    }
                float adjFactor = adjacentToFarm ? 1.0f : 0.3f;

                float score = adjFactor * distFactor * biomeFactor * homeFactor;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestTile = (tx, ty);
                }
            }

        return bestTile;
    }

    // GDD v1.8: ScoreTeach REMOVED — knowledge propagation is communal within settlements.
    // No agent-to-agent teaching action exists. See communal knowledge propagation system.

    /// <summary>
    /// Directive #5 Fix 2: Proper buildable-recipe scan replacing the single-shelter gate.
    /// Scans all known structure recipes and scores the most valuable buildable option.
    /// Priority: emergency shelter > shelter upgrade > granary > utility structures.
    /// </summary>
    private static void ScoreBuild(Agent agent, World world, Tile currentTile, List<ScoredAction> results)
    {
        int woodHeld = agent.Inventory.GetValueOrDefault(ResourceType.Wood, 0);
        int stoneHeld = agent.Inventory.GetValueOrDefault(ResourceType.Stone, 0);

        // Determine build site: HomeTile if valid, otherwise current tile
        int buildX = agent.HomeTile.HasValue ? agent.HomeTile.Value.X : currentTile.X;
        int buildY = agent.HomeTile.HasValue ? agent.HomeTile.Value.Y : currentTile.Y;
        var buildTile = world.GetTile(buildX, buildY);

        // ── Priority 1: Emergency shelter when agent is exposed ──
        if (agent.Knowledge.Contains("lean_to") && !buildTile.HasShelter)
        {
            bool alreadySheltered = agent.HomeTile.HasValue
                && world.IsInBounds(agent.HomeTile.Value.X, agent.HomeTile.Value.Y)
                && world.GetTile(agent.HomeTile.Value.X, agent.HomeTile.Value.Y).HasShelter;

            if (!alreadySheltered)
            {
                bool shelterNearby = false;
                int sRadius = SimConfig.ShelterProximityRadius;
                for (int sdx = -sRadius; sdx <= sRadius && !shelterNearby; sdx++)
                    for (int sdy = -sRadius; sdy <= sRadius && !shelterNearby; sdy++)
                    {
                        int sx = buildX + sdx, sy = buildY + sdy;
                        if (world.IsInBounds(sx, sy) && world.GetTile(sx, sy).HasShelter)
                            shelterNearby = true;
                    }

                float needsBuilding = shelterNearby ? 0.3f : 1.0f;
                float exposureMultiplier = (agent.IsExposed && !shelterNearby) ? 3.0f : 1.0f;

                if (woodHeld >= SimConfig.ShelterWoodCost && stoneHeld >= SimConfig.ShelterStoneCost)
                {
                    float score = 0.5f * needsBuilding * exposureMultiplier;

                    // Directive #6 Fix 2: Hardcoded override — exposed agent with shelter
                    // knowledge and materials scores minimum 0.70 for Build.
                    // This beats everything except Urgent survival (90.0) and night rest (75.0).
                    // Being homeless with the ability to not be is a survival imperative.
                    if (agent.IsExposed)
                        score = Math.Max(score, 0.70f);

                    results.Add(new ScoredAction(ActionType.Build, score,
                        targetTile: (buildX, buildY), targetRecipeId: "lean_to"));
                }
            }
        }

        // ── Priority 2: Shelter upgrade (lean_to → improved_shelter) ──
        if (agent.Knowledge.Contains("reinforced_shelter") && buildTile.HasShelter
            && !buildTile.Structures.Contains("improved_shelter"))
        {
            // reinforced_shelter recipe requires 5 wood + 3 stone (same as granary)
            if (woodHeld >= SimConfig.GranaryWoodCost && stoneHeld >= SimConfig.GranaryStoneCost)
            {
                results.Add(new ScoredAction(ActionType.Build, 0.35f,
                    targetTile: (buildX, buildY), targetRecipeId: "reinforced_shelter"));
            }
        }

        // ── Priority 3: Granary (requires shelter on tile, not already built) ──
        if (agent.Knowledge.Contains("granary") && buildTile.HasShelter && !buildTile.HasGranary)
        {
            if (woodHeld >= SimConfig.GranaryWoodCost && stoneHeld >= SimConfig.GranaryStoneCost)
            {
                results.Add(new ScoredAction(ActionType.Build, 0.25f,
                    targetTile: (buildX, buildY), targetRecipeId: "granary"));
            }
        }

        // ── Priority 4: Campfire (requires fire knowledge, shelter on tile) ──
        if (agent.Knowledge.Contains("fire") && buildTile.HasShelter
            && !buildTile.Structures.Contains("campfire"))
        {
            // Campfire: 2 wood, 1 stone
            if (woodHeld >= 2 && stoneHeld >= 1)
            {
                results.Add(new ScoredAction(ActionType.Build, 0.20f,
                    targetTile: (buildX, buildY), targetRecipeId: "campfire"));
            }
        }

        // ── Priority 5: Animal Pen (requires animal_domestication, shelter) ──
        if (agent.Knowledge.Contains("animal_domestication") && buildTile.HasShelter
            && !buildTile.Structures.Contains("animal_pen"))
        {
            // Animal pen: 5 wood, 2 stone (D25d directive spec)
            if (woodHeld >= 5 && stoneHeld >= 2)
            {
                results.Add(new ScoredAction(ActionType.Build, 0.20f,
                    targetTile: (buildX, buildY), targetRecipeId: "animal_pen"));
            }
        }
    }

    private static void ScoreSocialize(Agent agent, World world, int currentTick, List<ScoredAction> results)
    {
        // Directive #6 Fix 4: Don't socialize while starving with no food
        if (agent.Hunger < 45f && agent.FoodInInventory() == 0)
            return;

        // D12 Fix 4: Caretaker food emergency — suppress Socialize when family is starving.
        // Don't bond with the baby while it starves; go gather food instead.
        if (agent.CurrentMode == BehaviorMode.Caretaker && agent.FoodInInventory() == 0)
        {
            int homeFood = 0;
            if (agent.HomeTile.HasValue)
            {
                var homeTile = world.GetTile(agent.HomeTile.Value.X, agent.HomeTile.Value.Y);
                homeFood = homeTile.HomeTotalFood;
            }
            if (homeFood < 2) return; // Food emergency — no socializing
        }

        // Directive Fix 1: Socialize requires proximity, not pursuit.
        // Only target agents within 3 tiles. No Socialize-driven movement.
        var rememberedAgents = agent.GetRememberedAgents(currentTick);
        if (rememberedAgents.Count == 0) return;

        // socialSat: rough approximation — more bonds = more satisfied
        float socialSat = Math.Min(1f, agent.SocialBonds.Count / 5f);
        float baseScore = 0.3f * (1f - socialSat);

        // Directive #5 Fix 1: Cooldown after completion — linear ramp 0→1 over 60 ticks
        int ticksSinceSocial = currentTick - agent.LastSocializedTick;
        float socialCooldown = (ticksSinceSocial < 60)
            ? ticksSinceSocial / 60.0f
            : 1.0f;

        // Directive #5 Fix 1: Daily diminishing returns — stacking penalty per completion today
        float dailyPenalty = 1.0f / (1.0f + agent.SocializeCountToday * 0.3f);

        baseScore *= socialCooldown * dailyPenalty;

        if (baseScore <= 0.01f) return;

        // Find best target — must be within 3 tiles (Principle 2: proximity, not pursuit)
        MemoryEntry? bestTarget = null;
        float bestTargetScore = -1f;

        foreach (var mem in rememberedAgents)
        {
            if (!mem.AgentId.HasValue) continue;
            int dist = Math.Max(Math.Abs(mem.X - agent.X), Math.Abs(mem.Y - agent.Y));
            if (dist > 3) continue; // Beyond conversation range — no pursuit

            // D13: Daily cap of 3 Socialize per partner pair per sim-day.
            // Once reached, this partner scores 0 — try someone else or do something productive.
            if (agent.SocializePartnerCountToday.TryGetValue(mem.AgentId.Value, out int partnerCount)
                && partnerCount >= 3)
                continue;

            float targetScore = baseScore;

            // Bonus for bonded agents
            if (agent.SocialBonds.TryGetValue(mem.AgentId.Value, out int bondStrength)
                && bondStrength >= SimConfig.SocialBondFriendThreshold)
            {
                targetScore += SimConfig.SocialBondUtilityBonus;
            }

            // Closer is better, but all within 3 tiles are valid
            float distPenalty = 1f / (1f + dist * 0.15f);
            targetScore *= distPenalty;

            if (targetScore > bestTargetScore)
            {
                bestTargetScore = targetScore;
                bestTarget = mem;
            }
        }

        if (bestTarget != null)
        {
            results.Add(new ScoredAction(ActionType.Socialize, bestTargetScore,
                targetTile: (bestTarget.X, bestTarget.Y),
                targetAgentId: bestTarget.AgentId));
        }
    }

    /// <summary>
    /// GDD v1.8 Section 3: Reproduction scoring using stability score system.
    /// Score = 0.3 × stabilityScore. Hard gates: CanReproduceWithPartner (age, cooldown, shelter proximity).
    /// </summary>
    private static void ScoreReproduce(Agent agent, World world, Tile currentTile, List<ScoredAction> results, List<Agent>? allAgents = null)
    {
        if (!agent.CanReproduce()) return;

        // Check for adjacent partner with full hard gates (including shelter proximity)
        var adjacentAgents = world.GetAdjacentAgents(agent.X, agent.Y);
        var samePos = world.GetAgentsAt(agent.X, agent.Y).Where(a => a.Id != agent.Id).ToList();
        var nearby = new List<Agent>(adjacentAgents);
        nearby.AddRange(samePos);

        var partner = nearby.FirstOrDefault(a => agent.CanReproduceWithPartner(a, world) && a.Id != agent.Id);
        if (partner == null) return;

        // Compute stability score — uses rolling history, food reserves, shelter, dependents, health
        float stabilityScore = allAgents != null
            ? agent.ComputeStabilityScore(world, allAgents)
            : 0.3f; // Fallback if allAgents not provided (backward compat)

        float score = 0.3f * stabilityScore;
        if (score > 0.01f)
        {
            results.Add(new ScoredAction(ActionType.Reproduce, score,
                targetAgentId: partner.Id));
        }
    }

    private static void ScoreExperiment(Agent agent, World world, int currentTick, Random random, List<ScoredAction> results,
        SettlementKnowledge? knowledgeSystem = null, List<Settlement>? settlements = null,
        Action<string>? trace = null)
    {
        // Survival gate: no experimenting while exposed and able to build shelter
        if (agent.IsExposed && agent.Knowledge.Contains("lean_to"))
            return;

        // Homeless urgency: agents who don't know any shelter recipe use a lower hunger gate (55)
        // This ensures unsheltered agents can experiment during the critical early-game window
        bool needsShelterDiscovery = agent.IsExposed
            && !agent.Knowledge.Contains("lean_to")
            && !agent.Knowledge.Contains("improved_shelter");
        float hungerGate = needsShelterDiscovery ? 55f : SimConfig.ExperimentHungerGate;

        // Survival gate: no experimenting while moderately hungry
        if (agent.Hunger <= hungerGate)
            return;

        var available = RecipeRegistry.GetAvailableRecipes(agent, knowledgeSystem, settlements);

        // Fix 3C: Trace log eligible recipe count during Experiment scoring
        trace?.Invoke($"[TRACE Agent {agent.Id}] EXPERIMENT-SCORING: {available.Count} eligible recipes, hunger={agent.Hunger:F0}, health={agent.Health}, exposed={agent.IsExposed}");

        if (available.Count == 0) return;

        // Score = 0.25 × surplusResources × (0.3 + 0.7 × foodSaturation)
        // When food abundant and content: up to 0.35. When scarce: drops to ~0.08.
        float foodSaturation = CalculateFoodSaturation(agent, world);
        bool hasResources = agent.InventoryCount() > 3;

        float baseScore = 0.25f * (hasResources ? 1f : 0.5f) * (0.3f + 0.7f * foodSaturation);

        // Content bonus: sheltered and well-fed agents should experiment more
        if (!agent.IsExposed && agent.Hunger > 70f && agent.Health > 70)
            baseScore += 0.10f;

        // D12 Fix 5: Curiosity ramp — time-since-last-discovery bonus
        // After 20 sim-days without a discovery, Experiment gets +0.25, making it competitive
        // Only applies when agent is fed and sheltered (content agents experiment, starving ones don't)
        if (!agent.IsExposed && agent.Hunger > 60f)
        {
            int ticksSinceLastDiscovery = currentTick - agent.LastSettlementDiscoveryTick;
            if (ticksSinceLastDiscovery > 0)
            {
                float curiosityBonus = Math.Min(ticksSinceLastDiscovery / 2000f * 0.05f, 0.25f);
                baseScore += curiosityBonus;
            }
        }

        // EARLY GAME: Exposed agents who don't know any shelter recipe should
        // desperately want to experiment for shelter. This is the #1 early-game priority.
        // 0.55 beats Gather (~0.30-0.50), Socialize (capped), Idle/Move (0.0).
        // 0.65 with wood means "I have materials and I'm homeless — figure out building."
        bool exposureBoosted = false;
        Recipe? shelterRecipe = null;
        if (needsShelterDiscovery && agent.Hunger > 50)
        {
            shelterRecipe = available.FirstOrDefault(r => r.Id == "lean_to");
            if (shelterRecipe != null)
            {
                float exposedScore = agent.HasWoodInInventory() ? 0.65f : 0.55f;
                baseScore = Math.Max(baseScore, exposedScore);
                exposureBoosted = true;
            }
        }

        // POST-SHELTER: Content agent baseline near home — Experiment as default productive activity.
        bool isAtHome = agent.HomeTile.HasValue
            && Math.Abs(agent.X - agent.HomeTile.Value.X) <= 2
            && Math.Abs(agent.Y - agent.HomeTile.Value.Y) <= 2;
        bool isContent = agent.Hunger > 60
            && agent.Health > 70
            && !agent.IsExposed
            && isAtHome;

        if (isContent)
        {
            baseScore = Math.Max(baseScore, 0.30f);
        }

        if (baseScore <= 0.01f) return;

        // When exposure-boosted, always target lean_to. Otherwise pick a random available recipe.
        var recipe = (exposureBoosted && shelterRecipe != null)
            ? shelterRecipe
            : available[random.Next(available.Count)];
        results.Add(new ScoredAction(ActionType.Experiment, baseScore,
            targetRecipeId: recipe.Id));
    }

    private static void ScoreReturnHome(Agent agent, List<ScoredAction> results)
    {
        if (!agent.HomeTile.HasValue) return;

        var home = agent.HomeTile.Value;
        int dist = Math.Max(Math.Abs(home.X - agent.X), Math.Abs(home.Y - agent.Y));

        if (dist <= 50) return; // 350×350 scale: within home radius (~50 tiles) — free to act

        // Trip commitment: suppress ReturnHome when actively on a task nearby
        // Prevents Gather↔ReturnHome and Socialize↔ReturnHome oscillation
        if (agent.Hunger > 50f && dist <= 80) // Not hungry and still reasonably close to home
        {
            if (agent.LastChosenUtilityAction == ActionType.Gather && agent.HasInventorySpace())
                return; // Stay out and keep gathering until full
            if (agent.LastChosenUtilityAction == ActionType.Socialize)
                return; // Let the Socialize action reach its target
            if (agent.LastChosenUtilityAction == ActionType.Explore && agent.ConsecutiveSameActionTicks <= 5)
                return; // Commit to a short exploration burst
        }

        // 350×350 scale: strong pull at ~50-80 tiles, negligible at ~150 tiles.
        // At dist 60: score = 4/(1+0.1*60) = 0.57; at dist 100: 0.36; at dist 150: 0.25
        // Floor of 0.06 ensures ReturnHome always competes at extreme distance
        float score = SimConfig.HomePullStrength / (1f + dist * 0.1f);
        score = Math.Max(score, 0.06f);

        // Systemic 1: Hunger-modulated home pull — go home before starving
        if (agent.Hunger < 70f)
            score += (1f - agent.Hunger / 100f) * 0.3f;

        results.Add(new ScoredAction(ActionType.ReturnHome, score,
            targetTile: (home.X, home.Y)));
    }

    private static void ScoreDepositGranary(Agent agent, World world, Tile currentTile,
        int currentTick, List<ScoredAction> results)
    {
        int food = agent.FoodInInventory();
        if (food <= 2 || agent.Hunger <= 60f) return; // Keep personal stash, don't deposit if hungry

        // Check for granary with space (current tile or memory)
        Tile? granaryTile = null;
        if (currentTile.HasGranary && currentTile.GranaryTotalFood < SimConfig.GranaryCapacity)
            granaryTile = currentTile;

        if (granaryTile == null)
        {
            var granaryMemories = agent.Memory.Where(m =>
                m.Type == MemoryType.Structure
                && m.StructureId == "granary"
                && currentTick - m.TickObserved <= SimConfig.MemoryDecayTicks
            ).ToList();

            foreach (var mem in granaryMemories)
            {
                var tile = world.GetTile(mem.X, mem.Y);
                if (tile.HasGranary && tile.GranaryTotalFood < SimConfig.GranaryCapacity)
                {
                    granaryTile = tile;
                    break;
                }
            }
        }

        if (granaryTile == null) return;

        float fullness = (float)granaryTile.GranaryTotalFood / SimConfig.GranaryCapacity;
        float baseScore = 0.4f * (1f - fullness);

        results.Add(new ScoredAction(ActionType.Deposit, baseScore,
            targetTile: (granaryTile.X, granaryTile.Y)));
    }

    private static void ScoreWithdrawGranary(Agent agent, World world, Tile currentTile,
        int currentTick, List<ScoredAction> results)
    {
        if (agent.Hunger >= 60f) return; // Not hungry enough to bother
        if (agent.FoodInInventory() >= 3) return; // Already has food

        // Find granary with food
        Tile? granaryTile = null;
        if (currentTile.HasGranary && currentTile.GranaryTotalFood > 0)
            granaryTile = currentTile;

        if (granaryTile == null)
        {
            var granaryMemories = agent.Memory.Where(m =>
                m.Type == MemoryType.Structure
                && m.StructureId == "granary"
                && currentTick - m.TickObserved <= SimConfig.MemoryDecayTicks
            ).ToList();

            foreach (var mem in granaryMemories)
            {
                var tile = world.GetTile(mem.X, mem.Y);
                if (tile.HasGranary && tile.GranaryTotalFood > 0)
                {
                    granaryTile = tile;
                    break;
                }
            }
        }

        if (granaryTile == null) return;

        float hungerNeed = 1f - (agent.Hunger / 100f);
        float baseScore = 0.5f * hungerNeed;

        results.Add(new ScoredAction(ActionType.Withdraw, baseScore,
            targetTile: (granaryTile.X, granaryTile.Y)));
    }

    // ── GDD v1.8 Section 7: Home Storage Deposit ────────────────────────

    private static void ScoreDepositHome(Agent agent, World world, Tile currentTile, int currentTick, List<ScoredAction> results)
    {
        if (!agent.HomeTile.HasValue) return;
        if (agent.Hunger <= 60f) return; // Don't deposit if hungry

        // Fix 2B: Cooldown — don't re-score DepositHome within DEPOSIT_HOME_COOLDOWN ticks of last deposit.
        if (agent.LastDepositTick > 0 && (currentTick - agent.LastDepositTick) < SimConfig.DEPOSIT_HOME_COOLDOWN)
            return;

        bool atHomeTile = agent.X == agent.HomeTile.Value.X && agent.Y == agent.HomeTile.Value.Y;

        // Fix 2C: Don't override committed goals.
        // If agent has an active goal that isn't DepositHome/ReturnHome AND is not at home, score 0.
        if (!atHomeTile && agent.CurrentGoal.HasValue
            && agent.CurrentGoal.Value != GoalType.ReturnHome)
            return;

        // At home — deposit directly (mode system handles returning home)
        if (!atHomeTile)
            return; // Not at home — mode transitions handle the return

        // Count inventory
        int food = agent.FoodInInventory();
        int woodHeld = agent.Inventory.GetValueOrDefault(ResourceType.Wood, 0);
        int stoneHeld = agent.Inventory.GetValueOrDefault(ResourceType.Stone, 0);

        // Check if there's room to deposit anything
        bool canDepositFood = food > 0 && currentTile.HasHomeStorage && currentTile.HomeTotalFood < currentTile.HomeStorageCapacity;
        bool canDepositMaterials = (woodHeld > 0 || stoneHeld > 0) && currentTile.HomeTotalMaterials < Tile.MaterialStorageCapacity;
        if (!canDepositFood && !canDepositMaterials) return;

        // D21 Fix 1: Compute actual depositable amounts matching CompleteDepositHome
        // keep-for-self logic (keeps 2 food, 3 of each material). An action that
        // can't actually deposit anything shouldn't score — prevents phantom scoring
        // where DaytimeIdleGuard zeros Rest based on a score that fails at dispatch.
        int depositableFood = canDepositFood ? Math.Max(0, food - 2) : 0;
        int depositableMaterials = canDepositMaterials
            ? Math.Max(0, woodHeld - 3) + Math.Max(0, stoneHeld - 3) : 0;
        int totalDepositable = depositableFood + depositableMaterials;
        if (totalDepositable <= 0) return;

        // Fix 2A: Minimum deposit threshold — don't deposit trivial amounts.
        if (totalDepositable < SimConfig.DEPOSIT_HOME_MIN_THRESHOLD) return;

        // D12 Fix 3: Inventory threshold replaces tick-based cooldown.
        // Don't deposit until carrying 5+ depositable items. Creates natural batch-gathering cycles.
        if (totalDepositable < 5) return;

        // D12 Fix 3: Scale with depositable inventory above threshold.
        // 5 items: 0.30, 8 items: 0.54, 12 items: 0.86, cap at 0.90
        float depositScore = Math.Min(0.90f, 0.30f + (totalDepositable - 5) * 0.08f);

        // D14 Fix 1: Scale deposit score inversely with home stockpile size.
        // Prevents deposit spam when the home is already well-stocked.
        int homeStockpile = currentTile.HomeTotalFood + currentTile.HomeTotalMaterials;
        if (homeStockpile > 20)
            depositScore *= 0.3f;
        else if (homeStockpile > 10)
            depositScore *= 0.5f;

        results.Add(new ScoredAction(ActionType.DepositHome, depositScore,
            targetTile: (currentTile.X, currentTile.Y)));
    }

    private static void ScorePreserve(Agent agent, List<ScoredAction> results)
    {
        if (!agent.Knowledge.Contains("food_preservation")) return;
        if (agent.FoodInInventory() < 2) return;
        if (agent.Hunger <= 60f) return; // Don't preserve when hungry

        // Systemic 2.5: Must be at a fire source (home tile with fire knowledge, or campfire)
        bool atFireSource = false;
        if (agent.HomeTile.HasValue && agent.X == agent.HomeTile.Value.X && agent.Y == agent.HomeTile.Value.Y
            && agent.Knowledge.Contains("fire"))
            atFireSource = true;
        // Could also check for campfire structure on current tile here
        if (!atFireSource) return;

        float baseScore = 0.35f;
        results.Add(new ScoredAction(ActionType.Preserve, baseScore));
    }

    private static void ScoreExplore(Agent agent, World world, Tile currentTile,
        int currentTick, List<ScoredAction> results)
    {
        // Survival gate: no exploring when exposed and able to build shelter
        if (agent.IsExposed && agent.Knowledge.Contains("lean_to"))
        {
            results.Add(new ScoredAction(ActionType.Explore, 0.01f));
            return;
        }

        // resourceDensity: how many resources are visible nearby
        int visibleResources = 0;
        int scanTiles = 0;
        int radius = 3;
        for (int dx = -radius; dx <= radius; dx++)
            for (int dy = -radius; dy <= radius; dy++)
            {
                int tx = agent.X + dx, ty = agent.Y + dy;
                if (!world.IsInBounds(tx, ty)) continue;
                scanTiles++;
                var tile = world.GetTile(tx, ty);
                foreach (var kvp in tile.Resources)
                    if (kvp.Value > 0) visibleResources++;
            }

        float resourceDensity = scanTiles > 0 ? Math.Min(1f, visibleResources / (float)(scanTiles * 0.5f)) : 0f;

        // Social pull: slightly higher score when agents are remembered nearby
        float socialPull = 0f;
        var rememberedAgents = agent.GetRememberedAgents(currentTick);
        if (rememberedAgents.Count > 0)
            socialPull = 0.05f; // Tiny bonus — explore is still about novelty

        float baseScore = 0.05f * (1f - resourceDensity) + socialPull;

        // Minimum floor — explore should always be an option (but very low)
        baseScore = Math.Max(0.01f, baseScore);

        // Systemic 1: Suppress explore when far from home
        if (agent.HomeTile.HasValue)
        {
            int homeDist = Math.Max(Math.Abs(agent.HomeTile.Value.X - agent.X), Math.Abs(agent.HomeTile.Value.Y - agent.Y));
            if (homeDist > SimConfig.CaretakerTetherRadius)
                baseScore = 0.01f; // Don't wander further from home
        }

        results.Add(new ScoredAction(ActionType.Explore, baseScore));
    }

    // ── Systemic 2: Idle Rest — low-priority rest when content ──────────

    private static void ScoreIdleRest(Agent agent, Tile currentTile, int currentTick, List<ScoredAction> results)
    {
        // Only rest when at home/shelter, not hungry, health not full
        if (agent.Health >= 90) return;
        if (agent.Hunger <= 60f) return; // Hungry, don't idle rest
        if (!currentTile.HasShelter && (!agent.HomeTile.HasValue || agent.X != agent.HomeTile.Value.X || agent.Y != agent.HomeTile.Value.Y))
            return; // Not at shelter or home

        float healthNeed = 1f - (agent.Health / 100f);
        float score = 0.15f * healthNeed;

        if (score > 0.01f)
            results.Add(new ScoredAction(ActionType.Rest, score));
    }

    // ── Fix B3: Hungry Dependent Detection ──────────────────────────────

    /// <summary>Returns true if the agent has a nearby child (relationship) with hunger below 70.</summary>
    private static bool HasNearbyHungryDependent(Agent agent, List<Agent>? allAgents)
    {
        if (allAgents == null) return false;
        foreach (var kvp in agent.Relationships)
        {
            if (kvp.Value != RelationshipType.Child) continue;
            var child = allAgents.FirstOrDefault(a => a.Id == kvp.Key);
            if (child != null && child.IsAlive && child.Hunger < 70f)
            {
                int dist = Math.Max(Math.Abs(child.X - agent.X), Math.Abs(child.Y - agent.Y));
                if (dist <= SimConfig.CaretakerForageRange)
                    return true;
            }
        }
        return false;
    }

    // ── Directive: Surplus Drives (Anti-Idleness) ──────────────────────

    /// <summary>
    /// Scores proactive surplus behavior when agent is content (fed, sheltered, healthy).
    /// Drives: food stockpiling, material stockpiling, settlement improvement, experimentation.
    /// These scores beat Socialize (~0.07-0.15) and Idle (0.0) so content agents stay productive.
    /// </summary>
    private static void ScoreSurplusDrives(Agent agent, World world, Tile currentTile,
        List<ScoredAction> results, SettlementKnowledge? knowledgeSystem, List<Settlement>? settlements,
        List<Agent>? allAgents = null)
    {
        // Only when content: fed, not in crisis
        if (agent.Hunger <= 70f) return; // Hungry — survival scoring handles this
        if (!agent.HomeTile.HasValue) return; // Homeless — shelter discovery handles this

        // Don't generate surplus drives when agent has young dependents — Caretaker mode takes priority
        if (allAgents != null)
        {
            foreach (var kvp in agent.Relationships)
            {
                if (kvp.Value != RelationshipType.Child) continue;
                var child = allAgents.FirstOrDefault(a => a.Id == kvp.Key);
                if (child != null && child.IsAlive && child.Age < SimConfig.CaretakerExitChildAge)
                    return;
            }
        }

        bool atHome = agent.X == agent.HomeTile.Value.X && agent.Y == agent.HomeTile.Value.Y;
        var homeTile = world.GetTile(agent.HomeTile.Value.X, agent.HomeTile.Value.Y);

        // ── Drive 1: Food Stockpiling ──
        // Trigger: home storage below capacity AND not currently hungry
        if (homeTile.HasHomeStorage)
        {
            int storedFood = homeTile.HomeTotalFood;
            int capacity = homeTile.HomeStorageCapacity;
            if (storedFood < capacity)
            {
                float fillRatio = (float)storedFood / capacity;
                float stockpileScore = 0.3f * (1.0f - fillRatio);
                if (stockpileScore > 0.02f)
                {
                    // Score as Gather with home tile target — dispatch will trigger Forage
                    results.Add(new ScoredAction(ActionType.Gather, stockpileScore,
                        targetTile: agent.HomeTile, targetResource: ResourceType.Berries));
                }
            }
        }

        // ── Drive 2: Material Stockpiling ──
        // Trigger: home wood < 10 OR home stone < 5
        {
            // D21 Hotfix 2: Apply same non-food inventory guard as ScoreGatherResource
            int nonFoodCount = agent.Inventory.GetValueOrDefault(ResourceType.Stone, 0)
                             + agent.Inventory.GetValueOrDefault(ResourceType.Ore, 0)
                             + agent.Inventory.GetValueOrDefault(ResourceType.Wood, 0)
                             + agent.Inventory.GetValueOrDefault(ResourceType.Hide, 0)
                             + agent.Inventory.GetValueOrDefault(ResourceType.Bone, 0);
            float nonFoodMult = nonFoodCount <= SimConfig.NonFoodGatherFullScoreCap ? 1.0f
                              : nonFoodCount <= SimConfig.NonFoodGatherReducedCap ? SimConfig.NonFoodGatherReducedMultiplier
                              : 0.0f;

            int homeWood = homeTile.HomeMaterialStorage.GetValueOrDefault(ResourceType.Wood, 0);
            int homeStone = homeTile.HomeMaterialStorage.GetValueOrDefault(ResourceType.Stone, 0);

            if (homeWood < 10)
            {
                float woodNeed = 1.0f - (homeWood / 10f);
                float woodScore = 0.2f * woodNeed * nonFoodMult;
                if (woodScore > 0.02f)
                {
                    results.Add(new ScoredAction(ActionType.Gather, woodScore,
                        targetTile: agent.HomeTile, targetResource: ResourceType.Wood));
                }
            }

            if (homeStone < 5)
            {
                float stoneNeed = 1.0f - (homeStone / 5f);
                float stoneScore = 0.2f * stoneNeed * nonFoodMult;
                if (stoneScore > 0.02f)
                {
                    results.Add(new ScoredAction(ActionType.Gather, stoneScore,
                        targetTile: agent.HomeTile, targetResource: ResourceType.Stone));
                }
            }
        }

        // ── Drive 3: Settlement Improvement ──
        // Build structures the agent knows but hasn't built yet
        if (atHome)
        {
            // Improved shelter: knows it, has lean_to, doesn't have improved yet
            if (agent.Knowledge.Contains("reinforced_shelter") && homeTile.HasShelter
                && !homeTile.Structures.Contains("improved_shelter"))
            {
                results.Add(new ScoredAction(ActionType.Build, 0.25f,
                    targetTile: (homeTile.X, homeTile.Y), targetRecipeId: "reinforced_shelter"));
            }

            // Granary: knows it, has shelter, no granary yet
            if (agent.Knowledge.Contains("granary") && homeTile.HasShelter && !homeTile.HasGranary)
            {
                int woodHeld = agent.Inventory.GetValueOrDefault(ResourceType.Wood, 0)
                    + homeTile.HomeMaterialStorage.GetValueOrDefault(ResourceType.Wood, 0);
                int stoneHeld = agent.Inventory.GetValueOrDefault(ResourceType.Stone, 0)
                    + homeTile.HomeMaterialStorage.GetValueOrDefault(ResourceType.Stone, 0);
                if (woodHeld >= SimConfig.GranaryWoodCost && stoneHeld >= SimConfig.GranaryStoneCost)
                {
                    results.Add(new ScoredAction(ActionType.Build, 0.25f,
                        targetTile: (homeTile.X, homeTile.Y), targetRecipeId: "granary"));
                }
            }
        }
    }

    // ── Directive #5 Fix 3b: ClearLand Scoring ─────────────────────────

    /// <summary>
    /// Scores ClearLand action: converts a forested/overgrown adjacent tile to cleared ground
    /// suitable for farming or building. Requires land_clearing knowledge.
    /// </summary>
    private static void ScoreClearLand(Agent agent, World world, Tile currentTile,
        int currentTick, List<ScoredAction> results)
    {
        if (!agent.Knowledge.Contains("land_clearing")) return;
        if (agent.Hunger <= 60f) return; // Don't clear land while hungry

        // Only clear land when settlement needs more farmable space
        bool needsFarmland = agent.Knowledge.Contains("farming");
        if (!needsFarmland) return;

        // Check adjacent tiles for clearable vegetation (forest tiles with wood)
        if (!agent.HomeTile.HasValue) return;

        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                int tx = agent.HomeTile.Value.X + dx, ty = agent.HomeTile.Value.Y + dy;
                if (!world.IsInBounds(tx, ty)) continue;

                var tile = world.GetTile(tx, ty);
                // Clearable: forest tile with wood, not already farmable, not already cleared
                if (tile.Biome == BiomeType.Forest
                    && tile.Resources.GetValueOrDefault(ResourceType.Wood, 0) > 3
                    && !tile.IsFarmable
                    && !tile.Structures.Contains("cleared"))
                {
                    results.Add(new ScoredAction(ActionType.ClearLand, 0.20f,
                        targetTile: (tx, ty)));
                    return; // One clearing option is enough
                }
            }
    }

    // ── Directive #5 Fix 3c: TendAnimals Scoring ─────────────────────

    /// <summary>
    /// Scores TendAnimals action: work at an animal pen to produce food.
    /// Requires animal_domestication knowledge and an animal_pen structure.
    /// </summary>
    private static void ScoreTendAnimals(Agent agent, World world, Tile currentTile,
        int currentTick, List<ScoredAction> results)
    {
        if (!agent.Knowledge.Contains("animal_domestication")) return;

        // Check for animal pen at home or current tile
        Tile? penTile = null;
        if (currentTile.Structures.Contains("animal_pen"))
            penTile = currentTile;
        else if (agent.HomeTile.HasValue)
        {
            var homeTile = world.GetTile(agent.HomeTile.Value.X, agent.HomeTile.Value.Y);
            if (homeTile.Structures.Contains("animal_pen"))
                penTile = homeTile;
            // Check adjacent to home
            for (int dx = -1; dx <= 1 && penTile == null; dx++)
                for (int dy = -1; dy <= 1 && penTile == null; dy++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int tx = agent.HomeTile.Value.X + dx, ty = agent.HomeTile.Value.Y + dy;
                    if (world.IsInBounds(tx, ty) && world.GetTile(tx, ty).Structures.Contains("animal_pen"))
                        penTile = world.GetTile(tx, ty);
                }
        }

        if (penTile == null) return;

        // Score: 0.25, slightly below farming but reliable food source
        float homeStorageFill = 1.0f;
        if (agent.HomeTile.HasValue)
        {
            var ht = world.GetTile(agent.HomeTile.Value.X, agent.HomeTile.Value.Y);
            if (ht.HasHomeStorage)
                homeStorageFill = (float)ht.HomeTotalFood / ht.HomeStorageCapacity;
        }
        float baseScore = 0.25f * (1.0f - homeStorageFill);
        if (baseScore < 0.01f) return;

        results.Add(new ScoredAction(ActionType.TendAnimals, baseScore,
            targetTile: (penTile.X, penTile.Y)));
    }

    // ── D24 Fix 3: New Adult Bootstrap Boost ──────────────────────────────

    /// <summary>D24 Fix 3: Multiply productive action scores for newly matured adults.
    /// Gather boost only applies to food-type resources to prevent inventory crowding.</summary>
    private static void ApplyNewAdultBoost(List<ScoredAction> results)
    {
        for (int i = 0; i < results.Count; i++)
        {
            var sa = results[i];
            float boost = sa.Action switch
            {
                ActionType.Gather when sa.TargetResource.HasValue
                    && ModeTransitionManager.IsFoodResource(sa.TargetResource.Value)
                    => SimConfig.NewAdultGatherBoost,
                ActionType.Gather => 1.0f, // Non-food gathers: no boost (prevents inventory crowding)
                ActionType.Explore => SimConfig.NewAdultExploreBoost,
                ActionType.Experiment => SimConfig.NewAdultExperimentBoost,
                ActionType.Build => SimConfig.NewAdultBuildBoost,
                _ => 1.0f
            };
            if (boost != 1.0f)
            {
                sa.Score *= boost;
                results[i] = sa;
            }
        }
    }

    // ── Trait Multiplier Application ────────────────────────────────────

    private static void ApplyTraitMultipliers(Agent agent, List<ScoredAction> results)
    {
        for (int i = 0; i < results.Count; i++)
        {
            var sa = results[i];
            float multiplier = 1f;

            foreach (var trait in agent.Traits)
            {
                multiplier *= GetTraitMultiplier(trait, sa.Action);
            }

            sa.Score *= multiplier;
            results[i] = sa;
        }
    }

    private static float GetTraitMultiplier(PersonalityTrait trait, ActionType action)
    {
        return trait switch
        {
            PersonalityTrait.Builder => action switch
            {
                ActionType.Build or ActionType.Craft => 1.4f,
                ActionType.Gather => 1.2f, // Boosts all gathering (food + resources)
                _ => 1.0f
            },
            PersonalityTrait.Explorer => action switch
            {
                ActionType.Explore => 1.5f,
                ActionType.ReturnHome => 0.8f,
                _ => 1.0f
            },
            // GDD v1.8: Social trait — Teach removed. 1.4x propagation speed handled in
            // communal knowledge system. Socialize/ShareFood/Reproduce retained.
            PersonalityTrait.Social => action switch
            {
                ActionType.Socialize => 1.4f,
                ActionType.ShareFood => 1.4f,
                ActionType.Reproduce => 1.2f,
                _ => 1.0f
            },
            PersonalityTrait.Cautious => action switch
            {
                ActionType.Gather => 1.3f, // Boosts all gathering (food included)
                ActionType.ReturnHome => 1.3f,
                ActionType.Explore => 0.8f,
                _ => 1.0f
            },
            PersonalityTrait.Curious => action switch
            {
                ActionType.Experiment => 1.5f,
                ActionType.Explore => 1.2f,
                _ => 1.0f
            },
            _ => 1.0f
        };
    }

    // ── Action Dampening ────────────────────────────────────────────────

    private static void ApplyDampening(Agent agent, List<ScoredAction> results)
    {
        if (!agent.LastChosenUtilityAction.HasValue) return;

        var lastAction = agent.LastChosenUtilityAction.Value;
        int consecutive = agent.ConsecutiveSameActionTicks;

        float dampening = MathF.Max(
            SimConfig.ActionDampeningFloor,
            MathF.Pow(SimConfig.ActionDampeningFactor, consecutive));

        for (int i = 0; i < results.Count; i++)
        {
            var sa = results[i];
            if (sa.Action == lastAction)
            {
                sa.Score *= dampening;
                results[i] = sa;
            }
        }
    }

    // ── D19: Restlessness Motivation Multiplier ──────────────────────────

    /// <summary>D19: Applies restlessness-based scoring multiplier. Higher restlessness = stronger
    /// motivation toward productive actions. Formula: multiplier = 1.0 + (restlessness/100) * maxBoost</summary>
    private static void ApplyRestlessnessMultiplier(Agent agent, List<ScoredAction> results)
    {
        if (agent.Restlessness <= 0f) return; // No boost at 0 restlessness

        float r = agent.Restlessness / 100f;

        for (int i = 0; i < results.Count; i++)
        {
            var sa = results[i];
            float maxBoost = sa.Action switch
            {
                ActionType.Experiment => SimConfig.RestlessnessExperimentBoost,
                ActionType.Build => SimConfig.RestlessnessBuildBoost,
                ActionType.TendFarm => SimConfig.RestlessnessTendFarmBoost,
                ActionType.TendAnimals => SimConfig.RestlessnessTendFarmBoost, // Same as TendFarm
                ActionType.Gather => SimConfig.RestlessnessGatherBoost,
                ActionType.ClearLand => SimConfig.RestlessnessGatherBoost, // Same as Gather
                _ => 0f
            };

            if (maxBoost > 0f)
            {
                float multiplier = 1.0f + r * maxBoost;
                sa.Score *= multiplier;
                results[i] = sa;
            }
        }
    }

    // ── Fix 3A: Post-dampening content-agent Experiment floor ────────────

    /// <summary>
    /// Fix 3A: After dampening and trait multipliers, re-enforce the 0.30 floor
    /// on Experiment for content agents with available recipes.
    /// Without this, dampening (0.73 floor) reduces the pre-dampening 0.30 to ~0.219,
    /// allowing other actions to permanently outcompete Experiment and causing stagnation.
    /// Conditions: hunger > 60, health > 60, has shelter (not exposed), at home (within 2 tiles),
    /// AND undiscovered recipes the agent could attempt with available materials.
    /// </summary>
    private static void ApplyExperimentContentFloor(Agent agent, World world, int currentTick,
        List<ScoredAction> results,
        SettlementKnowledge? knowledgeSystem = null, List<Settlement>? settlements = null)
    {
        // Content check: well-fed, healthy, sheltered, near home
        if (agent.Hunger <= 60f || agent.Health <= 60) return;
        if (agent.IsExposed) return;
        if (!agent.HomeTile.HasValue) return;

        bool isAtHome = Math.Abs(agent.X - agent.HomeTile.Value.X) <= 2
                      && Math.Abs(agent.Y - agent.HomeTile.Value.Y) <= 2;
        if (!isAtHome) return;

        // Check if Experiment is already in results (ScoreExperiment found available recipes)
        bool hasExperiment = false;
        for (int i = 0; i < results.Count; i++)
        {
            if (results[i].Action == ActionType.Experiment)
            {
                hasExperiment = true;
                if (results[i].Score < 0.30f)
                {
                    var sa = results[i];
                    sa.Score = 0.30f;
                    results[i] = sa;
                }
                break;
            }
        }

        // If Experiment wasn't scored (e.g., survival gate blocked it but floor conditions are met),
        // check if recipes actually exist and inject one
        if (!hasExperiment)
        {
            var available = RecipeRegistry.GetAvailableRecipes(agent, knowledgeSystem, settlements);
            if (available.Count > 0)
            {
                // Pick a random recipe using a deterministic seed
                var rng = new Random(currentTick + agent.Id);
                var recipe = available[rng.Next(available.Count)];
                results.Add(new ScoredAction(ActionType.Experiment, 0.30f,
                    targetRecipeId: recipe.Id));
            }
        }
    }

    // ── Fix 3B: Suppress Idle/Rest when eligible recipes exist ───────────

    /// <summary>
    /// Fix 3B: When the agent has undiscovered recipes they could attempt AND materials
    /// for at least one, Rest should not beat Experiment. An agent sitting idle (Rest)
    /// while there are things to discover is broken.
    /// This zeroes Rest score when Experiment is available and the agent is content,
    /// so that Experiment always wins over resting/idling.
    /// Note: Night rest (0.80 floor) and urgent health rest are exempt.
    /// </summary>
    private static void SuppressIdleWhenRecipesAvailable(Agent agent, World world, int currentTick,
        List<ScoredAction> results,
        SettlementKnowledge? knowledgeSystem = null, List<Settlement>? settlements = null)
    {
        // Only suppress when content (same conditions as the floor)
        if (agent.Hunger <= 60f || agent.Health <= 60) return;
        if (agent.IsExposed) return;

        // Don't suppress Rest at night — sleep is important
        if (Agent.IsNightTime(currentTick)) return;

        // Don't suppress Rest when health is low — healing is important
        if (agent.Health < 80) return;

        // Check if Experiment is in the results (meaning recipes are available)
        bool hasExperiment = results.Any(r => r.Action == ActionType.Experiment && r.Score > 0f);
        if (!hasExperiment) return;

        // Zero out Rest scores so Experiment wins
        for (int i = 0; i < results.Count; i++)
        {
            if (results[i].Action == ActionType.Rest)
            {
                var sa = results[i];
                sa.Score = 0f;
                results[i] = sa;
            }
        }
    }

    // ── Fix 4A: Daytime Idle Guard ──────────────────────────────────────
    // Safety net: during daytime, if ANY productive action scored > 0,
    // Rest must not win. Zeros Rest score so highest productive action wins.
    // Exempt: nighttime (sleep is valid), low health (< 30, healing is critical).
    private static void ApplyDaytimeIdleGuard(Agent agent, int currentTick, List<ScoredAction> results)
    {
        // Night rest is always valid
        if (Agent.IsNightTime(currentTick)) return;

        // Low health — healing rest is valid even during daytime
        if (agent.Health < 30) return;

        // Check if any non-Rest action scored > 0
        bool hasProductiveAction = false;
        for (int i = 0; i < results.Count; i++)
        {
            if (results[i].Action != ActionType.Rest && results[i].Score > 0f)
            {
                hasProductiveAction = true;
                break;
            }
        }

        if (!hasProductiveAction) return;

        // Zero out Rest so productive actions win
        for (int i = 0; i < results.Count; i++)
        {
            if (results[i].Action == ActionType.Rest)
            {
                var sa = results[i];
                sa.Score = 0f;
                results[i] = sa;
            }
        }
    }

    // ── GDD v1.7.2: ShareFood Utility ───────────────────────────────────

    private static void ScoreShareFood(Agent agent, World world, List<ScoredAction> results)
    {
        int food = agent.FoodInInventory();
        if (food <= 0) return;

        // Fix 3: Broadened from ≤30 to ≤60 so parents feed moderately hungry children, not just starving
        var nearby = world.GetAdjacentAgents(agent.X, agent.Y);
        var samePos = world.GetAgentsAt(agent.X, agent.Y).Where(a => a.Id != agent.Id).ToList();

        Agent? neediest = null;
        float lowestHunger = 60f;

        foreach (var other in nearby.Concat(samePos))
        {
            if (!other.IsAlive || other.Hunger >= lowestHunger) continue;
            neediest = other;
            lowestHunger = other.Hunger;
        }

        if (neediest == null) return;

        // GDD v1.7.2: Score = 0.7 × (1 + bondBonus)
        float bondBonus = 0f;
        if (agent.SocialBonds.TryGetValue(neediest.Id, out int interactionCount))
        {
            if (interactionCount >= SimConfig.SocialBondFamilyStart)
                bondBonus = 0.3f; // Family
            else if (interactionCount >= SimConfig.SocialBondFriendThreshold)
                bondBonus = 0.15f; // Friend
        }

        float score = SimConfig.ShareFoodBaseUtility * (1f + bondBonus);
        results.Add(new ScoredAction(ActionType.ShareFood, score,
            targetAgentId: neediest.Id));
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// GDD v1.7.2: Calculates food saturation in the agent's perception range.
    /// foodSaturation = clamp(nearbyFood / (nearbyPop × 10), 0, 1)
    /// </summary>
    private static float CalculateFoodSaturation(Agent agent, World world)
    {
        int radius = SimConfig.PerceptionRadius;
        int nearbyFood = 0;
        int nearbyPop = 0;

        for (int dx = -radius; dx <= radius; dx++)
        {
            for (int dy = -radius; dy <= radius; dy++)
            {
                int tx = agent.X + dx, ty = agent.Y + dy;
                if (!world.IsInBounds(tx, ty)) continue;

                var tile = world.GetTile(tx, ty);

                // Count edible food on tiles
                foreach (var kvp in tile.Resources)
                {
                    if (IsEdible(kvp.Key))
                        nearbyFood += kvp.Value;
                }

                // Count granary food
                if (tile.HasGranary)
                    nearbyFood += tile.GranaryTotalFood;

                // Count agents on this tile
                nearbyPop += world.GetAgentsAt(tx, ty).Count;
            }
        }

        if (nearbyPop <= 0) nearbyPop = 1;
        return Math.Clamp(nearbyFood / (nearbyPop * 10f), 0f, 1f);
    }

    private static bool IsEdible(ResourceType type)
    {
        return type == ResourceType.Berries || type == ResourceType.Grain
            || type == ResourceType.Meat || type == ResourceType.Fish
            || type == ResourceType.PreservedFood;
    }

    // ── D25b: Hunt & Harvest scoring ──────────────────────────────────

    /// <summary>
    /// Scores hunting live animals. Adults only. Requires remembered animal sightings.
    /// Tool gating: bare-handed can only attempt Rabbit. Better tools = higher success.
    /// </summary>
    private static void ScoreHunt(Agent agent, World world, Tile currentTile,
        int currentTick, List<ScoredAction> results)
    {
        // Only adults in Forage, Home, or Caretaker mode can hunt
        if (agent.Stage != DevelopmentStage.Adult) return;
        if (agent.CurrentMode != BehaviorMode.Forage && agent.CurrentMode != BehaviorMode.Home
            && agent.CurrentMode != BehaviorMode.Caretaker) return;

        // Get remembered huntable animals (excludes Boar/Wolf)
        var remembered = agent.GetRememberedHuntableAnimals(currentTick);
        if (remembered.Count == 0) return;

        float hungerNeed = 1f - (agent.Hunger / 100f);
        // Hunt proactively — especially important for feeding dependents
        if (hungerNeed < 0.02f) return;

        foreach (var mem in remembered)
        {
            if (!mem.AnimalSpecies.HasValue) continue;
            var species = mem.AnimalSpecies.Value;

            // Home mode: only hunt nearby animals (within 5 tiles of home)
            if (agent.CurrentMode == BehaviorMode.Home && agent.HomeTile.HasValue)
            {
                int distFromHome = Math.Max(Math.Abs(mem.X - agent.HomeTile.Value.X),
                    Math.Abs(mem.Y - agent.HomeTile.Value.Y));
                if (distFromHome > 5) continue;
            }

            // Get meat yield for scoring
            var config = Animal.SpeciesConfig[species];
            float meatYieldFactor = config.MeatYield / 3f; // Normalize around deer (3 meat)

            int dist = Math.Max(Math.Abs(mem.X - agent.X), Math.Abs(mem.Y - agent.Y));
            float distFactor = Math.Max(0.1f, 1f - dist * 0.05f);

            // Tool modifier (better tools = higher score)
            float toolMod = 1.0f;
            if (agent.Knowledge.Contains("hafted_tools")) toolMod = 1.5f;
            else if (agent.Knowledge.Contains("refined_tools")) toolMod = 1.3f;
            else if (agent.Knowledge.Contains("crude_axe")) toolMod = 1.1f;
            else if (agent.Knowledge.Contains("stone_knife")) toolMod = 1.0f;
            else toolMod = 0.7f; // Bare-handed (still viable)

            // Health check: low health reduces hunt score
            float healthFactor = agent.Health >= 50 ? 1.0f : (agent.Health / 50f) * 0.5f;

            float score = hungerNeed * meatYieldFactor * distFactor * toolMod * healthFactor * SimConfig.HuntScoreMultiplier;

            // D25c: Danger penalty for dangerous prey
            if (species == AnimalSpecies.Boar)
            {
                score *= SimConfig.BoarHuntDangerPenalty;
                if (agent.Health < SimConfig.BoarHuntMinHealth) continue; // Don't hunt boar while hurt
            }
            else if (species == AnimalSpecies.Wolf)
            {
                score *= SimConfig.WolfHuntDangerPenalty;
                if (agent.Health < SimConfig.WolfHuntMinHealth) continue; // Don't hunt wolf while hurt
                // Pack size penalty
                int nearbyWolves = agent.AnimalMemory.Count(am =>
                    am.AnimalSpecies == AnimalSpecies.Wolf
                    && Math.Max(Math.Abs(am.X - mem.X), Math.Abs(am.Y - mem.Y)) <= 5
                    && currentTick - am.TickObserved < SimConfig.MemoryDecayTicks);
                if (nearbyWolves >= 3) score *= SimConfig.WolfPackSizeScorePenalty;
            }

            if (score > 0.01f)
            {
                results.Add(new ScoredAction
                {
                    Action = ActionType.Hunt,
                    Score = score,
                    TargetTile = (mem.X, mem.Y),
                    TargetResource = ResourceType.Meat // Used to identify this as a food-acquiring action
                });
            }
        }
    }

    /// <summary>
    /// Scores harvesting carcasses (butchering dead animals for meat).
    /// Carcasses are free food — scores higher than hunting when available.
    /// </summary>
    private static void ScoreHarvest(Agent agent, World world, Tile currentTile,
        int currentTick, List<ScoredAction> results)
    {
        if (agent.Stage != DevelopmentStage.Adult) return;
        if (agent.CurrentMode != BehaviorMode.Forage && agent.CurrentMode != BehaviorMode.Home) return;

        var carcasses = agent.GetRememberedCarcasses(currentTick);
        if (carcasses.Count == 0) return;

        float hungerNeed = 1f - (agent.Hunger / 100f);

        foreach (var mem in carcasses)
        {
            int dist = Math.Max(Math.Abs(mem.X - agent.X), Math.Abs(mem.Y - agent.Y));
            float distFactor = Math.Max(0.1f, 1f - dist * 0.05f);
            float yieldFactor = mem.Quantity / 3f; // Normalize around 3 meat

            // Carcasses are free food — score higher than hunting
            float score = (0.3f + 0.7f * hungerNeed) * distFactor * yieldFactor;

            if (score > 0.01f)
            {
                results.Add(new ScoredAction
                {
                    Action = ActionType.Harvest,
                    Score = score,
                    TargetTile = (mem.X, mem.Y),
                    TargetResource = ResourceType.Meat
                });
            }
        }
    }

    // ── D25d: Tame Scoring (Home mode only) ─────────────────────────────

    /// <summary>D25d Fix 1: Score taming wild animals. Home mode only.
    /// Requires animal_domestication knowledge and food in inventory.</summary>
    private static void ScoreTame(Agent agent, World world, Tile currentTile,
        int currentTick, List<ScoredAction> results)
    {
        if (agent.Stage != DevelopmentStage.Adult) return;
        if (agent.CurrentMode != BehaviorMode.Home) return;
        if (!agent.Knowledge.Contains("animal_domestication")) return;

        int food = agent.FoodInInventory();
        if (food <= 0) return;

        // Count already domesticated animals
        int domesticatedCount = 0;
        foreach (var a in world.Animals)
            if (a.IsDomesticated) domesticatedCount++;

        float baseScore = 0.20f * (1f - domesticatedCount / 6f);
        if (baseScore <= 0f) return;

        // Food surplus modifier
        if (food > 10) baseScore += 0.1f;

        // Breeding pair urgency: if any pen has exactly 1 animal, boost score
        bool needsPartner = false;
        foreach (var pen in world.Pens)
        {
            if (pen.IsActive && pen.AnimalCount == 1)
            { needsPartner = true; break; }
        }
        if (needsPartner)
            baseScore = Math.Max(baseScore, SimConfig.TameBreedingPairUrgencyScore);

        // Find the nearest tameable animal from AnimalMemory
        // Priority: Rabbit > Sheep > Cow > Deer > Boar
        // Exclude wolves (wolf pup taming is separate) and already domesticated
        var remembered = agent.AnimalMemory.Where(m =>
            m.Type == MemoryType.AnimalSighting
            && m.AnimalSpecies.HasValue
            && currentTick - m.TickObserved <= SimConfig.MemoryDecayTicks
            && m.AnimalSpecies.Value != AnimalSpecies.Wolf
        ).ToList();

        if (remembered.Count == 0)
        {
            // No tameable animals in memory — apply penalty but don't emit
            return;
        }

        // Filter out already-domesticated animals by checking World.Animals
        var candidates = new List<(MemoryEntry mem, int dist, int priority)>();
        foreach (var mem in remembered)
        {
            if (!mem.AnimalId.HasValue) continue;
            var animal = world.Animals.FirstOrDefault(a => a.Id == mem.AnimalId.Value);
            if (animal == null || !animal.IsAlive || animal.IsDomesticated) continue;

            // Boar requires trapping knowledge AND must be near a trap
            if (mem.AnimalSpecies!.Value == AnimalSpecies.Boar)
            {
                if (!agent.Knowledge.Contains("trapping")) continue;
                bool nearTrap = false;
                foreach (var trap in world.Traps)
                {
                    if (trap.IsActive && Math.Max(Math.Abs(trap.X - animal.X), Math.Abs(trap.Y - animal.Y)) <= 2)
                    { nearTrap = true; break; }
                }
                if (!nearTrap) continue;
            }

            int dist = Math.Max(Math.Abs(mem.X - agent.X), Math.Abs(mem.Y - agent.Y));
            int priority = mem.AnimalSpecies!.Value switch
            {
                AnimalSpecies.Rabbit => 0,
                AnimalSpecies.Sheep => 1,
                AnimalSpecies.Cow => 2,
                AnimalSpecies.Deer => 3,
                AnimalSpecies.Boar => 4,
                _ => 99
            };
            candidates.Add((mem, dist, priority));
        }

        if (candidates.Count == 0) return;

        // Sort by priority first, then distance
        candidates.Sort((a, b) =>
        {
            int cmp = a.priority.CompareTo(b.priority);
            return cmp != 0 ? cmp : a.dist.CompareTo(b.dist);
        });

        var best = candidates[0];
        float finalScore = baseScore;

        // Home mode: only tame nearby animals (within HomeGatherRadius of home)
        if (agent.HomeTile.HasValue)
        {
            int distFromHome = Math.Max(Math.Abs(best.mem.X - agent.HomeTile.Value.X),
                Math.Abs(best.mem.Y - agent.HomeTile.Value.Y));
            if (distFromHome > SimConfig.HomeGatherRadius) return;
        }

        if (finalScore > 0.01f)
        {
            results.Add(new ScoredAction
            {
                Action = ActionType.Tame,
                Score = finalScore,
                TargetTile = (best.mem.X, best.mem.Y),
                TargetAgentId = best.mem.AnimalId
            });
        }
    }

    /// <summary>
    /// D25d Fix 6: Score taming a wolf pup. Requires both "bow" and "animal_domestication".
    /// Wolf pups are rare, so this scores lower than regular taming.
    /// </summary>
    private static void ScoreTameWolfPup(Agent agent, World world, int currentTick, List<ScoredAction> results)
    {
        if (agent.Stage != DevelopmentStage.Adult) return;
        if (!agent.Knowledge.Contains("animal_domestication")) return;
        if (!agent.Knowledge.Contains("bow")) return;

        int food = agent.FoodInInventory();
        if (food <= 0) return;

        // Search AnimalMemory and World.Animals for wolf pups
        Animal? bestPup = null;
        int bestDist = int.MaxValue;

        // Check via AnimalMemory first
        foreach (var mem in agent.AnimalMemory)
        {
            if (mem.Type != MemoryType.AnimalSighting) continue;
            if (!mem.AnimalSpecies.HasValue || mem.AnimalSpecies.Value != AnimalSpecies.Wolf) continue;
            if (currentTick - mem.TickObserved > SimConfig.MemoryDecayTicks) continue;
            if (!mem.AnimalId.HasValue) continue;

            var animal = world.Animals.FirstOrDefault(a => a.Id == mem.AnimalId.Value);
            if (animal == null || !animal.IsAlive || animal.IsDomesticated) continue;
            if (!animal.IsPup) continue;

            int dist = Math.Max(Math.Abs(animal.X - agent.X), Math.Abs(animal.Y - agent.Y));
            if (dist < bestDist)
            {
                bestDist = dist;
                bestPup = animal;
            }
        }

        // Also check directly visible animals (within perception range)
        foreach (var animal in world.Animals)
        {
            if (!animal.IsAlive || animal.IsDomesticated) continue;
            if (animal.Species != AnimalSpecies.Wolf || !animal.IsPup) continue;
            int dist = Math.Max(Math.Abs(animal.X - agent.X), Math.Abs(animal.Y - agent.Y));
            if (dist <= SimConfig.PerceptionRadius && dist < bestDist)
            {
                bestDist = dist;
                bestPup = animal;
            }
        }

        if (bestPup == null) return;

        float score = 0.10f; // Low base — wolf pup taming is rare/opportunistic
        if (food > 10) score += 0.05f;

        results.Add(new ScoredAction
        {
            Action = ActionType.Tame,
            Score = score,
            TargetTile = (bestPup.X, bestPup.Y),
            TargetAgentId = bestPup.Id
        });
    }

    /// <summary>
    /// D25d Fix 3a: Score feeding grain to a pen that has animals and needs food.
    /// Prerequisites: agent knows animal_domestication, has Grain in inventory or home storage,
    /// world has pens with animals.
    /// </summary>
    private static void ScoreFeedPen(Agent agent, World world, Tile currentTile,
        int currentTick, List<ScoredAction> results)
    {
        if (agent.Stage != DevelopmentStage.Adult) return;
        if (!agent.Knowledge.Contains("animal_domestication")) return;

        // Check if agent has grain in inventory or home storage
        // NOTE: Scorer checks grain only to preserve emission determinism.
        // Dispatch (AgentAI.TryDispatchFeedPen) accepts any food type.
        int grainInInventory = agent.Inventory.TryGetValue(ResourceType.Grain, out int gi) ? gi : 0;
        int grainInHome = 0;
        if (agent.HomeTile.HasValue)
        {
            var homeTile = world.GetTile(agent.HomeTile.Value.X, agent.HomeTile.Value.Y);
            if (homeTile.HasHomeStorage && homeTile.HomeFoodStorage.TryGetValue(ResourceType.Grain, out int hg))
                grainInHome = hg;
        }
        if (grainInInventory + grainInHome <= 0) return;

        // Find nearest pen with animals that needs food
        Pen? bestPen = null;
        int bestDist = int.MaxValue;
        foreach (var pen in world.Pens)
        {
            if (!pen.IsActive || pen.AnimalCount == 0) continue;
            if (pen.FoodStore >= pen.MaxFoodStore) continue; // Already full
            int dist = Math.Max(Math.Abs(pen.TileX - agent.X), Math.Abs(pen.TileY - agent.Y));
            if (dist < bestDist) { bestDist = dist; bestPen = pen; }
        }
        if (bestPen == null) return;

        float foodRatio = (float)bestPen.FoodStore / bestPen.MaxFoodStore;
        // Tiered: critical (<25%) uses higher base score for urgency
        float tierBase = foodRatio < 0.25f ? SimConfig.FeedPenCriticalScore : SimConfig.FeedPenBaseScore;
        float baseScore = tierBase * (1f - foodRatio);
        if (baseScore <= 0.01f) return;

        results.Add(new ScoredAction
        {
            Action = ActionType.FeedPen,
            Score = baseScore,
            TargetTile = (bestPen.TileX, bestPen.TileY)
        });
    }

    /// <summary>
    /// D25d: Score penning a following domesticated animal into a nearby pen with capacity.
    /// Prerequisites: agent has a following domesticated animal, pen with capacity exists nearby.
    /// </summary>
    private static void ScorePenAnimal(Agent agent, World world, Tile currentTile,
        int currentTick, List<ScoredAction> results)
    {
        if (agent.Stage != DevelopmentStage.Adult) return;
        if (!agent.Knowledge.Contains("animal_domestication")) return;

        // Check if agent has a following domesticated animal (not penned)
        Animal? follower = null;
        foreach (var animal in world.Animals)
        {
            if (animal.IsAlive && animal.IsDomesticated && animal.OwnerAgentId == agent.Id && !animal.PenId.HasValue)
            {
                follower = animal;
                break;
            }
        }
        if (follower == null) return;

        // Find nearest pen with capacity for this species
        Pen? bestPen = null;
        int bestDist = int.MaxValue;
        foreach (var pen in world.Pens)
        {
            if (!pen.IsActive || pen.IsFull) continue;
            int dist = Math.Max(Math.Abs(pen.TileX - agent.X), Math.Abs(pen.TileY - agent.Y));
            if (dist < bestDist) { bestDist = dist; bestPen = pen; }
        }
        if (bestPen == null) return;

        results.Add(new ScoredAction
        {
            Action = ActionType.PenAnimal,
            Score = 0.30f,
            TargetTile = (bestPen.TileX, bestPen.TileY),
            TargetAgentId = follower.Id
        });
    }

    /// <summary>
    /// D25d Fix 5a: Score slaughtering a penned animal for meat/hide/bone.
    /// Prerequisites: agent knows animal_domestication, pen has 3+ same-species animals
    /// (preserve breeding pair of 2).
    /// </summary>
    private static void ScoreSlaughter(Agent agent, World world, Tile currentTile,
        int currentTick, List<ScoredAction> results)
    {
        if (agent.Stage != DevelopmentStage.Adult) return;
        if (!agent.Knowledge.Contains("animal_domestication")) return;

        float hungerFactor = 1f - (agent.Hunger / 100f);

        // Find best pen + animal to slaughter
        Pen? bestPen = null;
        Animal? bestAnimal = null;
        float bestScore = 0f;

        foreach (var pen in world.Pens)
        {
            if (!pen.IsActive || pen.AnimalCount == 0) continue;

            // Group penned animals by species
            var speciesCounts = new Dictionary<AnimalSpecies, List<Animal>>();
            foreach (var animalId in pen.AnimalIds)
            {
                var animal = world.Animals.FirstOrDefault(a => a.Id == animalId && a.IsAlive);
                if (animal == null) continue;
                if (!speciesCounts.ContainsKey(animal.Species))
                    speciesCounts[animal.Species] = new List<Animal>();
                speciesCounts[animal.Species].Add(animal);
            }

            foreach (var kvp in speciesCounts)
            {
                if (kvp.Value.Count < SimConfig.SlaughterBreedingPairMin + 1) continue; // Need 3+ (preserve 2)
                float score = SimConfig.SlaughterBaseScore * hungerFactor * ((float)pen.AnimalCount / pen.Capacity);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestPen = pen;
                    bestAnimal = kvp.Value[kvp.Value.Count - 1]; // Slaughter the last one
                }
            }
        }

        if (bestPen == null || bestAnimal == null || bestScore <= 0.01f) return;

        results.Add(new ScoredAction
        {
            Action = ActionType.Slaughter,
            Score = bestScore,
            TargetTile = (bestPen.TileX, bestPen.TileY),
            TargetAgentId = bestAnimal.Id
        });
    }
}
