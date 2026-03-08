using CivSim.Core;

namespace CivSim.Diagnostics;

/// <summary>
/// D23 Explore Trace: Runs seed 16001 for 100K ticks and traces all Explore-mode
/// activity, mode transitions, stuck detection, tile visits, stone gathering from
/// non-mountain tiles, and action distributions.
/// </summary>
public static class D23ExploreTrace
{
    public static void Run()
    {
        const int seed = 16001;
        const int tickCount = 100_000;

        Console.WriteLine("================================================================");
        Console.WriteLine("  D23 Explore Trace — seed 16001, 100K ticks");
        Console.WriteLine("================================================================");
        Console.WriteLine();

        Agent.ResetIdCounter();
        var world = new World(64, 64, seed);
        var sim = new Simulation(world, seed);
        for (int i = 0; i < 2; i++) sim.SpawnAgent();

        // Per-agent tracking
        var exploreTicks = new Dictionary<int, int>();           // agentId -> total ticks in Explore
        var exploreCycles = new Dictionary<int, int>();          // agentId -> Explore->Home->Explore cycles
        var stuckTicks = new Dictionary<int, int>();             // agentId -> ticks where position didn't change despite Move
        var tileVisits = new Dictionary<int, Dictionary<(int, int), int>>();  // agentId -> tile -> visit count
        var modeTransitions = new Dictionary<int, List<(int Tick, BehaviorMode From, BehaviorMode To)>>();
        var prevPositions = new Dictionary<int, (int X, int Y)>();
        var prevModes = new Dictionary<int, BehaviorMode>();
        var lastExploreExit = new Dictionary<int, bool>();       // agentId -> true if last exit was to Home

        // Action distribution tracking (post-youth only)
        var actionCountsPostYouth = new Dictionary<int, Dictionary<ActionType, int>>();

        // Stone gather from non-mountain tracking
        var stoneFromNonMountain = new List<(int Tick, string AgentName, int X, int Y, BiomeType Biome)>();

        // Explore log samples (first 50 per agent)
        var exploreLogSamples = new Dictionary<int, List<string>>();

        sim.TraceCallback = msg =>
        {
            // Detect stone gather from non-mountain via trace messages
            // (This is imprecise; we'll also check directly below)
        };

        for (int t = 0; t < tickCount; t++)
        {
            sim.Tick();

            foreach (var agent in sim.Agents)
            {
                if (!agent.IsAlive) continue;

                int id = agent.Id;
                var pos = (agent.X, agent.Y);

                // Initialize tracking
                if (!exploreTicks.ContainsKey(id))
                {
                    exploreTicks[id] = 0;
                    exploreCycles[id] = 0;
                    stuckTicks[id] = 0;
                    tileVisits[id] = new Dictionary<(int, int), int>();
                    modeTransitions[id] = new List<(int, BehaviorMode, BehaviorMode)>();
                    prevPositions[id] = pos;
                    prevModes[id] = agent.CurrentMode;
                    lastExploreExit[id] = false;
                    actionCountsPostYouth[id] = new Dictionary<ActionType, int>();
                    exploreLogSamples[id] = new List<string>();
                }

                // Mode transition detection
                if (prevModes.TryGetValue(id, out var oldMode) && oldMode != agent.CurrentMode)
                {
                    modeTransitions[id].Add((sim.CurrentTick, oldMode, agent.CurrentMode));

                    // Track Explore->Home->Explore cycles
                    if (oldMode == BehaviorMode.Explore && agent.CurrentMode == BehaviorMode.Home)
                        lastExploreExit[id] = true;
                    else if (oldMode == BehaviorMode.Home && agent.CurrentMode == BehaviorMode.Explore && lastExploreExit.GetValueOrDefault(id))
                    {
                        exploreCycles[id]++;
                        lastExploreExit[id] = false;
                    }
                    else if (agent.CurrentMode != BehaviorMode.Home)
                        lastExploreExit[id] = false;
                }
                prevModes[id] = agent.CurrentMode;

                // Explore mode tracking
                if (agent.CurrentMode == BehaviorMode.Explore)
                {
                    exploreTicks[id]++;

                    // Log sample (first 50)
                    if (exploreLogSamples[id].Count < 50)
                    {
                        var dir = agent.ModeCommit.ExploreDirection;
                        string dirStr = dir.HasValue ? $"({dir.Value.Dx},{dir.Value.Dy})" : "none";
                        string goalStr = agent.CurrentGoal.HasValue ? agent.CurrentGoal.Value.ToString() : "none";
                        string goalTargetStr = agent.GoalTarget.HasValue ? $"({agent.GoalTarget.Value.X},{agent.GoalTarget.Value.Y})" : "none";
                        exploreLogSamples[id].Add(
                            $"  tick={sim.CurrentTick} pos=({agent.X},{agent.Y}) dir={dirStr} budget={agent.ModeCommit.ExploreBudget} action={agent.CurrentAction} goal={goalStr} goalTarget={goalTargetStr}");
                    }
                }

                // Stuck detection: agent action is Move but position unchanged
                if (agent.CurrentAction == ActionType.Move)
                {
                    var prev = prevPositions.GetValueOrDefault(id, pos);
                    if (prev == pos)
                        stuckTicks[id]++;
                }

                // Tile visit counting
                if (!tileVisits[id].ContainsKey(pos))
                    tileVisits[id][pos] = 0;
                tileVisits[id][pos]++;

                // Post-youth action distribution
                if (agent.Stage == DevelopmentStage.Adult)
                {
                    if (!actionCountsPostYouth[id].ContainsKey(agent.CurrentAction))
                        actionCountsPostYouth[id][agent.CurrentAction] = 0;
                    actionCountsPostYouth[id][agent.CurrentAction]++;
                }

                // Stone gather from non-mountain detection
                if (agent.CurrentAction == ActionType.Gather
                    && agent.ActionTargetResource == ResourceType.Stone)
                {
                    var tile = world.GetTile(agent.X, agent.Y);
                    if (tile.Biome != BiomeType.Mountain)
                    {
                        stoneFromNonMountain.Add((sim.CurrentTick, agent.Name, agent.X, agent.Y, tile.Biome));
                    }
                }

                prevPositions[id] = pos;
            }
        }

        // ── Report ──
        Console.WriteLine($"Simulation complete: {sim.CurrentTick} ticks, {sim.Agents.Count(a => a.IsAlive)} alive");
        Console.WriteLine();

        // Farm count
        int farmCount = 0;
        for (int x = 0; x < world.Width; x++)
            for (int y = 0; y < world.Height; y++)
                if (world.GetTile(x, y).HasFarm) farmCount++;
        Console.WriteLine($"Farm tiles in world: {farmCount}");
        Console.WriteLine();

        // Per-agent report
        foreach (var agent in sim.Agents)
        {
            int id = agent.Id;
            Console.WriteLine($"══════════════════════════════════════════");
            Console.WriteLine($"Agent: {agent.Name} (ID={id}, Alive={agent.IsAlive})");
            Console.WriteLine($"  Final position: ({agent.X},{agent.Y})  Home: {(agent.HomeTile.HasValue ? $"({agent.HomeTile.Value.X},{agent.HomeTile.Value.Y})" : "none")}");
            Console.WriteLine($"  Age: {Agent.FormatTicks(agent.Age)}  Mode: {agent.CurrentMode}");
            Console.WriteLine();

            // Explore stats
            Console.WriteLine($"  Total ticks in Explore: {exploreTicks.GetValueOrDefault(id)}");
            Console.WriteLine($"  Explore->Home->Explore cycles: {exploreCycles.GetValueOrDefault(id)}");
            Console.WriteLine($"  Stuck ticks (Move but no position change): {stuckTicks.GetValueOrDefault(id)}");
            Console.WriteLine();

            // Mode transitions
            if (modeTransitions.ContainsKey(id))
            {
                var transitions = modeTransitions[id];
                Console.WriteLine($"  Mode transitions ({transitions.Count} total):");

                // Summarize transition counts
                var transitionCounts = new Dictionary<(BehaviorMode, BehaviorMode), int>();
                foreach (var (tick, from, to) in transitions)
                {
                    var key = (from, to);
                    transitionCounts.TryGetValue(key, out int c);
                    transitionCounts[key] = c + 1;
                }
                foreach (var kvp in transitionCounts.OrderByDescending(k => k.Value))
                    Console.WriteLine($"    {kvp.Key.Item1} -> {kvp.Key.Item2}: {kvp.Value}x");

                // First 20 transitions with ticks
                Console.WriteLine($"  First 20 transitions:");
                foreach (var (tick, from, to) in transitions.Take(20))
                    Console.WriteLine($"    tick={tick}: {from} -> {to}");
                Console.WriteLine();
            }

            // Top 5 most visited tiles
            if (tileVisits.ContainsKey(id))
            {
                var top5 = tileVisits[id].OrderByDescending(kv => kv.Value).Take(5);
                Console.WriteLine($"  Most visited tiles (top 5):");
                foreach (var kv in top5)
                {
                    var tile = world.GetTile(kv.Key.Item1, kv.Key.Item2);
                    Console.WriteLine($"    ({kv.Key.Item1},{kv.Key.Item2}) [{tile.Biome}]: {kv.Value} ticks");
                }
                Console.WriteLine();
            }

            // Action distribution (post-youth)
            if (actionCountsPostYouth.ContainsKey(id) && actionCountsPostYouth[id].Count > 0)
            {
                int totalTicks = actionCountsPostYouth[id].Values.Sum();
                Console.WriteLine($"  Action distribution (post-youth, {totalTicks} ticks):");
                foreach (var kv in actionCountsPostYouth[id].OrderByDescending(k => k.Value))
                {
                    float pct = 100f * kv.Value / totalTicks;
                    Console.WriteLine($"    {kv.Key,-16}: {kv.Value,6} ({pct:F1}%)");
                }
                Console.WriteLine();
            }

            // Explore log samples
            if (exploreLogSamples.ContainsKey(id) && exploreLogSamples[id].Count > 0)
            {
                Console.WriteLine($"  Explore mode samples (first {exploreLogSamples[id].Count}):");
                foreach (var line in exploreLogSamples[id])
                    Console.WriteLine(line);
                Console.WriteLine();
            }
        }

        // Stone from non-mountain
        Console.WriteLine("══════════════════════════════════════════");
        Console.WriteLine($"Stone gathered from non-mountain tiles: {stoneFromNonMountain.Count} instances");
        if (stoneFromNonMountain.Count > 0)
        {
            Console.WriteLine("  First 20:");
            foreach (var (tick, name, x, y, biome) in stoneFromNonMountain.Take(20))
                Console.WriteLine($"    tick={tick} {name} at ({x},{y}) [{biome}]");
        }
        Console.WriteLine();

        // Discovery timeline
        Console.WriteLine("══════════════════════════════════════════");
        Console.WriteLine("Discovery timeline:");
        if (sim.DiscoveryRecords.Count == 0)
            Console.WriteLine("  (none)");
        else
            foreach (var (tick, recipeId, agentName) in sim.DiscoveryRecords)
                Console.WriteLine($"  tick={tick}: {agentName} discovered {recipeId}");
    }
}
