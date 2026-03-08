using CivSim.Core;

namespace CivSim.Diagnostics;

/// <summary>
/// Extended analysis diagnostic for long-duration runs (200K+ ticks).
/// Tracks population, per-agent action/mode distributions, discoveries,
/// goal completion rates, farm placement, and behavioral red flags.
///
/// READ-ONLY diagnostic — does not modify any simulation behavior.
/// </summary>
public static class ExtendedAnalysis
{
    // ── Per-Agent Tracking Data ─────────────────────────────────────────

    private class AgentStats
    {
        public int AgentId;
        public string Name = "";
        public bool WasBornAsChild; // true if born during the run (not a founder)
        public int BirthTick;
        public int? DeathTick;
        public string? DeathCause;
        public int PeakAge; // highest age reached

        // Action counters (adult waking ticks only)
        public Dictionary<ActionType, int> ActionTicks = new();
        public int TotalAdultWakingTicks;

        // Mode counters (all alive ticks)
        public Dictionary<BehaviorMode, int> ModeTicks = new();
        public int TotalAliveTicks;

        // Goal tracking
        public int GatherGoalsCreated;
        public int GatherGoalsCompleted;
        public int GatherGoalsAbandoned;

        // DepositHome tracking
        public int DepositHomeCount;
        public int DepositHomeInventorySum; // sum of inventory counts at each deposit

        // Experiment tracking
        public int ExperimentAttempts;
        // (successes tracked via discoveries)

        // Stuck tracking (reserved for future use)
    }

    // ── Per-Agent Goal State (live tracking) ────────────────────────────

    private class GoalState
    {
        public GoalType? LastGoal;
        public (int X, int Y)? LastGoalTarget;
    }

    // ── Discovery Record ────────────────────────────────────────────────

    private class DiscoveryRecord
    {
        public int Tick;
        public int Day;
        public string Name = "";
        public string AgentName = "";
    }

    // ── Death Record ────────────────────────────────────────────────────

    private class DeathRecord
    {
        public int Tick;
        public int AgentId;
        public string AgentName = "";
        public string Cause = "";
        public int Age;
        public bool HomeFoodAvailable; // was there food at home when they died?
    }

    // ── Main Entry Point ────────────────────────────────────────────────

