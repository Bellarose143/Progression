using CivSim.Core;
using Xunit;
using Xunit.Abstractions;

namespace CivSim.Tests;

[Collection("Integration")]
public class D25bDeathDiagnosticTests
{
    private readonly ITestOutputHelper _output;
    public D25bDeathDiagnosticTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(42)]
    [InlineData(16001)]
    [InlineData(55555)]
    public void D25b_TraceDeathCause(int seed)
    {
        Agent.ResetIdCounter();
        Animal.ResetIdCounter();
        Carcass.ResetIdCounter();
        Pen.ResetIdCounter();
        var world = new World(64, 64, seed);
        var sim = new Simulation(world, seed);
        for (int i = 0; i < 2; i++) sim.SpawnAgent();

        _output.WriteLine($"=== SEED {seed} ===");
        _output.WriteLine($"Total animals at start: {world.Animals.Count}");
        _output.WriteLine($"Total world food (tiles): {world.TotalWorldFood}");

        // Track agent stats at intervals
        int huntAttempts = 0;
        int huntSuccesses = 0;
        int harvestCount = 0;
        var lastHunger = new Dictionary<string, float>();
        int deathTick = -1;
        string deadAgentName = "";

        for (int t = 0; t < 50000; t++)
        {
            sim.Tick();

            foreach (var agent in sim.Agents)
            {
                if (agent.CurrentAction == ActionType.Hunt)
                    huntAttempts++;
                if (agent.CurrentAction == ActionType.Harvest)
                    harvestCount++;

                lastHunger[agent.Name] = agent.Hunger;

                // Check for fresh death
                if (!agent.IsAlive && deathTick == -1 && agent.Name != deadAgentName)
                {
                    deathTick = t;
                    deadAgentName = agent.Name;
                    _output.WriteLine($"\n*** {agent.Name} DIED at tick {t} ***");
                    _output.WriteLine($"  Hunger: {agent.Hunger:F0}");
                    _output.WriteLine($"  Health: {agent.Health}");
                    _output.WriteLine($"  Position: ({agent.X},{agent.Y})");
                    _output.WriteLine($"  Home: ({agent.HomeTile?.X},{agent.HomeTile?.Y})");
                    _output.WriteLine($"  Mode: {agent.CurrentMode}");
                    _output.WriteLine($"  Inventory: {string.Join(", ", agent.Inventory.Select(kv => $"{kv.Key}:{kv.Value}"))}");
                    _output.WriteLine($"  AnimalMemory count: {agent.AnimalMemory.Count}");
                    var animalTypes = agent.AnimalMemory
                        .GroupBy(m => $"{m.Type}:{m.AnimalSpecies}")
                        .Select(g => $"{g.Key}={g.Count()}")
                        .ToList();
                    _output.WriteLine($"  AnimalMemory breakdown: {string.Join(", ", animalTypes)}");
                    var huntable = agent.GetRememberedHuntableAnimals(t);
                    _output.WriteLine($"  Huntable animals remembered: {huntable.Count}");
                    _output.WriteLine($"  Knowledge: {string.Join(", ", agent.Knowledge)}");
                    _output.WriteLine($"  Animals alive on map: {world.Animals.Count(a => a.IsAlive)}");
                    _output.WriteLine($"  Carcasses on map: {world.Carcasses.Count}");

                    // Nearest food
                    int nearestBerry = int.MaxValue;
                    int nearestAnimal = int.MaxValue;
                    for (int x = 0; x < world.Width; x++)
                    for (int y = 0; y < world.Height; y++)
                    {
                        var tile = world.GetTile(x, y);
                        if (tile.Resources.GetValueOrDefault(ResourceType.Berries, 0) > 0)
                        {
                            int d = Math.Abs(x - agent.X) + Math.Abs(y - agent.Y);
                            nearestBerry = Math.Min(nearestBerry, d);
                        }
                    }
                    foreach (var a in world.Animals.Where(a => a.IsAlive))
                    {
                        int d = Math.Abs(a.X - agent.X) + Math.Abs(a.Y - agent.Y);
                        nearestAnimal = Math.Min(nearestAnimal, d);
                    }
                    _output.WriteLine($"  Nearest berry tile: {(nearestBerry == int.MaxValue ? "NONE" : nearestBerry.ToString())} tiles");
                    _output.WriteLine($"  Nearest animal: {(nearestAnimal == int.MaxValue ? "NONE" : nearestAnimal.ToString())} tiles");
                }
            }

            // Periodic reports
            if (t == 4999 || t == 9999 || t == 24999 || t == 49999)
            {
                _output.WriteLine($"\n--- Tick {t + 1} ---");
                foreach (var agent in sim.Agents.Where(a => a.IsAlive))
                {
                    int food = agent.FoodInInventory();
                    int meat = agent.Inventory.GetValueOrDefault(ResourceType.Meat, 0);
                    var huntableCount = agent.GetRememberedHuntableAnimals(t).Count;
                    _output.WriteLine($"  {agent.Name}: hunger={agent.Hunger:F0}, food={food}, meat={meat}, mode={agent.CurrentMode}, animalMem={agent.AnimalMemory.Count}, huntable={huntableCount}, stage={agent.Stage}");
                }
                _output.WriteLine($"  Animals alive: {world.Animals.Count(a => a.IsAlive)}, Carcasses: {world.Carcasses.Count}");
            }
        }

        _output.WriteLine($"\n=== FINAL STATS seed {seed} ===");
        _output.WriteLine($"Hunt action ticks: {huntAttempts}");
        _output.WriteLine($"Harvest action ticks: {harvestCount}");
        _output.WriteLine($"Deaths: {sim.Agents.Count(a => !a.IsAlive)}");
        _output.WriteLine($"Animals remaining: {world.Animals.Count(a => a.IsAlive)}");
    }
}
