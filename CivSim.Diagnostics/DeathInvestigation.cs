using CivSim.Core;

namespace CivSim.Diagnostics;

/// <summary>
/// Diagnostic tool to investigate agent deaths in a specific seed run.
/// Captures per-tick data for the dying agent in the 100 ticks before death,
/// plus full context at the moment of death.
///
/// This is a READ-ONLY investigation tool -- it does not modify any simulation behavior.
/// </summary>
public class DeathInvestigation
{
    /// <summary>
    /// Snapshot of an agent's state at a given tick. Captured every tick and stored in
    /// a rolling buffer so we have the last 100 ticks when death occurs.
    /// </summary>
    private class AgentTickSnapshot
    {
        public int Tick;
        public string Name = "";
        public int AgentId;
        public int Age;
        public string AgeFormatted = "";
        public float Hunger;
        public int Health;
        public ActionType CurrentAction;
        public BehaviorMode CurrentMode;
        public int X, Y;
        public int HomeX, HomeY;
        public int DistFromHome;
        public GoalType? CurrentGoal;
        public (int X, int Y)? GoalTarget;
        public ResourceType? GoalResource;
        public int FoodInInventory;
        public int HomeFoodStorage;
        public bool IsExposed;
        public DevelopmentStage Stage;
        public float StarvationDmgAccum;
        public float ExposureDmgAccum;
        public float HealthRegenAccum;
        public int StuckCounter;
        public int MoveFailCount;
    }

    /// <summary>
    /// Snapshot of another alive agent at the moment of a death event.
    /// </summary>
    private class OtherAgentSnapshot
    {
        public string Name = "";
        public int X, Y;
        public float Hunger;
        public int Health;
        public ActionType CurrentAction;
        public BehaviorMode CurrentMode;
        public GoalType? CurrentGoal;
        public int FoodInInventory;
        public int Age;
        public string AgeFormatted = "";
        public DevelopmentStage Stage;
    }

    public static void Run(int seed = 16001, int ticks = 50000)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine("  Death Investigation Diagnostic");
        Console.WriteLine($"  Seed: {seed}, Ticks: {ticks}");
        Console.WriteLine("================================================================");
        Console.WriteLine();

        var world = new World(64, 64, seed);
        var simulation = new Simulation(world, seed);

        // Spawn 2 starting agents
        for (int i = 0; i < 2; i++)
            simulation.SpawnAgent();

        Console.WriteLine($"  Spawned {simulation.Agents.Count} agents.");
        foreach (var a in simulation.Agents)
            Console.WriteLine($"    {a.Name} (Id={a.Id}) at ({a.X},{a.Y}), Home=({a.HomeTile?.X},{a.HomeTile?.Y})");
        Console.WriteLine();

        // Rolling buffer of snapshots per agent (last 150 ticks to be safe)
        const int BufferSize = 2000;
        var rollingBuffers = new Dictionary<int, Queue<AgentTickSnapshot>>();

        // Track alive state from previous tick for death detection
        var wasAlive = new Dictionary<int, bool>();
        foreach (var agent in simulation.Agents)
        {
            wasAlive[agent.Id] = agent.IsAlive;
            rollingBuffers[agent.Id] = new Queue<AgentTickSnapshot>();
        }

        // Death records
        var deathRecords = new List<(
            int DeathTick,
            Agent DeadAgent,
            List<AgentTickSnapshot> PreDeathTrace,
            List<OtherAgentSnapshot> OtherAgentsAtDeath,
            string DeathCause,
            int HomeFoodAtDeath,
            int HomeMaterialsAtDeath,
            bool WasOnExpedition
        )>();

        int deathCount = 0;

        Console.WriteLine($"  Running {ticks} ticks...");