    public static void Run(int seed, int ticks)
    {
        Console.WriteLine("========================================");
        Console.WriteLine($"EXTENDED ANALYSIS: Seed {seed}, {ticks} ticks");
        Console.WriteLine($"  (~{SimConfig.TicksToYears(ticks):F1} sim-years)");
        Console.WriteLine("========================================");
        Console.WriteLine();

        // Create simulation
        var world = new World(64, 64, seed);
        var simulation = new Simulation(world, seed);

        // Spawn founding pair
        for (int i = 0; i < 2; i++)
            simulation.SpawnAgent();

        // ── Tracking State ──────────────────────────────────────────────
        var agentStats = new Dictionary<int, AgentStats>();
        var goalStates = new Dictionary<int, GoalState>();
        var discoveries = new List<DiscoveryRecord>();
        var deaths = new List<DeathRecord>();
        var knownAgentIds = new HashSet<int>();

        // Population tracking
        int peakPopulation = 0;
        int peakPopulationTick = 0;
        int totalBirths = 0;

        // Per-tick population for averaging
        long populationTickSum = 0;

        // Initialize stats for founding agents
        foreach (var agent in simulation.Agents)
        {
            InitAgentStats(agentStats, agent, simulation.CurrentTick, false);
            knownAgentIds.Add(agent.Id);
            goalStates[agent.Id] = new GoalState();
        }

        // Capture initial discovery state to detect new ones
        var knownDiscoveries = new HashSet<string>();

        Console.WriteLine($"Running {ticks} ticks...");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // ── Main Simulation Loop ────────────────────────────────────────
        for (int t = 1; t <= ticks; t++)
        {
            // Snapshot pre-tick state for goal tracking
            var preTickGoals = new Dictionary<int, (GoalType? Goal, (int X, int Y)? Target)>();
            foreach (var agent in simulation.Agents)
            {
                if (!agent.IsAlive) continue;
                preTickGoals[agent.Id] = (agent.CurrentGoal, agent.GoalTarget);
            }

            // Tick the simulation
            simulation.Tick();

            // ── Detect new agents (births) ──────────────────────────────
            foreach (var agent in simulation.Agents)
            {
                if (!knownAgentIds.Contains(agent.Id))
                {
                    knownAgentIds.Add(agent.Id);
                    totalBirths++;
                    InitAgentStats(agentStats, agent, simulation.CurrentTick, true);
                    goalStates[agent.Id] = new GoalState();
                }
            }

            // ── Track population ────────────────────────────────────────
            int aliveCount = 0;
            foreach (var agent in simulation.Agents)
                if (agent.IsAlive) aliveCount++;

            populationTickSum += aliveCount;

            if (aliveCount > peakPopulation)
            {
                peakPopulation = aliveCount;
                peakPopulationTick = simulation.CurrentTick;
            }

            // ── Per-Agent Tracking ──────────────────────────────────────
            foreach (var agent in simulation.Agents)
            {
                if (!agentStats.TryGetValue(agent.Id, out var stats)) continue;

                if (agent.IsAlive)
                {
                    stats.TotalAliveTicks++;
                    stats.PeakAge = Math.Max(stats.PeakAge, agent.Age);

                    // Mode tracking (all alive ticks)
                    if (!stats.ModeTicks.ContainsKey(agent.CurrentMode))
                        stats.ModeTicks[agent.CurrentMode] = 0;
                    stats.ModeTicks[agent.CurrentMode]++;

                    // Action tracking (adult + daytime only)
                    bool isAdult = agent.Stage == DevelopmentStage.Adult;
                    bool isDaytime = !Agent.IsNightTime(simulation.CurrentTick);

                    if (isAdult && isDaytime)
                    {
                        stats.TotalAdultWakingTicks++;
                        var action = agent.CurrentAction;
                        if (!stats.ActionTicks.ContainsKey(action))
                            stats.ActionTicks[action] = 0;
                        stats.ActionTicks[action]++;

                        // Track Experiment attempts
                        if (action == ActionType.Experiment)
                            stats.ExperimentAttempts++;

                        // Track DepositHome fires
                        if (action == ActionType.DepositHome)
                        {
                            // Only count on the first tick of the action (when progress is near 0)
                            if (agent.ActionProgress <= 1.0f)
                            {
                                stats.DepositHomeCount++;
                                stats.DepositHomeInventorySum += TotalInventoryCount(agent);
                            }
                        }
                    }

                    // ── Goal Tracking (GatherResource goals) ────────────
                    var gs = goalStates[agent.Id];
                    var currentGoal = agent.CurrentGoal;
                    var currentTarget = agent.GoalTarget;

                    bool hadGatherGoal = gs.LastGoal == GoalType.GatherFoodAt || gs.LastGoal == GoalType.GatherResourceAt;
                    bool hasGatherGoal = currentGoal == GoalType.GatherFoodAt || currentGoal == GoalType.GatherResourceAt;

                    if (hasGatherGoal && !hadGatherGoal)
                    {
                        // New gather goal started
                        stats.GatherGoalsCreated++;
                    }
                    else if (hadGatherGoal && !hasGatherGoal)
                    {
                        // Gather goal ended — was it completed or abandoned?
                        if (gs.LastGoalTarget.HasValue)
                        {
                            int distToTarget = ChebyshevDist(agent.X, agent.Y,
                                gs.LastGoalTarget.Value.X, gs.LastGoalTarget.Value.Y);
                            if (distToTarget <= 2)
                                stats.GatherGoalsCompleted++;
                            else
                                stats.GatherGoalsAbandoned++;
                        }
                        else
                        {
                            stats.GatherGoalsAbandoned++;
                        }
                    }
                    else if (hadGatherGoal && hasGatherGoal && gs.LastGoalTarget != currentTarget)
                    {
                        // Goal target changed — treat as completed old + started new
                        // unless it's a minor redirect (within 5 tiles)
                        if (gs.LastGoalTarget.HasValue && currentTarget.HasValue)
                        {
                            int redirectDist = ChebyshevDist(
                                gs.LastGoalTarget.Value.X, gs.LastGoalTarget.Value.Y,
                                currentTarget.Value.X, currentTarget.Value.Y);
                            if (redirectDist > 5)
                            {
                                stats.GatherGoalsAbandoned++;
                                stats.GatherGoalsCreated++;
                            }
                        }
                    }

                    // Stuck detection — count transitions where StuckCounter resets from >5
                    if (agent.StuckCounter == 0 && gs.LastGoal != null)
                    {
                        // Simple proxy: if agent was stuck and isn't now, that's one episode
                        // We'll use a simpler approach: just sample StuckCounter > 10 at end
                    }

                    gs.LastGoal = currentGoal;
                    gs.LastGoalTarget = currentTarget;
                }
                else if (stats.DeathTick == null)
                {
                    // Agent just died — record death
                    stats.DeathTick = simulation.CurrentTick;
                    stats.DeathCause = agent.DeathCause ?? "unknown";

                    bool homeFoodAvailable = false;
                    if (agent.HomeTile.HasValue)
                    {
                        var homeTile = world.GetTile(agent.HomeTile.Value.X, agent.HomeTile.Value.Y);
                        homeFoodAvailable = homeTile.HomeTotalFood > 0 || homeTile.TotalFood() > 0;
                    }

                    deaths.Add(new DeathRecord
                    {
                        Tick = simulation.CurrentTick,
                        AgentId = agent.Id,
                        AgentName = agent.Name,
                        Cause = agent.DeathCause ?? "unknown",
                        Age = agent.Age,
                        HomeFoodAvailable = homeFoodAvailable
                    });
                }
            }

            // ── Discovery Tracking ──────────────────────────────────────
            foreach (var rec in simulation.DiscoveryRecords)
            {
                // Skip non-recipe entries (build completion messages that leak through)
                // Real recipe IDs are snake_case without spaces
                if (rec.RecipeId.Contains(' ') || rec.RecipeId.Contains('!'))
                    continue;

                if (!knownDiscoveries.Contains(rec.RecipeId))
                {
                    knownDiscoveries.Add(rec.RecipeId);
                    discoveries.Add(new DiscoveryRecord
                    {
                        Tick = rec.Tick,
                        Day = SimConfig.TicksToSimDays(rec.Tick),
                        Name = rec.RecipeId,
                        AgentName = rec.AgentName
                    });
                }
            }

            // ── Progress Indicator ──────────────────────────────────────
            if (t % 10000 == 0)
            {
                double elapsed = sw.Elapsed.TotalSeconds;
                double ticksPerSec = t / elapsed;
                double eta = (ticks - t) / ticksPerSec;
                Console.WriteLine($"  Tick {t,7}/{ticks} | Pop: {aliveCount,3} | " +
                    $"Discoveries: {discoveries.Count,2} | " +
                    $"{ticksPerSec:F0} ticks/s | ETA: {eta:F0}s");
            }

            // Early termination if everyone is dead
            if (aliveCount == 0 && t > 1000)
            {
                Console.WriteLine($"  EXTINCTION at tick {t} (day {SimConfig.TicksToSimDays(t)})");
                break;
            }
        }

        sw.Stop();
        Console.WriteLine();
        Console.WriteLine($"Simulation completed in {sw.Elapsed.TotalSeconds:F1}s");
        Console.WriteLine();

        // ════════════════════════════════════════════════════════════════
        // REPORT
        // ════════════════════════════════════════════════════════════════

        int finalAlive = simulation.Agents.Count(a => a.IsAlive);
        int totalAgents = simulation.Agents.Count;
        int deathsByStarvation = deaths.Count(d => d.Cause == "starvation");
        int deathsByOldAge = deaths.Count(d => d.Cause == "old age");
        int deathsByExposure = deaths.Count(d => d.Cause == "exposure");
        int deathsByOther = deaths.Count - deathsByStarvation - deathsByOldAge - deathsByExposure;

        // ── POPULATION ──────────────────────────────────────────────────
        Console.WriteLine("=== POPULATION ===");
        Console.WriteLine($"Starting: 2, Peak: {peakPopulation} (tick {peakPopulationTick}, day {SimConfig.TicksToSimDays(peakPopulationTick)}), Final: {finalAlive}");
        Console.WriteLine($"Total births: {totalBirths}, Total deaths: {deaths.Count}");
        Console.WriteLine($"Deaths by cause: Starvation={deathsByStarvation}, Old Age={deathsByOldAge}, Exposure={deathsByExposure}" +
            (deathsByOther > 0 ? $", Other={deathsByOther}" : ""));

        // Generational tracking
        var childrenWhoReachedAdulthood = new List<string>();
        var childrenWhoReproduced = new List<string>();
        foreach (var kvp in agentStats)
        {
            var stats = kvp.Value;
            if (!stats.WasBornAsChild) continue;
            if (stats.PeakAge >= SimConfig.ChildYouthAge)
                childrenWhoReachedAdulthood.Add(stats.Name);
        }

        // Check if any born-during-run agents have children of their own
        var agentNamesById = agentStats.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Name);
        foreach (var agent in simulation.Agents)
        {
            if (!agentStats.TryGetValue(agent.Id, out var stats)) continue;
            if (!stats.WasBornAsChild) continue;
            // Check if this agent is a parent of any other agent
            foreach (var otherAgent in simulation.Agents)
            {
                if (otherAgent.Id == agent.Id) continue;
                if (otherAgent.Relationships.TryGetValue(agent.Id, out var rel) && rel == RelationshipType.Parent)
                {
                    if (!childrenWhoReproduced.Contains(stats.Name))
                        childrenWhoReproduced.Add(stats.Name);
                    break;
                }
            }
        }

