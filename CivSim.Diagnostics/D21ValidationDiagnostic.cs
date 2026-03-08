using CivSim.Core;

namespace CivSim.Diagnostics;

/// <summary>
/// D21 Final Validation: Runs multiple seeds and generates the report
/// required by the D21 directive — discovery timelines, action distributions,
/// opportunistic pickup counts, deaths, and population state.
/// </summary>
public static class D21ValidationDiagnostic
{
    public static void Run(int[] seeds, int tickCount = 50000)
    {
        Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
        Console.WriteLine("║         D21 Final Validation Diagnostic                 ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════╝");
        Console.WriteLine();

        foreach (int seed in seeds)
        {
            RunSeed(seed, tickCount);
            Console.WriteLine();
        }
    }

    private static void RunSeed(int seed, int tickCount)
    {
        Console.WriteLine($"═══ SEED {seed} — {tickCount} ticks ═══");

        var world = new World(64, 64, seed);
        var sim = new Simulation(world, seed);
        for (int i = 0; i < 2; i++) sim.SpawnAgent();

        // Track opportunistic pickups via TraceCallback
        int pickupStone = 0, pickupOre = 0, pickupWood = 0;
        int totalPickups = 0;
        int firstPickupTick = -1;

        sim.TraceCallback = msg =>
        {
            if (msg.Contains("picked up"))
            {
                totalPickups++;
                if (firstPickupTick < 0) firstPickupTick = sim.CurrentTick;
                if (msg.Contains("Stone")) pickupStone++;
                else if (msg.Contains("Ore")) pickupOre++;
                else if (msg.Contains("Wood")) pickupWood++;
            }
        };

        // Track per-agent action counts
        var actionCounts = new Dictionary<int, Dictionary<ActionType, int>>();

        for (int t = 0; t < tickCount; t++)
        {
            sim.Tick();

            // Record each agent's current action every tick
            foreach (var agent in sim.Agents)
            {
                if (!agent.IsAlive) continue;
                if (!actionCounts.ContainsKey(agent.Id))
                    actionCounts[agent.Id] = new Dictionary<ActionType, int>();

                var dict = actionCounts[agent.Id];
                if (!dict.ContainsKey(agent.CurrentAction))
                    dict[agent.CurrentAction] = 0;
                dict[agent.CurrentAction]++;
            }
        }

        // ── Discovery Timeline ──
        Console.WriteLine("\n  Discovery Timeline:");
        if (sim.DiscoveryRecords.Count == 0)
        {
            Console.WriteLine("    (none)");
        }
        else
        {
            foreach (var (tick, recipeId, agentName) in sim.DiscoveryRecords)
            {
                int day = SimConfig.TicksToSimDays(tick);
                Console.WriteLine($"    Day {day,3} (tick {tick,5}): {recipeId,-25} by {agentName}");
            }
        }

        bool hasStoneKnife = sim.DiscoveryRecords.Any(d => d.RecipeId == "stone_knife");
        bool hasFire = sim.DiscoveryRecords.Any(d => d.RecipeId == "fire");
        int stoneKnifeDay = hasStoneKnife ? SimConfig.TicksToSimDays(sim.DiscoveryRecords.First(d => d.RecipeId == "stone_knife").Tick) : -1;
        int fireDay = hasFire ? SimConfig.TicksToSimDays(sim.DiscoveryRecords.First(d => d.RecipeId == "fire").Tick) : -1;

        Console.WriteLine($"\n  stone_knife: {(hasStoneKnife ? $"YES (day {stoneKnifeDay})" : "NO")}");
        Console.WriteLine($"  fire:        {(hasFire ? $"YES (day {fireDay})" : "NO")}");
        Console.WriteLine($"  Total discoveries: {sim.DiscoveryRecords.Select(d => d.RecipeId).Distinct().Count()}");

        // ── Per-Agent Action Distributions ──
        Console.WriteLine("\n  Per-Agent Action Distributions (adults only):");
        foreach (var agent in sim.Agents)
        {
            if (agent.Stage == DevelopmentStage.Infant) continue; // skip infants
            if (!actionCounts.ContainsKey(agent.Id)) continue;

            var dict = actionCounts[agent.Id];
            int totalActions = dict.Values.Sum();
            if (totalActions == 0) continue;

            string status = agent.IsAlive ? "Alive" : "Dead";
            Console.Write($"    {agent.Name} ({status}, age {agent.FormatAge()}): ");

            var sorted = dict.OrderByDescending(kv => kv.Value).ToList();
            var parts = sorted.Select(kv =>
                $"{kv.Key}={kv.Value * 100.0 / totalActions:F1}%");
            Console.WriteLine(string.Join(", ", parts));

            // Flag Idle% > 20%
            int idleCount = dict.GetValueOrDefault(ActionType.Idle, 0);
            double idlePct = idleCount * 100.0 / totalActions;
            if (idlePct > 20.0)
                Console.WriteLine($"      *** WARNING: Idle% = {idlePct:F1}% (above 20% threshold)");
            else if (idlePct > 15.0)
                Console.WriteLine($"      * NOTE: Idle% = {idlePct:F1}% (above 15%, within tolerance)");
        }

        // ── Opportunistic Pickup Events ──
        Console.WriteLine($"\n  Opportunistic Pickups:");
        Console.WriteLine($"    Total: {totalPickups} (Stone: {pickupStone}, Ore: {pickupOre}, Wood: {pickupWood})");
        if (firstPickupTick >= 0)
            Console.WriteLine($"    First pickup: tick {firstPickupTick} (day {SimConfig.TicksToSimDays(firstPickupTick)})");
        if (totalPickups == 0)
            Console.WriteLine("    *** WARNING: Zero pickups across entire run!");

        // ── Deaths & Anomalies ──
        int deaths = sim.Agents.Count(a => !a.IsAlive);
        Console.WriteLine($"\n  Deaths: {deaths}");
        foreach (var agent in sim.Agents.Where(a => !a.IsAlive))
        {
            Console.WriteLine($"    {agent.Name} — died at age {agent.FormatAge()}");
        }

        // ── Final Population & Settlement State ──
        int alive = sim.Agents.Count(a => a.IsAlive);
        Console.WriteLine($"\n  Final Population: {alive} alive, {sim.Agents.Count} total ever");
        Console.WriteLine($"  Peak Population: {sim.PeakPopulation} (tick {sim.PeakPopulationTick})");

        // Check for Stone in inventories
        bool anyStoneInInventory = sim.Agents.Any(a => a.IsAlive &&
            a.Inventory.GetValueOrDefault(ResourceType.Stone, 0) > 0);
        Console.WriteLine($"  Stone in alive agent inventories: {(anyStoneInInventory ? "YES" : "NO")}");

        // Write run summary file
        string solutionRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string summaryPath = Path.Combine(solutionRoot, "diagnostics", $"d21_summary_seed_{seed}.txt");
        RunSummaryWriter.Write(sim, summaryPath);
        Console.WriteLine($"  Summary: {summaryPath}");
    }
}
