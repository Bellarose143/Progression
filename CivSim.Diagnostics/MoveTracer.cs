using CivSim.Core;

namespace CivSim.Diagnostics;

/// <summary>
/// D20 Fix 2: Trace a specific agent's movement patterns over a tick window.
/// Tracks goal types during Move actions, trip lengths, oscillation patterns.
/// </summary>
public static class MoveTracer
{
    public static void TraceAgent(int seed, int agentIndex, int startTick, int windowSize)
    {
        var world = new World(64, 64, seed);
        var sim = new Simulation(world, seed);

        // Spawn initial agents
        for (int i = 0; i < 2; i++)
            sim.SpawnAgent();

        // Fast-forward to startTick (agents may be born during simulation)
        Console.WriteLine($"Fast-forwarding to tick {startTick}...");
        for (int t = 0; t < startTick; t++)
            sim.Tick();

        if (agentIndex >= sim.Agents.Count)
        {
            Console.WriteLine($"Error: Agent index {agentIndex} out of range. Population is {sim.Agents.Count}.");
            Console.WriteLine("Available agents:");
            for (int i = 0; i < sim.Agents.Count; i++)
                Console.WriteLine($"  [{i}] Agent {sim.Agents[i].Id} ({sim.Agents[i].Name}) alive={sim.Agents[i].IsAlive}");
            return;
        }

        var agent = sim.Agents[agentIndex];
        Console.WriteLine($"Tracing Agent {agent.Id} ({agent.Name}) on seed {seed}");
        Console.WriteLine($"Window: tick {startTick} to {startTick + windowSize}");
        Console.WriteLine();

        // Track stats
        int moveTicks = 0;
        int totalTicks = 0;
        var goalMoveTicks = new Dictionary<string, int>();
        var tripLengths = new List<int>();
        int currentTripLength = 0;
        bool wasMoving = false;
        var positions = new List<(int X, int Y, int Tick, string Goal, string Mode)>();
        var goalCompletions = new Dictionary<string, int>();
        var goalAbandons = new Dictionary<string, int>();
        string? lastGoalStr = null;
        (int X, int Y)? lastPos = null;
        int positionChanges = 0;

        // Track round-trip patterns
        var visitCounts = new Dictionary<(int, int), int>();

        for (int t = 0; t < windowSize; t++)
        {
            int tick = startTick + t;
            var goalBefore = agent.CurrentGoal;
            var modeBefore = agent.CurrentMode;
            int xBefore = agent.X, yBefore = agent.Y;

            sim.Tick();
            totalTicks++;

            if (!agent.IsAlive)
            {
                Console.WriteLine($"Agent died at tick {tick}!");
                break;
            }

            string goalStr = agent.CurrentGoal?.ToString() ?? "None";
            string modeStr = agent.CurrentMode.ToString();
            bool isMoving = agent.CurrentAction == ActionType.Move ||
                           agent.PendingAction == ActionType.Move;

            // Position change tracking
            if (agent.X != xBefore || agent.Y != yBefore)
                positionChanges++;

            // Track tile visits
            var pos = (agent.X, agent.Y);
            visitCounts.TryGetValue(pos, out int vc);
            visitCounts[pos] = vc + 1;

            if (isMoving)
            {
                moveTicks++;
                currentTripLength++;

                string goalKey = goalStr;
                goalMoveTicks.TryGetValue(goalKey, out int gmt);
                goalMoveTicks[goalKey] = gmt + 1;
            }
            else if (wasMoving)
            {
                // Trip ended
                if (currentTripLength > 0)
                    tripLengths.Add(currentTripLength);
                currentTripLength = 0;
            }

            // Goal change tracking
            if (goalStr != lastGoalStr && lastGoalStr != null)
            {
                // Was the old goal completed or abandoned?
                if (agent.CurrentGoal == null && goalBefore.HasValue)
                {
                    // Goal was cleared — could be completion or abandon
                    goalAbandons.TryGetValue(lastGoalStr, out int ga);
                    goalAbandons[lastGoalStr] = ga + 1;
                }
            }
            lastGoalStr = goalStr;

            // Sample positions periodically
            if (t % 100 == 0)
            {
                positions.Add((agent.X, agent.Y, tick, goalStr, modeStr));
            }

            wasMoving = isMoving;
            lastPos = (agent.X, agent.Y);
        }

        // If still in a trip, record it
        if (currentTripLength > 0)
            tripLengths.Add(currentTripLength);

        // Report
        Console.WriteLine($"=== Move Analysis for {agent.Name} (Agent {agent.Id}) ===");
        Console.WriteLine($"Window: tick {startTick}-{startTick + windowSize}");
        Console.WriteLine($"Total ticks: {totalTicks}");
        Console.WriteLine($"Move ticks: {moveTicks} ({100.0 * moveTicks / totalTicks:F1}%)");
        Console.WriteLine($"Position changes: {positionChanges}");
        Console.WriteLine();

        Console.WriteLine("Goal breakdown during Move:");
        foreach (var (goal, count) in goalMoveTicks.OrderByDescending(kv => kv.Value))
        {
            double pct = 100.0 * count / Math.Max(1, moveTicks);
            Console.WriteLine($"  {goal,-25}: {pct:F1}% ({count} ticks)");
        }
        Console.WriteLine();

        if (tripLengths.Count > 0)
        {
            Console.WriteLine($"Trip count: {tripLengths.Count}");
            Console.WriteLine($"Average trip length: {tripLengths.Average():F1} ticks");
            Console.WriteLine($"Min/Max trip: {tripLengths.Min()}/{tripLengths.Max()} ticks");
            Console.WriteLine($"Median trip: {tripLengths.OrderBy(x => x).ElementAt(tripLengths.Count / 2)} ticks");
        }
        Console.WriteLine();

        // Most-visited tiles (oscillation detection)
        Console.WriteLine("Most visited tiles (top 10):");
        foreach (var (tile, count) in visitCounts.OrderByDescending(kv => kv.Value).Take(10))
        {
            Console.WriteLine($"  ({tile.Item1},{tile.Item2}): {count} ticks");
        }
        Console.WriteLine();

        // Position samples
        Console.WriteLine("Position samples (every 100 ticks):");
        foreach (var (x, y, tick, goal, mode) in positions)
        {
            int distHome = agent.HomeTile.HasValue
                ? Math.Max(Math.Abs(x - agent.HomeTile.Value.X), Math.Abs(y - agent.HomeTile.Value.Y))
                : -1;
            Console.WriteLine($"  Tick {tick}: ({x},{y}) dist={distHome} goal={goal} mode={mode}");
        }
        Console.WriteLine();

        // Goal abandon/complete stats
        if (goalAbandons.Count > 0)
        {
            Console.WriteLine("Goal abandonment counts:");
            foreach (var (goal, count) in goalAbandons.OrderByDescending(kv => kv.Value))
                Console.WriteLine($"  {goal}: {count}");
        }

        Console.WriteLine($"\nFinal state: ({agent.X},{agent.Y}) hunger={agent.Hunger:F0} mode={agent.CurrentMode} goal={agent.CurrentGoal}");
        if (agent.HomeTile.HasValue)
        {
            int finalDist = Math.Max(Math.Abs(agent.X - agent.HomeTile.Value.X), Math.Abs(agent.Y - agent.HomeTile.Value.Y));
            Console.WriteLine($"Distance from home: {finalDist}");
        }
    }
}