        Console.WriteLine($"Children who reached adulthood: {(childrenWhoReachedAdulthood.Count > 0 ? string.Join(", ", childrenWhoReachedAdulthood) : "None")}");
        Console.WriteLine($"Second-generation parents: {(childrenWhoReproduced.Count > 0 ? string.Join(", ", childrenWhoReproduced) : "None")}");

        // Longest living agent
        var longestLiving = agentStats.Values.OrderByDescending(s => s.PeakAge).FirstOrDefault();
        if (longestLiving != null)
            Console.WriteLine($"Longest living: {longestLiving.Name}, age {Agent.FormatTicks(longestLiving.PeakAge)} ({SimConfig.TicksToYears(longestLiving.PeakAge):F1} years)");

        double avgPop = ticks > 0 ? (double)populationTickSum / ticks : 0;
        Console.WriteLine($"Average population: {avgPop:F1}");
        Console.WriteLine();

        // ── DISCOVERIES ─────────────────────────────────────────────────
        int totalRecipes = RecipeRegistry.AllRecipes.Count;
        var uniqueDiscoveries = discoveries.Select(d => d.Name).Distinct().ToList();

        Console.WriteLine("=== DISCOVERIES ===");
        Console.WriteLine($"Total: {uniqueDiscoveries.Count} / {totalRecipes}");

