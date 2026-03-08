using CivSim.Core;

namespace CivSim.Diagnostics;

/// <summary>
/// One-off investigation: trace agent deaths for seeds 1337 and 16001.
/// </summary>
public static class DeathTraceInvestigation
{
    public record TickSnapshot(
        int Tick, float Hunger, float Health, ActionType Action, BehaviorMode Mode,
        int X, int Y, Dictionary<ResourceType, int> Inventory, bool Alive);

    public static void Run()
    {
        // Quick death inventory check across all 5 seeds
        Console.WriteLine("═══ DEATH INVENTORY CHECK — All 5 Seeds ═══\n");
        QuickDeathCheck(42);
        QuickDeathCheck(1337);
        QuickDeathCheck(16001);
        QuickDeathCheck(55555);
        QuickDeathCheck(99999);
        return;

        Console.WriteLine("═══ SEED 1337 — Death Investigation ═══\n");
        TraceSeed1337();
        // Reproduce the D21 validation agent ID offset by running seeds 42, 1337 first
        // D21 validation order was: 42, 1337, 16001, 55555, 99999
        Console.WriteLine("═══ Pre-running seeds 42, 1337 to match D21 validation Agent._nextId offset ═══");
        foreach (int preSeed in new[] { 42 })
        {
            var pw = new World(64, 64, preSeed);
            var ps = new Simulation(pw, preSeed);
            for (int i = 0; i < 2; i++) ps.SpawnAgent();
            for (int t = 0; t < 50000; t++) ps.Tick();
            Console.WriteLine($"  Seed {preSeed} done (agents created through id offsets)");
        }

        Console.WriteLine("\n\n═══ SEED 16001 — Lily Death Investigation (with ID offset) ═══\n");
        TraceSeed16001Lily();
    }

    private static void QuickDeathCheck(int seed)
    {
        var world = new World(64, 64, seed);
        var sim = new Simulation(world, seed);
        for (int i = 0; i < 2; i++) sim.SpawnAgent();

        // Track last-alive snapshots per agent
        var lastAlive = new Dictionary<int, TickSnapshot>();
        int pickupCount = 0;
        sim.TraceCallback = msg => { if (msg.Contains("picked up")) pickupCount++; };

        for (int t = 0; t < 50000; t++)
        {
            sim.Tick();
            foreach (var agent in sim.Agents)
            {
                if (agent.IsAlive)
                {
                    lastAlive[agent.Id] = new TickSnapshot(
                        sim.CurrentTick, agent.Hunger, agent.Health,
                        agent.CurrentAction, agent.CurrentMode,
                        agent.X, agent.Y,
                        new Dictionary<ResourceType, int>(agent.Inventory), true);
                }
            }
        }

        var dead = sim.Agents.Where(a => !a.IsAlive).ToList();
        Console.WriteLine($"  Seed {seed}: {dead.Count} deaths, {pickupCount} pickups, {sim.Agents.Count(a => a.IsAlive)} alive");
        foreach (var agent in dead)
        {
            if (lastAlive.TryGetValue(agent.Id, out var snap))
            {
                int nonFood = snap.Inventory.GetValueOrDefault(ResourceType.Stone, 0)
                            + snap.Inventory.GetValueOrDefault(ResourceType.Ore, 0)
                            + snap.Inventory.GetValueOrDefault(ResourceType.Wood, 0)
                            + snap.Inventory.GetValueOrDefault(ResourceType.Hide, 0)
                            + snap.Inventory.GetValueOrDefault(ResourceType.Bone, 0);
                int food = snap.Inventory.GetValueOrDefault(ResourceType.Berries, 0)
                         + snap.Inventory.GetValueOrDefault(ResourceType.Grain, 0)
                         + snap.Inventory.GetValueOrDefault(ResourceType.Meat, 0)
                         + snap.Inventory.GetValueOrDefault(ResourceType.Fish, 0);
                Console.WriteLine($"    {agent.Name} died tick {snap.Tick}: Mode={snap.Mode}, Action={snap.Action}, " +
                    $"Hunger={snap.Hunger:F1}, HP={snap.Health:F0}, " +
                    $"Inv={FormatInv(snap.Inventory)}, Food:{food} NonFood:{nonFood} ({food + nonFood}/20)");
            }
        }
        Console.WriteLine();
    }

