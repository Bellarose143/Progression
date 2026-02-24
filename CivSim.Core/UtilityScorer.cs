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
        { ResourceType.Berries, ResourceType.Grain, ResourceType.Animals, ResourceType.Fish };

    /// <summary>
    /// Scores all non-survival actions for the given agent. Returns sorted list (highest first).
    /// </summary>
    public static List<ScoredAction> ScoreAll(Agent agent, World world, int currentTick, Random random,
        List<Agent>? allAgents = null, SettlementKnowledge? knowledgeSystem = null, List<Settlement>? settlements = null)
    {
        var results = new List<ScoredAction>();
        var currentTile = world.GetTile(agent.X, agent.Y);

        // ── Score each action category ──────────────────────────────
        ScoreGatherFood(agent, world, currentTile, currentTick, results);
        ScoreGatherResource(agent, world, currentTile, currentTick, results, knowledgeSystem, settlements);
        ScoreTendFarm(agent, world, currentTile, currentTick, results);
        // GDD v1.8: Teach removed — knowledge propagation is communal within settlements
        ScoreBuild(agent, world, currentTile, results);
        ScoreSocialize(agent, world, currentTick, results);
        ScoreReproduce(agent, world, currentTile, results, allAgents);
        ScoreExperiment(agent, world, random, results, knowledgeSystem, settlements);
        ScoreReturnHome(agent, results);
        ScoreDepositGranary(agent, world, currentTile, currentTick, results);
        ScoreWithdrawGranary(agent, world, currentTile, currentTick, results);
        ScorePreserve(agent, results);
        ScoreDepositHome(agent, world, currentTile, results);
        ScoreShareFood(agent, world, results);
        ScoreExplore(agent, world, currentTile, currentTick, results);
        ScoreIdleRest(agent, currentTile, currentTick, results);

        // ── Apply trait multipliers ─────────────────────────────────
        ApplyTraitMultipliers(agent, results);

        // ── Apply action dampening ──────────────────────────────────
        ApplyDampening(agent, results);

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
        }

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
        List<Agent>? allAgents = null, SettlementKnowledge? knowledgeSystem = null, List<Settlement>? settlements = null)
    {
        var results = new List<ScoredAction>();
        var currentTile = world.GetTile(agent.X, agent.Y);

        // ── Local food gathering (at current tile only — trips are Forage mode) ──
        ScoreGatherFood(agent, world, currentTile, currentTick, results, localOnly: true);

        // ── Score Home-appropriate actions ────────────────────────────
        ScoreTendFarm(agent, world, currentTile, currentTick, results);
        ScoreBuild(agent, world, currentTile, results);
        ScoreSocialize(agent, world, currentTick, results);
        ScoreReproduce(agent, world, currentTile, results, allAgents);
        ScoreExperiment(agent, world, random, results, knowledgeSystem, settlements);
        ScoreDepositGranary(agent, world, currentTile, currentTick, results);
        ScoreWithdrawGranary(agent, world, currentTile, currentTick, results);
        ScorePreserve(agent, results);
        ScoreDepositHome(agent, world, currentTile, results);
        ScoreShareFood(agent, world, results);
        ScoreIdleRest(agent, currentTile, currentTick, results);

        // When exposed, also score GatherResource (for building materials)
        if (agent.IsExposed)
            ScoreGatherResource(agent, world, currentTile, currentTick, results, knowledgeSystem, settlements);

        // ── Apply trait multipliers ───────────────────────────────────
        ApplyTraitMultipliers(agent, results);

        // ── Apply action dampening ────────────────────────────────────
        ApplyDampening(agent, results);

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
        }

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
        int currentTick, List<ScoredAction> results, bool localOnly = false)
    {
        // Base: 0.6 × (1 − hunger/100) — higher score when MORE hungry (lower hunger value)
        float hungerNeed = 1f - (agent.Hunger / 100f);
        float baseScore = 0.6f * hungerNeed;

        // Food buffer bonus: if carrying little food, always maintain a small reserve regardless of hunger
        int foodInInv = agent.FoodInInventory();
        if (foodInInv < 2) baseScore = Math.Max(baseScore, 0.25f);

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

        // localOnly: Home mode only gathers nearby (within 2 tiles). Remote food is Forage mode's job.
        if (localOnly)
        {
            // Check adjacent tiles (1-2 distance) — still "local" gathering, not a foraging trip
            var rememberedNearby = agent.GetRememberedFood(currentTick);
            foreach (var mem in rememberedNearby)
            {
                int dist = Math.Max(Math.Abs(mem.X - agent.X), Math.Abs(mem.Y - agent.Y));
                if (dist < 1 || dist > 2) continue; // Skip same tile (handled above) and far tiles
                if (!agent.HasInventorySpace()) break;

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
        SettlementKnowledge? knowledgeSystem = null, List<Settlement>? settlements = null)
    {
        if (!agent.HasInventorySpace()) return;

        int capacity = SimConfig.InventoryCapacity;
        if (agent.Knowledge.Contains("improved_shelter")) capacity += 10;
        float invSpace = (float)(capacity - agent.InventoryCount()) / capacity;

        float baseScore = 0.2f * invSpace;

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

        // GDD v1.7.2: Farming saturation — reduce farming priority when food is abundant
        // foodSaturation = clamp(nearbyFood / (nearbyPop × 10), 0, 1)
        float foodSaturation = CalculateFoodSaturation(agent, world);
        float baseScore = 0.5f * (1f - foodSaturation);
        if (baseScore < 0.01f) return; // Don't bother farming if food is abundant

        // Check current tile
        if (currentTile.IsFarmable &&
            currentTick - currentTile.LastTendedTick >= SimConfig.FarmTendedGracePeriod)
        {
            results.Add(new ScoredAction(ActionType.TendFarm, baseScore,
                targetTile: (agent.X, agent.Y)));
            return;
        }

        // Check memory for farms
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
    }

    // GDD v1.8: ScoreTeach REMOVED — knowledge propagation is communal within settlements.
    // No agent-to-agent teaching action exists. See communal knowledge propagation system.

    private static void ScoreBuild(Agent agent, World world, Tile currentTile, List<ScoredAction> results)
    {
        int woodHeld = agent.Inventory.GetValueOrDefault(ResourceType.Wood, 0);
        int stoneHeld = agent.Inventory.GetValueOrDefault(ResourceType.Stone, 0);

        // Shelter building — target the HomeTile (agents build at home, not wherever they stand)
        if (agent.Knowledge.Contains("lean_to"))
        {
            // Determine build site: HomeTile if valid, otherwise current tile
            int buildX = agent.HomeTile.HasValue ? agent.HomeTile.Value.X : currentTile.X;
            int buildY = agent.HomeTile.HasValue ? agent.HomeTile.Value.Y : currentTile.Y;
            var buildTile = world.GetTile(buildX, buildY);

            if (buildTile.HasShelter) goto skipShelter; // Already has shelter at build site

            // Don't build another shelter if agent already has a sheltered home
            bool alreadySheltered = agent.HomeTile.HasValue
                && world.IsInBounds(agent.HomeTile.Value.X, agent.HomeTile.Value.Y)
                && world.GetTile(agent.HomeTile.Value.X, agent.HomeTile.Value.Y).HasShelter;

            if (!alreadySheltered)
            {
                // Check proximity — needsBuilding = 1.0 if no shelter within radius, 0.3 if there is
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

                // Exposure boost: exposed agents with materials should build shelter ASAP
                float exposureMultiplier = (agent.IsExposed && !shelterNearby) ? 3.0f : 1.0f;

                if (woodHeld >= SimConfig.ShelterWoodCost && stoneHeld >= SimConfig.ShelterStoneCost)
                {
                    float score = 0.5f * needsBuilding * exposureMultiplier;
                    results.Add(new ScoredAction(ActionType.Build, score,
                        targetTile: (buildX, buildY), targetRecipeId: "lean_to"));
                }
            }
        }
        skipShelter:

        // Granary building (requires shelter on tile)
        if (agent.Knowledge.Contains("granary") && currentTile.HasShelter && !currentTile.HasGranary)
        {
            if (woodHeld >= SimConfig.GranaryWoodCost && stoneHeld >= SimConfig.GranaryStoneCost)
            {
                results.Add(new ScoredAction(ActionType.Build, 0.4f,
                    targetTile: (currentTile.X, currentTile.Y), targetRecipeId: "granary"));
            }
        }
    }

    private static void ScoreSocialize(Agent agent, World world, int currentTick, List<ScoredAction> results)
    {
        // Look for visible agents not already adjacent
        var rememberedAgents = agent.GetRememberedAgents(currentTick);
        if (rememberedAgents.Count == 0) return;

        // socialSat: rough approximation — more bonds = more satisfied
        float socialSat = Math.Min(1f, agent.SocialBonds.Count / 5f);
        float baseScore = 0.3f * (1f - socialSat);

        // Diminishing returns: if agent just socialized, reduce score
        // Prevents Socialize from dominating every single tick
        if (agent.LastChosenUtilityAction == ActionType.Socialize)
            baseScore *= 0.5f;

        if (baseScore <= 0.01f) return;

        // Find best target (prefer bonded agents, must be reachable without home-pull conflict)
        MemoryEntry? bestTarget = null;
        float bestTargetScore = -1f;

        foreach (var mem in rememberedAgents)
        {
            if (!mem.AgentId.HasValue) continue;
            int dist = Math.Max(Math.Abs(mem.X - agent.X), Math.Abs(mem.Y - agent.Y));
            if (dist <= 1) continue; // Already adjacent, no need to socialize
            if (dist > 4) continue; // Too far — agent will oscillate with ReturnHome before arriving

            float targetScore = baseScore;

            // Bonus for bonded agents
            if (agent.SocialBonds.TryGetValue(mem.AgentId.Value, out int bondStrength)
                && bondStrength >= SimConfig.SocialBondFriendThreshold)
            {
                targetScore += SimConfig.SocialBondUtilityBonus;
            }

            float distPenalty = 1f / (1f + dist * 0.1f);
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

    private static void ScoreExperiment(Agent agent, World world, Random random, List<ScoredAction> results,
        SettlementKnowledge? knowledgeSystem = null, List<Settlement>? settlements = null)
    {
        // Survival gate: no experimenting while exposed and able to build shelter
        if (agent.IsExposed && agent.Knowledge.Contains("lean_to"))
            return;

        // Survival gate: no experimenting while moderately hungry
        if (agent.Hunger <= SimConfig.ExperimentHungerGate)
            return;

        var available = RecipeRegistry.GetAvailableRecipes(agent, knowledgeSystem, settlements);
        if (available.Count == 0) return;

        // Score = 0.25 × surplusResources × (0.3 + 0.7 × foodSaturation)
        // When food abundant and content: up to 0.35. When scarce: drops to ~0.08.
        float foodSaturation = CalculateFoodSaturation(agent, world);
        bool hasResources = agent.InventoryCount() > 3;

        float baseScore = 0.25f * (hasResources ? 1f : 0.5f) * (0.3f + 0.7f * foodSaturation);

        // Content bonus: sheltered and well-fed agents should experiment more
        if (!agent.IsExposed && agent.Hunger > 70f && agent.Health > 70)
            baseScore += 0.10f;

        // Exposure urgency: when exposed and lean_to is available to discover, boost significantly
        // Shelter is THE critical early-game discovery — it unlocks reproduction, health regen, storage
        bool exposureBoosted = false;
        Recipe? shelterRecipe = null;
        if (agent.IsExposed && !agent.Knowledge.Contains("lean_to"))
        {
            shelterRecipe = available.FirstOrDefault(r => r.Id == "lean_to");
            if (shelterRecipe != null)
            {
                baseScore = Math.Max(baseScore, 0.45f); // Must beat Socialize (0.3-0.5)
                exposureBoosted = true;
            }
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

        if (dist <= 3) return; // Within home radius — free to act without pull

        // Trip commitment: suppress ReturnHome when actively on a task nearby
        // Prevents Gather↔ReturnHome and Socialize↔ReturnHome oscillation
        if (agent.Hunger > 50f && dist <= 8) // Not hungry and still close to home
        {
            if (agent.LastChosenUtilityAction == ActionType.Gather && agent.HasInventorySpace())
                return; // Stay out and keep gathering until full
            if (agent.LastChosenUtilityAction == ActionType.Socialize)
                return; // Let the Socialize action reach its target
            if (agent.LastChosenUtilityAction == ActionType.Explore && agent.ConsecutiveSameActionTicks <= 5)
                return; // Commit to a short exploration burst
        }

        // Quadratic falloff: score = HomePullStrength / (1 + dist²)
        float score = SimConfig.HomePullStrength / (1f + dist * dist);

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

    private static void ScoreDepositHome(Agent agent, World world, Tile currentTile, List<ScoredAction> results)
    {
        if (!agent.HomeTile.HasValue) return;

        int food = agent.FoodInInventory();
        if (food <= 2) return;
        if (agent.Hunger <= 60f) return; // Don't deposit if hungry

        // At home — deposit directly (mode system handles returning home)
        if (agent.X != agent.HomeTile.Value.X || agent.Y != agent.HomeTile.Value.Y)
            return; // Not at home — mode transitions handle the return

        if (!currentTile.HasHomeStorage) return;
        int capacity = currentTile.HomeStorageCapacity;
        int stored = currentTile.HomeTotalFood;
        if (stored >= capacity) return;

        float fullness = (float)stored / capacity;
        float baseScore = SimConfig.DepositHomeBaseUtility * (1f - fullness);

        if (baseScore > 0.01f)
        {
            results.Add(new ScoredAction(ActionType.DepositHome, baseScore,
                targetTile: (currentTile.X, currentTile.Y)));
        }
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
            if (homeDist > 8)
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

    // ── GDD v1.7.2: ShareFood Utility ───────────────────────────────────

    private static void ScoreShareFood(Agent agent, World world, List<ScoredAction> results)
    {
        int food = agent.FoodInInventory();
        if (food <= 0) return;

        // Look for starving adjacent agents (hunger ≤ 30)
        var nearby = world.GetAdjacentAgents(agent.X, agent.Y);
        var samePos = world.GetAgentsAt(agent.X, agent.Y).Where(a => a.Id != agent.Id).ToList();

        Agent? neediest = null;
        float lowestHunger = 30f;

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
            || type == ResourceType.Animals || type == ResourceType.Fish
            || type == ResourceType.PreservedFood;
    }
}