        if (discoveries.Count > 0)
        {
            Console.WriteLine("Timeline:");
            // Show unique discoveries in chronological order
            var seen = new HashSet<string>();
            foreach (var d in discoveries.OrderBy(d => d.Tick))
            {
                if (seen.Contains(d.Name)) continue;
                seen.Add(d.Name);
                Console.WriteLine($"  Day {d.Day,5}: {d.Name} (by {d.AgentName})");
            }

            // Longest gap between discoveries
            var sortedTicks = discoveries.Select(d => d.Tick).OrderBy(t => t).ToList();
            if (sortedTicks.Count > 1)
            {
                int longestGap = 0;
                int gapStart = 0, gapEnd = 0;
                for (int i = 1; i < sortedTicks.Count; i++)
                {
                    int gap = sortedTicks[i] - sortedTicks[i - 1];
                    if (gap > longestGap)
                    {
                        longestGap = gap;
                        gapStart = i - 1;
                        gapEnd = i;
                    }
                }

                var discoveryByTick = discoveries.OrderBy(d => d.Tick).ToList();
                string gapFromName = gapStart < discoveryByTick.Count ? discoveryByTick[gapStart].Name : "?";
                string gapToName = gapEnd < discoveryByTick.Count ? discoveryByTick[gapEnd].Name : "?";
                Console.WriteLine($"Longest gap: {SimConfig.TicksToSimDays(longestGap)} days ({longestGap} ticks) between '{gapFromName}' and '{gapToName}'");
            }

            // Discovery rate by era
            int earlyCount = discoveries.Count(d => d.Tick <= 10000);
            int midCount = discoveries.Count(d => d.Tick > 5000 && d.Tick <= 50000);
            int lateCount = discoveries.Count(d => d.Tick > 30000);
            Console.WriteLine($"Discovery rate: Early(0-10K)={earlyCount}, Mid(5K-50K)={midCount}, Late(30K+)={lateCount}");
        }

