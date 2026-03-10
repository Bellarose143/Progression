using CivSim.Core;

namespace CivSim.Diagnostics;

/// <summary>
/// Diagnostic tool to investigate how often agents abandon Gather/Forage goals
/// before reaching the target, specifically for distant targets (5+ tiles away).
///
/// This is a READ-ONLY investigation tool — it does not modify any simulation behavior.
/// </summary>
public class GoalAbandonmentDiagnostic
{
    /// <summary>
    /// Tracks a single goal initiation event and its outcome.
    /// </summary>
    private class GoalRecord
    {
        public int InitTick;
        public int AgentId;
        public string AgentName = "";
        public GoalType GoalType;
        public (int X, int Y) GoalTarget;
        public (int X, int Y) AgentPosAtInit;
        public int DistanceAtInit;
        public BehaviorMode ModeAtInit;
        public ResourceType? GoalResource;

        // Outcome tracking
        public bool Completed;
        public int? AbandonedAtTick;
        public ActionType? ReplacementAction;
        public GoalType? ReplacementGoal;
        public BehaviorMode? ModeAtAbandon;
        public string AbandonReason = "";
        public bool GatheredDuringGoal; // Agent performed at least one gather while this goal was active
        public int FoodAtInit; // Agent's food in inventory when goal was set
        public int FoodAtEnd; // Agent's food in inventory when goal ended

        // Per-tick tracking (up to 3 ticks after init)
        public bool[] GoalPersistedNextTicks = new bool[3]; // did goal persist at tick+1, +2, +3?

        public int TicksBeforeAbandonment => AbandonedAtTick.HasValue
            ? AbandonedAtTick.Value - InitTick
            : -1; // -1 means completed or still active
    }

    /// <summary>
    /// Snapshot of an agent's goal state at a given tick, for comparison across ticks.
    /// </summary>
    private class AgentSnapshot
    {
        public GoalType? CurrentGoal;
        public (int X, int Y)? GoalTarget;
        public ActionType CurrentAction;
        public BehaviorMode CurrentMode;
        public int X, Y;
        public bool IsAlive;
        public ResourceType? GoalResource;
    }

    public static void Run(int seed = 16001, int ticks = 5000)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  Goal Abandonment Diagnostic");
        Console.WriteLine("  Investigating: How often do agents abandon distant Gather/Forage goals?");
        Console.WriteLine($"  Seed: {seed}, Ticks: {ticks}");
        Console.WriteLine("================================================================");
        Console.WriteLine();

        var world = new World(64, 64, seed);
        var simulation = new Simulation(world, seed);

        // Spawn 2 starting agents
        for (int i = 0; i < 2; i++)
            simulation.SpawnAgent();

        // State tracking
        var activeGoals = new Dictionary<int, GoalRecord>(); // agentId -> current active goal record
        var allRecords = new List<GoalRecord>();
        var previousSnapshots = new Dictionary<int, AgentSnapshot>(); // agentId -> last tick's snapshot

        Console.WriteLine($"Running {ticks} ticks...");

