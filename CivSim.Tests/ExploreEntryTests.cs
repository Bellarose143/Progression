using CivSim.Core;
using Xunit;

namespace CivSim.Tests;

/// <summary>
/// Tests for Explore mode entry conditions (CanEnterExplore gates).
/// These would have caught the HomePull drift bug where agents immediately
/// entered Explore mode because entry conditions were too permissive.
/// </summary>
public class ExploreEntryTests
{
    /// <summary>
    /// An agent not at their home tile should never enter Explore.
    /// Expeditions launch FROM home, not from random field positions.
    /// </summary>
    [Fact]
    public void Explore_Blocked_When_Not_At_Home()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Alice", isMale: false, hunger: 90f)
            .AgentAt("Alice", 3, 0)       // 3 tiles east of home
            .AgentHome("Alice", 0, 0)
            .ShelterAt(0, 0)
            .AgentInventory("Alice", ResourceType.Berries, 8)
            .Build();

        sim.Tick(10);

        var alice = sim.GetAgent("Alice");
        Assert.NotEqual(BehaviorMode.Explore, alice.CurrentMode);
    }

    /// <summary>
    /// An agent that JUST entered Home mode should not immediately explore.
    /// They need to settle (eat, deposit, craft) before launching an expedition.
    /// ExploreMinHomeDwell = 100 ticks.
    /// </summary>
    [Fact]
    public void Explore_Blocked_Before_Minimum_Dwell_Time()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Alice", isMale: false, hunger: 95f, health: 100)
            .AgentAt("Alice", 0, 0)
            .AgentHome("Alice", 0, 0)
            .ShelterAt(0, 0)
            .AgentInventory("Alice", ResourceType.Berries, 8)
            .Build();

        // Run 50 ticks — well under the 100-tick dwell requirement
        sim.Tick(50);

        var alice = sim.GetAgent("Alice");
        Assert.NotEqual(BehaviorMode.Explore, alice.CurrentMode);
    }

    /// <summary>
    /// An agent without shelter should never enter Explore.
    /// Shelter is a prerequisite for stability.
    /// </summary>
    [Fact]
    public void Explore_Blocked_Without_Shelter()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Alice", isMale: false, hunger: 90f, health: 100)
            .AgentAt("Alice", 0, 0)
            .AgentHome("Alice", 0, 0)
            // No ShelterAt — no shelter
            .AgentInventory("Alice", ResourceType.Berries, 8)
            .Build();

        sim.Tick(150); // Past dwell time, but no shelter

        var alice = sim.GetAgent("Alice");
        Assert.NotEqual(BehaviorMode.Explore, alice.CurrentMode);
    }

    /// <summary>
    /// An agent without enough food should not explore.
    /// ExploreEntryFood = 6 for non-explorers.
    /// </summary>
    [Fact]
    public void Explore_Blocked_Without_Enough_Food()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Alice", isMale: false, hunger: 90f, health: 100)
            .AgentAt("Alice", 0, 0)
            .AgentHome("Alice", 0, 0)
            .ShelterAt(0, 0)
            .AgentInventory("Alice", ResourceType.Berries, 3) // Below threshold of 6
            .Build();

        sim.Tick(150);

        var alice = sim.GetAgent("Alice");
        // Agent may forage or stay home, but should not explore with only 3 food
        Assert.NotEqual(BehaviorMode.Explore, alice.CurrentMode);
    }

    /// <summary>
    /// A hungry agent (below ExploreEntryHunger=80) should not explore.
    /// </summary>
    [Fact]
    public void Explore_Blocked_When_Hungry()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Alice", isMale: false, hunger: 60f, health: 100)
            .AgentAt("Alice", 0, 0)
            .AgentHome("Alice", 0, 0)
            .ShelterAt(0, 0)
            .AgentInventory("Alice", ResourceType.Berries, 8)
            .Build();

        sim.Tick(150);

        var alice = sim.GetAgent("Alice");
        Assert.NotEqual(BehaviorMode.Explore, alice.CurrentMode);
    }

    /// <summary>
    /// A low-health agent (below ExploreEntryHealth=80) should not explore.
    /// </summary>
    [Fact]
    public void Explore_Blocked_When_Low_Health()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Alice", isMale: false, hunger: 90f, health: 50)
            .AgentAt("Alice", 0, 0)
            .AgentHome("Alice", 0, 0)
            .ShelterAt(0, 0)
            .AgentInventory("Alice", ResourceType.Berries, 8)
            .Build();

        sim.Tick(150);

        var alice = sim.GetAgent("Alice");
        Assert.NotEqual(BehaviorMode.Explore, alice.CurrentMode);
    }
}