        // Undiscovered recipes
        var discoveredSet = new HashSet<string>(uniqueDiscoveries);
        var undiscovered = RecipeRegistry.AllRecipes
            .Where(r => !discoveredSet.Contains(r.Id))
            .Select(r => r.Id)
            .ToList();
        if (undiscovered.Count > 0)
            Console.WriteLine($"Undiscovered: {string.Join(", ", undiscovered.Take(30))}" +
                (undiscovered.Count > 30 ? $" (+{undiscovered.Count - 30} more)" : ""));
        Console.WriteLine();

        // ── ACTION DISTRIBUTION ─────────────────────────────────────────
        Console.WriteLine("=== ACTION DISTRIBUTION (adults, waking time) ===");

        // Collect all action types that appear
        var allActions = new HashSet<ActionType>();
        foreach (var stats in agentStats.Values)
            foreach (var action in stats.ActionTicks.Keys)
                allActions.Add(action);

        var sortedActions = allActions.OrderBy(a => a.ToString()).ToList();

        // Header
        Console.Write($"{"Agent",-16}");
        foreach (var action in sortedActions)
            Console.Write($"{action.ToString().Substring(0, Math.Min(action.ToString().Length, 8)),9}");
        Console.Write($"{"Total",9}");
        Console.WriteLine();

        // Per-agent rows
        foreach (var stats in agentStats.Values.OrderBy(s => s.AgentId))
        {
            if (stats.TotalAdultWakingTicks == 0) continue; // skip infants/youth who never reached adulthood
            Console.Write($"{stats.Name,-16}");
            foreach (var action in sortedActions)
            {
                int count = stats.ActionTicks.GetValueOrDefault(action, 0);
                double pct = 100.0 * count / stats.TotalAdultWakingTicks;
                Console.Write($"{pct,8:F1}%");
            }
            Console.Write($"{stats.TotalAdultWakingTicks,9}");
            Console.WriteLine();
        }
        Console.WriteLine();

        // ── MODE DISTRIBUTION ───────────────────────────────────────────
        Console.WriteLine("=== MODE DISTRIBUTION ===");
        var allModes = Enum.GetValues<BehaviorMode>();

        Console.Write($"{"Agent",-16}");
        foreach (var mode in allModes)
            Console.Write($"{mode,10}");
        Console.Write($"{"Total",10}");
        Console.WriteLine();

        foreach (var stats in agentStats.Values.OrderBy(s => s.AgentId))
        {
            if (stats.TotalAliveTicks == 0) continue;
            Console.Write($"{stats.Name,-16}");
            foreach (var mode in allModes)
            {
                int count = stats.ModeTicks.GetValueOrDefault(mode, 0);
                double pct = 100.0 * count / stats.TotalAliveTicks;
                Console.Write($"{pct,9:F1}%");
            }
            Console.Write($"{stats.TotalAliveTicks,10}");
            Console.WriteLine();
        }
        Console.WriteLine();

        // ── BEHAVIORAL HEALTH ───────────────────────────────────────────
        Console.WriteLine("=== BEHAVIORAL HEALTH ===");

        // GatherResource goals
        int totalGatherCreated = agentStats.Values.Sum(s => s.GatherGoalsCreated);
        int totalGatherCompleted = agentStats.Values.Sum(s => s.GatherGoalsCompleted);
        int totalGatherAbandoned = agentStats.Values.Sum(s => s.GatherGoalsAbandoned);
        Console.WriteLine($"GatherResource: {totalGatherCreated} created / {totalGatherCompleted} completed / {totalGatherAbandoned} abandoned");

        // DepositHome
        int totalDeposits = agentStats.Values.Sum(s => s.DepositHomeCount);
        double avgDepositInv = totalDeposits > 0
            ? (double)agentStats.Values.Sum(s => s.DepositHomeInventorySum) / totalDeposits
            : 0;
        Console.WriteLine($"DepositHome: {totalDeposits} fires, avg inventory {avgDepositInv:F1}");

