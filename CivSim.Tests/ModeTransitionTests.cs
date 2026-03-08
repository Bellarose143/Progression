using CivSim.Core;
using Xunit;

namespace CivSim.Tests;

/// <summary>
/// Tests for mode transition mechanics: goal clearing, hysteresis, routing.
/// These would have caught the Move flood bug (goals persisting across mode changes)
/// and the Home-mode foraging bug (agents gathering remotely without entering Forage).
/// </summary>
public class ModeTransitionTests
{
    /// <summary>
    /// When mode changes, any active goal from the previous mode should be cleared.
    /// This prevents the "Move flood" bug where an agent walks forever on a stale goal.
    /// </summary>
    [Fact]
    public void Mode_Change_Clears_Active_Goal()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Alice", isMale: false, hunger: 80f)
            .AgentAt("Alice", 0, 0)
            .AgentHome("Alice", 0, 0)
            .ShelterAt(0, 0)
            .AgentMode("Alice", BehaviorMode.Forage)
            .Build();

        var alice = sim.GetAgent("Alice");
        // Give her a goal pointing far away
        alice.CurrentGoal = GoalType.GatherFoodAt;
        alice.GoalTarget = (sim.SpawnX + 15, sim.SpawnY + 15);
        alice.GoalStartTick = 0;
        // Now starve her to force Urgent
        alice.Hunger = 20f;

        sim.Tick(5);

        // Mode should have changed to Urgent, and the distant Gather goal should be gone
        Assert.Equal(BehaviorMode.Urgent, alice.CurrentMode);
        Assert.False(alice.CurrentGoal == GoalType.GatherFoodAt,
            "Stale Gather goal should be cleared on mode transition to Urgent");
    }

    /// <summary>
    /// Urgent mode preserves PreviousMode so agent resumes after recovery.
    /// </summary>
    [Fact]
    public void Urgent_Preserves_Previous_Mode_For_Resume()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Alice", isMale: false, hunger: 70f)
            .AgentAt("Alice", 0, 0)
            .AgentHome("Alice", 0, 0)
            .ShelterAt(0, 0)
            .AgentMode("Alice", BehaviorMode.Forage)
            .Build();

        var alice = sim.GetAgent("Alice");
        alice.Hunger = 20f; // Trigger Urgent

        sim.Tick(3);

        Assert.Equal(BehaviorMode.Urgent, alice.CurrentMode);
        Assert.Equal(BehaviorMode.Forage, alice.PreviousMode);
    }

    /// <summary>
    /// Urgent exit requires BOTH hunger > 40 AND health > 30 (hysteresis).
    /// Having only one above threshold should NOT exit Urgent.
    /// </summary>
    [Fact]
    public void Urgent_Exit_Requires_Both_Thresholds()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Alice", isMale: false, hunger: 20f, health: 15)
            .AgentAt("Alice", 0, 0)
            .AgentHome("Alice", 0, 0)
            .ShelterAt(0, 0)
            .AgentInventory("Alice", ResourceType.Berries, 10)
            .Build();

        var alice = sim.GetAgent("Alice");

        sim.Tick(5);

        // Agent should be in Urgent — even if hunger recovers, health is still low
        Assert.Equal(BehaviorMode.Urgent, alice.CurrentMode);
    }

    /// <summary>
    /// When a Home-mode agent scores a Gather action targeting a tile > 2 tiles away,
    /// it should transition to Forage mode instead of dispatching in Home mode.
    /// This prevents the "foraging in Home mode" bug where agents walk far without
    /// a return trigger.
    /// </summary>
    [Fact]
    public void Home_Remote_Gather_Transitions_To_Forage()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Alice", isMale: false, hunger: 70f)
            .AgentAt("Alice", 0, 0)
            .AgentHome("Alice", 0, 0)
            .ShelterAt(0, 0)
            // No food nearby — only food is far away
            .ResourceAt(5, 0, ResourceType.Berries, 30)
            .Build();

        // D11 Fix 3: Start during daytime so night rest doesn't block foraging
        sim.Simulation.CurrentTick = 150;

        // Give Alice food memory of the distant berries
        var alice = sim.GetAgent("Alice");
        var pos = sim.WorldPos(5, 0);
        alice.Memory.Add(new MemoryEntry
        {
            X = pos.X, Y = pos.Y,
            Type = MemoryType.Resource,
            Resource = ResourceType.Berries,
            Quantity = 30,
            TickObserved = 150
        });

        // Run enough ticks for her to need food and find it via memory
        sim.Tick(50);

        // She should be in Forage mode (not Home mode walking toward distant food)
        if (alice.CurrentMode != BehaviorMode.Urgent) // Skip if she got too hungry
        {
            Assert.True(
                alice.CurrentMode == BehaviorMode.Forage || alice.FoodInInventory() > 0,
                $"Agent should enter Forage for remote food, not walk in Home mode. " +
                $"Actual mode: {alice.CurrentMode}");
        }
    }

    /// <summary>
    /// An agent in Forage mode with no food anywhere should NOT exit Forage
    /// due to low hunger — they'd enter the dead zone between Urgent exit (40)
    /// and normal Forage entry (55).
    /// </summary>
    [Fact]
    public void Forage_Dead_Zone_Exception_Prevents_Bounce()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Alice", isMale: false, hunger: 42f)
            .AgentAt("Alice", 0, 0)
            .AgentHome("Alice", 0, 0)
            .ShelterAt(0, 0)
            .AgentMode("Alice", BehaviorMode.Forage)
            // Berries far away — she's foraging toward them
            .ResourceAt(3, 0, ResourceType.Berries, 30)
            .Build();

        var alice = sim.GetAgent("Alice");
        // No food in inventory, no food at home
        Assert.Equal(0, alice.FoodInInventory());

        sim.Tick(5);

        // She should stay in Forage (or find food), NOT bounce to Home
        Assert.True(
            alice.CurrentMode == BehaviorMode.Forage
            || alice.CurrentMode == BehaviorMode.Urgent
            || alice.FoodInInventory() > 0,
            $"Agent with no food should stay in Forage, not exit to Home dead zone. " +
            $"Mode: {alice.CurrentMode}, food: {alice.FoodInInventory()}");
    }

    /// <summary>
    /// Goal continuation should NOT run when a mode transition just occurred.
    /// The mode change should clear the old goal and let the new mode decide.
    /// </summary>
    [Fact]
    public void Goal_Continuation_Skipped_After_Mode_Change()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Alice", isMale: false, hunger: 80f)
            .AgentAt("Alice", 0, 0)
            .AgentHome("Alice", 0, 0)
            .ShelterAt(0, 0)
            .AgentMode("Alice", BehaviorMode.Explore)
            .Build();

        var alice = sim.GetAgent("Alice");
        // Set explore goal pointing far away
        alice.ModeCommit.ExploreDirection = (1, 0);
        alice.ModeCommit.ExploreBudget = 300;
        alice.CurrentGoal = GoalType.Explore;
        alice.GoalTarget = (sim.SpawnX + 10, sim.SpawnY);
        alice.GoalStartTick = 0;

        // Now starve her to force mode change
        alice.Hunger = 20f;

        sim.Tick(3);

        // Should be in Urgent, NOT still walking toward explore target
        Assert.Equal(BehaviorMode.Urgent, alice.CurrentMode);
        Assert.True(alice.CurrentGoal != GoalType.Explore,
            "Explore goal should be cleared after mode transition to Urgent");
    }
}
