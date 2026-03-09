using CivSim.Core;
using Xunit;

namespace CivSim.Tests;

/// <summary>
/// Tests for inventory display filtering logic.
/// The UI renders agent.Inventory.Where(item => item.Value > 0),
/// so zero-count items should never appear in the displayed inventory.
/// </summary>
[Trait("Category", "Integration")]
public class InventoryDisplayTests
{
    [Fact]
    public void InventoryDisplay_HidesZeroCountItems()
    {
        // Arrange: create an agent with a mix of zero and non-zero inventory items
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Alice", isMale: false, hunger: 80f)
            .AgentAt("Alice", 0, 0)
            .AgentInventory("Alice", ResourceType.Berries, 5)
            .AgentInventory("Alice", ResourceType.Stone, 0)
            .AgentInventory("Alice", ResourceType.Wood, 3)
            .Build();

        var alice = sim.GetAgent("Alice");

        // Simulate the same filter the UI uses: only items with Value > 0
        var displayedItems = alice.Inventory
            .Where(item => item.Value > 0)
            .ToList();

        // Assert: zero-count Stone should not appear; Berries and Wood should
        Assert.DoesNotContain(displayedItems, item => item.Key == ResourceType.Stone);
        Assert.Contains(displayedItems, item => item.Key == ResourceType.Berries && item.Value == 5);
        Assert.Contains(displayedItems, item => item.Key == ResourceType.Wood && item.Value == 3);
        Assert.Equal(2, displayedItems.Count);
    }

    [Fact]
    public void InventoryDisplay_ShowsNothingWhenAllZero()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Bob", isMale: true, hunger: 80f)
            .AgentAt("Bob", 0, 0)
            .AgentInventory("Bob", ResourceType.Berries, 0)
            .AgentInventory("Bob", ResourceType.Stone, 0)
            .Build();

        var bob = sim.GetAgent("Bob");

        var displayedItems = bob.Inventory
            .Where(item => item.Value > 0)
            .ToList();

        Assert.Empty(displayedItems);
    }

    [Fact]
    public void InventoryDisplay_ShowsAllWhenNoneZero()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Carol", isMale: false, hunger: 80f)
            .AgentAt("Carol", 0, 0)
            .AgentInventory("Carol", ResourceType.Berries, 2)
            .AgentInventory("Carol", ResourceType.Wood, 4)
            .AgentInventory("Carol", ResourceType.Stone, 1)
            .Build();

        var carol = sim.GetAgent("Carol");

        var displayedItems = carol.Inventory
            .Where(item => item.Value > 0)
            .ToList();

        Assert.Equal(3, displayedItems.Count);
    }
}