        // Experiment
        int totalExpAttempts = agentStats.Values.Sum(s => s.ExperimentAttempts);
        Console.WriteLine($"Experiment: {totalExpAttempts} attempt-ticks, {uniqueDiscoveries.Count} successes");

        // Idle/Rest per agent
        Console.WriteLine("Idle/Rest % (waking time, flagged if >25%):");
        foreach (var stats in agentStats.Values.OrderBy(s => s.AgentId))
        {
            if (stats.TotalAdultWakingTicks == 0) continue;
            int idleRest = stats.ActionTicks.GetValueOrDefault(ActionType.Idle, 0) +
                           stats.ActionTicks.GetValueOrDefault(ActionType.Rest, 0);
            double pct = 100.0 * idleRest / stats.TotalAdultWakingTicks;
            string flag = pct > 25 ? " *** FLAG" : "";
            Console.WriteLine($"  {stats.Name}: {pct:F1}%{flag}");
        }

        // Farm tiles
        int farmCount = 0;
        int invalidFarmCount = 0;
        int maxFarmDist = 0;
        var invalidFarms = new List<string>();

        for (int x = 0; x < world.Width; x++)
        {
            for (int y = 0; y < world.Height; y++)
            {
                var tile = world.GetTile(x, y);
                if (!tile.HasFarm) continue;
                farmCount++;

                // Check validity
                bool isInvalid = false;
                string reason = "";

                if (tile.Biome == BiomeType.Water)
                {
                    isInvalid = true;
                    reason = $"water@({x},{y})";
                }
                else if (tile.Biome == BiomeType.Forest && !tile.Structures.Contains("cleared"))
                {
                    isInvalid = true;
                    reason = $"raw_forest@({x},{y})";
                }

                // Check distance from any agent's home
                int minDistFromHome = int.MaxValue;
                foreach (var agent in simulation.Agents)
                {
                    if (!agent.HomeTile.HasValue) continue;
                    int dist = ChebyshevDist(x, y, agent.HomeTile.Value.X, agent.HomeTile.Value.Y);
                    minDistFromHome = Math.Min(minDistFromHome, dist);
                }

                if (minDistFromHome > maxFarmDist && minDistFromHome < int.MaxValue)
                    maxFarmDist = minDistFromHome;

                // Check if farm is on a home tile
                foreach (var agent in simulation.Agents)
                {
                    if (agent.HomeTile.HasValue && agent.HomeTile.Value.X == x && agent.HomeTile.Value.Y == y)
                    {
                        reason = $"on_home@({x},{y})";
                        isInvalid = true;
                        break;
                    }
                }

                if (isInvalid)
                {
                    invalidFarmCount++;
                    invalidFarms.Add(reason);
                }
            }
        }

        Console.WriteLine($"Farm tiles: {farmCount}, max dist from home={maxFarmDist}" +
            (invalidFarms.Count > 0 ? $", invalid=[{string.Join(", ", invalidFarms)}]" : ""));
        Console.WriteLine();

        // ── SETTLEMENT STATE ────────────────────────────────────────────
        Console.WriteLine("=== SETTLEMENT STATE ===");

        // Structures
        var structureCounts = new Dictionary<string, int>();
        for (int x = 0; x < world.Width; x++)
        {
            for (int y = 0; y < world.Height; y++)
            {
                var tile = world.GetTile(x, y);
                foreach (var s in tile.Structures)
                {
                    if (!structureCounts.ContainsKey(s))
                        structureCounts[s] = 0;
                    structureCounts[s]++;
                }
            }
        }

        if (structureCounts.Count > 0)
            Console.WriteLine($"Structures: {string.Join(", ", structureCounts.Select(kvp => $"{kvp.Key}={kvp.Value}"))}");
        else
            Console.WriteLine("Structures: None");

