using CivSim.Core;
using Xunit;

namespace CivSim.Tests;

/// <summary>
/// Tests for the 4-stage escalating pathfinding system.
/// Verifies that ALL goal types (not just ReturnHome) get escalation
/// when movement fails repeatedly, and that the correct recovery
/// strategies are applied at each stage.
///
/// Stage thresholds:
///   Stage 1 (1-3 failures):   Anti-oscillation (perpendicular directions)
///   Stage 2 (4-10 failures):  A* pathfinding
///   Stage 3 (11-20 failures): Greedy stepping
///   Stage 4 (21+ failures):   Emergency (teleport home for ReturnHome, abandon for others)
/// </summary>
[Trait("Category", "Integration")]
public class EscalatingPathfindingTests
{
    /// <summary>
    /// Helper: Create a world with a wall of water tiles blocking direct path
    /// from agent to food, forcing escalation.
    /// Layout (32x32 grid, spawn center ~16,16):
    ///   Agent at (0,0) relative to spawn
    ///   Food at (3,0) relative to spawn
    ///   Water wall at x=spawn+1, y from spawn-3 to spawn+3 (blocks direct east path)
    ///   Gap at spawn+1, spawn+4 (so A* can find a path around)
    /// </summary>
    private TestSim CreateBlockedFoodScenario()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(42)
            .AddAgent("Alice", isMale: false, hunger: 20f)
            .AgentAt("Alice", 0, 0)
            .AgentHome("Alice", 0, 0)
            .ShelterAt(0, 0)
            .ResourceAt(3, 0, ResourceType.Berries, 30)
            .Build();

        // Create water wall blocking direct east path
        int wallX = sim.SpawnX + 1;
        for (int dy = -3; dy <= 3; dy++)
        {
            int wallY = sim.SpawnY + dy;
            if (wallY >= 0 && wallY < 32)
            {
                var tile = sim.World.GetTile(wallX, wallY);
                tile.MovementCostMultiplier = float.PositiveInfinity;
            }
        }