    private static void TraceSeed1337()
    {
        var world = new World(64, 64, 1337);
        var sim = new Simulation(world, 1337);
        for (int i = 0; i < 2; i++) sim.SpawnAgent();

        // Find Alexander and Aurora
        var alexander = sim.Agents.FirstOrDefault(a => a.Name == "Alexander");
        var aurora = sim.Agents.FirstOrDefault(a => a.Name == "Aurora");

        if (alexander == null || aurora == null)
        {
            // Names may differ due to RNG shift — use agent indices
            alexander = sim.Agents[0];
            aurora = sim.Agents[1];
            Console.WriteLine($"  Agent 0: {alexander.Name}, Agent 1: {aurora.Name}");
        }
        else
        {
            Console.WriteLine($"  Found Alexander (id={alexander.Id}) and Aurora (id={aurora.Id})");
        }

        // Track pickups per agent
        var pickups = new Dictionary<int, List<(int tick, string resource)>>();
        pickups[alexander.Id] = new();
        pickups[aurora.Id] = new();

        sim.TraceCallback = msg =>
        {
            if (msg.Contains("picked up"))
            {
                if (msg.Contains($"Agent {alexander.Id}"))
                    pickups[alexander.Id].Add((sim.CurrentTick, msg));
                else if (msg.Contains($"Agent {aurora.Id}"))
                    pickups[aurora.Id].Add((sim.CurrentTick, msg));
            }
        };

        // Ring buffers for last 200 ticks
        var alexSnapshots = new Queue<TickSnapshot>();
        var auroraSnapshots = new Queue<TickSnapshot>();
        int? alexDeathTick = null;
        int? auroraDeathTick = null;

        for (int t = 0; t < 50000; t++)
        {
            sim.Tick();

            // Snapshot Alexander
            if (alexander.IsAlive || alexDeathTick == null)
            {
                var snap = new TickSnapshot(
                    sim.CurrentTick, alexander.Hunger, alexander.Health,
                    alexander.CurrentAction, alexander.CurrentMode,
                    alexander.X, alexander.Y,
                    new Dictionary<ResourceType, int>(alexander.Inventory),
                    alexander.IsAlive);
                alexSnapshots.Enqueue(snap);
                if (alexSnapshots.Count > 200) alexSnapshots.Dequeue();

                if (!alexander.IsAlive && alexDeathTick == null)
                    alexDeathTick = sim.CurrentTick;
            }

            // Snapshot Aurora
            if (aurora.IsAlive || auroraDeathTick == null)
            {
                var snap = new TickSnapshot(
                    sim.CurrentTick, aurora.Hunger, aurora.Health,
                    aurora.CurrentAction, aurora.CurrentMode,
                    aurora.X, aurora.Y,
                    new Dictionary<ResourceType, int>(aurora.Inventory),
                    aurora.IsAlive);
                auroraSnapshots.Enqueue(snap);
                if (auroraSnapshots.Count > 200) auroraSnapshots.Dequeue();

                if (!aurora.IsAlive && auroraDeathTick == null)
                    auroraDeathTick = sim.CurrentTick;
            }
        }

        // Total pickups
        Console.WriteLine($"\n  Total opportunistic pickups — {alexander.Name}: {pickups[alexander.Id].Count}, {aurora.Name}: {pickups[aurora.Id].Count}");
        Console.WriteLine($"  {alexander.Name} death tick: {alexDeathTick?.ToString() ?? "survived"}");
        Console.WriteLine($"  {aurora.Name} death tick: {auroraDeathTick?.ToString() ?? "survived"}");

        // Print Alexander's last 200 ticks
        if (alexDeathTick != null)
        {
            Console.WriteLine($"\n  ── {alexander.Name} — Last {alexSnapshots.Count} ticks before death ──");
            PrintSnapshots(alexSnapshots, pickups[alexander.Id]);
        }

        // Print Aurora's last 200 ticks
        if (auroraDeathTick != null)
        {
            Console.WriteLine($"\n  ── {aurora.Name} — Last {auroraSnapshots.Count} ticks before death ──");
            PrintSnapshots(auroraSnapshots, pickups[aurora.Id]);
        }

        // Summary: what were they carrying at death?
        if (alexDeathTick != null)
        {
            var last = alexSnapshots.Last();
            var prevAlive = alexSnapshots.LastOrDefault(s => s.Alive);
            Console.WriteLine($"\n  ── {alexander.Name} DEATH SUMMARY ──");
            if (prevAlive != null)
            {
                Console.WriteLine($"  Last alive tick {prevAlive.Tick}: Hunger={prevAlive.Hunger:F1}, Action={prevAlive.Action}, Mode={prevAlive.Mode}");
                Console.WriteLine($"  Inventory: {FormatInv(prevAlive.Inventory)}");
                int nonFood = CountNonFood(prevAlive.Inventory);
                int food = CountFood(prevAlive.Inventory);
                Console.WriteLine($"  Food items: {food}, Non-food items (Stone/Wood/Ore): {nonFood}");
                Console.WriteLine($"  Inventory fullness: {food + nonFood}/20");
            }
        }

        if (auroraDeathTick != null)
        {
            var prevAlive = auroraSnapshots.LastOrDefault(s => s.Alive);
            Console.WriteLine($"\n  ── {aurora.Name} DEATH SUMMARY ──");
            if (prevAlive != null)
            {
                Console.WriteLine($"  Last alive tick {prevAlive.Tick}: Hunger={prevAlive.Hunger:F1}, Action={prevAlive.Action}, Mode={prevAlive.Mode}");
                Console.WriteLine($"  Inventory: {FormatInv(prevAlive.Inventory)}");
                int nonFood = CountNonFood(prevAlive.Inventory);
                int food = CountFood(prevAlive.Inventory);
                Console.WriteLine($"  Food items: {food}, Non-food items (Stone/Wood/Ore): {nonFood}");
                Console.WriteLine($"  Inventory fullness: {food + nonFood}/20");
            }
        }
    }

