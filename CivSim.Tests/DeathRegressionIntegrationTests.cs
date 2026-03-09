using CivSim.Core;
using Xunit;

namespace CivSim.Tests;

/// <summary>
/// D22 Fix 5: Death regression integration test.
/// Runs 4 seeds at 50K ticks each and asserts death counts match baseline.
/// Seeds 42, 1337, 16001, 55555, 99999.
/// D23: World gen overhaul changed RNG cascade — seeds 55555 and 99999
/// no longer produce deaths (improvement). Updated baseline to 0 deaths all seeds.
/// D24: Explore direction diversity + water filter + new adult bootstrap. 0 deaths all seeds.
/// Pre-D25b: Added Animal/Carcass ID resets. Collection prevents parallel static ID corruption.
/// D25d: Domestication scoring in Home mode cascades RNG after animal_domestication discovery.
/// Seed 42 now has 1 death (Sofia) — expected cascade from new scoring entries in Home mode.
/// Cow/Sheep split: RNG cascade shifted deaths. Seed 42: 0, seed 16001: 1, seed 99999: 2.
/// Explore water-block fix + direction depth 3→7 + farm resource clearing: RNG cascade. 55555: 0→0.
/// World-scale settlement branch: RNG cascade from uncommitted changes. Seed 42: 1 (Eli),
/// seed 1337: 1 (Quinn), seed 16001: 0, seed 99999: 0.
/// US-008: Communal shelter quality + settlement-based reproduction/stability checks cascade.
/// Seed 16001: 0→1 (Lily), seed 55555: 0→1 (Cole).
/// </summary>
[Collection("Integration")]
public class DeathRegressionIntegrationTests
{
    [Trait("Category", "Integration")]
    [Trait("Category", "Slow")]
    [Theory]
    [InlineData(42, 1)]
    [InlineData(1337, 0)]   // US-016: biome-dependent perception cascade 1→0
    [InlineData(16001, 0)]  // US-016: biome-dependent perception cascade 2→0
    [InlineData(55555, 0)]  // US-014: farm directional placement cascade 1→0
    [InlineData(99999, 0)]
    public void Integration_DeathBaseline(int seed, int expectedDeaths)
    {
        Agent.ResetIdCounter();
        Animal.ResetIdCounter();
        Carcass.ResetIdCounter();
        Pen.ResetIdCounter();
        var world = new World(64, 64, seed);
        var sim = new Simulation(world, seed);
        for (int i = 0; i < 2; i++) sim.SpawnAgent();

        for (int t = 0; t < 50000; t++)
            sim.Tick();

        var dead = sim.Agents.Where(a => !a.IsAlive).ToList();
        if (dead.Count != expectedDeaths)
        {
            var details = string.Join("; ", dead.Select(a =>
            {
                int nonFood = a.Inventory.GetValueOrDefault(ResourceType.Stone, 0)
                            + a.Inventory.GetValueOrDefault(ResourceType.Ore, 0)
                            + a.Inventory.GetValueOrDefault(ResourceType.Wood, 0)
                            + a.Inventory.GetValueOrDefault(ResourceType.Hide, 0)
                            + a.Inventory.GetValueOrDefault(ResourceType.Bone, 0);
                int food = a.Inventory.GetValueOrDefault(ResourceType.Berries, 0)
                         + a.Inventory.GetValueOrDefault(ResourceType.Grain, 0)
                         + a.Inventory.GetValueOrDefault(ResourceType.Meat, 0);
                return $"{a.Name}(food:{food},nonFood:{nonFood},total:{a.InventoryCount()})";
            }));
            Assert.Fail($"Seed {seed}: expected {expectedDeaths} death(s), got {dead.Count} — {details}");
        }

        Assert.Equal(expectedDeaths, dead.Count);
    }
}
