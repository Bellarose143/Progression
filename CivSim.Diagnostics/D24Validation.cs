using CivSim.Core;

namespace CivSim.Diagnostics;

/// <summary>
/// D24 Validation: Runs seed 16001 for 50K ticks and validates:
/// 1. Explore direction diversity (per-agent distribution across 8 directions)
/// 2. Maximum consecutive same-direction streaks
/// 3. Water gather attempts (should be 0)
/// 4. Ryan's adult action distribution
/// 5. Total explore ticks per agent and percentage of life
/// 6. Discovery count
/// 7. Death count
/// 8. Budget waste early returns (explore ended near start before budget expired)
/// </summary>
public static class D24Validation
{
    public static void Run()
    {
        const int seed = 16001;
        const int tickCount = 50_000;

        Console.WriteLine("================================================================");
        Console.WriteLine("  D24 Validation — seed 16001, 50K ticks");
        Console.WriteLine("================================================================");
        Console.WriteLine();

        Agent.ResetIdCounter();
        var world = new World(64, 64, seed);
        var sim = new Simulation(world, seed);
        for (int i = 0; i < 2; i++) sim.SpawnAgent();

        // ── Tracking structures ──

        // 1. Per-agent explore direction distribution
        var exploreDirectionCounts = new Dictionary<int, Dictionary<(int, int), int>>();

        // 2. Per-agent consecutive same-direction tracking
        var lastExploreDir = new Dictionary<int, (int, int)?>();
        var currentStreak = new Dictionary<int, int>();
        var maxStreak = new Dictionary<int, int>();
        var maxStreakDir = new Dictionary<int, (int, int)>();

        // Track when agent enters Explore to detect new trips
        var wasInExplore = new Dictionary<int, bool>();

        // 3. Water gather attempts
        int waterGatherAttempts = 0;
        var waterGatherDetails = new List<(int Tick, string AgentName, int X, int Y)>();

        // 4. Ryan's adult action distribution
        var ryanAdultActions = new Dictionary<ActionType, int>();
        int ryanId = -1;
        int ryanAdultStartTick = -1;

        // 5. Explore ticks per agent
        var exploreTicks = new Dictionary<int, int>();
        var agentAliveTicks = new Dictionary<int, int>();

        // 7. Deaths
        int deathCount = 0;
        var deathDetails = new List<(int Tick, string Name, string Cause)>();

        // 8. Budget waste early returns
        int budgetWasteCount = 0;
        var budgetWasteDetails = new List<(int Tick, string AgentName, int DistFromStart, int BudgetRemaining)>();

        // Track explore start positions and budgets for waste detection
        var exploreStartPos = new Dictionary<int, (int X, int Y)>();
        var exploreBudgetAtStart = new Dictionary<int, int>();
        var exploreModeEntryTick = new Dictionary<int, int>();

        // Previous alive state for death detection
        var prevAlive = new Dictionary<int, bool>();

        for (int t = 0; t < tickCount; t++)
        {
            sim.Tick();

            foreach (var agent in sim.Agents)
            {
                int id = agent.Id;

                // Initialize tracking
                if (!exploreTicks.ContainsKey(id))
                {
                    exploreTicks[id] = 0;
                    agentAliveTicks[id] = 0;
                    exploreDirectionCounts[id] = new Dictionary<(int, int), int>();
                    wasInExplore[id] = false;
                    lastExploreDir[id] = null;
                    currentStreak[id] = 0;
                    maxStreak[id] = 0;
                    maxStreakDir[id] = (0, 0);
                    prevAlive[id] = true;

                    // Identify Ryan
                    if (agent.Name == "Ryan")
                        ryanId = id;
                }

                // Death detection
                if (prevAlive.GetValueOrDefault(id, true) && !agent.IsAlive)
                {
                    deathCount++;
                    deathDetails.Add((sim.CurrentTick, agent.Name, "died"));
                    prevAlive[id] = false;
                    continue;
                }
                if (!agent.IsAlive) continue;
                prevAlive[id] = true;

                agentAliveTicks[id]++;

                bool inExplore = agent.CurrentMode == BehaviorMode.Explore;

                // Detect new explore trip start
                if (inExplore && !wasInExplore[id])
                {
                    // New explore trip started
                    exploreStartPos[id] = (agent.X, agent.Y);
                    exploreBudgetAtStart[id] = agent.ModeCommit.ExploreBudget;
                    exploreModeEntryTick[id] = sim.CurrentTick;

                    // Record this trip's direction for diversity tracking
                    var dir = agent.ModeCommit.ExploreDirection;
                    if (dir.HasValue)
                    {
                        var d = dir.Value;
                        if (!exploreDirectionCounts[id].ContainsKey((d.Dx, d.Dy)))
                            exploreDirectionCounts[id][(d.Dx, d.Dy)] = 0;
                        exploreDirectionCounts[id][(d.Dx, d.Dy)]++;

                        // Consecutive streak tracking
                        if (lastExploreDir[id].HasValue && lastExploreDir[id].Value == (d.Dx, d.Dy))
                        {
                            currentStreak[id]++;
                        }
                        else
                        {
                            currentStreak[id] = 1;
                        }
                        if (currentStreak[id] > maxStreak[id])
                        {
                            maxStreak[id] = currentStreak[id];
                            maxStreakDir[id] = (d.Dx, d.Dy);
                        }
                        lastExploreDir[id] = (d.Dx, d.Dy);
                    }
                }

                // Detect explore trip end (was in explore, now not)
                if (!inExplore && wasInExplore[id])
                {
                    // Check for budget waste: ended before budget expired AND within 3 tiles of start
                    if (exploreStartPos.ContainsKey(id) && exploreModeEntryTick.ContainsKey(id))
                    {
                        int ticksInExplore = sim.CurrentTick - exploreModeEntryTick[id];
                        int budget = exploreBudgetAtStart.GetValueOrDefault(id, 300);
                        int distFromStart = Math.Max(
                            Math.Abs(agent.X - exploreStartPos[id].X),
                            Math.Abs(agent.Y - exploreStartPos[id].Y));

                        if (ticksInExplore < budget && distFromStart <= 3)
                        {
                            budgetWasteCount++;
                            budgetWasteDetails.Add((sim.CurrentTick, agent.Name, distFromStart, budget - ticksInExplore));
                        }
                    }
                }

                wasInExplore[id] = inExplore;

                // 5. Count explore ticks
                if (inExplore)
                    exploreTicks[id]++;

                // DEBUG: Check if any agent has fish-on-water in memory at tick 215
                if (sim.CurrentTick == 215)
                {
                    var fishMems = agent.Memory.Where(m => m.Type == MemoryType.Resource && m.Resource == ResourceType.Fish).ToList();
                    if (fishMems.Count > 0)
                    {
                        Console.WriteLine($"  DEBUG tick 215: {agent.Name} has {fishMems.Count} fish memories:");
                        foreach (var fm in fishMems)
                        {
                            var ft = world.GetTile(fm.X, fm.Y);
                            Console.WriteLine($"    ({fm.X},{fm.Y}) biome={ft.Biome} qty={fm.Quantity} passable={!float.IsPositiveInfinity(ft.MovementCostMultiplier)}");
                        }
                    }
                }

                // 3. Water gather detection: agent's goal targets a water biome tile
                if (agent.CurrentAction == ActionType.Gather || agent.CurrentAction == ActionType.Move)
                {
                    if (agent.GoalTarget.HasValue)
                    {
                        var gt = agent.GoalTarget.Value;
                        if (gt.X >= 0 && gt.X < world.Width && gt.Y >= 0 && gt.Y < world.Height)
                        {
                            var targetTile = world.GetTile(gt.X, gt.Y);
                            if (targetTile.Biome == BiomeType.Water && agent.CurrentGoal.HasValue)
                            {
                                // Only count gather-related goals targeting water
                                var goal = agent.CurrentGoal.Value;
                                if (goal == GoalType.GatherFoodAt || goal == GoalType.GatherResourceAt)
                                {
                                    waterGatherAttempts++;
                                    if (waterGatherDetails.Count < 20)
                                        waterGatherDetails.Add((sim.CurrentTick, agent.Name, gt.X, gt.Y));
                                }
                            }
                        }
                    }
                }

                // 4. Ryan's adult action distribution
                if (id == ryanId && agent.Stage == DevelopmentStage.Adult)
                {
                    if (ryanAdultStartTick < 0)
                        ryanAdultStartTick = sim.CurrentTick;

                    if (!ryanAdultActions.ContainsKey(agent.CurrentAction))
                        ryanAdultActions[agent.CurrentAction] = 0;
                    ryanAdultActions[agent.CurrentAction]++;
                }
            }
        }

        // ── Report ──

        Console.WriteLine($"Simulation complete: {sim.CurrentTick} ticks");
        Console.WriteLine($"Agents alive: {sim.Agents.Count(a => a.IsAlive)} / {sim.Agents.Count}");
        Console.WriteLine();

        // 7. Death count
        Console.WriteLine("════════════════════════════════════════");
        Console.WriteLine($"DEATHS: {deathCount}");
        foreach (var (tick, name, cause) in deathDetails)
            Console.WriteLine($"  tick={tick}: {name} {cause}");
        Console.WriteLine();

        // 6. Discovery count
        Console.WriteLine("════════════════════════════════════════");
        Console.WriteLine($"DISCOVERIES: {sim.DiscoveryRecords.Count}");
        foreach (var (tick, recipeId, agentName) in sim.DiscoveryRecords)
            Console.WriteLine($"  tick={tick}: {agentName} discovered {recipeId}");
        Console.WriteLine();

        // 1 & 2 & 5. Per-agent explore direction distribution
        Console.WriteLine("════════════════════════════════════════");
        Console.WriteLine("EXPLORE DIRECTION DISTRIBUTION (per agent):");
        Console.WriteLine();

        foreach (var agent in sim.Agents)
        {
            int id = agent.Id;
            if (!exploreDirectionCounts.ContainsKey(id)) continue;

            var dirCounts = exploreDirectionCounts[id];
            int totalTrips = dirCounts.Values.Sum();
            if (totalTrips == 0)
            {
                Console.WriteLine($"  {agent.Name}: 0 explore trips");
                continue;
            }

            Console.WriteLine($"  {agent.Name} ({totalTrips} explore trips):");

            // All 8 directions
            var allDirs = new (int Dx, int Dy)[]
            {
                (1, 0), (-1, 0), (0, 1), (0, -1),
                (1, 1), (1, -1), (-1, 1), (-1, -1)
            };
            foreach (var d in allDirs)
            {
                int count = dirCounts.GetValueOrDefault(d, 0);
                float pct = totalTrips > 0 ? 100f * count / totalTrips : 0;
                string label = d switch
                {
                    (1, 0) => "E ",
                    (-1, 0) => "W ",
                    (0, 1) => "S ",
                    (0, -1) => "N ",
                    (1, 1) => "SE",
                    (1, -1) => "NE",
                    (-1, 1) => "SW",
                    (-1, -1) => "NW",
                    _ => "??"
                };
                Console.WriteLine($"    ({d.Dx,2},{d.Dy,2}) {label}: {count,3} trips ({pct:F1}%)");
            }

            // Max consecutive streak
            Console.WriteLine($"    Max consecutive same-direction: {maxStreak.GetValueOrDefault(id)} (direction: ({maxStreakDir.GetValueOrDefault(id).Item1},{maxStreakDir.GetValueOrDefault(id).Item2}))");

            // Explore ticks and percentage of life
            int eTicks = exploreTicks.GetValueOrDefault(id);
            int alive = agentAliveTicks.GetValueOrDefault(id);
            float ePct = alive > 0 ? 100f * eTicks / alive : 0;
            Console.WriteLine($"    Total explore ticks: {eTicks} ({ePct:F1}% of {alive} alive ticks)");
            Console.WriteLine();
        }

        // 3. Water gather attempts
        Console.WriteLine("════════════════════════════════════════");
        Console.WriteLine($"WATER GATHER ATTEMPTS: {waterGatherAttempts}");
        if (waterGatherAttempts > 0)
        {
            Console.WriteLine("  First 20:");
            foreach (var (tick, name, x, y) in waterGatherDetails)
            {
                var wt = world.GetTile(x, y);
                Console.WriteLine($"    tick={tick}: {name} targeting ({x},{y}) biome={wt.Biome} fish={wt.Resources.GetValueOrDefault(ResourceType.Fish, 0)} cost={wt.MovementCostMultiplier}");
            }
        }
        Console.WriteLine();

        // 4. Ryan's adult action distribution
        Console.WriteLine("════════════════════════════════════════");
        if (ryanId >= 0 && ryanAdultActions.Count > 0)
        {
            int totalAdultTicks = ryanAdultActions.Values.Sum();
            Console.WriteLine($"RYAN ADULT ACTION DISTRIBUTION (matured tick ~{ryanAdultStartTick}, {totalAdultTicks} adult ticks):");
            foreach (var kv in ryanAdultActions.OrderByDescending(k => k.Value))
            {
                float pct = 100f * kv.Value / totalAdultTicks;
                Console.WriteLine($"    {kv.Key,-16}: {kv.Value,6} ({pct:F1}%)");
            }
        }
        else
        {
            Console.WriteLine("RYAN ADULT ACTION DISTRIBUTION: Ryan not found or did not reach adulthood in 50K ticks");
            Console.WriteLine($"  (Ryan ID={ryanId}, ChildYouthAge={SimConfig.ChildYouthAge} ticks)");
        }
        Console.WriteLine();

        // 8. Budget waste early returns
        Console.WriteLine("════════════════════════════════════════");
        Console.WriteLine($"BUDGET WASTE EARLY RETURNS: {budgetWasteCount}");
        if (budgetWasteCount > 0)
        {
            Console.WriteLine($"  (Explore trips ending before budget expired while within 3 tiles of start)");
            Console.WriteLine($"  First 20:");
            foreach (var (tick, name, dist, remaining) in budgetWasteDetails.Take(20))
                Console.WriteLine($"    tick={tick}: {name} dist={dist} from start, {remaining} budget remaining");
        }
        Console.WriteLine();

        // Summary pass/fail
        Console.WriteLine("════════════════════════════════════════");
        Console.WriteLine("D24 ACCEPTANCE CRITERIA:");

        // Check direction diversity: no single direction > 50% for any agent with 10+ trips
        bool dirDiversityPass = true;
        foreach (var agent in sim.Agents)
        {
            int id = agent.Id;
            if (!exploreDirectionCounts.ContainsKey(id)) continue;
            var dirCounts = exploreDirectionCounts[id];
            int totalTrips = dirCounts.Values.Sum();
            if (totalTrips < 10) continue;
            int maxInOneDir = dirCounts.Values.Max();
            if (100f * maxInOneDir / totalTrips > 50f)
            {
                dirDiversityPass = false;
                Console.WriteLine($"  [FAIL] {agent.Name}: {maxInOneDir}/{totalTrips} trips in single direction ({100f * maxInOneDir / totalTrips:F1}% > 50%)");
            }
        }
        if (dirDiversityPass)
            Console.WriteLine("  [PASS] Explore direction diversity: no agent >50% in one direction");

        // Max streak check
        bool streakPass = true;
        foreach (var agent in sim.Agents)
        {
            int id = agent.Id;
            if (maxStreak.GetValueOrDefault(id) > 5)
            {
                streakPass = false;
                Console.WriteLine($"  [FAIL] {agent.Name}: max consecutive same-direction streak = {maxStreak[id]} (>5)");
            }
        }
        if (streakPass)
            Console.WriteLine("  [PASS] Max consecutive same-direction streaks <= 5");

        // Water gathers
        Console.WriteLine(waterGatherAttempts == 0
            ? "  [PASS] Water gather attempts: 0"
            : $"  [FAIL] Water gather attempts: {waterGatherAttempts} (expected 0)");

        // Ryan idle check
        if (ryanId >= 0 && ryanAdultActions.Count > 0)
        {
            int totalAdultTicks = ryanAdultActions.Values.Sum();
            int idleTicks = ryanAdultActions.GetValueOrDefault(ActionType.Idle, 0);
            float idlePct = 100f * idleTicks / totalAdultTicks;
            Console.WriteLine(idlePct < 20f
                ? $"  [PASS] Ryan adult Idle%: {idlePct:F1}% (< 20%)"
                : $"  [FAIL] Ryan adult Idle%: {idlePct:F1}% (>= 20%)");
        }
        else
        {
            Console.WriteLine("  [SKIP] Ryan adult Idle%: Ryan did not reach adulthood");
        }

        Console.WriteLine();
    }
}