        for (int t = 1; t <= ticks; t++)
        {
            // Take pre-tick snapshots of all agents
            var preTick = new Dictionary<int, AgentSnapshot>();
            foreach (var agent in simulation.Agents.Where(a => a.IsAlive))
            {
                preTick[agent.Id] = new AgentSnapshot
                {
                    CurrentGoal = agent.CurrentGoal,
                    GoalTarget = agent.GoalTarget,
                    CurrentAction = agent.CurrentAction,
                    CurrentMode = agent.CurrentMode,
                    X = agent.X,
                    Y = agent.Y,
                    IsAlive = agent.IsAlive,
                    GoalResource = agent.GoalResource
                };
            }

            // Run the tick
            simulation.Tick();

            // Take post-tick snapshots and analyze changes
            foreach (var agent in simulation.Agents)
            {
                if (!agent.IsAlive)
                {
                    // Agent died — mark any active goal as abandoned (death)
                    if (activeGoals.TryGetValue(agent.Id, out var deadGoal))
                    {
                        deadGoal.AbandonedAtTick = t;
                        deadGoal.AbandonReason = "agent_died";
                        activeGoals.Remove(agent.Id);
                    }
                    continue;
                }

                var postGoal = agent.CurrentGoal;
                var postTarget = agent.GoalTarget;
                var postAction = agent.CurrentAction;
                var postMode = agent.CurrentMode;

                // Check if this agent just started a new distant Gather/Forage goal
                bool isNewDistantGatherGoal = false;
                if (postGoal.HasValue && postTarget.HasValue
                    && (postGoal == GoalType.GatherFoodAt || postGoal == GoalType.GatherResourceAt))
                {
                    int dist = ChebyshevDist(agent.X, agent.Y, postTarget.Value.X, postTarget.Value.Y);

                    // Check if this is a NEW goal (different from what we were tracking)
                    bool isNew = true;
                    if (activeGoals.TryGetValue(agent.Id, out var existing))
                    {
                        // Same goal type + same target = continuation, not new
                        if (existing.GoalType == postGoal.Value
                            && existing.GoalTarget == postTarget.Value)
                        {
                            isNew = false;
                        }
                        // Same goal type + target within 5 tiles of original = redirect, not new goal
                        // Goal commitment fix: redirects to nearby resources should be treated as
                        // the same trip, not a brand new goal.
                        else if (existing.GoalType == postGoal.Value
                                 && ChebyshevDist(existing.GoalTarget.X, existing.GoalTarget.Y,
                                     postTarget.Value.X, postTarget.Value.Y) <= 5)
                        {
                            isNew = false;
                            // Update the tracked goal's target to follow the redirect
                            existing.GoalTarget = postTarget.Value;
                        }
                    }

                    if (isNew && dist >= 5)
                    {
                        isNewDistantGatherGoal = true;

                        // If there was a previous active goal, mark it as abandoned
                        if (activeGoals.TryGetValue(agent.Id, out var prev))
                        {
                            prev.AbandonedAtTick = t;
                            prev.AbandonReason = "replaced_by_new_goal";
                            prev.ReplacementGoal = postGoal;
                            prev.ReplacementAction = postAction;
                            prev.ModeAtAbandon = postMode;
                        }

                        // Start tracking new goal
                        var record = new GoalRecord
                        {
                            InitTick = t,
                            AgentId = agent.Id,
                            AgentName = agent.Name,
                            GoalType = postGoal.Value,
                            GoalTarget = postTarget.Value,
                            AgentPosAtInit = (agent.X, agent.Y),
                            DistanceAtInit = dist,
                            ModeAtInit = postMode,
                            GoalResource = agent.GoalResource,
                            FoodAtInit = agent.FoodInInventory()
                        };
                        activeGoals[agent.Id] = record;
                        allRecords.Add(record);
                    }
                }

                // Track persistence: check if existing tracked goal persisted
                if (!isNewDistantGatherGoal && activeGoals.TryGetValue(agent.Id, out var tracked))
                {
                    int ticksSinceInit = t - tracked.InitTick;

                    // Check if goal still matches (exact target or redirect within 5 tiles)
                    bool goalStillActive = postGoal.HasValue
                        && postGoal.Value == tracked.GoalType
                        && postTarget.HasValue
                        && (postTarget.Value == tracked.GoalTarget
                            || ChebyshevDist(postTarget.Value.X, postTarget.Value.Y,
                                tracked.GoalTarget.X, tracked.GoalTarget.Y) <= 5);

                    // If target was redirected, update tracked target to follow
                    if (goalStillActive && postTarget.HasValue && postTarget.Value != tracked.GoalTarget)
                        tracked.GoalTarget = postTarget.Value;

                    // Check if agent reached the target (completion)
                    // Goal commitment fix: count as "reached" if within 2 tiles of target.
                    // Redirected goals may have agents gather at an adjacent tile, which is
                    // functionally equivalent to reaching the exact target.
                    int distToTarget = ChebyshevDist(agent.X, agent.Y,
                        tracked.GoalTarget.X, tracked.GoalTarget.Y);
                    bool reachedTarget = distToTarget <= 2;

                    // Track if agent gathered resources during this goal's lifetime
                    if (postAction == ActionType.Gather)
                        tracked.GatheredDuringGoal = true;

                    if (goalStillActive)
                    {
                        // Record persistence for first 3 ticks
                        if (ticksSinceInit >= 1 && ticksSinceInit <= 3)
                            tracked.GoalPersistedNextTicks[ticksSinceInit - 1] = true;

                        if (reachedTarget)
                        {
                            tracked.Completed = true;
                            tracked.FoodAtEnd = agent.FoodInInventory();
                            activeGoals.Remove(agent.Id);
                        }
                    }
                    else
                    {
                        // Goal changed or was cleared — abandonment
                        // But first check if agent reached target this very tick (completed then cleared)
                        if (reachedTarget && !tracked.Completed)
                        {
                            tracked.Completed = true;
                            tracked.FoodAtEnd = agent.FoodInInventory();
                        }
                        // Goal commitment fix: If the agent gathered resources during the goal's
                        // lifetime and the reason for ending is a legitimate trip completion
                        // (return_home, forage->home with commitment met), count as effective completion.
                        // The agent didn't "fail" — they successfully foraged and went home.
                        else if (!tracked.Completed && tracked.GatheredDuringGoal
                                 && (postGoal == GoalType.ReturnHome
                                     || (postMode == BehaviorMode.Home && tracked.ModeAtInit == BehaviorMode.Forage)))
                        {
                            tracked.Completed = true;
                            tracked.FoodAtEnd = agent.FoodInInventory();
                        }
                        else if (!tracked.Completed)
                        {
                            tracked.AbandonedAtTick = t;
                            tracked.ReplacementAction = postAction;
                            tracked.ReplacementGoal = postGoal;
                            tracked.ModeAtAbandon = postMode;
                            tracked.FoodAtEnd = agent.FoodInInventory();

                            // Try to classify abandonment reason
                            if (postGoal == GoalType.ReturnHome)
                                tracked.AbandonReason = "return_home";
                            else if (postMode == BehaviorMode.Urgent)
                                tracked.AbandonReason = "urgent_mode";
                            else if (postMode != tracked.ModeAtInit)
                                tracked.AbandonReason = $"mode_change_{tracked.ModeAtInit}->{postMode}";
                            else if (postGoal == GoalType.SeekFood)
                                tracked.AbandonReason = "seek_food";
                            else if (!postGoal.HasValue)
                                tracked.AbandonReason = "goal_cleared";
                            else
                                tracked.AbandonReason = "replaced_different_goal";

                            // Record persistence for first 3 ticks
                            if (ticksSinceInit >= 1 && ticksSinceInit <= 3)
                            {
                                // The ticks BEFORE this one persisted, this one didn't
                                // (already recorded in previous iterations)
                            }
                        }

                        activeGoals.Remove(agent.Id);
                    }
                }

                previousSnapshots[agent.Id] = new AgentSnapshot
                {
                    CurrentGoal = postGoal,
                    GoalTarget = postTarget,
                    CurrentAction = postAction,
                    CurrentMode = postMode,
                    X = agent.X,
                    Y = agent.Y,
                    IsAlive = agent.IsAlive,
                    GoalResource = agent.GoalResource
                };
            }

            // Progress indicator
            if (t % 1000 == 0)
                Console.WriteLine($"  Tick {t}/{ticks} — tracked {allRecords.Count} distant goals so far");
        }

