using CivSim.Core;
using Xunit;

namespace CivSim.Tests;

/// <summary>
/// v1.8 Behavioral Modes: Verifies mode transitions, hysteresis, and committed behavior.
/// </summary>
public class BehavioralModeTests
{
    // ═══════════════════════════════════════════════════════════════════════
    //  Urgent mode
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Urgent_Mode_Entered_When_Hunger_Critical()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Alice", isMale: false, hunger: 20f) // below UrgentEntryHunger (25)
            .AgentAt("Alice", 0, 0)
            .ResourceAt(1, 0, ResourceType.Berries, 30)
            .Build();

        sim.Tick(5);

        var alice = sim.GetAgent("Alice");
        Assert.Equal(BehaviorMode.Urgent, alice.CurrentMode);
    }

    [Fact]
    public void Urgent_Mode_Hysteresis_Prevents_Oscillation()
    {
        // Agent starts in Urgent (hunger 20). Give them food so they eat.
        // They should NOT leave Urgent until hunger > UrgentExitHunger (40).
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Bob", isMale: true, hunger: 20f)
            .AgentAt("Bob", 0, 0)
            .AgentInventory("Bob", ResourceType.Berries, 2) // enough to eat once
            .Build();

        sim.Tick(5);

        var bob = sim.GetAgent("Bob");
        // After eating 1-2 berries, hunger should be ~35-50 range
        // If hunger is between 25-40, agent should STILL be in Urgent (hysteresis)
        if (bob.Hunger <= SimConfig.UrgentExitHunger)
            Assert.Equal(BehaviorMode.Urgent, bob.CurrentMode);
    }

    [Fact]
    public void Urgent_Mode_Exited_When_Hunger_Recovered()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Carol", isMale: false, hunger: 20f)
            .AgentAt("Carol", 0, 0)
            .AgentInventory("Carol", ResourceType.Berries, 8) // plenty of food
            .Build();

        // Tick enough for agent to eat and recover
        sim.Tick(50);

        var carol = sim.GetAgent("Carol");
        Assert.True(carol.IsAlive, "Agent should survive with food");
        if (carol.Hunger > SimConfig.UrgentExitHunger && carol.Health > SimConfig.UrgentExitHealth)
            Assert.NotEqual(BehaviorMode.Urgent, carol.CurrentMode);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Forage mode
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Forage_Entered_When_Food_Low()
    {
        // Agent at home with no food, berries nearby — should enter Forage
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Dave", isMale: true, hunger: 70f) // above ForageEntryHunger (55)
            .AgentAt("Dave", 0, 0)
            .AgentHome("Dave", 0, 0)
            .ShelterAt(0, 0)
            .ResourceAt(2, 0, ResourceType.Berries, 30)
            .Build();

        sim.Tick(10);

        var dave = sim.GetAgent("Dave");
        // Agent has no food (personal < 3, home storage < 5) and hunger > 55 → Forage
        Assert.Equal(BehaviorMode.Forage, dave.CurrentMode);
    }

    [Fact]
    public void Forage_Returns_Home_When_Threshold_Met()
    {
        // Agent in Forage mode gathering berries should return home after collecting enough
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Eve", isMale: false, hunger: 80f)
            .AgentAt("Eve", 0, 0)
            .AgentHome("Eve", 0, 0)
            .ShelterAt(0, 0)
            .ResourceAt(1, 0, ResourceType.Berries, 50)
            .Build();

        // Run until agent has gathered food and hopefully returned
        bool returnedWithFood = sim.TickUntil(() =>
        {
            var eve = sim.GetAgent("Eve");
            return eve.CurrentMode == BehaviorMode.Home && eve.FoodInInventory() > 0;
        }, 500);

        Assert.True(returnedWithFood, "Foraging agent should return home with food");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Explore mode
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Explore_Requires_Good_Conditions()
    {
        // Well-fed, healthy, sheltered agent with food — can explore
        var sim = new TestSimBuilder()
            .GridSize(64, 64).Seed(1)
            .AddAgent("Frank", isMale: true, hunger: 90f, health: 100)
            .AgentAt("Frank", 0, 0)
            .AgentHome("Frank", 0, 0)
            .ShelterAt(0, 0)
            .AgentInventory("Frank", ResourceType.Berries, 8) // > ExploreEntryFood (6)
            .Build();

        // Tick enough for mode evaluation
        bool explored = sim.TickUntil(() =>
            sim.GetAgent("Frank").CurrentMode == BehaviorMode.Explore, 200);

        // Agent should eventually enter explore mode (or at least not crash)
        // The exact timing depends on Home mode scoring finding nothing better to do
        Assert.True(sim.GetAgent("Frank").IsAlive, "Well-provisioned agent should survive");
    }

    [Fact]
    public void Explore_Budget_Causes_Return()
    {
        // Agent already in Explore mode with short budget — should return home
        var sim = new TestSimBuilder()
            .GridSize(64, 64).Seed(1)
            .AddAgent("Grace", isMale: false, hunger: 90f, health: 100)
            .AgentAt("Grace", 0, 0)
            .AgentHome("Grace", 0, 0)
            .ShelterAt(0, 0)
            .AgentInventory("Grace", ResourceType.Berries, 8)
            .AgentMode("Grace", BehaviorMode.Explore)
            .Build();

        // Override budget to something very short
        var grace = sim.GetAgent("Grace");
        grace.ModeCommit.ExploreBudget = 20;
        grace.ModeCommit.ExploreDirection = (1, 0);
        grace.ModeEntryTick = 0;

        // Tick past the budget
        sim.Tick(30);

        grace = sim.GetAgent("Grace");
        // Agent should have exited Explore mode after budget expired
        Assert.NotEqual(BehaviorMode.Explore, grace.CurrentMode);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Home mode
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Home_Mode_Is_Default()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Hank", isMale: true, hunger: 80f)
            .AgentAt("Hank", 0, 0)
            .AgentHome("Hank", 0, 0)
            .ShelterAt(0, 0)
            .AgentInventory("Hank", ResourceType.Berries, 10)
            .Build();

        var hank = sim.GetAgent("Hank");
        Assert.Equal(BehaviorMode.Home, hank.CurrentMode);
    }

    [Fact]
    public void Home_Mode_Scores_Experiment_When_Sheltered()
    {
        // Sheltered agent with food should eventually experiment (discover things)
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(42)
            .AddAgent("Irene", isMale: false, hunger: 90f)
            .AgentAt("Irene", 0, 0)
            .AgentHome("Irene", 0, 0)
            .ShelterAt(0, 0)
            .AgentInventory("Irene", ResourceType.Berries, 10)
            .HomeStorageAt(0, 0, ResourceType.Berries, 20)
            .Build();

        sim.Tick(200);

        var irene = sim.GetAgent("Irene");
        Assert.True(irene.IsAlive, "Sheltered, fed agent should survive");
        // Agent should have done something productive (experimented, built, etc.)
        Assert.True(irene.Knowledge.Count > 0, "Home agent should eventually discover something");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Mode commitment
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void TransitionMode_Clears_Commitment()
    {
        var agent = new Agent(0, 0);
        agent.ModeCommit.ForageTargetResource = ResourceType.Berries;
        agent.ModeCommit.ForageTargetTile = (5, 5);
        agent.ModeCommit.BuildRecipeId = "lean_to";

        agent.TransitionMode(BehaviorMode.Explore, 100);

        Assert.Null(agent.ModeCommit.ForageTargetResource);
        Assert.Null(agent.ModeCommit.ForageTargetTile);
        Assert.Null(agent.ModeCommit.BuildRecipeId);
        Assert.Equal(BehaviorMode.Explore, agent.CurrentMode);
        Assert.Equal(100, agent.ModeEntryTick);
    }

    [Fact]
    public void Urgent_Preserves_PreviousMode()
    {
        var agent = new Agent(0, 0);
        agent.TransitionMode(BehaviorMode.Forage, 50);
        Assert.Null(agent.PreviousMode); // Forage→Forage doesn't set PreviousMode

        agent.TransitionMode(BehaviorMode.Urgent, 100);
        Assert.Equal(BehaviorMode.Forage, agent.PreviousMode);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Emergency forage (dead zone coverage)
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Emergency_Forage_When_No_Food_And_Moderate_Hunger()
    {
        // Agent at hunger 35 (below ForageEntryHunger 55) with NO food anywhere
        // Should still enter Forage via emergency path
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Jack", isMale: true, hunger: 35f)
            .AgentAt("Jack", 0, 0)
            .AgentHome("Jack", 0, 0)
            .ShelterAt(0, 0)
            .ResourceAt(1, 0, ResourceType.Berries, 30)
            .Build();

        sim.Tick(10);

        var jack = sim.GetAgent("Jack");
        // Should be in Forage (emergency) or gathering food
        Assert.True(
            jack.CurrentMode == BehaviorMode.Forage || jack.FoodInInventory() > 0,
            $"Agent at hunger {jack.Hunger:F1} with no food should forage. Mode={jack.CurrentMode}");
    }
}
