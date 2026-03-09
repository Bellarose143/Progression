using CivSim.Core;
using Xunit;

namespace CivSim.Tests;

/// <summary>
/// Category 1: Survival Priority
/// All positions are OFFSETS from the simulation spawn center.
/// Rules: RULE-S1, RULE-S2, RULE-S4
/// </summary>
[Trait("Category", "Integration")]
public class SurvivalTests
{
    [Fact]
    public void Starving_Agent_Eats_From_Inventory()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Alice", isMale: false, hunger: 10f)
            .AgentAt("Alice", 0, 0)
            .AgentInventory("Alice", ResourceType.Berries, 5)
            .Build();

        sim.Tick(10);

        var alice = sim.GetAgent("Alice");
        Assert.True(alice.IsAlive, "Agent should be alive after 10 ticks with food in inventory");
        Assert.True(alice.Hunger > 10f || alice.FoodInInventory() < 5,
            $"Agent with Hunger=10 and food in inventory should eat. " +
            $"Hunger={alice.Hunger:F1}, FoodInInventory={alice.FoodInInventory()}");
    }

    [Fact]
    public void Hungry_Agent_Gathers_Food_Rather_Than_Experiments()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Bob", isMale: true, hunger: 35f)
            .AgentAt("Bob", 0, 0)
            .AgentKnows("Bob", "fire_making")
            .ResourceAt(1, 0, ResourceType.Berries, 30)
            .ResourceAt(-1, 0, ResourceType.Berries, 30)
            .ResourceAt(0, 1, ResourceType.Berries, 30)
            .Build();

        float hungerBefore = sim.GetAgent("Bob").Hunger;
        sim.Tick(300);

        var bob = sim.GetAgent("Bob");
        Assert.True(bob.IsAlive, "Agent should survive 300 ticks");
        Assert.True(bob.Hunger > hungerBefore || bob.FoodInInventory() > 0,
            $"Hungry agent should gather food (Hunger {hungerBefore:F1} -> {bob.Hunger:F1}, " +
            $"inventory={bob.FoodInInventory()})");
    }

    [Fact]
    public void Exposed_Agent_Builds_Shelter_When_Resourced()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Eve", isMale: false, hunger: 80f)
            .AgentAt("Eve", 0, 0)
            .AgentKnows("Eve", "lean_to")
            .AgentInventory("Eve", ResourceType.Wood, 8)
            .AgentInventory("Eve", ResourceType.Stone, 3)   // lean_to needs 3 wood + 1 stone
            .ResourceAt(1, 0, ResourceType.Berries, 20)
            .ResourceAt(-1, 0, ResourceType.Berries, 20)
            .ResourceAt(0, 1, ResourceType.Wood, 10)
            .ResourceAt(0, -1, ResourceType.Stone, 10)
            .Build();

        sim.Tick(800);

        var eve = sim.GetAgent("Eve");
        Assert.True(!eve.IsExposed || sim.CountSheltersInWorld() > 0,
            $"Exposed agent with lean_to and wood should build shelter within 800 ticks. " +
            $"IsExposed={eve.IsExposed}, shelters={sim.CountSheltersInWorld()}");
    }

    [Fact]
    public void No_Starvation_With_Abundant_Food()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Adam", isMale: true,  hunger: 90f).AgentAt("Adam", -1, 0)
            .AddAgent("Eve",  isMale: false, hunger: 90f).AgentAt("Eve",   1, 0)
            .ResourceAt(-2, 0,  ResourceType.Berries, 60)
            .ResourceAt(-1, -1, ResourceType.Berries, 60)
            .ResourceAt(-1, 1,  ResourceType.Berries, 60)
            .ResourceAt(0, 0,   ResourceType.Berries, 60)
            .ResourceAt(1, -1,  ResourceType.Berries, 60)
            .ResourceAt(1, 1,   ResourceType.Berries, 60)
            .ResourceAt(2, 0,   ResourceType.Berries, 60)
            .ResourceAt(0, -1,  ResourceType.Berries, 60)
            .ResourceAt(0, 1,   ResourceType.Berries, 60)
            .Build();

        sim.Tick(2000);

        var adam = sim.GetAgent("Adam");
        var eve  = sim.GetAgent("Eve");

        if (!adam.IsAlive)
            Assert.False(adam.DeathCause?.Contains("starvation") ?? false,
                $"Adam should not die of starvation. Cause: {adam.DeathCause}");
        if (!eve.IsAlive)
            Assert.False(eve.DeathCause?.Contains("starvation") ?? false,
                $"Eve should not die of starvation. Cause: {eve.DeathCause}");
    }
}
