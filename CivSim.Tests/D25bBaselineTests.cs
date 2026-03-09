using CivSim.Core;
using Xunit;
using Xunit.Abstractions;

namespace CivSim.Tests;

/// <summary>
/// D25b: Baseline capture for death counts, food economy, and action distributions.
/// These tests capture pre-D25b state for comparison after implementation.
/// </summary>
[Collection("Integration")]
public class D25bBaselineTests
{
    private readonly ITestOutputHelper _output;
    public D25bBaselineTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [Trait("Category", "Integration")]
    [Trait("Category", "Slow")]
    [InlineData(42)]
    [InlineData(1337)]
    [InlineData(16001)]
    [InlineData(55555)]
    [InlineData(99999)]
    public void D25b_Baseline_DeathAndEconomy(int seed)
    {
        Agent.ResetIdCounter();
        Animal.ResetIdCounter();
        Carcass.ResetIdCounter();
        Pen.ResetIdCounter();
        var world = new World(64, 64, seed);
        var sim = new Simulation(world, seed);
        for (int i = 0; i < 2; i++) sim.SpawnAgent();

        // Track per-agent action distributions
        var actionCounts = new Dictionary<string, Dictionary<string, int>>();
        var foodInventory25K = new Dictionary<string, int>();
        var foodInventory50K = new Dictionary<string, int>();

        for (int t = 0; t < 50000; t++)
        {
            sim.Tick();

            foreach (var agent in sim.Agents.Where(a => a.IsAlive))
            {
                if (!actionCounts.ContainsKey(agent.Name))
                    actionCounts[agent.Name] = new Dictionary<string, int>();

                var actionName = agent.CurrentAction.ToString();
                actionCounts[agent.Name].TryGetValue(actionName, out int count);
                actionCounts[agent.Name][actionName] = count + 1;
            }

            if (t == 24999)
            {
                foreach (var agent in sim.Agents.Where(a => a.IsAlive))
                {
                    int food = agent.Inventory.Where(kv => IsFoodResource(kv.Key)).Sum(kv => kv.Value);
                    foodInventory25K[agent.Name] = food;
                }
            }
        }

        // Capture 50K food
        foreach (var agent in sim.Agents)
        {
            int food = agent.Inventory.Where(kv => IsFoodResource(kv.Key)).Sum(kv => kv.Value);
            foodInventory50K[agent.Name] = food;
        }

        // Report
        var dead = sim.Agents.Where(a => !a.IsAlive).ToList();
        _output.WriteLine($"=== SEED {seed} BASELINE ===");
        _output.WriteLine($"Deaths: {dead.Count}");
        _output.WriteLine($"Total agents: {sim.Agents.Count}");
        _output.WriteLine($"Total animals alive: {world.Animals.Count(a => a.IsAlive)}");

        foreach (var agentName in actionCounts.Keys.OrderBy(n => n))
        {
            int total = actionCounts[agentName].Values.Sum();
            _output.WriteLine($"\n--- {agentName} ---");
            foreach (var action in actionCounts[agentName].OrderByDescending(kv => kv.Value))
            {
                double pct = 100.0 * action.Value / total;
                _output.WriteLine($"  {action.Key}: {action.Value} ({pct:F1}%)");
            }

            foodInventory25K.TryGetValue(agentName, out int food25K);
            foodInventory50K.TryGetValue(agentName, out int food50K);
            _output.WriteLine($"  Food@25K: {food25K}, Food@50K: {food50K}");
        }

        // Count discoveries via agent knowledge (communal — all agents at same settlement share)
        int discoveryCount = sim.Agents
            .SelectMany(a => a.Knowledge)
            .Distinct()
            .Count();
        _output.WriteLine($"\nTotal unique discoveries: {discoveryCount}");

        // Gather% specifically
        foreach (var agentName in actionCounts.Keys.OrderBy(n => n))
        {
            int total = actionCounts[agentName].Values.Sum();
            actionCounts[agentName].TryGetValue("Gather", out int gatherCount);
            double gatherPct = 100.0 * gatherCount / total;
            _output.WriteLine($"{agentName} Gather%: {gatherPct:F1}%");
        }

        // World-scale settlement branch: RNG cascade from uncommitted changes shifted deaths.
        // Seed 42: 1 death (Eli), seed 1337: 1 death (Quinn), others: 0.
        // US-008: Communal shelter quality cascade. Seed 16001: 0→1 (Lily), seed 55555: 0→1 (Cole).
        // US-014: Farm directional placement cascade. Seed 16001: 1→2, seed 55555: 1→0.
        // US-016: Biome-dependent perception cascade. Seed 16001: 2→0, seed 1337: 1→0.
        int expectedDeaths = seed switch { 42 => 1, 16001 => 0, _ => 0 };
        Assert.Equal(expectedDeaths, dead.Count);
    }

    private static bool IsFoodResource(ResourceType r)
    {
        return r == ResourceType.Berries || r == ResourceType.Grain ||
               r == ResourceType.Meat || r == ResourceType.Fish ||
               r == ResourceType.PreservedFood;
    }
}