    private static void TraceSeed16001Lily()
    {
        var world = new World(64, 64, 16001);
        var sim = new Simulation(world, 16001);
        for (int i = 0; i < 2; i++) sim.SpawnAgent();

        // Find Lily (agent 2 / female)
        var lily = sim.Agents.FirstOrDefault(a => a.Name == "Lily");
        if (lily == null)
        {
            lily = sim.Agents[1]; // fallback
            Console.WriteLine($"  Agent 1: {lily.Name} (expected Lily)");
        }
        else
        {
            Console.WriteLine($"  Found Lily (id={lily.Id})");
        }

        var pickups = new List<(int tick, string msg)>();

        sim.TraceCallback = msg =>
        {
            if (msg.Contains("picked up") && msg.Contains($"Agent {lily.Id}"))
                pickups.Add((sim.CurrentTick, msg));
        };

        var snapshots = new Queue<TickSnapshot>();
        int? deathTick = null;

        for (int t = 0; t < 50000; t++)
        {
            sim.Tick();

            if (lily.IsAlive || deathTick == null)
            {
                var snap = new TickSnapshot(
                    sim.CurrentTick, lily.Hunger, lily.Health,
                    lily.CurrentAction, lily.CurrentMode,
                    lily.X, lily.Y,
                    new Dictionary<ResourceType, int>(lily.Inventory),
                    lily.IsAlive);
                snapshots.Enqueue(snap);
                if (snapshots.Count > 100) snapshots.Dequeue();

                if (!lily.IsAlive && deathTick == null)
                    deathTick = sim.CurrentTick;
            }
        }

        Console.WriteLine($"  Total opportunistic pickups for Lily: {pickups.Count}");
        Console.WriteLine($"  Lily death tick: {deathTick?.ToString() ?? "survived"}");

        if (deathTick != null)
        {
            Console.WriteLine($"\n  ── Lily — Last {snapshots.Count} ticks before death ──");
            PrintSnapshots(snapshots, pickups);

            var prevAlive = snapshots.LastOrDefault(s => s.Alive);
            Console.WriteLine($"\n  ── Lily DEATH SUMMARY ──");
            if (prevAlive != null)
            {
                Console.WriteLine($"  Last alive tick {prevAlive.Tick}: Hunger={prevAlive.Hunger:F1}, Action={prevAlive.Action}, Mode={prevAlive.Mode}");
                Console.WriteLine($"  Position: ({prevAlive.X},{prevAlive.Y})");
                Console.WriteLine($"  Inventory: {FormatInv(prevAlive.Inventory)}");
                int nonFood = CountNonFood(prevAlive.Inventory);
                int food = CountFood(prevAlive.Inventory);
                Console.WriteLine($"  Food items: {food}, Non-food items (Stone/Wood/Ore): {nonFood}");
                Console.WriteLine($"  Inventory fullness: {food + nonFood}/20");
            }
        }
        else
        {
            Console.WriteLine("  Lily survived the full 50K ticks.");
        }
    }

    private static void PrintSnapshots(Queue<TickSnapshot> snapshots, List<(int tick, string msg)> pickups)
    {
        var pickupSet = new HashSet<int>(pickups.Select(p => p.tick));
        Console.WriteLine($"  {"Tick",6} {"Hunger",7} {"HP",4} {"Action",-14} {"Mode",-12} {"Pos",8} {"Alive",5}  Inventory");
        Console.WriteLine($"  {"----",6} {"------",7} {"--",4} {"------",-14} {"----",-12} {"---",8} {"-----",5}  ---------");
        foreach (var s in snapshots)
        {
            string pickup = pickupSet.Contains(s.Tick) ? " <<PICKUP" : "";
            Console.WriteLine($"  {s.Tick,6} {s.Hunger,7:F1} {s.Health,4:F0} {s.Action,-14} {s.Mode,-12} ({s.X,2},{s.Y,2}) {(s.Alive ? "Y" : "N"),5}  {FormatInv(s.Inventory)}{pickup}");
        }
    }

    private static string FormatInv(Dictionary<ResourceType, int> inv)
    {
        var parts = inv.Where(kv => kv.Value > 0)
                       .OrderBy(kv => kv.Key.ToString())
                       .Select(kv => $"{kv.Key}:{kv.Value}");
        string result = string.Join(" ", parts);
        return result.Length > 0 ? result : "(empty)";
    }

    private static int CountNonFood(Dictionary<ResourceType, int> inv)
    {
        int count = 0;
        count += inv.GetValueOrDefault(ResourceType.Stone, 0);
        count += inv.GetValueOrDefault(ResourceType.Wood, 0);
        count += inv.GetValueOrDefault(ResourceType.Ore, 0);
        return count;
    }

    private static int CountFood(Dictionary<ResourceType, int> inv)
    {
        int count = 0;
        count += inv.GetValueOrDefault(ResourceType.Berries, 0);
        count += inv.GetValueOrDefault(ResourceType.Grain, 0);
        count += inv.GetValueOrDefault(ResourceType.Meat, 0);
        count += inv.GetValueOrDefault(ResourceType.Fish, 0);
        return count;
    }
}
