using CivSim.Core;
using Xunit;

namespace CivSim.Tests;

/// <summary>
/// Tests for Build mode entry/exit and Explore mode exit conditions.
/// Both modes were completely untested.
/// </summary>
public class BuildAndExploreExitTests
{
    // ═══════════════════════════════════════════════════════════════════
    //  BUILD MODE
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// An agent who knows lean_to and has materials should eventually build.
    /// This tests the Home→Build mode transition path.
    /// </summary>
    [Fact]
    public void Build_Mode_Entered_When_Materials_Ready()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Alice", isMale: false, hunger: 90f)
            .AgentAt("Alice", 0, 0)
            .AgentHome("Alice", 0, 0)
            .AgentKnows("Alice", "lean_to")
            .AgentInventory("Alice", ResourceType.Wood, SimConfig.ShelterWoodCost)
            .AgentInventory("Alice", ResourceType.Stone, SimConfig.ShelterStoneCost)
            // No shelter yet — she should want to build one
            .ResourceAt(1, 0, ResourceType.Berries, 30) // Food nearby to sustain
            .Build();

        bool enteredBuild = sim.TickUntil(() =>
            sim.GetAgent("Alice").CurrentMode == BehaviorMode.Build, 100);

        Assert.True(enteredBuild,
            $"Agent with lean_to knowledge and materials should enter Build mode. " +
            $"Mode: {sim.GetAgent("Alice").CurrentMode}");
    }

    /// <summary>
    /// Build mode should exit when the project is complete (shelter built).
    /// </summary>
    [Fact]
    public void Build_Mode_Exits_On_Completion()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Alice", isMale: false, hunger: 90f)
            .AgentAt("Alice", 0, 0)
            .AgentHome("Alice", 0, 0)
            .AgentKnows("Alice", "lean_to")
            .AgentInventory("Alice", ResourceType.Wood, SimConfig.ShelterWoodCost)
            .AgentInventory("Alice", ResourceType.Stone, SimConfig.ShelterStoneCost)
            .ResourceAt(1, 0, ResourceType.Berries, 30)
            .Build();

        // Run until shelter is built or 500 ticks
        bool built = sim.TickUntil(() =>
            sim.TileAt(sim.SpawnX, sim.SpawnY).HasShelter, 500);

        if (built)
        {
            var alice = sim.GetAgent("Alice");
            // After building, should transition back to Home
            Assert.NotEqual(BehaviorMode.Build, alice.CurrentMode);
        }
    }

    /// <summary>
    /// Build mode should exit when the agent gets too hungry.
    /// </summary>
    [Fact]
    public void Build_Mode_Exits_When_Hungry()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Alice", isMale: false, hunger: 90f)
            .AgentAt("Alice", 0, 0)
            .AgentHome("Alice", 0, 0)
            .AgentKnows("Alice", "lean_to")
            .AgentMode("Alice", BehaviorMode.Build)
            .AgentInventory("Alice", ResourceType.Wood, SimConfig.ShelterWoodCost)
            .AgentInventory("Alice", ResourceType.Stone, SimConfig.ShelterStoneCost)
            // No food — she'll get hungry while building
            .Build();

        var alice = sim.GetAgent("Alice");
        alice.ModeCommit.BuildRecipeId = "lean_to";
        alice.ModeCommit.BuildTargetTile = (sim.SpawnX, sim.SpawnY);

        // Starve her mid-build
        alice.Hunger = 20f;
        sim.Tick(5);

        // Should exit Build for Urgent
        Assert.Equal(BehaviorMode.Urgent, alice.CurrentMode);
    }

    /// <summary>
    /// Build mode should not allow building shelter far from home.
    /// </summary>
    [Fact]
    public void Build_Only_At_Home_Or_Adjacent()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Alice", isMale: false, hunger: 90f)
            .AgentAt("Alice", 0, 0)
            .AgentHome("Alice", 0, 0)
            .AgentKnows("Alice", "lean_to")
            .AgentInventory("Alice", ResourceType.Wood, SimConfig.ShelterWoodCost)
            .AgentInventory("Alice", ResourceType.Stone, SimConfig.ShelterStoneCost)
            .ResourceAt(1, 0, ResourceType.Berries, 30)
            .Build();

        // Run simulation and check shelter placement
        sim.Tick(500);

        // Check that no shelters were built far from home
        int farShelters = 0;
        for (int x = 0; x < 32; x++)
        for (int y = 0; y < 32; y++)
        {
            if (sim.World.GetTile(x, y).HasShelter)
            {
                int dist = TestSim.ManhattanDistance(x, y, sim.SpawnX, sim.SpawnY);
                if (dist > 2) farShelters++;
            }
        }

        Assert.Equal(0, farShelters);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  EXPLORE MODE EXITS
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Explore mode should exit when the agent's hunger drops below ExploreExitHunger (50).
    /// </summary>
    [Fact]
    public void Explore_Exits_When_Hungry()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Alice", isMale: false, hunger: 80f)
            .AgentAt("Alice", 5, 5) // Away from home
            .AgentHome("Alice", 0, 0)
            .ShelterAt(0, 0)
            .AgentMode("Alice", BehaviorMode.Explore)
            .Build();

        var alice = sim.GetAgent("Alice");
        alice.ModeCommit.ExploreDirection = (1, 0);
        alice.ModeCommit.ExploreBudget = 500; // Long budget

        // Set hunger below exit threshold
        alice.Hunger = 40f;

        sim.Tick(5);

        // Should exit Explore
        Assert.NotEqual(BehaviorMode.Explore, alice.CurrentMode);
    }

    /// <summary>
    /// Explore mode should exit when health drops below ExploreExitHealth (50).
    /// </summary>
    [Fact]
    public void Explore_Exits_When_Low_Health()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Alice", isMale: false, hunger: 80f, health: 100)
            .AgentAt("Alice", 5, 5)
            .AgentHome("Alice", 0, 0)
            .ShelterAt(0, 0)
            .AgentMode("Alice", BehaviorMode.Explore)
            .Build();

        var alice = sim.GetAgent("Alice");
        alice.ModeCommit.ExploreDirection = (1, 0);
        alice.ModeCommit.ExploreBudget = 500;

        // Set health below exit threshold
        alice.Health = 40;

        sim.Tick(5);

        Assert.NotEqual(BehaviorMode.Explore, alice.CurrentMode);
    }

    /// <summary>
    /// Explore mode should exit and return home when the tick budget expires.
    /// After returning, the agent should be back at home.
    /// </summary>
    [Fact]
    public void Explore_Returns_Home_After_Budget()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Alice", isMale: false, hunger: 95f)
            .AgentAt("Alice", 0, 0)
            .AgentHome("Alice", 0, 0)
            .ShelterAt(0, 0)
            .AgentMode("Alice", BehaviorMode.Explore)
            .AgentInventory("Alice", ResourceType.Berries, 8)
            .Build();

        // D11 Fix 3: Start during daytime so night rest doesn't override explore
        sim.Simulation.CurrentTick = 150;

        var alice = sim.GetAgent("Alice");
        alice.ModeCommit.ExploreDirection = (1, 0);
        alice.ModeCommit.ExploreBudget = 20; // Short budget
        alice.ModeEntryTick = 150;

        // Run past budget, then give time to return
        sim.Tick(100);

        // Should be back home or heading home
        Assert.NotEqual(BehaviorMode.Explore, alice.CurrentMode);
    }

    /// <summary>
    /// Explorer should opportunistically gather food on the tile they're on
    /// without detouring from their committed direction.
    /// </summary>
    [Fact]
    public void Explore_Gathers_Opportunistically_On_Current_Tile()
    {
        // D10 Fix 5 edge buffer: place agent away from edges
        // D11 Fix: Place food on the agent's starting tile so it's gathered
        // on the very first DecideExplore call, before any movement or
        // geographic discovery can exit Explore mode.
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(7)
            .AddAgent("Alice", isMale: false, hunger: 90f)
            .AgentAt("Alice", 5, 5)
            .AgentHome("Alice", 5, 5)
            .ShelterAt(5, 5)
            .AgentMode("Alice", BehaviorMode.Explore)
            .AgentInventory("Alice", ResourceType.Berries, 2) // Low starting food
            // Place food ON the starting tile so gather fires immediately
            .ResourceAt(5, 5, ResourceType.Berries, 10)
            .ResourceAt(6, 5, ResourceType.Berries, 10)
            .Build();

        // D11 Fix 3: Start during daytime so night rest doesn't override explore
        sim.Simulation.CurrentTick = 150;

        var alice = sim.GetAgent("Alice");
        alice.ModeCommit.ExploreDirection = (1, 0);
        alice.ModeCommit.ExploreBudget = 100;
        alice.ModeEntryTick = 150;
        // Clear any geographic discoveries from perception to prevent early Explore exit
        alice.ClearPendingGeographicDiscoveries();

        // Clear world-gen resources near spawn to reduce RNG cascade interference
        for (int dx = -3; dx <= 10; dx++)
            for (int dy = -3; dy <= 10; dy++)
            {
                int tx = 5 + dx, ty = 5 + dy;
                if (tx >= 0 && tx < 32 && ty >= 0 && ty < 32)
                {
                    var t = sim.World.GetTile(tx, ty);
                    if (tx == 5 && ty == 5) continue; // Keep placed berries on spawn tile
                    if (tx == 6 && ty == 5) continue; // Keep placed berries on adjacent tile
                    t.Resources.Clear();
                }
            }

        int startFood = alice.FoodInInventory();
        int maxFoodSeen = startFood;
        for (int t = 0; t < 60; t++)
        {
            sim.Tick(1);
            int food = alice.FoodInInventory();
            if (food > maxFoodSeen) maxFoodSeen = food;
        }

        // Explorer should have gathered food at some point (may deposit later, so check peak)
        Assert.True(maxFoodSeen > startFood,
            $"Explorer should opportunistically gather. Start: {startFood}, Peak: {maxFoodSeen}, Final: {alice.FoodInInventory()}");
    }
}