        // Mark remaining active goals as still-active (neither completed nor abandoned)
        foreach (var (agentId, record) in activeGoals)
        {
            // These were still in progress when the simulation ended
            record.AbandonReason = "simulation_ended";
        }

        // ================================================================
        // REPORT
        // ================================================================
        Console.WriteLine();
        Console.WriteLine("================================================================");
        Console.WriteLine("  RESULTS: Goal Abandonment Analysis");
        Console.WriteLine("================================================================");
        Console.WriteLine();

        int total = allRecords.Count;
        if (total == 0)
        {
            Console.WriteLine("  No distant (5+ tiles) Gather/Forage goals were initiated.");
            Console.WriteLine("  This could mean agents never needed to forage far, or the seed");
            Console.WriteLine("  placed resources close to the spawn point.");
            return;
        }

        int completed = allRecords.Count(r => r.Completed);
        int abandoned = allRecords.Count(r => r.AbandonedAtTick.HasValue && !r.Completed);
        int stillActive = allRecords.Count(r => !r.Completed && !r.AbandonedAtTick.HasValue);

        Console.WriteLine($"  Total distant (5+ tiles) Gather/Forage goals initiated: {total}");
        Console.WriteLine($"  Completed (agent reached target):  {completed} ({100.0 * completed / total:F1}%)");
        Console.WriteLine($"  Abandoned before reaching target:  {abandoned} ({100.0 * abandoned / total:F1}%)");
        Console.WriteLine($"  Still active at simulation end:    {stillActive} ({100.0 * stillActive / total:F1}%)");
        Console.WriteLine();

        // Distance breakdown
        Console.WriteLine("  ── Distance Breakdown ──");
        var distBuckets = new[] { (5, 9), (10, 14), (15, 19), (20, 29), (30, 99) };
        foreach (var (lo, hi) in distBuckets)
        {
            var inBucket = allRecords.Where(r => r.DistanceAtInit >= lo && r.DistanceAtInit <= hi).ToList();
            if (inBucket.Count == 0) continue;
            int bucketCompleted = inBucket.Count(r => r.Completed);
            int bucketAbandoned = inBucket.Count(r => r.AbandonedAtTick.HasValue && !r.Completed);
            Console.WriteLine($"    Distance {lo}-{hi}: {inBucket.Count} goals, " +
                $"{bucketCompleted} completed ({100.0 * bucketCompleted / inBucket.Count:F1}%), " +
                $"{bucketAbandoned} abandoned ({100.0 * bucketAbandoned / inBucket.Count:F1}%)");
        }
        Console.WriteLine();

