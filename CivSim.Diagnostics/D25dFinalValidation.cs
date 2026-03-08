using CivSim.Core;
using CivSim.Core.Events;

namespace CivSim.Diagnostics;

/// <summary>
/// D25d Final Validation: Runs 5 seeds at 150K ticks each to verify the domestication system.
/// Reports deaths, discoveries, taming, pens, breeding, slaughter, wolf pups, and dogs.
/// </summary>
public static class D25dFinalValidation
{
    public static void Run()
    {
        int[] seeds = { 42, 1337, 16001, 55555, 99999 };
        const int tickCount = 150_000;

        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.WriteLine("  D25d FINAL VALIDATION — Domestication System (150K ticks)");
        Console.WriteLine("═══════════════════════════════════════════════════════════════\n");

        foreach (var seed in seeds)
        {
            RunSeed(seed, tickCount);
        }
    }

    private static void RunSeed(int seed, int tickCount)
    {
        Agent.ResetIdCounter();
        Animal.ResetIdCounter();
        Carcass.ResetIdCounter();
        Pen.ResetIdCounter();

        var world = new World(64, 64, seed);
        var sim = new Simulation(world, seed);
        for (int i = 0; i < 2; i++) sim.SpawnAgent();

        // Tracking counters
        int deathsStarvation = 0;
        int deathsCombat = 0;
        int deathsOther = 0;
        var deathDetails = new List<string>();
        var discoveryList = new List<(int tick, string id)>();
        int tameAttemptCount = 0;
        int tameSuccessCount = 0;
        int breedingEventCount = 0;
        int slaughterEventCount = 0;
        int penBuiltCount = 0;
        int feedPenEventCount = 0;
        int totalGrainFed = 0;

        // Subscribe to EventBus to capture ALL events (EventLogger only keeps 1000)
        sim.EventBus.Subscribe(events =>
        {
            foreach (var evt in events)
            {
                string msg = evt.Message;

                // Deaths
                if (evt.Type == EventType.Death)
                {
                    if (msg.Contains("starv", StringComparison.OrdinalIgnoreCase) ||
                        msg.Contains("hunger", StringComparison.OrdinalIgnoreCase))
                        deathsStarvation++;
                    else if (msg.Contains("combat", StringComparison.OrdinalIgnoreCase) ||
                             msg.Contains("attack", StringComparison.OrdinalIgnoreCase) ||
                             msg.Contains("killed by", StringComparison.OrdinalIgnoreCase))
                        deathsCombat++;
                    else
                    {
                        deathsOther++;
                    }
                    deathDetails.Add($"  Tick {evt.Tick}: {msg}");
                }

                // Discoveries
                if (evt.Type == EventType.Discovery && evt.RecipeId != null)
                {
                    discoveryList.Add((evt.Tick, evt.RecipeId));
                }

                // Taming attempts
                if (msg.Contains("tame", StringComparison.OrdinalIgnoreCase) &&
                    msg.Contains("offer", StringComparison.OrdinalIgnoreCase))
                    tameAttemptCount++;

                if (msg.Contains("tamed", StringComparison.OrdinalIgnoreCase) &&
                    !msg.Contains("attempt", StringComparison.OrdinalIgnoreCase))
                    tameSuccessCount++;

                // Pen built
                if (msg.Contains("pen", StringComparison.OrdinalIgnoreCase) &&
                    msg.Contains("built", StringComparison.OrdinalIgnoreCase))
                    penBuiltCount++;

                // Breeding
                if (msg.Contains("breed", StringComparison.OrdinalIgnoreCase) ||
                    msg.Contains("born in pen", StringComparison.OrdinalIgnoreCase) ||
                    msg.Contains("offspring", StringComparison.OrdinalIgnoreCase))
                    breedingEventCount++;

                // Slaughter
                if (msg.Contains("slaughter", StringComparison.OrdinalIgnoreCase))
                    slaughterEventCount++;

                // FeedPen
                if (msg.Contains("fed") && msg.Contains("to the animal pen", StringComparison.OrdinalIgnoreCase))
                    feedPenEventCount++;
            }
        });

        Console.WriteLine($"=== Seed {seed} ({tickCount / 1000}K ticks) ===");
        Console.Write("  Running");

        // Run simulation with progress
        for (int t = 0; t < tickCount; t++)
        {
            sim.Tick();
            if (t % 30000 == 0 && t > 0) Console.Write(".");
        }
        Console.WriteLine(" done.");

        // === Post-run analysis ===

        // Force accumulate discoveries from all agents
        sim.GetStats();

        // Deaths
        int totalDeaths = deathsStarvation + deathsCombat + deathsOther;
        int aliveCount = sim.Agents.Count(a => a.IsAlive);
        int totalAgents = sim.Agents.Count;
        Console.WriteLine($"\n  Population: {aliveCount} alive / {totalAgents} total");
        Console.WriteLine($"  Deaths: {totalDeaths} (starvation: {deathsStarvation}, combat: {deathsCombat}, other: {deathsOther})");
        if (deathDetails.Count > 0 && deathDetails.Count <= 20)
        {
            foreach (var d in deathDetails) Console.WriteLine(d);
        }
        else if (deathDetails.Count > 20)
        {
            Console.WriteLine($"  (showing first 20 of {deathDetails.Count} deaths)");
            foreach (var d in deathDetails.Take(20)) Console.WriteLine(d);
        }

        // Discoveries
        Console.WriteLine($"\n  Discoveries: {sim.CumulativeDiscoveries.Count} total");
        var keyDiscoveries = new[] { "animal_domestication", "bow", "trapping", "spear", "animal_pen", "breeding", "slaughter" };
        foreach (var key in keyDiscoveries)
        {
            bool found = sim.CumulativeDiscoveries.Contains(key);
            var entry = discoveryList.FirstOrDefault(d => d.id == key);
            if (found)
                Console.WriteLine($"    {key}: YES (tick {entry.tick})");
            else
                Console.WriteLine($"    {key}: no");
        }

        // Also show all discoveries for reference
        Console.WriteLine($"  All discoveries: {string.Join(", ", sim.CumulativeDiscoveries.OrderBy(d => d))}");

        // Domestication state from world
        var domesticated = world.Animals.Where(a => a.IsAlive && a.IsDomesticated).ToList();
        var tamingInProgress = world.Animals.Where(a => a.IsAlive && a.TameProgress > 0 && !a.IsDomesticated).ToList();
        var wolfPups = world.Animals.Where(a => a.IsAlive && a.IsPup && a.Species == AnimalSpecies.Wolf).ToList();
        var dogs = world.Animals.Where(a => a.IsAlive && a.IsDog).ToList();
        var pennedAnimals = world.Animals.Where(a => a.IsAlive && a.PenId != null).ToList();
        int pensAtEnd = world.Pens.Count(p => p.IsActive);

        Console.WriteLine($"\n  Taming attempts (event count): {tameAttemptCount}");
        Console.WriteLine($"  Tame successes (event count): {tameSuccessCount}");
        Console.WriteLine($"  Taming in progress: {tamingInProgress.Count}");
        foreach (var a in tamingInProgress.Take(5))
            Console.WriteLine($"    Animal {a.Id} ({a.Species}) TameProgress={a.TameProgress} at ({a.X},{a.Y})");

        Console.WriteLine($"\n  Domesticated animals (alive): {domesticated.Count}");
        foreach (var a in domesticated.Take(10))
            Console.WriteLine($"    Animal {a.Id} ({a.Species}) PenId={a.PenId} Owner={a.OwnerAgentId} at ({a.X},{a.Y})");

        Console.WriteLine($"\n  Pens built (event count): {penBuiltCount}");
        Console.WriteLine($"  Active pens at end: {pensAtEnd}");
        foreach (var pen in world.Pens.Where(p => p.IsActive))
            Console.WriteLine($"    Pen {pen.Id} at ({pen.TileX},{pen.TileY}) animals={pen.AnimalCount}/{pen.Capacity} food={pen.FoodStore}");

        Console.WriteLine($"\n  Penned animals: {pennedAnimals.Count}");
        foreach (var a in pennedAnimals.Take(10))
            Console.WriteLine($"    Animal {a.Id} ({a.Species}) PenId={a.PenId} Domesticated={a.IsDomesticated}");

        Console.WriteLine($"\n  FeedPen events: {feedPenEventCount}");
        Console.WriteLine($"  Breeding events: {breedingEventCount}");
        Console.WriteLine($"  Slaughter events: {slaughterEventCount}");

        Console.WriteLine($"\n  Wolf pups (alive): {wolfPups.Count}");
        foreach (var w in wolfPups.Take(5))
            Console.WriteLine($"    Animal {w.Id} TameProgress={w.TameProgress} PenId={w.PenId} at ({w.X},{w.Y})");

        Console.WriteLine($"  Dogs (alive): {dogs.Count}");
        foreach (var d in dogs.Take(5))
            Console.WriteLine($"    Animal {d.Id} Owner={d.OwnerAgentId} PenId={d.PenId} at ({d.X},{d.Y})");

        // Food economy
        Console.WriteLine($"\n  Agent food inventory at end:");
        foreach (var agent in sim.Agents.Where(a => a.IsAlive).Take(10))
        {
            int food = agent.Inventory.GetValueOrDefault(ResourceType.Berries, 0)
                     + agent.Inventory.GetValueOrDefault(ResourceType.Grain, 0)
                     + agent.Inventory.GetValueOrDefault(ResourceType.Meat, 0)
                     + agent.Inventory.GetValueOrDefault(ResourceType.Fish, 0);
            Console.WriteLine($"    {agent.Name} (age {agent.Age:F0}): food={food}, hunger={agent.Hunger:F0}");
        }

        // Total alive animals
        int aliveAnimals = world.Animals.Count(a => a.IsAlive);
        Console.WriteLine($"\n  Total alive animals: {aliveAnimals}");
        var speciesCounts = world.Animals.Where(a => a.IsAlive)
            .GroupBy(a => a.Species)
            .OrderBy(g => g.Key.ToString())
            .Select(g => $"{g.Key}:{g.Count()}");
        Console.WriteLine($"  By species: {string.Join(", ", speciesCounts)}");

        Console.WriteLine();
        Console.WriteLine(new string('-', 60));
        Console.WriteLine();
    }
}
