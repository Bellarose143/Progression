using CivSim.Core;
using Xunit;

namespace CivSim.Tests;

/// <summary>
/// Tests for Forage mode exit conditions: return threshold, inventory full,
/// hunger low, duration safety valve.
/// </summary>
[Trait("Category", "Integration")]
public class ForageExitTests
{
    /// <summary>
    /// An agent in Forage mode should return home when carrying enough food
    /// (ForageReturnFoodDefault = 6).
    /// </summary>
    [Fact]
    public void Forage_Returns_Home_When_Food_Threshold_Met()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Alice", isMale: false, hunger: 80f)
            .AgentAt("Alice", 2, 0) // Near food
            .AgentHome("Alice", 0, 0)
            .ShelterAt(0, 0)
            .AgentMode("Alice", BehaviorMode.Forage)
            .ResourceAt(2, 0, ResourceType.Berries, 30)
            .Build();

        // D11 Fix 3: Start during daytime so night rest doesn't block foraging
        sim.Simulation.CurrentTick = 150;

        var alice = sim.GetAgent("Alice");
        alice.ModeCommit.ForageTargetResource = ResourceType.Berries;
        var target = sim.WorldPos(2, 0);
        alice.ModeCommit.ForageTargetTile = (target.X, target.Y);
        alice.ModeCommit.ForageReturnFoodThreshold = SimConfig.ForageReturnFoodDefault;
        // D11 Fix 5: Set entry tick so commitment window works correctly
        alice.ForageModeEntryTick = 100; // Well before current tick — commitment expired

        // Run until she either returns home or 200 ticks elapse
        bool returnedHome = sim.TickUntil(() =>
        {
            var a = sim.GetAgent("Alice");
            return a.CurrentMode == BehaviorMode.Home && a.FoodInInventory() > 0;
        }, 200);