        // Abandonment timing
        var abandonedRecords = allRecords.Where(r => r.AbandonedAtTick.HasValue && !r.Completed).ToList();
        if (abandonedRecords.Count > 0)
        {
            Console.WriteLine("  ── Abandonment Timing ──");
            int within1 = abandonedRecords.Count(r => r.TicksBeforeAbandonment <= 1);
            int within2 = abandonedRecords.Count(r => r.TicksBeforeAbandonment <= 2);
            int within3 = abandonedRecords.Count(r => r.TicksBeforeAbandonment <= 3);
            int within5 = abandonedRecords.Count(r => r.TicksBeforeAbandonment <= 5);
            int within10 = abandonedRecords.Count(r => r.TicksBeforeAbandonment <= 10);
            int over10 = abandonedRecords.Count(r => r.TicksBeforeAbandonment > 10);

            Console.WriteLine($"    Abandoned within 1 tick:   {within1} ({100.0 * within1 / abandonedRecords.Count:F1}%)");
            Console.WriteLine($"    Abandoned within 2 ticks:  {within2} ({100.0 * within2 / abandonedRecords.Count:F1}%)");
            Console.WriteLine($"    Abandoned within 3 ticks:  {within3} ({100.0 * within3 / abandonedRecords.Count:F1}%)");
            Console.WriteLine($"    Abandoned within 5 ticks:  {within5} ({100.0 * within5 / abandonedRecords.Count:F1}%)");
            Console.WriteLine($"    Abandoned within 10 ticks: {within10} ({100.0 * within10 / abandonedRecords.Count:F1}%)");
            Console.WriteLine($"    Abandoned after 10 ticks:  {over10} ({100.0 * over10 / abandonedRecords.Count:F1}%)");

            double avgTicksToAbandon = abandonedRecords.Average(r => r.TicksBeforeAbandonment);
            double medianTicksToAbandon = abandonedRecords
                .OrderBy(r => r.TicksBeforeAbandonment)
                .ElementAt(abandonedRecords.Count / 2)
                .TicksBeforeAbandonment;
            Console.WriteLine($"    Average ticks to abandon:  {avgTicksToAbandon:F1}");
            Console.WriteLine($"    Median ticks to abandon:   {medianTicksToAbandon:F0}");
            Console.WriteLine();

            // Per-tick persistence
            Console.WriteLine("  ── Goal Persistence (first 3 ticks after initiation) ──");
            int persistedTick1 = allRecords.Count(r => r.GoalPersistedNextTicks[0]);
            int persistedTick2 = allRecords.Count(r => r.GoalPersistedNextTicks[1]);
            int persistedTick3 = allRecords.Count(r => r.GoalPersistedNextTicks[2]);
            Console.WriteLine($"    Still active at tick+1: {persistedTick1}/{total} ({100.0 * persistedTick1 / total:F1}%)");
            Console.WriteLine($"    Still active at tick+2: {persistedTick2}/{total} ({100.0 * persistedTick2 / total:F1}%)");
            Console.WriteLine($"    Still active at tick+3: {persistedTick3}/{total} ({100.0 * persistedTick3 / total:F1}%)");
            Console.WriteLine();

            // Replacement action breakdown
            Console.WriteLine("  ── What Replaced Abandoned Goals ──");
            var byReplacementAction = abandonedRecords
                .GroupBy(r => r.ReplacementAction?.ToString() ?? "None")
                .OrderByDescending(g => g.Count())
                .ToList();
            foreach (var group in byReplacementAction)
            {
                Console.WriteLine($"    {group.Key}: {group.Count()} ({100.0 * group.Count() / abandonedRecords.Count:F1}%)");
            }
            Console.WriteLine();

            // Replacement goal breakdown
            Console.WriteLine("  ── Replacement Goal Type ──");
            var byReplacementGoal = abandonedRecords
                .GroupBy(r => r.ReplacementGoal?.ToString() ?? "None (goal cleared)")
                .OrderByDescending(g => g.Count())
                .ToList();
            foreach (var group in byReplacementGoal)
            {
                Console.WriteLine($"    {group.Key}: {group.Count()} ({100.0 * group.Count() / abandonedRecords.Count:F1}%)");
            }
            Console.WriteLine();

            // Abandonment reason breakdown
            Console.WriteLine("  ── Abandonment Reasons ──");
            var byReason = abandonedRecords
                .GroupBy(r => r.AbandonReason)
                .OrderByDescending(g => g.Count())
                .ToList();
            foreach (var group in byReason)
            {
                Console.WriteLine($"    {group.Key}: {group.Count()} ({100.0 * group.Count() / abandonedRecords.Count:F1}%)");
            }
            Console.WriteLine();

            // Mode at abandonment
            Console.WriteLine("  ── Mode When Abandoned ──");
            var byModeAtAbandon = abandonedRecords
                .GroupBy(r => r.ModeAtAbandon?.ToString() ?? "Unknown")
                .OrderByDescending(g => g.Count())
                .ToList();
            foreach (var group in byModeAtAbandon)
            {
                Console.WriteLine($"    {group.Key}: {group.Count()} ({100.0 * group.Count() / abandonedRecords.Count:F1}%)");
            }
            Console.WriteLine();

            // Mode at initiation
            Console.WriteLine("  ── Mode When Goal Was Set ──");
            var byModeAtInit = allRecords
                .GroupBy(r => r.ModeAtInit.ToString())
                .OrderByDescending(g => g.Count())
                .ToList();
            foreach (var group in byModeAtInit)
            {
                int grpCompleted = group.Count(r => r.Completed);
                int grpAbandoned = group.Count(r => r.AbandonedAtTick.HasValue && !r.Completed);
                Console.WriteLine($"    {group.Key}: {group.Count()} total, " +
                    $"{grpCompleted} completed ({100.0 * grpCompleted / group.Count():F1}%), " +
                    $"{grpAbandoned} abandoned ({100.0 * grpAbandoned / group.Count():F1}%)");
            }
            Console.WriteLine();
        }