        for (int t = 1; t <= ticks; t++)
        {
            // Capture pre-tick snapshots for all alive agents
            foreach (var agent in simulation.Agents.Where(a => a.IsAlive))
            {
                if (!rollingBuffers.ContainsKey(agent.Id))
                    rollingBuffers[agent.Id] = new Queue<AgentTickSnapshot>();

                var snap = CaptureSnapshot(agent, t, world);
                var buffer = rollingBuffers[agent.Id];
                buffer.Enqueue(snap);
                while (buffer.Count > BufferSize)
                    buffer.Dequeue();
            }

            // Run the tick
            simulation.Tick();

            // Check for newly spawned agents (children)
            foreach (var agent in simulation.Agents)
            {
                if (!wasAlive.ContainsKey(agent.Id))
                {
                    wasAlive[agent.Id] = agent.IsAlive;
                    rollingBuffers[agent.Id] = new Queue<AgentTickSnapshot>();
                }
            }

            // Detect deaths: was alive last tick, now dead
            foreach (var agent in simulation.Agents)
            {
                if (wasAlive.TryGetValue(agent.Id, out bool prevAlive) && prevAlive && !agent.IsAlive)
                {
                    deathCount++;

                    // Gather pre-death trace from rolling buffer
                    var trace = rollingBuffers.TryGetValue(agent.Id, out var buf)
                        ? buf.ToList()
                        : new List<AgentTickSnapshot>();

                    // Gather other alive agents at this moment
                    var others = simulation.Agents
                        .Where(a => a.IsAlive && a.Id != agent.Id)
                        .Select(a => new OtherAgentSnapshot
                        {
                            Name = a.Name,
                            X = a.X,
                            Y = a.Y,
                            Hunger = a.Hunger,
                            Health = a.Health,
                            CurrentAction = a.CurrentAction,
                            CurrentMode = a.CurrentMode,
                            CurrentGoal = a.CurrentGoal,
                            FoodInInventory = a.FoodInInventory(),
                            Age = a.Age,
                            AgeFormatted = Agent.FormatTicks(a.Age),
                            Stage = a.Stage
                        })
                        .ToList();

                    // Home food at death
                    int homeFood = 0;
                    int homeMaterials = 0;
                    if (agent.HomeTile.HasValue)
                    {
                        var homeTile = world.GetTile(agent.HomeTile.Value.X, agent.HomeTile.Value.Y);
                        homeFood = homeTile.HomeTotalFood;
                        homeMaterials = homeTile.HomeTotalMaterials;
                    }

                    // Was on expedition?
                    int distFromHome = 0;
                    if (agent.HomeTile.HasValue)
                        distFromHome = ChebyshevDist(agent.X, agent.Y, agent.HomeTile.Value.X, agent.HomeTile.Value.Y);
                    bool wasOnExpedition = distFromHome > 10;

                    deathRecords.Add((
                        DeathTick: t,
                        DeadAgent: agent,
                        PreDeathTrace: trace,
                        OtherAgentsAtDeath: others,
                        DeathCause: agent.DeathCause ?? "unknown",
                        HomeFoodAtDeath: homeFood,
                        HomeMaterialsAtDeath: homeMaterials,
                        WasOnExpedition: wasOnExpedition
                    ));

                    Console.WriteLine($"  ** DEATH at tick {t}: {agent.Name} died of {agent.DeathCause} at age {Agent.FormatTicks(agent.Age)}");
                }

                wasAlive[agent.Id] = agent.IsAlive;
            }

            // Progress
            if (t % 10000 == 0)
            {
                int alive = simulation.Agents.Count(a => a.IsAlive);
                Console.WriteLine($"  Tick {t}/{ticks} -- {alive} alive, {deathCount} deaths so far");
            }
        }

        // ================================================================
        // REPORT
        // ================================================================
        Console.WriteLine();
        Console.WriteLine("================================================================");
        Console.WriteLine("  DEATH INVESTIGATION REPORT");
        Console.WriteLine($"  Seed: {seed}, Ticks: {ticks}");
        Console.WriteLine($"  Total deaths: {deathCount}");
        Console.WriteLine("================================================================");

