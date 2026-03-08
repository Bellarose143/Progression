using CivSim.Core;
using Xunit;

namespace CivSim.Tests;

/// <summary>
/// Tests for Urgent mode priority cascade, night rest behavior,
/// and ShouldInterrupt logic.
/// </summary>
public class UrgentAndNightTests
{
    // ═══════════════════════════════════════════════════════════════════
    //  URGENT MODE PRIORITY CASCADE
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// A starving agent with food in inventory should eat it immediately.
    /// This is the highest-priority action in Urgent mode.
    /// </summary>
    [Fact]
    public void Urgent_Eats_From_Inventory_First()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Alice", isMale: false, hunger: 20f)
            .AgentAt("Alice", 0, 0)
            .AgentHome("Alice", 0, 0)
            .ShelterAt(0, 0)
            .AgentInventory("Alice", ResourceType.Berries, 5)
            .Build();

        float hungerBefore = sim.GetAgent("Alice").Hunger;
        sim.Tick(10);

        var alice = sim.GetAgent("Alice");
        Assert.True(alice.Hunger > hungerBefore,
            $"Urgent agent with food should eat. Before: {hungerBefore:F1}, After: {alice.Hunger:F1}");
    }

    /// <summary>
    /// A starving agent at home with food in home storage should withdraw and eat.
    /// </summary>
    [Fact]
    public void Urgent_Eats_From_Home_Storage()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Alice", isMale: false, hunger: 20f)
            .AgentAt("Alice", 0, 0)
            .AgentHome("Alice", 0, 0)
            .HomeStorageAt(0, 0, ResourceType.Berries, 10)
            .Build();

        sim.Tick(20);

        var alice = sim.GetAgent("Alice");
        var homeTile = sim.TileAt(sim.SpawnX, sim.SpawnY);

        // Either Alice ate (hunger recovered) or home food decreased
        Assert.True(alice.Hunger > 30f || homeTile.HomeTotalFood < 10,
            $"Urgent agent at home should eat from storage. " +
            $"Hunger: {alice.Hunger:F1}, Home food: {homeTile.HomeTotalFood}");
    }

    /// <summary>
    /// A starving agent AT home with food in home storage should eat from storage.
    /// Tests the "eat from home storage" branch in DecideUrgent.
    /// </summary>
    [Fact]
    public void Urgent_Eats_From_Home_Storage_When_At_Home()
    {
        var sim = new TestSimBuilder()
            .GridSize(16, 16).Seed(42)
            .AddAgent("Alice", isMale: false, hunger: 22f)
            .AgentAt("Alice", 0, 0)
            .AgentHome("Alice", 0, 0)
            .HomeStorageAt(0, 0, ResourceType.Berries, 10)
            .Build();

        var alice = sim.GetAgent("Alice");
        var homeTile = sim.TileAt(sim.SpawnX, sim.SpawnY);
        int storageBefore = homeTile.HomeTotalFood;

        sim.Tick(10);

        // Either Alice ate (hunger went up) or home food went down
        Assert.True(alice.Hunger > 22f || homeTile.HomeTotalFood < storageBefore,
            $"Urgent agent at home should eat from storage. " +
            $"Hunger: {alice.Hunger:F1}, Home food: {homeTile.HomeTotalFood} (was {storageBefore})");
    }

    /// <summary>
    /// A starving agent AWAY from home should head home when home has stored food.
    /// Uses SeekFood goal so the commitment persists (ReturnHome would be
    /// interrupted by the hunger override in TryAdvanceGoal).
    /// Uses a larger grid so the agent isn't trapped by edge water tiles.
    /// </summary>
    [Fact]
    public void Urgent_Heads_Home_For_Stored_Food()
    {
        // Use standard 32x32 grid — the agent is placed at offset (3,0)
        // which puts them near spawn center, away from edges where water clusters.
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Alice", isMale: false, hunger: 22f)
            .AgentAt("Alice", 3, 0) // 3 tiles east of home
            .AgentHome("Alice", 0, 0)
            .HomeStorageAt(0, 0, ResourceType.Berries, 10)
            .Build();

        var alice = sim.GetAgent("Alice");
        int startDist = TestSim.ManhattanDistance(alice.X, alice.Y, sim.SpawnX, sim.SpawnY);

        // Clear food near Alice so she can't eat locally — she must go home
        for (int dx = -3; dx <= 3; dx++)
        for (int dy = -3; dy <= 3; dy++)
        {
            int tx = Math.Clamp(alice.X + dx, 0, 31);
            int ty = Math.Clamp(alice.Y + dy, 0, 31);
            var tile = sim.World.GetTile(tx, ty);
            tile.Resources.Remove(ResourceType.Berries);
            tile.Resources.Remove(ResourceType.Meat);
            tile.Resources.Remove(ResourceType.Fish);
            tile.Resources.Remove(ResourceType.Grain);
        }

        // Also ensure the path home isn't water — clear any water tiles between agent and home
        for (int dx = -1; dx <= 4; dx++)
        for (int dy = -1; dy <= 1; dy++)
        {
            int tx = Math.Clamp(sim.SpawnX + dx, 0, 31);
            int ty = Math.Clamp(sim.SpawnY + dy, 0, 31);
            var tile = sim.World.GetTile(tx, ty);
            if (float.IsPositiveInfinity(tile.MovementCostMultiplier))
            {
                tile.MovementCostMultiplier = 1.0f;
            }
        }

        // Run until Alice reaches home or eats (from home storage)
        bool succeeded = sim.TickUntil(() =>
        {
            int dist = TestSim.ManhattanDistance(alice.X, alice.Y, sim.SpawnX, sim.SpawnY);
            return dist <= 1 || alice.Hunger > 30f;
        }, 80);

        Assert.True(succeeded,
            $"Urgent agent with no local food should head home for stored food. " +
            $"Pos: ({alice.X},{alice.Y}), Home: ({sim.SpawnX},{sim.SpawnY}), " +
            $"StartDist: {startDist}, Hunger: {alice.Hunger:F1}, Goal: {alice.CurrentGoal}");
    }

    /// <summary>
    /// A critically low-health agent should rest (if not starving).
    /// </summary>
    [Fact]
    public void Urgent_Rests_When_Health_Critical()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Alice", isMale: false, hunger: 60f, health: 15)
            .AgentAt("Alice", 0, 0)
            .AgentHome("Alice", 0, 0)
            .ShelterAt(0, 0)
            .AgentInventory("Alice", ResourceType.Berries, 5) // Has food, hunger not critical
            .Build();

        sim.Tick(20);

        var alice = sim.GetAgent("Alice");
        // Health should be recovering (resting or eating then resting)
        Assert.True(alice.Health > 15 || alice.CurrentAction == ActionType.Rest,
            $"Agent with critical health should rest. Health: {alice.Health}, Action: {alice.CurrentAction}");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  SHOULD INTERRUPT
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// A starving agent mid-action (not eating/gathering) who has food in inventory
    /// should interrupt to eat. InterruptHungerThreshold = 30.
    /// </summary>
    [Fact]
    public void Interrupt_Fires_When_Starving_With_Food()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Alice", isMale: false, hunger: 50f)
            .AgentAt("Alice", 0, 0)
            .AgentHome("Alice", 0, 0)
            .ShelterAt(0, 0)
            .AgentInventory("Alice", ResourceType.Berries, 5)
            .Build();

        var alice = sim.GetAgent("Alice");

        // Run until she starts some action
        sim.Tick(5);

        // Now starve her while she's busy
        alice.Hunger = 25f;

        sim.Tick(10);

        // She should have eaten (hunger should recover)
        Assert.True(alice.Hunger > 25f || alice.FoodInInventory() < 5,
            $"Agent should interrupt to eat when starving with food. " +
            $"Hunger: {alice.Hunger:F1}, Food: {alice.FoodInInventory()}");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  NIGHT REST
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// An agent in Home mode should rest at night when sheltered.
    /// Night = hourOfDay >= 360 or hourOfDay < 120.
    /// Sim starts at tick 0 (hourOfDay 0 = nighttime), so we first advance past the
    /// initial night, then wait for the evening night period (tick 360+).
    /// NeedsRest requires (currentTick - LastRestTick) > 0.7 * TicksPerSimDay (~336).
    /// </summary>
    [Fact]
    public void Home_Agent_Rests_At_Night()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Alice", isMale: false, hunger: 90f)
            .AgentAt("Alice", 0, 0)
            .AgentHome("Alice", 0, 0)
            .ShelterAt(0, 0)
            .AgentInventory("Alice", ResourceType.Berries, 8)
            .Build();

        var alice = sim.GetAgent("Alice");

        // Advance past the initial night (tick 0-119) into daytime
        sim.TickUntil(() => !Agent.IsNightTime(sim.Simulation.CurrentTick), 200);

        // Capture rest state before evening
        int restTickBefore = alice.LastRestTick;

        // Now wait for the evening night (tick 360+)
        bool reachedEvening = sim.TickUntil(() =>
            Agent.IsNightTime(sim.Simulation.CurrentTick)
            && sim.Simulation.CurrentTick > 200, 500);

        Assert.True(reachedEvening, "Should reach evening nighttime");

        // Run through the night
        sim.Tick(120);

        // Agent should have rested at some point during the evening/night period
        Assert.True(alice.LastRestTick > restTickBefore,
            $"Home agent should rest during night. " +
            $"LastRestTick: {restTickBefore} → {alice.LastRestTick}, " +
            $"Tick: {sim.Simulation.CurrentTick}");
    }

    /// <summary>
    /// A parent in Urgent mode should rush home to feed a starving child.
    /// </summary>
    [Fact]
    public void Urgent_Rushes_Home_For_Starving_Child()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Mom", isMale: false, hunger: 60f)
            .AddAgent("Baby", isMale: false, hunger: 30f) // Starving baby
            .AgentAt("Mom", 3, 0) // Away from home
            .AgentHome("Mom", 0, 0)
            .AgentAt("Baby", 0, 0) // Baby at home
            .AgentHome("Baby", 0, 0)
            .AgentAge("Baby", 100) // Infant
            .AgentInventory("Mom", ResourceType.Berries, 5)
            .ShelterAt(0, 0)
            .Build();

        sim.SetParentChild("Mom", "Baby");

        // Mom isn't in urgent herself, but baby needs help
        // Put Mom in urgent with moderate hunger
        sim.GetAgent("Mom").Hunger = 22f;

        sim.Tick(10);

        var mom = sim.GetAgent("Mom");
        // Mom should be heading home or already there
        Assert.True(
            mom.CurrentGoal == GoalType.ReturnHome
            || (mom.X == sim.SpawnX && mom.Y == sim.SpawnY)
            || mom.Hunger > 30f, // She ate and is now heading to baby
            $"Urgent mom should rush home for starving baby. " +
            $"Goal: {mom.CurrentGoal}, Pos: ({mom.X},{mom.Y})");
    }
}
