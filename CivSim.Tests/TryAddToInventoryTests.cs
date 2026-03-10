using CivSim.Core;
using Xunit;

namespace CivSim.Tests;

/// <summary>
/// D22 Gate 1: TryAddToInventory centralized invariant tests.
/// </summary>
[Trait("Category", "Integration")]
public class TryAddToInventoryTests
{
    private Agent CreateAgent(float hunger = 90f)
    {
        var sim = new TestSimBuilder()
            .GridSize(16, 16).Seed(1)
            .AddAgent("Test", isMale: true, hunger: hunger)
            .AgentAt("Test", 0, 0)
            .Build();
        return sim.GetAgent("Test");
    }

    [Fact]
    public void TryAddToInventory_CapacityLimit()
    {
        var agent = CreateAgent();
        // Fill inventory to capacity (20)
        for (int i = 0; i < 20; i++)
            agent.TryAddToInventory(ResourceType.Berries, 1);

        Assert.Equal(20, agent.InventoryCount());

        // Next add should fail
        bool result = agent.TryAddToInventory(ResourceType.Berries, 1);
        Assert.False(result, "TryAddToInventory should return false when inventory is at capacity (20/20)");
        Assert.Equal(20, agent.InventoryCount());
    }

    [Fact]
    public void TryAddToInventory_NonFoodCap()
    {
        var agent = CreateAgent();
        // Add 9 non-food items (3 Stone + 3 Ore + 3 Wood)
        agent.TryAddToInventory(ResourceType.Stone, 3);
        agent.TryAddToInventory(ResourceType.Ore, 3);
        agent.TryAddToInventory(ResourceType.Wood, 3);

        // Stone (non-food) should be blocked at 9 non-food
        bool stoneResult = agent.TryAddToInventory(ResourceType.Stone, 1);
        Assert.False(stoneResult, "TryAddToInventory should return false for non-food when carrying 9+ non-food items");

        // Berries (food) should still work
        bool berriesResult = agent.TryAddToInventory(ResourceType.Berries, 1);
        Assert.True(berriesResult, "TryAddToInventory should return true for food even when carrying 9+ non-food items");
    }

    [Fact]
    public void TryAddToInventory_HungerGate()
    {
        var agent = CreateAgent(hunger: 40f); // Below 50 threshold

        // Stone (non-food) should be blocked when hungry
        bool stoneResult = agent.TryAddToInventory(ResourceType.Stone, 1);
        Assert.False(stoneResult, "TryAddToInventory should return false for non-food when Hunger < 50");

        // Berries (food) should still work when hungry
        bool berriesResult = agent.TryAddToInventory(ResourceType.Berries, 1);
        Assert.True(berriesResult, "TryAddToInventory should return true for food even when Hunger < 50");
    }

    [Fact]
    public void TryAddToInventory_NormalAdd()
    {
        var agent = CreateAgent();
        Assert.Equal(0, agent.InventoryCount());

        bool result = agent.TryAddToInventory(ResourceType.Stone, 3);
        Assert.True(result, "TryAddToInventory should return true for normal add with space and no gate triggers");
        Assert.Equal(3, agent.Inventory[ResourceType.Stone]);
        Assert.Equal(3, agent.InventoryCount());

        // Add food
        result = agent.TryAddToInventory(ResourceType.Berries, 5);
        Assert.True(result);
        Assert.Equal(5, agent.Inventory[ResourceType.Berries]);
        Assert.Equal(8, agent.InventoryCount());
    }
}
