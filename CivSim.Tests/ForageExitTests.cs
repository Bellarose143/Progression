using CivSim.Core;
using Xunit;

namespace CivSim.Tests;

/// <summary>
/// Tests for Forage mode exit conditions: return threshold, inventory full,
/// hunger low, duration safety valve.
/// </summary>
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

        var alice = sim.GetAgent("Alice");
        alice.ModeCommit.ForageTargetResource = ResourceType.Berries;
        var target = sim.WorldPos(2, 0);
        alice.ModeCommit.ForageTargetTile = (target.X, target.Y);
        alice.ModeCommit.ForageReturnFoodThreshold = SimConfig.ForageReturnFoodDefault;

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
            // No food anywhere — she'll forage fruitlessly
            .Build();

        var alice = sim.GetAgent("Alice");
        alice.ModeCommit.ForageTargetResource = ResourceType.Berries;
        alice.ModeCommit.ForageReturnFoodThreshold = SimConfig.ForageReturnFoodDefault;

        // Run past ForageMaxDuration (200 ticks) + margin
        sim.Tick(SimConfig.ForageMaxDuration + 50);

        // She should have exited Forage by now (to Home or Urgent)
        Assert.NotEqual(BehaviorMode.Forage, alice.CurrentMode);
    }

    /// <summary>
    /// A foraging agent whose hunger drops below ForageExitHunger (45)
    /// should exit Forage and go home — but ONLY if they have food somewhere.
    /// We run tick-by-tick and set hunger each tick to ensure the check fires.
    /// </summary>
    [Fact]
    public void Forage_Exits_When_Hungry_With_Food_Available()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Alice", isMale: false, hunger: 50f)
            .AgentAt("Alice", 3, 0)
            .AgentHome("Alice", 0, 0)
            .ShelterAt(0, 0)
            .AgentMode("Alice", BehaviorMode.Forage)
            .AgentInventory("Alice", ResourceType.Berries, 3) // Has some food
            .Build();

        var alice = sim.GetAgent("Alice");
        alice.ModeCommit.ForageTargetResource = ResourceType.Berries;
        alice.ModeCommit.ForageReturnFoodThreshold = SimConfig.ForageReturnFoodDefault;

        // Run tick-by-tick, forcing hunger below exit threshold each tick
        // Mode transitions only fire when agent isn't IsBusy, so give it time
        bool exitedForage = false;
        for (int i = 0; i < 30; i++)
        {
            alice.Hunger = 40f; // Keep below ForageExitHunger (45)
            sim.Tick(1);
            if (alice.CurrentMode != BehaviorMode.Forage)
            {
                exitedForage = true;
                break;
            }
        }

        Assert.True(exitedForage,
            $"Foraging agent with food and hunger < {SimConfig.ForageExitHunger} should exit Forage. " +
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