        // Home storage for alive agents
        foreach (var agent in simulation.Agents.Where(a => a.IsAlive && a.HomeTile.HasValue))
        {
            var homeTile = world.GetTile(agent.HomeTile!.Value.X, agent.HomeTile.Value.Y);
            var foodItems = new List<string>();
            foreach (var kvp in homeTile.HomeFoodStorage)
                if (kvp.Value > 0) foodItems.Add($"{kvp.Key}={kvp.Value}");
            var materialItems = new List<string>();
            foreach (var kvp in homeTile.HomeMaterialStorage)
                if (kvp.Value > 0) materialItems.Add($"{kvp.Key}={kvp.Value}");

            string contents = "";
            if (foodItems.Count > 0) contents += "Food: " + string.Join(", ", foodItems);
            if (materialItems.Count > 0) contents += (contents.Length > 0 ? " | " : "") + "Materials: " + string.Join(", ", materialItems);
            if (contents.Length == 0) contents = "Empty";

            Console.WriteLine($"  {agent.Name}'s home ({agent.HomeTile.Value.X},{agent.HomeTile.Value.Y}): {contents}");
        }

        Console.WriteLine($"Farm count: {farmCount}");

        // Settlements
        if (simulation.Settlements.Count > 0)
        {
            Console.WriteLine($"Settlements: {string.Join(", ", simulation.Settlements.Select(s => $"'{s.Name}' ({s.ShelterCount} shelters)"))}");
        }
        Console.WriteLine();

        // ── RED FLAGS ───────────────────────────────────────────────────
        Console.WriteLine("=== RED FLAGS ===");
        var redFlags = new List<string>();

        // 1. Starvation death while home has food
        foreach (var death in deaths)
        {
            if (death.Cause == "starvation" && death.HomeFoodAvailable)
                redFlags.Add($"STARVATION_WITH_HOME_FOOD: {death.AgentName} starved at tick {death.Tick} while home had food");
        }

        // 2. Any agent with Idle/Rest > 25% of waking time
        foreach (var stats in agentStats.Values)
        {
            if (stats.TotalAdultWakingTicks == 0) continue;
            int idleRest = stats.ActionTicks.GetValueOrDefault(ActionType.Idle, 0) +
                           stats.ActionTicks.GetValueOrDefault(ActionType.Rest, 0);
            double pct = 100.0 * idleRest / stats.TotalAdultWakingTicks;
            if (pct > 25)
                redFlags.Add($"HIGH_IDLE: {stats.Name} spent {pct:F1}% of waking time idle/resting");
        }

        // 3. Farm on home/forest/water tile
        foreach (var inv in invalidFarms)
            redFlags.Add($"INVALID_FARM: {inv}");

        // 4. Discovery count < 15
        if (uniqueDiscoveries.Count < 15)
            redFlags.Add($"LOW_DISCOVERIES: Only {uniqueDiscoveries.Count} discoveries (threshold: 15)");

        // 5. Extinction
        if (finalAlive == 0)
            redFlags.Add("EXTINCTION: All agents died");

        // 6. Bonus: very high abandonment rate
        if (totalGatherCreated > 0)
        {
            double abandonRate = 100.0 * totalGatherAbandoned / totalGatherCreated;
            if (abandonRate > 50)
                redFlags.Add($"HIGH_GATHER_ABANDONMENT: {abandonRate:F1}% of gather goals abandoned");
        }

        if (redFlags.Count == 0)
            Console.WriteLine("None");
        else
            foreach (var flag in redFlags)
                Console.WriteLine($"  {flag}");

        Console.WriteLine();
        Console.WriteLine("========================================");
        Console.WriteLine("EXTENDED ANALYSIS COMPLETE");
        Console.WriteLine("========================================");
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static void InitAgentStats(Dictionary<int, AgentStats> dict, Agent agent, int tick, bool wasBornAsChild)
    {
        dict[agent.Id] = new AgentStats
        {
            AgentId = agent.Id,
            Name = agent.Name,
            WasBornAsChild = wasBornAsChild,
            BirthTick = tick,
            PeakAge = agent.Age
        };
    }

    private static int ChebyshevDist(int x1, int y1, int x2, int y2)
        => Math.Max(Math.Abs(x1 - x2), Math.Abs(y1 - y2));

    private static int TotalInventoryCount(Agent agent)
    {
        int total = 0;
        foreach (var kvp in agent.Inventory)
            total += kvp.Value;
        return total;
    }
}