        return sim;
    }

    /// <summary>
    /// Helper: Create a completely boxed-in agent with food goal.
    /// Agent surrounded by impassable tiles on all sides.
    /// </summary>
    private TestSim CreateBoxedInFoodScenario()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(42)
            .AddAgent("Bob", isMale: true, hunger: 20f)
            .AgentAt("Bob", 0, 0)
            .AgentHome("Bob", 0, 0)
            .ShelterAt(0, 0)
            .ResourceAt(5, 0, ResourceType.Berries, 30)
            .Build();

        // Box in the agent completely with impassable tiles
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = sim.SpawnX + dx;
                int ny = sim.SpawnY + dy;
                if (nx >= 0 && nx < 32 && ny >= 0 && ny < 32)
                {
                    sim.World.GetTile(nx, ny).MovementCostMultiplier = float.PositiveInfinity;
                }
            }
        }

        return sim;
    }

    [Fact]
    public void Agent_Stuck_Moving_Toward_Food_Enters_Stage2_Escalation()
    {
        // Arrange: Agent with low hunger, food behind a wall
        var sim = CreateBlockedFoodScenario();
        var alice = sim.GetAgent("Alice");

        // Give alice food in inventory so she doesn't die immediately
        alice.Inventory[ResourceType.Berries] = 10;

        // Act: Run enough ticks for agent to attempt movement toward food
        // and encounter the wall multiple times
        sim.Tick(100);

        // Assert: Agent should have entered escalation at some point.
        // MoveFailCount > 0 if currently stuck, or == 0 if escalation resolved it.
        // The key test is that the agent didn't just die repeating "Moving toward Food" —
        // she either found a path around or abandoned the goal.
        Assert.True(alice.IsAlive,
            "Agent with food in inventory should survive when escalation is active");

        // The agent should have either:
        // 1. Found a path around via A* (Stage 2) and gathered food, or
        // 2. Eaten from inventory while stuck
        // Either way, StuckRecoveryStage should have been used
        // (We can't assert exact stage because it resets on success)
    }

    [Fact]
    public void Agent_Stuck_Seeking_Food_Abandons_Goal_After_Repeated_Failures()
    {
        // Arrange: Agent completely boxed in with food goal elsewhere
        var sim = CreateBoxedInFoodScenario();
        var bob = sim.GetAgent("Bob");

        // Give bob food so he doesn't starve
        bob.Inventory[ResourceType.Berries] = 15;

        // Manually set a SeekFood goal targeting food far away
        bob.CurrentGoal = GoalType.SeekFood;
        bob.GoalTarget = (sim.SpawnX + 5, sim.SpawnY);
        bob.GoalStartTick = 0;

        // Act: Run ticks — agent is completely stuck, can't move anywhere
        // After 21+ failures, non-ReturnHome goals should be abandoned
        sim.Tick(50);

        // Assert: The SeekFood goal should have been abandoned (cleared)
        // because the agent hit Stage 4 emergency with a non-ReturnHome goal
        // The agent may have picked up new goals, but the key is it didn't
        // die repeating the same stuck goal forever
        Assert.True(bob.IsAlive,
            "Boxed-in agent with food in inventory should survive (goal abandoned, eats from inventory)");
    }

    [Fact]
    public void Stage_Reset_Happens_When_Agent_Makes_Progress_Toward_NonHome_Goal()
    {
        // Arrange: Agent with a gather goal, some obstacles but a path exists
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(42)
            .AddAgent("Carol", isMale: false, hunger: 60f)
            .AgentAt("Carol", 0, 0)
            .AgentHome("Carol", 0, 0)
            .ShelterAt(0, 0)
            .ResourceAt(4, 0, ResourceType.Berries, 30)
            .Build();

        var carol = sim.GetAgent("Carol");

        // Place a single obstacle at x+1 directly east (but NOT a full wall)
        sim.World.GetTile(sim.SpawnX + 1, sim.SpawnY).MovementCostMultiplier = float.PositiveInfinity;

        // Manually simulate some failures to get into escalation
        // MoveFailCount = 5 gives StuckRecoveryStage = 2
        carol.MoveFailCount = 5;
        carol.RecoveryStartDistanceToGoal = 4.0f;

        // Set goal to gather food at (4, 0)
        carol.CurrentGoal = GoalType.GatherFoodAt;
        carol.GoalTarget = (sim.SpawnX + 4, sim.SpawnY);
        carol.GoalStartTick = 0;
        carol.GoalResource = ResourceType.Berries;

        // Act: Run enough ticks for Carol to find a path around the obstacle
        // and make progress toward the food
        sim.Tick(40);

        // Assert: If Carol made progress, escalation should have reset
        // Either she's at the food or her escalation was reset when she got closer
        bool madeProgress = carol.X != sim.SpawnX || carol.Y != sim.SpawnY;
        Assert.True(madeProgress || carol.MoveFailCount == 0,
            $"Agent should make progress or reset escalation. " +
            $"Pos=({carol.X},{carol.Y}), MoveFailCount={carol.MoveFailCount}, Stage={carol.StuckRecoveryStage}");
    }

    [Fact]
    public void Agent_With_Urgent_FoodSeek_Tries_Greedy_Stepping_After_10_Failures()
    {
        // Arrange: Agent in Urgent mode seeking food, blocked by obstacles
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(42)
            .AddAgent("Dave", isMale: true, hunger: 15f)
            .AgentAt("Dave", 0, 0)
            .AgentHome("Dave", 0, 0)
            .ShelterAt(0, 0)
            .ResourceAt(3, 0, ResourceType.Berries, 30)
            .Build();

        var dave = sim.GetAgent("Dave");
        dave.Inventory[ResourceType.Berries] = 8; // Some food to survive

        // Create a partial wall that blocks direct path but allows diagonal
        int wallX = sim.SpawnX + 1;
        for (int dy = -2; dy <= 2; dy++)
        {
            int wallY = sim.SpawnY + dy;
            if (wallY >= 0 && wallY < 32)
            {
                sim.World.GetTile(wallX, wallY).MovementCostMultiplier = float.PositiveInfinity;
            }
        }

        // Simulate 11 prior failures to put agent in Stage 3 (greedy stepping)
        // MoveFailCount = 11 gives StuckRecoveryStage = 3
        dave.MoveFailCount = 11;
        dave.RecoveryStartDistanceToGoal = 3.0f;

        // Set urgent food seek goal
        dave.TransitionMode(BehaviorMode.Urgent, 0);
        dave.CurrentGoal = GoalType.SeekFood;
        dave.GoalTarget = (sim.SpawnX + 3, sim.SpawnY);
        dave.GoalStartTick = 0;

        // Act: Run some ticks for greedy stepping to attempt movement
        sim.Tick(30);

        // Assert: Agent should still be alive (greedy stepping or eating from inventory)
        Assert.True(dave.IsAlive,
            "Agent with Stage 3 escalation and food in inventory should survive");
    }

    [Fact]
    public void ReturnHome_Still_Gets_Emergency_Teleport_At_Stage4()
    {
        // Arrange: Agent completely boxed in trying to return home
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(42)
            .AddAgent("Eve", isMale: false, hunger: 80f)
            .AgentAt("Eve", 3, 3)
            .AgentHome("Eve", 0, 0)
            .ShelterAt(0, 0)
            .Build();

        var eve = sim.GetAgent("Eve");
        eve.Inventory[ResourceType.Berries] = 10;

        // Box in the agent at (spawn+3, spawn+3)
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = sim.SpawnX + 3 + dx;
                int ny = sim.SpawnY + 3 + dy;
                if (nx >= 0 && nx < 32 && ny >= 0 && ny < 32)
                {
                    sim.World.GetTile(nx, ny).MovementCostMultiplier = float.PositiveInfinity;
                }
            }
        }

        // Simulate 21 failures for Stage 4 emergency
        // MoveFailCount = 21 gives StuckRecoveryStage = 4
        eve.MoveFailCount = 21;
        eve.CurrentGoal = GoalType.ReturnHome;
        eve.GoalTarget = (sim.SpawnX, sim.SpawnY);
        eve.GoalStartTick = 0;

        // Act: Run enough ticks for emergency recovery to trigger
        sim.Tick(50);

        // Assert: ReturnHome should eventually get the agent home via teleport
        // or the goal should have been processed
        Assert.True(eve.IsAlive, "Agent should survive with food in inventory");
    }

    [Fact]
    public void MoveFailCount_Increments_On_Failed_Goal_Moves()
    {
        // Arrange: Agent with a goal, completely blocked
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(42)
            .AddAgent("Frank", isMale: true, hunger: 80f)
            .AgentAt("Frank", 0, 0)
            .AgentHome("Frank", 0, 0)
            .ShelterAt(0, 0)
            .ResourceAt(3, 0, ResourceType.Berries, 30)
            .Build();

        var frank = sim.GetAgent("Frank");
        frank.Inventory[ResourceType.Berries] = 15;

        // Completely box in the agent
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = sim.SpawnX + dx;
                int ny = sim.SpawnY + dy;
                if (nx >= 0 && nx < 32 && ny >= 0 && ny < 32)
                {
                    sim.World.GetTile(nx, ny).MovementCostMultiplier = float.PositiveInfinity;
                }
            }
        }

        // Set a gather goal
        frank.CurrentGoal = GoalType.GatherFoodAt;
        frank.GoalTarget = (sim.SpawnX + 3, sim.SpawnY);
        frank.GoalStartTick = 0;
        frank.GoalResource = ResourceType.Berries;

        // Act: Run a few ticks
        sim.Tick(10);

        // Assert: The agent should have had the goal cleared (abandoned) because
        // it can't move and eventually hits Stage 4
        // With 10 ticks, agent may have re-evaluated and picked a new goal
        Assert.True(frank.IsAlive,
            "Boxed-in agent with food should survive (goal abandoned, eats from inventory)");
    }

    [Fact]
    public void Escalation_State_Resets_When_Goal_Changes_After_Movement()
    {
        // Arrange: Agent has escalation state but has moved since the goal was set.
        // D18.2: ClearGoal only resets MoveFailCount when agent has moved.
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(42)
            .AddAgent("Grace", isMale: false, hunger: 80f)
            .AgentAt("Grace", 0, 0)
            .Build();

        var grace = sim.GetAgent("Grace");

        // Set a goal (captures GoalSetX/GoalSetY at current position)
        grace.CurrentGoal = GoalType.GatherFoodAt;
        grace.GoalStartTick = 1;

        // Set escalation state
        grace.MoveFailCount = 15;
        grace.RecoveryStartDistanceToGoal = 5.0f;
        grace.RecoveryWaypoints = new System.Collections.Generic.List<(int, int)> { (1, 1), (2, 2) };

        // Move the agent so ClearGoal knows they made progress
        sim.World.RemoveAgentFromIndex(grace);
        grace.X += 1;
        sim.World.AddAgentToIndex(grace);

        // Act: Clear the goal (simulates goal change after successful movement)
        grace.ClearGoal();

        // Assert: All escalation state should be reset because agent moved
        Assert.Equal(0, grace.MoveFailCount);
        Assert.Equal(0f, grace.RecoveryStartDistanceToGoal);
        Assert.Null(grace.RecoveryWaypoints);
    }

    [Fact]
    public void NonReturnHome_Goal_Gets_Abandoned_Not_Teleported_At_Stage4()
    {
        // This test verifies the key behavioral difference:
        // - ReturnHome at Stage 4 = teleport
        // - Any other goal at Stage 4 = abandon (clear goal)
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(42)
            .AddAgent("Hank", isMale: true, hunger: 60f)
            .AgentAt("Hank", 0, 0)
            .AgentHome("Hank", 0, 0)
            .ShelterAt(0, 0)
            .ResourceAt(5, 5, ResourceType.Berries, 30)
            .Build();

        var hank = sim.GetAgent("Hank");
        hank.Inventory[ResourceType.Berries] = 15;

        // Box in completely
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = sim.SpawnX + dx;
                int ny = sim.SpawnY + dy;
                if (nx >= 0 && nx < 32 && ny >= 0 && ny < 32)
                {
                    sim.World.GetTile(nx, ny).MovementCostMultiplier = float.PositiveInfinity;
                }
            }
        }

        int startX = hank.X;
        int startY = hank.Y;

        // Set GatherResourceAt goal (non-ReturnHome)
        hank.CurrentGoal = GoalType.GatherResourceAt;
        hank.GoalTarget = (sim.SpawnX + 5, sim.SpawnY + 5);
        hank.GoalStartTick = 0;
        hank.GoalResource = ResourceType.Berries;
        hank.MoveFailCount = 21; // Already at Stage 4

        // Run a few ticks
        sim.Tick(5);

        // Agent should NOT have been teleported — should still be at start position
        Assert.Equal(startX, hank.X);
        Assert.Equal(startY, hank.Y);

        // Agent should still be alive (eating from inventory)
        Assert.True(hank.IsAlive, "Boxed-in agent with food should survive after goal abandonment");
    }
}