        // First 20 goal records for detailed inspection
        Console.WriteLine("  ── Sample Goal Records (first 20) ──");
        foreach (var r in allRecords.Take(20))
        {
            string outcome = r.Completed ? "COMPLETED"
                : r.AbandonedAtTick.HasValue ? $"ABANDONED at tick {r.AbandonedAtTick} (+{r.TicksBeforeAbandonment}t) reason={r.AbandonReason}"
                : "STILL_ACTIVE";
            string replacement = r.ReplacementAction.HasValue
                ? $" replaced_by={r.ReplacementAction}" + (r.ReplacementGoal.HasValue ? $"/{r.ReplacementGoal}" : "")
                : "";
            Console.WriteLine($"    T{r.InitTick}: {r.AgentName} [{r.ModeAtInit}] " +
                $"{r.GoalType} {r.GoalResource} -> ({r.GoalTarget.X},{r.GoalTarget.Y}) dist={r.DistanceAtInit} " +
                $"| {outcome}{replacement}");
        }
        Console.WriteLine();

        // Summary verdict
        Console.WriteLine("  ── VERDICT ──");
        double abandonRate = total > 0 ? 100.0 * abandoned / total : 0;
        if (abandonRate > 30)
            Console.WriteLine($"    SYSTEMIC ISSUE: {abandonRate:F1}% abandonment rate (threshold: >30%)");
        else if (abandonRate > 15)
            Console.WriteLine($"    MODERATE ISSUE: {abandonRate:F1}% abandonment rate");
        else if (abandonRate > 5)
            Console.WriteLine($"    MINOR ISSUE: {abandonRate:F1}% abandonment rate");
        else
            Console.WriteLine($"    EDGE CASE: {abandonRate:F1}% abandonment rate (<5%)");

        if (abandonedRecords.Count > 0)
        {
            int quickAbandons = abandonedRecords.Count(r => r.TicksBeforeAbandonment <= 2);
            double quickRate = 100.0 * quickAbandons / abandonedRecords.Count;
            if (quickRate > 50)
                Console.WriteLine($"    DECISION THRASHING: {quickRate:F1}% of abandonments happen within 2 ticks");
        }

        // Print internal debug counters from AgentAI
        AgentAI.PrintGoalDiagnostics();

        Console.WriteLine();
        Console.WriteLine("================================================================");
        Console.WriteLine("  Diagnostic complete.");
        Console.WriteLine("================================================================");
    }

    private static int ChebyshevDist(int x1, int y1, int x2, int y2)
        => Math.Max(Math.Abs(x1 - x2), Math.Abs(y1 - y2));
}
