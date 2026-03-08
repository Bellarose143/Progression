using Xunit;
using Xunit.Abstractions;
using CivSim.Core;

namespace CivSim.Tests;

[Collection("Sequential")]
public class D25cFinalValidation
{
    private readonly ITestOutputHelper _output;
    public D25cFinalValidation(ITestOutputHelper output) { _output = output; }

    [Theory]
    [Trait("Category", "Slow")]
    [InlineData(42)]
    [InlineData(1337)]
    [InlineData(16001)]
    [InlineData(55555)]
    [InlineData(99999)]
    public void FinalValidation_100K(int seed)
    {
        Agent.ResetIdCounter();
        Animal.ResetIdCounter();
        Carcass.ResetIdCounter();
        Pen.ResetIdCounter();
        var world = new World(64, 64, seed);
        var sim = new Simulation(world, seed);
        for (int i = 0; i < 2; i++) sim.SpawnAgent();

        for (int i = 0; i < 100000; i++)
            sim.Tick();

        var agents = sim.Agents;
        var dead = agents.Where(a => !a.IsAlive).ToList();
        var alive = agents.Where(a => a.IsAlive).ToList();

        _output.WriteLine($"=== Seed {seed} at 100K ticks ===");
        _output.WriteLine($"Alive: {alive.Count}, Dead: {dead.Count}");

        foreach (var d in dead)
        {
            _output.WriteLine($"DEAD: {d.Name} at tick {d.DeathTick} cause={d.DeathCause ?? "unknown"} hunger={d.Hunger:F0} health={d.Health:F0}");
            var inv = string.Join(", ", d.Inventory.Where(kv => kv.Value > 0).Select(kv => $"{kv.Key}:{kv.Value}"));
            _output.WriteLine($"  Inventory: {inv}");
        }

        // Discoveries
        var allKnowledge = new HashSet<string>();
        foreach (var a in agents)
            foreach (var k in a.Knowledge)
                allKnowledge.Add(k);
        _output.WriteLine($"Discoveries ({allKnowledge.Count}): {string.Join(", ", allKnowledge.OrderBy(k => k))}");

        // Key weapon/trap discoveries
        _output.WriteLine($"Has spear: {allKnowledge.Contains("spear")}");
        _output.WriteLine($"Has bow: {allKnowledge.Contains("bow")}");
        _output.WriteLine($"Has trapping: {allKnowledge.Contains("trapping")}");
        _output.WriteLine($"Has bone_tools: {allKnowledge.Contains("bone_tools")}");
        _output.WriteLine($"Has hafted_tools: {allKnowledge.Contains("hafted_tools")}");

        // Traps
        _output.WriteLine($"Active traps: {world.Traps.Count(t => t.IsActive)}");
        _output.WriteLine($"Total traps ever: {world.Traps.Count}");
        _output.WriteLine($"Traps with catches: {world.Traps.Count(t => t.CaughtCarcass != null)}");

        // Hide/Bone in alive agents
        foreach (var a in alive)
        {
            int hide = a.Inventory.GetValueOrDefault(ResourceType.Hide);
            int bone = a.Inventory.GetValueOrDefault(ResourceType.Bone);
            if (hide > 0 || bone > 0)
                _output.WriteLine($"  {a.Name}: Hide={hide}, Bone={bone}");
        }

        // Carcasses
        _output.WriteLine($"Active carcasses: {world.Carcasses.Count}");

        // Animals alive
        _output.WriteLine($"Animals alive: {world.Animals.Count(a => a.IsAlive)}");

        // Combat deaths must be rare (0-2 per seed)
        var combatDeaths = dead.Where(d => d.DeathCause != null && d.DeathCause.Contains("combat")).ToList();
        var starvationDeaths = dead.Where(d => d.DeathCause == "starvation").ToList();
        _output.WriteLine($"Combat deaths: {combatDeaths.Count}, Starvation deaths: {starvationDeaths.Count}");
        Assert.True(combatDeaths.Count <= 2, $"Seed {seed}: Too many combat deaths ({combatDeaths.Count}). Max allowed: 2.");
    }
}
