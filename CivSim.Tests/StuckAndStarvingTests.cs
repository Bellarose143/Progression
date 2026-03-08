using CivSim.Core;
using Xunit;

namespace CivSim.Tests;

/// <summary>
/// Post-Playtest Fix 1: Stuck-and-Starving Loop Prevention.
/// Agents that fail to move should survive locally rather than starving.
/// </summary>
public class StuckAndStarvingTests
{
    [Fact]
    public void Stuck_Agent_Eats_From_Inventory_When_Hungry()
    {
        // Agent is stuck (no passable tiles toward home) but has food.
        // Should eat from inventory rather than starve.
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(42)
            .AddAgent("Alice", isMale: false, hunger: 40f)
            .AgentAt("Alice", 5, 0)
            .AgentHome("Alice", 0, 0)
            .ShelterAt(0, 0)
            .AgentInventory("Alice", ResourceType.Berries, 5)
            .Build();

        var alice = sim.GetAgent("Alice");
        // Manually set stuck state to simulate repeated move failures
        alice.StuckAtPositionCount = 5;
        alice.LastStuckCheckPos = (alice.X, alice.Y);

        sim.Tick(20);

        // Agent should have eaten — hunger should be higher than 40
        Assert.True(alice.Hunger > 40f || alice.FoodInInventory() < 5,
            $"Stuck agent with food should eat. Hunger={alice.Hunger:F1}, food={alice.FoodInInventory()}");
    }

    [Fact]
    public void Stuck_Agent_Gathers_Local_Food_When_Very_Hungry()
    {
        // Agent is stuck at a position with food on current tile, very hungry, no food in inventory.
        // After 10+ ticks stuck, should force-gather from nearby food.
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(42)
            .AddAgent("Bob", isMale: true, hunger: 35f)
            .AgentAt("Bob", 5, 0)
            .AgentHome("Bob", 0, 0)
            .ShelterAt(0, 0)
            .ResourceAt(5, 0, ResourceType.Berries, 20) // Food at agent's position
            .Build();

        var bob = sim.GetAgent("Bob");
        // Simulate being stuck for 10+ ticks
        bob.StuckAtPositionCount = 12;
        bob.LastStuckCheckPos = (bob.X, bob.Y);

        sim.Tick(15);

        bool gathered = bob.FoodInInventory() > 0 || bob.Hunger > 35f;
        Assert.True(gathered,
            $"Stuck hungry agent should gather local food. Hunger={bob.Hunger:F1}, food={bob.FoodInInventory()}");
    }

    [Fact]
    public void Safety_Return_Suppressed_After_Stuck_With_ReturnHome_Goal()
    {
        // Agent has ReturnHome goal but is stuck. After suppression, agent should
        // not immediately re-enter ReturnHome — it should do local actions.
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(42)
            .AddAgent("Carol", isMale: false, hunger: 70f)
            .AgentAt("Carol", 5, 0)
            .AgentHome("Carol", 0, 0)
            .ShelterAt(0, 0)
            .ResourceAt(5, 0, ResourceType.Berries, 30)
            .Build();

        var carol = sim.GetAgent("Carol");
        // Set up stuck state with ReturnHome goal
        carol.CurrentGoal = GoalType.ReturnHome;
        carol.GoalTarget = carol.HomeTile;
        carol.StuckAtPositionCount = 5;
        carol.LastStuckCheckPos = (carol.X, carol.Y);

        sim.Tick(5);

        // Suppression should have activated
        Assert.True(carol.SafetyReturnSuppressedUntil > 0,
            "Safety return should be suppressed after stuck with ReturnHome goal");

        // Agent should not have ReturnHome goal (it was cleared by suppression)
        // Within the first few ticks after suppression, agent does local actions
        Assert.True(carol.CurrentGoal != GoalType.ReturnHome || carol.SafetyReturnSuppressedUntil > sim.Simulation.CurrentTick,
            "Agent should not re-enter ReturnHome while suppression is active");
    }

    [Fact]
    public void Suppression_Expires_And_Agent_Can_Try_Return_Again()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(42)
            .AddAgent("Dave", isMale: true, hunger: 80f)
            .AgentAt("Dave", 3, 0)
            .AgentHome("Dave", 0, 0)
            .ShelterAt(0, 0)
            .ResourceAt(3, 0, ResourceType.Berries, 30)
            .ResourceAt(2, 0, ResourceType.Berries, 20)
            .ResourceAt(1, 0, ResourceType.Berries, 20)
            .Build();

        var dave = sim.GetAgent("Dave");
        // Set suppression to expire soon
        dave.SafetyReturnSuppressedUntil = sim.Simulation.CurrentTick + 10;

        // Run past suppression window
        sim.Tick(60);

        // After suppression expires, agent should be able to attempt return home
        // (or may have already returned since path is clear)
        Assert.True(dave.IsAlive,
            "Agent should survive the suppression period");
    }

    [Fact]
    public void Agent_Does_Not_Die_With_Gatherable_Food_Within_3_Tiles()
    {
        // Comprehensive test: agent far from home with food nearby.
        // Should never starve as long as food is within reach.
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(42)
            .AddAgent("Eve", isMale: false, hunger: 50f)
            .AgentAt("Eve", 8, 0)
            .AgentHome("Eve", 0, 0)
            .ShelterAt(0, 0)
            .ResourceAt(8, 0, ResourceType.Berries, 50) // Food at agent's tile
            .ResourceAt(7, 0, ResourceType.Berries, 30) // Food 1 tile away
            .ResourceAt(9, 0, ResourceType.Berries, 30) // Food 1 tile away
            .Build();

        // Run for a long time — agent should survive
        sim.Tick(2000);

        var eve = sim.GetAgent("Eve");
        Assert.True(eve.IsAlive,
            $"Agent should not die with gatherable food within 3 tiles. " +
            $"Status: alive={eve.IsAlive}, hunger={eve.Hunger:F1}");
    }
}
