using CivSim.Core;
using Xunit;

namespace CivSim.Tests;

/// <summary>
/// D18.2: Kill-Box Safety Net Tests.
/// Validates the invariant: an agent with food must NEVER starve.
/// </summary>
public class KillBoxSafetyNetTests
{
    [Fact]
    public void Agent_Eats_WhenStarving_DespiteHardDistanceCeiling()
    {
        // Place agent far from home (beyond ForageMaxRange=100), starving with food.
        // The Hard Distance Ceiling should NOT prevent eating.
        // Use 256x256 grid so offsets don't get clamped near world edges.
        var sim = new TestSimBuilder()
            .GridSize(256, 256).Seed(42)
            .AddAgent("Alice", isMale: false, hunger: 20f)
            .AgentAt("Alice", 105, 0) // Agent is 105 tiles east of spawn center
            .AgentHome("Alice", 0, 0)
            .ShelterAt(0, 0)
            .AgentInventory("Alice", ResourceType.Berries, 5)
            .Build();

        var alice = sim.GetAgent("Alice");
        int distFromHome = Math.Max(
            Math.Abs(alice.X - alice.HomeTile!.Value.X),
            Math.Abs(alice.Y - alice.HomeTile!.Value.Y));

        // Verify setup: agent is beyond ForageMaxRange
        Assert.True(distFromHome > SimConfig.ForageMaxRange,
            $"Setup check: agent should be beyond ceiling. Dist={distFromHome}, ceiling={SimConfig.ForageMaxRange}");

        // Run a few ticks — agent should eat, not just attempt to move home
        sim.Tick(5);

        // Agent must have eaten: hunger should be higher than starting 20, or food consumed
        Assert.True(alice.Hunger > 20f || alice.FoodInInventory() < 5,
            $"Starving agent with food should eat despite Hard Distance Ceiling. " +
            $"Hunger={alice.Hunger:F1}, food={alice.FoodInInventory()}");
    }

    [Fact]
    public void ClearGoal_PreservesMoveFailCount_WhenAgentHasNotMoved()
    {
        var world = new World(32, 32, 1);
        var sim = new Simulation(world, 1);
        var agent = sim.SpawnAgent();

        // Set up a goal at agent's current position
        agent.CurrentGoal = GoalType.ReturnHome;
        agent.GoalTarget = (agent.X, agent.Y);
        agent.GoalStartTick = 1; // This captures GoalSetX/GoalSetY = current position

        // Simulate stuck: increment MoveFailCount without moving
        agent.MoveFailCount = 3;

        // ClearGoal while agent hasn't moved — MoveFailCount must persist
        agent.ClearGoal();

        Assert.Equal(3, agent.MoveFailCount);

        // Now set a new goal, move the agent, and clear again — MoveFailCount should reset
        agent.CurrentGoal = GoalType.ReturnHome;
        agent.GoalStartTick = 2; // Captures position again

        agent.MoveFailCount = 5;

        // Move the agent to a different position
        world.RemoveAgentFromIndex(agent);
        agent.X += 1;
        world.AddAgentToIndex(agent);

        // ClearGoal after moving — MoveFailCount should reset to 0
        agent.ClearGoal();

        Assert.Equal(0, agent.MoveFailCount);
    }

    [Fact]
    public void ShouldInterrupt_ReturnsTrue_WhenStarving_DuringReturnHome()
    {
        // Agent is mid-ReturnHome move, starving with food in inventory.
        // ShouldInterrupt must return true so the agent can eat.
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(42)
            .AddAgent("Bob", isMale: true, hunger: 20f)
            .AgentAt("Bob", 5, 0)
            .AgentHome("Bob", 0, 0)
            .ShelterAt(0, 0)
            .AgentInventory("Bob", ResourceType.Berries, 5)
            .Build();

        var bob = sim.GetAgent("Bob");

        // Set up ReturnHome goal and a pending Move action
        bob.CurrentGoal = GoalType.ReturnHome;
        bob.GoalTarget = bob.HomeTile;
        bob.GoalStartTick = 1;
        bob.PendingAction = ActionType.Move;
        bob.ActionProgress = 0f;
        bob.ActionDurationTicks = 2f;
        bob.ActionTarget = (bob.X - 1, bob.Y);

        // Agent is busy with a move, starving, has food.
        // After 1 tick, the interrupt should fire and agent should eat.
        Assert.True(bob.IsBusy, "Setup check: agent should be busy with Move");

        sim.Tick(3);

        // Agent must have eaten — food consumed or hunger improved
        Assert.True(bob.Hunger > 20f || bob.FoodInInventory() < 5,
            $"Starving agent mid-ReturnHome should be interrupted to eat. " +
            $"Hunger={bob.Hunger:F1}, food={bob.FoodInInventory()}");
    }
}