        if (deathRecords.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("  No deaths occurred during this run. All agents survived.");
            Console.WriteLine();
            int alive = simulation.Agents.Count(a => a.IsAlive);
            Console.WriteLine($"  Final population: {alive} alive out of {simulation.Agents.Count} total");
            return;
        }

        foreach (var (deathTick, agent, trace, others, cause, homeFood, homeMaterials, wasExpedition) in deathRecords)
        {
            Console.WriteLine();
            Console.WriteLine("----------------------------------------------------------------");
            Console.WriteLine($"  DEATH: {agent.Name} (Id={agent.Id})");
            Console.WriteLine($"  Tick: {deathTick}");
            Console.WriteLine($"  Cause: {cause}");
            Console.WriteLine($"  Age at death: {Agent.FormatTicks(agent.Age)}");
            Console.WriteLine($"  Stage: {agent.Stage}");
            Console.WriteLine($"  Position at death: ({agent.X},{agent.Y})");
            Console.WriteLine($"  Home tile: ({agent.HomeTile?.X},{agent.HomeTile?.Y})");
            int distDeath = agent.HomeTile.HasValue
                ? ChebyshevDist(agent.X, agent.Y, agent.HomeTile.Value.X, agent.HomeTile.Value.Y) : -1;
            Console.WriteLine($"  Distance from home: {distDeath}");
            Console.WriteLine($"  Was on expedition (>10 tiles): {wasExpedition}");
            Console.WriteLine($"  Home food storage at death: {homeFood}");
            Console.WriteLine($"  Home material storage at death: {homeMaterials}");
            Console.WriteLine();

            // ── Pre-death trace ──
            Console.WriteLine("  ── Pre-Death Trace (state transitions + context) ──");
            Console.WriteLine($"  {"Tick",7} {"Hunger",8} {"Health",7} {"Action",-18} {"Mode",-12} {"Pos",10} {"DHm",4} {"Goal",-22} {"FdInv",6} {"HmFd",5} {"Exp",4} {"Stuck",6} {"MvFl",5}");
            Console.WriteLine($"  {"-------",7} {"--------",8} {"-------",7} {"------------------",-18} {"------------",-12} {"----------",10} {"----",4} {"----------------------",-22} {"------",6} {"-----",5} {"----",4} {"------",6} {"-----",5}");

            // Show lines where something CHANGED (state transitions) to keep output readable.
            // Also show first 10 and last 20 ticks unconditionally.
            AgentTickSnapshot? prev = null;
            int suppressedCount = 0;

            for (int idx = 0; idx < trace.Count; idx++)
            {
                var s = trace[idx];
                bool isFirst10 = idx < 10;
                bool isLast20 = idx >= trace.Count - 20;
                bool isTransition = prev != null && (
                    prev.CurrentAction != s.CurrentAction ||
                    prev.CurrentMode != s.CurrentMode ||
                    prev.CurrentGoal != s.CurrentGoal ||
                    prev.X != s.X || prev.Y != s.Y ||
                    prev.FoodInInventory != s.FoodInInventory ||
                    prev.Health != s.Health ||
                    (prev.Hunger > 0 && s.Hunger <= 0) ||
                    (prev.Hunger <= 0 && s.Hunger > 0) ||
                    prev.IsExposed != s.IsExposed ||
                    prev.StuckCounter != s.StuckCounter ||
                    prev.MoveFailCount != s.MoveFailCount
                );

                if (isFirst10 || isLast20 || isTransition)
                {
                    if (suppressedCount > 0)
                    {
                        Console.WriteLine($"          ... ({suppressedCount} identical ticks omitted) ...");
                        suppressedCount = 0;
                    }

                    string goalStr = s.CurrentGoal.HasValue
                        ? $"{s.CurrentGoal}" + (s.GoalTarget.HasValue ? $"@({s.GoalTarget.Value.X},{s.GoalTarget.Value.Y})" : "")
                        : "None";
                    if (goalStr.Length > 22) goalStr = goalStr[..22];

                    string actionStr = s.CurrentAction.ToString();
                    if (actionStr.Length > 18) actionStr = actionStr[..18];

                    string modeStr = s.CurrentMode.ToString();
                    if (modeStr.Length > 12) modeStr = modeStr[..12];

                    Console.WriteLine($"  {s.Tick,7} {s.Hunger,8:F1} {s.Health,7} {actionStr,-18} {modeStr,-12} ({s.X,3},{s.Y,3}) {s.DistFromHome,4} {goalStr,-22} {s.FoodInInventory,6} {s.HomeFoodStorage,5} {(s.IsExposed ? "YES" : "no"),4} {s.StuckCounter,6} {s.MoveFailCount,5}");
                }
                else
                {
                    suppressedCount++;
                }
                prev = s;
            }
            if (suppressedCount > 0)
                Console.WriteLine($"          ... ({suppressedCount} identical ticks omitted) ...");

            Console.WriteLine();

            // ── Other agents at death ──
            Console.WriteLine("  ── Other Agents at Time of Death ──");
            if (others.Count == 0)
            {
                Console.WriteLine("    No other agents alive.");
            }
            else
            {
                foreach (var o in others)
                {
                    Console.WriteLine($"    {o.Name} age={o.AgeFormatted} stage={o.Stage} pos=({o.X},{o.Y}) " +
                        $"hunger={o.Hunger:F1} health={o.Health} action={o.CurrentAction} mode={o.CurrentMode} " +
                        $"goal={o.CurrentGoal} food={o.FoodInInventory}");
                }
            }

            Console.WriteLine();

            // ── Key analysis ──
            Console.WriteLine("  ── Key Analysis ──");

            // Starvation analysis
            if (cause == "starvation")
            {
                // Find when hunger first hit 0
                int? firstZeroHungerTick = null;
                int? lastAteFloorTick = null;
                float maxHungerInTrace = 0;

                foreach (var s in trace)
                {
                    if (s.Hunger <= 0 && firstZeroHungerTick == null)
                        firstZeroHungerTick = s.Tick;
                    if (s.Hunger > 0)
                    {
                        firstZeroHungerTick = null; // Reset -- hunger recovered
                        lastAteFloorTick = s.Tick;
                    }
                    maxHungerInTrace = Math.Max(maxHungerInTrace, s.Hunger);
                }

                Console.WriteLine($"    Hunger hit 0 at tick: {firstZeroHungerTick?.ToString() ?? "N/A (already 0 at trace start)"}");
                Console.WriteLine($"    Last tick with hunger > 0: {lastAteFloorTick?.ToString() ?? "never in trace window"}");
                Console.WriteLine($"    Max hunger in trace window: {maxHungerInTrace:F1}");

                if (firstZeroHungerTick.HasValue)
                {
                    int ticksStarving = deathTick - firstZeroHungerTick.Value;
                    Console.WriteLine($"    Ticks spent starving before death: {ticksStarving}");
                }

                // Check if agent had food it didn't eat
                bool hadFoodAndDidntEat = trace.Any(s => s.FoodInInventory > 0 && s.Hunger < 50);
                Console.WriteLine($"    Had food in inventory while hungry (<50): {hadFoodAndDidntEat}");

                // Check if home had food
                bool homeHadFood = trace.Any(s => s.HomeFoodStorage > 0 && s.Hunger < 50);
                Console.WriteLine($"    Home had food while agent was hungry (<50): {homeHadFood}");
            }
            else if (cause == "exposure")
            {
                Console.WriteLine($"    Agent was exposed (no shelter nearby) at death.");
                bool everSheltered = trace.Any(s => !s.IsExposed);
                Console.WriteLine($"    Was ever sheltered in trace window: {everSheltered}");
            }
            else if (cause == "old age")
            {
                Console.WriteLine($"    Agent reached old age threshold.");
                Console.WriteLine($"    OldAgeThreshold={SimConfig.OldAgeThreshold} ticks ({Agent.FormatTicks(SimConfig.OldAgeThreshold)})");
                Console.WriteLine($"    GuaranteedDeathAge={SimConfig.GuaranteedDeathAge} ticks ({Agent.FormatTicks(SimConfig.GuaranteedDeathAge)})");
            }

            // Mode and action analysis
            var modeBreakdown = trace.GroupBy(s => s.CurrentMode)
                .Select(g => $"{g.Key}={g.Count()}")
                .ToList();
            Console.WriteLine($"    Mode breakdown in trace: {string.Join(", ", modeBreakdown)}");

            var actionBreakdown = trace.GroupBy(s => s.CurrentAction)
                .OrderByDescending(g => g.Count())
                .Select(g => $"{g.Key}={g.Count()}")
                .ToList();
            Console.WriteLine($"    Action breakdown in trace: {string.Join(", ", actionBreakdown)}");

            // Distance from home analysis
            int maxDist = trace.Max(s => s.DistFromHome);
            int minDist = trace.Min(s => s.DistFromHome);
            double avgDist = trace.Average(s => s.DistFromHome);
            Console.WriteLine($"    Distance from home: min={minDist}, max={maxDist}, avg={avgDist:F1}");

            Console.WriteLine();
        }

