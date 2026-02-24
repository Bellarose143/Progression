using CivSim.Core;
using Xunit;

namespace CivSim.Tests;

/// <summary>
/// Category 4: Parent-Child Behavior
/// All positions are OFFSETS from the simulation spawn center.
/// Rules: RULE-P1, RULE-P2, RULE-R1, Bug-3-fix
/// </summary>
public class ParentChildTests
{
    [Fact]
    public void Founding_Pair_Is_Always_Mixed_Gender()
    {
        var world      = new World(32, 32, 1);
        var simulation = new Simulation(world, 1);

        var a1 = simulation.SpawnAgent();
        var a2 = simulation.SpawnAgent();

        Assert.True(a1.IsMale != a2.IsMale,
            "First two spawned agents must be one male and one female (Bug 3 fix)");
    }

    [Fact]
    public void Infant_Survives_With_Stocked_Home_Storage()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Dad",  isMale: true,  hunger: 80f).AgentAt("Dad",  0, 0).AgentHome("Dad",  0, 0)
            .AddAgent("Mom",  isMale: false, hunger: 80f).AgentAt("Mom",  0, 0).AgentHome("Mom",  0, 0)
            .AddAgent("Baby", isMale: true,  hunger: 75f).AgentAt("Baby", 0, 0).AgentHome("Baby", 0, 0)
            .AgentAge("Baby", 1000)                 // infant age
            .ShelterAt(0, 0)
            .HomeStorageAt(0, 0, ResourceType.Berries, 80)
            .ResourceAt(1, 0,  ResourceType.Berries, 30)
            .ResourceAt(-1, 0, ResourceType.Berries, 30)
            .Build();

        sim.Tick(500);

        var baby = sim.GetAgent("Baby");
        Assert.True(baby.IsAlive,
            $"Infant should survive 500 ticks with stocked home storage. " +
            $"Cause: {baby.DeathCause ?? "N/A"}");
        Assert.False(baby.DeathCause?.Contains("starvation") ?? false,
            "Infant should not die of starvation with stocked home storage");
    }

    [Fact]
    public void At_Least_One_Parent_Stays_Near_Home_With_Infant()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Dad",  isMale: true,  hunger: 80f).AgentAt("Dad",  0, 0).AgentHome("Dad",  0, 0)
            .AddAgent("Mom",  isMale: false, hunger: 80f).AgentAt("Mom",  0, 0).AgentHome("Mom",  0, 0)
            .AddAgent("Baby", isMale: false, hunger: 80f).AgentAt("Baby", 0, 0).AgentHome("Baby", 0, 0)
            .AgentAge("Baby", 1000)
            .ShelterAt(0, 0)
            .HomeStorageAt(0, 0, ResourceType.Berries, 60)
            .ResourceAt(1, 0,  ResourceType.Berries, 30)
            .ResourceAt(-1, 0, ResourceType.Berries, 30)
            .ResourceAt(0, 1,  ResourceType.Berries, 30)
            .Build();

        int homeX = sim.SpawnX, homeY = sim.SpawnY;

        int bothFarCount = 0, totalChecked = 0;
        for (int t = 0; t < 500; t++)
        {
            sim.Tick(1);
            if (t % 5 == 0)
            {
                totalChecked++;
                var dad = sim.GetAgent("Dad");
                var mom = sim.GetAgent("Mom");
                if (!dad.IsAlive || !mom.IsAlive) break;
                bool dadFar = TestSim.ManhattanDistance(dad.X, dad.Y, homeX, homeY) > 10;
                bool momFar = TestSim.ManhattanDistance(mom.X, mom.Y, homeX, homeY) > 10;
                if (dadFar && momFar) bothFarCount++;
            }
        }

        double abandonPct = totalChecked > 0 ? 100.0 * bothFarCount / totalChecked : 0;
        Assert.True(abandonPct < 10.0,
            $"Both parents should not simultaneously be > 10 tiles from home for extended periods. " +
            $"Both-far: {abandonPct:F1}% ({bothFarCount}/{totalChecked} samples)");
    }

    [Fact]
    public void Same_Sex_Pair_Cannot_Reproduce()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Alice", isMale: false, hunger: 90f).AgentAt("Alice", 0, 0).AgentHome("Alice", 0, 0).AgentCooldown("Alice", 0)
            .AddAgent("Carol", isMale: false, hunger: 90f).AgentAt("Carol", 1, 0).AgentHome("Carol", 0, 0).AgentCooldown("Carol", 0)
            .ShelterAt(0, 0)
            .HomeStorageAt(0, 0, ResourceType.Berries, 80)
            .ResourceAt(-1, 0, ResourceType.Berries, 30)
            .Build();

        sim.Tick(2000);

        Assert.True(sim.Simulation.Agents.Count == 2,
            "Same-sex pair should never produce a child");
    }
}