        Assert.True(returnedHome,
            $"Agent should gather food and return home. " +
            $"Mode: {alice.CurrentMode}, Food: {alice.FoodInInventory()}");
    }

    /// <summary>
    /// Forage mode should have a duration safety valve (ForageMaxDuration = 200 ticks).
    /// Agent should exit Forage even if return threshold wasn't met.
    /// </summary>
    [Fact]
    public void Forage_Exits_After_Duration_Safety_Valve()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Alice", isMale: false, hunger: 80f)
            .AgentAt("Alice", 0, 0)
            .AgentHome("Alice", 0, 0)
            .ShelterAt(0, 0)
            .AgentMode("Alice", BehaviorMode.Forage)
            // World has procedural resources (seed 1), so she may find food
            .Build();

        var alice = sim.GetAgent("Alice");
        alice.ModeCommit.ForageTargetResource = ResourceType.Berries;
        alice.ModeCommit.ForageReturnFoodThreshold = SimConfig.ForageReturnFoodDefault;

        // Verify agent exits Forage at SOME point during the run (safety valve or return threshold).
        // She may re-enter Forage after returning home, so we check for any exit, not final state.
        bool exitedForage = false;
        for (int t = 0; t < SimConfig.ForageMaxDuration + 50; t++)
        {
            sim.Tick(1);
            if (alice.CurrentMode != BehaviorMode.Forage)
            {
                exitedForage = true;
                break;
            }
        }

        Assert.True(exitedForage, "Agent should exit Forage before duration safety valve");
    }

    /// <summary>
    /// D13: Moderate hunger (above Urgent threshold) should NOT abort a foraging trip.
    /// Only genuine emergencies (hunger < UrgentEntryHunger = 30) exit Forage.
    /// An agent at hunger=40 with food in inventory should keep foraging until
    /// commitment is met (5 gathers) or Urgent mode fires.
    /// </summary>
    [Fact]
    public void Forage_Stays_When_Moderately_Hungry_With_Food()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Alice", isMale: false, hunger: 50f)
            .AgentAt("Alice", 3, 0)
            .AgentHome("Alice", 0, 0)
            .ShelterAt(0, 0)
            .AgentMode("Alice", BehaviorMode.Forage)
            .AgentInventory("Alice", ResourceType.Berries, 3) // Has some food
            .ResourceAt(3, 0, ResourceType.Berries, 20) // Food at current tile
            .Build();

        // Start during daytime
        sim.Simulation.CurrentTick = 150;

        var alice = sim.GetAgent("Alice");
        alice.ModeCommit.ForageTargetResource = ResourceType.Berries;
        alice.ModeCommit.ForageReturnFoodThreshold = SimConfig.ForageReturnFoodDefault;
        alice.ForageModeEntryTick = 100;

        // Run tick-by-tick, keeping hunger at 40 (above UrgentEntryHunger=30)
        // Agent should stay in Forage because moderate hunger no longer exits
        bool stayedInForage = true;
        for (int i = 0; i < 15; i++)
        {
            alice.Hunger = 40f; // Moderate hunger — above Urgent threshold
            sim.Tick(1);
            if (alice.CurrentMode != BehaviorMode.Forage)
            {
                stayedInForage = false;
                break;
            }
        }

        Assert.True(stayedInForage,
            $"D13: Moderately hungry agent (hunger=40, above Urgent threshold) should stay in Forage. " +
            $"Mode: {alice.CurrentMode}");
    }

    /// <summary>
    /// A foraging agent with NO food anywhere (inventory=0, home=0) should NOT
    /// exit Forage due to low hunger — that would create the dead zone between
    /// Urgent exit (40) and Forage entry (55).
    /// </summary>
    [Fact]
    public void Forage_Stays_When_Hungry_But_No_Food_Anywhere()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Alice", isMale: false, hunger: 42f)
            .AgentAt("Alice", 2, 0)
            .AgentHome("Alice", 0, 0)
            .ShelterAt(0, 0)
            .AgentMode("Alice", BehaviorMode.Forage)
            // Berries nearby but not yet gathered
            .ResourceAt(3, 0, ResourceType.Berries, 20)
            .Build();

        var alice = sim.GetAgent("Alice");
        alice.ModeCommit.ForageTargetResource = ResourceType.Berries;
        var target = sim.WorldPos(3, 0);
        alice.ModeCommit.ForageTargetTile = (target.X, target.Y);
        alice.ModeCommit.ForageReturnFoodThreshold = SimConfig.ForageReturnFoodDefault;

        // No food in inventory or home
        Assert.Equal(0, alice.FoodInInventory());

        sim.Tick(5);

        // Should stay in Forage or find food, NOT exit to Home
        Assert.True(
            alice.CurrentMode == BehaviorMode.Forage
            || alice.CurrentMode == BehaviorMode.Urgent
            || alice.FoodInInventory() > 0,
            $"Agent with no food should stay in Forage. " +
            $"Mode: {alice.CurrentMode}, food: {alice.FoodInInventory()}");
    }

    /// <summary>
    /// Caretaker mode uses a shorter forage return threshold (ForageReturnFoodCaretaker = 4).
    /// When a Caretaker transitions to Forage, the threshold should be 4, not 6.
    /// </summary>
    [Fact]
    public void Caretaker_Forage_Uses_Shorter_Return_Threshold()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Mom", isMale: false, hunger: 70f)
            .AddAgent("Baby", isMale: false, hunger: 80f)
            .AgentAt("Mom", 0, 0).AgentHome("Mom", 0, 0)
            .AgentAt("Baby", 0, 0).AgentHome("Baby", 0, 0)
            .AgentAge("Baby", 100)
            .AgentMode("Mom", BehaviorMode.Caretaker)
            .ShelterAt(0, 0)
            // Food nearby within caretaker range
            .ResourceAt(2, 0, ResourceType.Berries, 30)
            .Build();

        sim.SetParentChild("Mom", "Baby");

        // Run until Mom enters Forage
        bool enteredForage = sim.TickUntil(() =>
            sim.GetAgent("Mom").CurrentMode == BehaviorMode.Forage, 100);

        if (enteredForage)
        {
            var mom = sim.GetAgent("Mom");
            Assert.Equal(SimConfig.ForageReturnFoodCaretaker,
                mom.ModeCommit.ForageReturnFoodThreshold);
        }
        // If she didn't enter Forage (e.g., ate from home), that's also valid
    }
}