        // ── Final summary ──
        Console.WriteLine("================================================================");
        Console.WriteLine("  SUMMARY");
        Console.WriteLine("================================================================");
        Console.WriteLine($"  Total deaths: {deathRecords.Count}");
        var byCause = deathRecords.GroupBy(d => d.DeathCause);
        foreach (var g in byCause)
            Console.WriteLine($"    {g.Key}: {g.Count()}");

        int aliveEnd = simulation.Agents.Count(a => a.IsAlive);
        Console.WriteLine($"  Final population: {aliveEnd} alive out of {simulation.Agents.Count} total");
        Console.WriteLine();
        Console.WriteLine("================================================================");
        Console.WriteLine("  Diagnostic complete.");
        Console.WriteLine("================================================================");
    }

    private static AgentTickSnapshot CaptureSnapshot(Agent agent, int tick, World world)
    {
        int homeX = agent.HomeTile?.X ?? -1;
        int homeY = agent.HomeTile?.Y ?? -1;
        int distFromHome = agent.HomeTile.HasValue
            ? ChebyshevDist(agent.X, agent.Y, homeX, homeY) : -1;

        int homeFood = 0;
        if (agent.HomeTile.HasValue)
        {
            var homeTile = world.GetTile(agent.HomeTile.Value.X, agent.HomeTile.Value.Y);
            homeFood = homeTile.HomeTotalFood;
        }

        return new AgentTickSnapshot
        {
            Tick = tick,
            Name = agent.Name,
            AgentId = agent.Id,
            Age = agent.Age,
            AgeFormatted = Agent.FormatTicks(agent.Age),
            Hunger = agent.Hunger,
            Health = agent.Health,
            CurrentAction = agent.CurrentAction,
            CurrentMode = agent.CurrentMode,
            X = agent.X,
            Y = agent.Y,
            HomeX = homeX,
            HomeY = homeY,
            DistFromHome = distFromHome,
            CurrentGoal = agent.CurrentGoal,
            GoalTarget = agent.GoalTarget,
            GoalResource = agent.GoalResource,
            FoodInInventory = agent.FoodInInventory(),
            HomeFoodStorage = homeFood,
            IsExposed = agent.IsExposed,
            Stage = agent.Stage,
            StarvationDmgAccum = agent.StarvationDamageAccumulator,
            ExposureDmgAccum = agent.ExposureDamageAccumulator,
            HealthRegenAccum = agent.HealthRegenAccumulator,
            StuckCounter = agent.StuckCounter,
            MoveFailCount = agent.MoveFailCount
        };
    }

    private static int ChebyshevDist(int x1, int y1, int x2, int y2)
        => Math.Max(Math.Abs(x1 - x2), Math.Abs(y1 - y2));
}
