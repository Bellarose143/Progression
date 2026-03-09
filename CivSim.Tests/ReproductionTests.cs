using System.Linq;
using CivSim.Core;
using Xunit;

namespace CivSim.Tests;

/// <summary>
/// Category 6: Reproduction
/// All positions are OFFSETS from the simulation spawn center.
/// Rules: RULE-R1, RULE-R2, RULE-R3
/// </summary>
public class ReproductionTests
{
    [Fact]
    public void Opposite_Gender_Pair_Can_Reproduce_Given_Ideal_Conditions()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Adam", isMale: true,  hunger: 90f).AgentAt("Adam", 0, 0).AgentHome("Adam", 0, 0).AgentCooldown("Adam", 0)
            .AddAgent("Eve",  isMale: false, hunger: 90f).AgentAt("Eve",  1, 0).AgentHome("Eve",  0, 0).AgentCooldown("Eve", 0)
            .ShelterAt(0, 0)
            .SettlementWith(0, 0, "Adam", "Eve")
            .HomeStorageAt(0, 0, ResourceType.Berries, 80)
            .ResourceAt(-1, 0, ResourceType.Berries, 40)
            .ResourceAt(0, -1, ResourceType.Berries, 40)
            .ResourceAt(0, 1,  ResourceType.Berries, 40)
            .ResourceAt(1, 1,  ResourceType.Berries, 40)
            .Build();

        bool childBorn = sim.TickUntil(() => sim.LiveAgentCount > 2, maxTicks: 3000);

        Assert.True(childBorn,
            "Male-female pair with shelter, food, no cooldown, adult age should produce a child within 3000 ticks");
    }

    [Fact]
    public void Same_Sex_Pair_Never_Reproduces()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Tom",   isMale: true, hunger: 90f).AgentAt("Tom",   0, 0).AgentHome("Tom",   0, 0).AgentCooldown("Tom", 0)
            .AddAgent("Jerry", isMale: true, hunger: 90f).AgentAt("Jerry", 1, 0).AgentHome("Jerry", 0, 0).AgentCooldown("Jerry", 0)
            .ShelterAt(0, 0)
            .SettlementWith(0, 0, "Tom", "Jerry")
            .HomeStorageAt(0, 0, ResourceType.Berries, 80)
            .ResourceAt(-1, 0, ResourceType.Berries, 30)
            .ResourceAt(0, 1,  ResourceType.Berries, 30)
            .Build();

        sim.Tick(2000);

        Assert.True(sim.Simulation.Agents.Count == 2,
            "Two male agents should never produce a child (RULE-R1)");
    }

    [Fact]
    public void Reproduction_Does_Not_Occur_When_Both_Agents_Are_Exposed()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Mark", isMale: true,  hunger: 80f).AgentAt("Mark", 0, 0).AgentCooldown("Mark", 0)
            .AddAgent("Lucy", isMale: false, hunger: 80f).AgentAt("Lucy", 1, 0).AgentCooldown("Lucy", 0)
            // No shelter and no settlement — US-011: agents without a settlement cannot reproduce.
            .ResourceAt(0, 1,  ResourceType.Berries, 40)
            .ResourceAt(1, 1,  ResourceType.Berries, 40)
            .ResourceAt(-1, 0, ResourceType.Berries, 40)
            .Build();

        // Tick-by-tick: while agents have no settlement, no reproduction should occur.
        // Agents may eventually build shelter (which founds a settlement and enables reproduction),
        // so we only check the constraint while they have no settlement.
        for (int t = 0; t < 1500; t++)
        {
            bool noSettlement = sim.Simulation.Agents.All(a => !a.SettlementId.HasValue);
            if (!noSettlement) break; // Settlement founded — constraint no longer applies

            sim.Tick(1);
            if (noSettlement)
            {
                Assert.True(sim.Simulation.Agents.Count == 2,
                    $"Agents without a settlement should not reproduce (US-011) at tick {t}");
            }
        }
    }

    [Fact]
    public void Under_Age_Agents_Cannot_Reproduce()
    {
        int youthAge = SimConfig.ReproductionMinAge - 10000;

        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Young_M", isMale: true,  hunger: 90f).AgentAt("Young_M", 0, 0).AgentAge("Young_M", youthAge).AgentHome("Young_M", 0, 0).AgentCooldown("Young_M", 0)
            .AddAgent("Young_F", isMale: false, hunger: 90f).AgentAt("Young_F", 1, 0).AgentAge("Young_F", youthAge).AgentHome("Young_F", 0, 0).AgentCooldown("Young_F", 0)
            .ShelterAt(0, 0)
            .SettlementWith(0, 0, "Young_M", "Young_F")
            .HomeStorageAt(0, 0, ResourceType.Berries, 80)
            .ResourceAt(-1, 0, ResourceType.Berries, 30)
            .ResourceAt(0, 1,  ResourceType.Berries, 30)
            .Build();

        sim.Tick(1000);

        Assert.True(sim.Simulation.Agents.Count == 2,
            $"Agents below ReproductionMinAge ({SimConfig.ReproductionMinAge} ticks) should not reproduce");
    }
}
