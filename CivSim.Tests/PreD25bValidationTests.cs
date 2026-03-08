using CivSim.Core;
using Xunit;
using Xunit.Abstractions;

namespace CivSim.Tests;

[Collection("Integration")]
public class PreD25bValidationTests
{
    private readonly ITestOutputHelper _output;
    public PreD25bValidationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Category", "Slow")]
    public void Seed16001_ExploreDirectionStreaks_NoneExceed3()
    {
        Agent.ResetIdCounter();
        Animal.ResetIdCounter();
        Carcass.ResetIdCounter();
        Pen.ResetIdCounter();
        var world = new World(64, 64, 16001);
        var sim = new Simulation(world, 16001);
        for (int i = 0; i < 2; i++) sim.SpawnAgent();

        // Track max streaks per agent
        var maxStreaks = new Dictionary<string, int>();

        for (int t = 0; t < 50000; t++)
        {
            sim.Tick();

            // Check every 100 ticks
            if (t % 100 == 0)
            {
                foreach (var agent in sim.Agents.Where(a => a.IsAlive))
                {
                    var dirs = agent.RecentExploreDirections;
                    if (dirs.Count < 2) continue;

                    int maxStreak = 1, currentStreak = 1;
                    for (int d = 1; d < dirs.Count; d++)
                    {
                        if (dirs[d] == dirs[d - 1])
                        {
                            currentStreak++;
                            if (currentStreak > maxStreak) maxStreak = currentStreak;
                        }
                        else currentStreak = 1;
                    }

                    if (!maxStreaks.ContainsKey(agent.Name) || maxStreak > maxStreaks[agent.Name])
                        maxStreaks[agent.Name] = maxStreak;
                }
            }
        }

        foreach (var kvp in maxStreaks)
            _output.WriteLine($"{kvp.Key}: max consecutive same-direction streak = {kvp.Value}");

        foreach (var kvp in maxStreaks)
            Assert.True(kvp.Value <= 3, $"{kvp.Key} had streak of {kvp.Value} consecutive same-direction explores");
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Category", "Slow")]
    public void Seed16001_NoAnimalsStuckAtEdge100Ticks()
    {
        Agent.ResetIdCounter();
        Animal.ResetIdCounter();
        Carcass.ResetIdCounter();
        Pen.ResetIdCounter();
        var world = new World(64, 64, 16001);
        var sim = new Simulation(world, 16001);
        for (int i = 0; i < 2; i++) sim.SpawnAgent();

        // Track consecutive edge ticks per animal
        var edgeTicks = new Dictionary<int, int>();
        int maxEdgeTicks = 0;
        string worstAnimal = "";

        for (int t = 0; t < 50000; t++)
        {
            sim.Tick();

            foreach (var animal in world.Animals.Where(a => a.IsAlive))
            {
                bool atEdge = animal.X <= 1 || animal.Y <= 1 || animal.X >= 62 || animal.Y >= 62;
                if (atEdge)
                {
                    edgeTicks.TryGetValue(animal.Id, out int prev);
                    edgeTicks[animal.Id] = prev + 1;
                    if (edgeTicks[animal.Id] > maxEdgeTicks)
                    {
                        maxEdgeTicks = edgeTicks[animal.Id];
                        worstAnimal = $"Animal {animal.Id} ({animal.Species}) at ({animal.X},{animal.Y})";
                    }
                }
                else
                {
                    edgeTicks[animal.Id] = 0;
                }
            }
        }

        _output.WriteLine($"Worst consecutive edge ticks: {maxEdgeTicks} -- {worstAnimal}");
        Assert.True(maxEdgeTicks < 100, $"Animal stuck at edge for {maxEdgeTicks} ticks: {worstAnimal}");
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Category", "Slow")]
    public void Seed16001_ReportLilyIdlePercent()
    {
        Agent.ResetIdCounter();
        Animal.ResetIdCounter();
        Carcass.ResetIdCounter();
        Pen.ResetIdCounter();
        var world = new World(64, 64, 16001);
        var sim = new Simulation(world, 16001);
        for (int i = 0; i < 2; i++) sim.SpawnAgent();

        // Track idle ticks per agent
        var idleTicks = new Dictionary<string, int>();
        var totalTicks = new Dictionary<string, int>();

        for (int t = 0; t < 50000; t++)
        {
            sim.Tick();

            foreach (var agent in sim.Agents.Where(a => a.IsAlive))
            {
                totalTicks.TryGetValue(agent.Name, out int total);
                totalTicks[agent.Name] = total + 1;

                if (agent.CurrentAction == ActionType.Idle)
                {
                    idleTicks.TryGetValue(agent.Name, out int idle);
                    idleTicks[agent.Name] = idle + 1;
                }
            }
        }

        foreach (var name in totalTicks.Keys.OrderBy(n => n))
        {
            int idle = idleTicks.GetValueOrDefault(name, 0);
            int total = totalTicks[name];
            double pct = 100.0 * idle / total;
            _output.WriteLine($"{name}: Idle {idle}/{total} = {pct:F1}%");
        }

        // Report-only test - always passes
        Assert.True(true);
    }
}
