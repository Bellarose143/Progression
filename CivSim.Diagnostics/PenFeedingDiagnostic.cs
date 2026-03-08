using CivSim.Core;

namespace CivSim.Diagnostics;

/// <summary>
/// Focused diagnostic to trace pen lifecycle: creation, feeding, starvation, breeding.
/// </summary>
public static class PenFeedingDiagnostic
{
    public static void Run(int seed, int ticks)
    {
        Agent.ResetIdCounter();
        Animal.ResetIdCounter();
        Carcass.ResetIdCounter();
        Pen.ResetIdCounter();

        var world = new World(64, 64, seed);
        var sim = new Simulation(world, seed);
        for (int i = 0; i < 2; i++) sim.SpawnAgent();

        int feedPenActions = 0;
        int breedingEvents = 0;
        int penDeaths = 0;
        int tameActions = 0;
        int penAnimalActions = 0;
        int slaughterActions = 0;
        int firstPenTick = -1;
        int firstAnimalPennedTick = -1;
        int firstFeedPenTick = -1;

        var penSnapshots = new List<(int tick, int penId, int foodStore, int animalCount, string species)>();
        var keyEvents = new List<string>();

        // Subscribe to EventBus to reliably capture all events
        sim.EventBus.Subscribe(events =>
        {
            foreach (var evt in events)
            {
                if (evt.Message.Contains("born in the pen"))
                {
                    breedingEvents++;
                    if (keyEvents.Count < 50)
                        keyEvents.Add($"  T{evt.Tick}: {evt.Message}");
                }
                if (evt.Message.Contains("starved to death") && evt.Message.Contains("penned"))
                {
                    penDeaths++;
                    if (keyEvents.Count < 50)
                        keyEvents.Add($"  T{evt.Tick}: {evt.Message}");
                }
                if (evt.Message.Contains("tamed a"))
                {
                    if (keyEvents.Count < 50)
                        keyEvents.Add($"  T{evt.Tick}: {evt.Message}");
                }
            }
        });

        for (int t = 0; t < ticks; t++)
        {
            int penCountBefore = world.Pens.Count;
            int totalPennedBefore = world.Pens.Sum(p => p.AnimalCount);

            sim.Tick();

            if (world.Pens.Count > penCountBefore && firstPenTick < 0)
                firstPenTick = t;

            int totalPennedAfter = world.Pens.Sum(p => p.AnimalCount);
            if (totalPennedAfter > totalPennedBefore && firstAnimalPennedTick < 0)
                firstAnimalPennedTick = t;

            foreach (var agent in sim.Agents.Where(a => a.IsAlive))
            {
                switch (agent.CurrentAction)
                {
                    case ActionType.FeedPen:
                        feedPenActions++;
                        if (firstFeedPenTick < 0) firstFeedPenTick = t;
                        break;
                    case ActionType.Tame: tameActions++; break;
                    case ActionType.PenAnimal: penAnimalActions++; break;
                    case ActionType.Slaughter: slaughterActions++; break;
                }
            }

            // Snapshot pen state every 5000 ticks
            if (world.Pens.Count > 0 && t % 5000 == 0)
            {
                foreach (var pen in world.Pens)
                {
                    var speciesCounts = new Dictionary<AnimalSpecies, int>();
                    foreach (var animalId in pen.AnimalIds)
                    {
                        var animal = world.Animals.FirstOrDefault(a => a.Id == animalId && a.IsAlive);
                        if (animal != null)
                            speciesCounts[animal.Species] = speciesCounts.GetValueOrDefault(animal.Species) + 1;
                    }
                    var speciesStr = speciesCounts.Count > 0
                        ? string.Join(",", speciesCounts.Select(kv => $"{kv.Key}:{kv.Value}"))
                        : "empty";
                    penSnapshots.Add((t, pen.Id, pen.FoodStore, pen.AnimalCount, speciesStr));
                }
            }
        }

        // Report
        Console.WriteLine($"\n=== SEED {seed} — PEN FEEDING DIAGNOSTIC ({ticks} ticks) ===");
        Console.WriteLine($"Tame actions:       {tameActions}");
        Console.WriteLine($"PenAnimal actions:  {penAnimalActions}");
        Console.WriteLine($"FeedPen actions:    {feedPenActions}");
        Console.WriteLine($"Slaughter actions:  {slaughterActions}");
        Console.WriteLine($"Breeding events:    {breedingEvents}");
        Console.WriteLine($"Pen starvation deaths: {penDeaths}");
        Console.WriteLine($"Total pens built:   {world.Pens.Count}");
        Console.WriteLine($"First pen built:    tick {firstPenTick}");
        Console.WriteLine($"First animal penned: tick {firstAnimalPennedTick}");
        Console.WriteLine($"First FeedPen:      tick {firstFeedPenTick}");

        Console.WriteLine("\nKnowledge check:");
        foreach (var agent in sim.Agents)
        {
            bool hasDomest = agent.Knowledge.Contains("animal_domestication");
            Console.WriteLine($"  {agent.Name}: animal_domestication={hasDomest}");
        }

        if (keyEvents.Count > 0)
        {
            Console.WriteLine($"\nKey pen events ({keyEvents.Count} total, first 50 shown):");
            foreach (var e in keyEvents.Take(50))
                Console.WriteLine(e);
        }

        if (penSnapshots.Count > 0)
        {
            Console.WriteLine("\nPen snapshots (every 5000 ticks):");
            Console.WriteLine("  Tick     | Pen# | Food  | Animals | Species");
            Console.WriteLine("  ---------|------|-------|---------|--------");
            foreach (var (tick, penId, food, count, species) in penSnapshots)
            {
                Console.WriteLine($"  {tick,8} | {penId,4} | {food,5} | {count,7} | {species}");
            }
        }
        else
        {
            Console.WriteLine("\nNo pens were ever built.");
        }

        Console.WriteLine("\nFinal pen state:");
        foreach (var pen in world.Pens)
        {
            Console.WriteLine($"  Pen {pen.Id} at ({pen.TileX},{pen.TileY}): Active={pen.IsActive}, Animals={pen.AnimalCount}, FoodStore={pen.FoodStore}/{pen.MaxFoodStore}");
            foreach (var animalId in pen.AnimalIds)
            {
                var animal = world.Animals.FirstOrDefault(a => a.Id == animalId);
                if (animal != null)
                    Console.WriteLine($"    Animal {animal.Id}: {animal.Species}, Alive={animal.IsAlive}, Health={animal.Health}/{animal.MaxHealth}, Dom={animal.IsDomesticated}");
            }
        }
    }
}
