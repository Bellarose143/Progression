using CivSim.Core;
using Xunit;

namespace CivSim.Tests;

/// <summary>
/// D21 Fix 1: DepositHome phantom bug tests.
/// Verifies DepositHome scores 0 when it cannot actually execute
/// (storage full, inventory empty, or below keep-for-self thresholds).
/// All positions are OFFSETS from the simulation spawn center.
/// </summary>
[Trait("Category", "Integration")]
public partial class DepositHomePhantomTests
{
    [Fact]
    public void DepositHome_ScoresZero_WhenFoodStorageFull_AndLowMaterials()
    {
        // Joshua scenario: lots of food, 1 wood, food storage full.
        // Keep-for-self: 2 food, 3 per material.
        // Depositable food = max(0, 10 - 2) = 8, but storage is full so canDepositFood = false.
        // Depositable materials = max(0, 1 - 3) = 0.
        // Total depositable = 0 → DepositHome should not appear.
        var sim = new TestSimBuilder()
            .GridSize(16, 16).Seed(42)
            .AddAgent("Joshua", isMale: true, hunger: 80f)
            .AgentAt("Joshua", 0, 0)
            .AgentHome("Joshua", 0, 0)
            .ShelterAt(0, 0)
            .Build();

        var joshua = sim.GetAgent("Joshua");

        // Give Joshua 10 food + 1 wood (mimics the stuck scenario)
        joshua.Inventory[ResourceType.Grain] = 9;
        joshua.Inventory[ResourceType.Meat] = 1;
        joshua.Inventory[ResourceType.Wood] = 1;

        // Fill home food storage to capacity
        var homeTile = sim.World.GetTile(sim.SpawnX, sim.SpawnY);
        int capacity = homeTile.HomeStorageCapacity;
        Assert.True(capacity > 0, "Shelter should provide food storage capacity");
        homeTile.DepositToHome(ResourceType.Grain, capacity);
        Assert.Equal(capacity, homeTile.HomeTotalFood);

        // Score actions — DepositHome should NOT appear
        var scored = UtilityScorer.ScoreHomeActions(joshua, sim.World, 100, new Random(42));

        bool hasDeposit = scored.Any(s => s.Action == ActionType.DepositHome);
        Assert.False(hasDeposit,
            "DepositHome should not score when food storage is full and materials are below keep-for-self threshold (1 wood < 3)");
    }

    [Fact]
    public void DepositHome_ScoresZero_WhenNoInventory()
    {
        var sim = new TestSimBuilder()
            .GridSize(16, 16).Seed(42)
            .AddAgent("Empty", isMale: true, hunger: 80f)
            .AgentAt("Empty", 0, 0)
            .AgentHome("Empty", 0, 0)
            .ShelterAt(0, 0)
            .Build();

        var empty = sim.GetAgent("Empty");
        // Clear all inventory
        empty.Inventory.Clear();

        var scored = UtilityScorer.ScoreHomeActions(empty, sim.World, 100, new Random(42));

        bool hasDeposit = scored.Any(s => s.Action == ActionType.DepositHome);
        Assert.False(hasDeposit,
            "DepositHome should not score when agent has no inventory at all");
    }

    [Fact]
    public void DepositHome_ScoresPositive_WhenStorageAvailable_AndSufficientItems()
    {
        // Counter-check: DepositHome SHOULD score when agent has enough depositable items.
        // 10 food - 2 (keep-for-self) = 8 depositable, which is >= 5 (batch threshold).
        var sim = new TestSimBuilder()
            .GridSize(16, 16).Seed(42)
            .AddAgent("Worker", isMale: true, hunger: 80f)
            .AgentAt("Worker", 0, 0)
            .AgentHome("Worker", 0, 0)
            .ShelterAt(0, 0)
            .Build();

        var worker = sim.GetAgent("Worker");
        // Give 10 food — more than the keep-for-self threshold (2)
        worker.Inventory[ResourceType.Grain] = 10;

        var homeTile = sim.World.GetTile(sim.SpawnX, sim.SpawnY);
        // Ensure storage has capacity and is empty
        Assert.True(homeTile.HomeStorageCapacity > 0, "Shelter should provide storage");
        Assert.Equal(0, homeTile.HomeTotalFood);

        var scored = UtilityScorer.ScoreHomeActions(worker, sim.World, 100, new Random(42));

        var depositAction = scored.FirstOrDefault(s => s.Action == ActionType.DepositHome);
        Assert.True(depositAction.Action == ActionType.DepositHome && depositAction.Score > 0,
            $"DepositHome should score > 0 when storage available and depositable items >= 5. " +
            $"Got action={depositAction.Action}, score={depositAction.Score}");
    }

    [Fact]
    public void DepositHome_ScoresZero_WhenBothStoragesFull()
    {
        var sim = new TestSimBuilder()
            .GridSize(16, 16).Seed(42)
            .AddAgent("Full", isMale: true, hunger: 80f)
            .AgentAt("Full", 0, 0)
            .AgentHome("Full", 0, 0)
            .ShelterAt(0, 0)
            .Build();

        var full = sim.GetAgent("Full");
        full.Inventory[ResourceType.Grain] = 10;
        full.Inventory[ResourceType.Wood] = 5;

        var homeTile = sim.World.GetTile(sim.SpawnX, sim.SpawnY);
        // Fill food storage to capacity
        homeTile.DepositToHome(ResourceType.Grain, homeTile.HomeStorageCapacity);
        Assert.Equal(homeTile.HomeStorageCapacity, homeTile.HomeTotalFood);
        // Fill material storage to capacity
        homeTile.DepositMaterialToHome(ResourceType.Wood, Tile.MaterialStorageCapacity);
        Assert.Equal(Tile.MaterialStorageCapacity, homeTile.HomeTotalMaterials);

        var scored = UtilityScorer.ScoreHomeActions(full, sim.World, 100, new Random(42));

        bool hasDeposit = scored.Any(s => s.Action == ActionType.DepositHome);
        Assert.False(hasDeposit,
            "DepositHome should not score when both food and material storage are completely full");
    }
}

// Gate 1 Check 4: Verify phantom DepositHome eliminated in seed 16001
[Trait("Category", "Integration")]
public partial class DepositHomePhantomTests
{
    [Fact]
    public void Seed16001_NoPhantomDepositHome()
    {
        // Gate 1 check 4: Run seed 16001 for 5000 ticks.
        // Before Fix 1, DepositHome phantom-scored 0.45 every tick when Joshua was
        // at home with full food storage + 1 wood (below keep-for-self threshold).
        // This caused DaytimeIdleGuard to zero Rest, then DepositHome failed dispatch → Idle.
        // After Fix 1, DepositHome should NOT appear in action tick counts at all
        // during the stuck window (it scores 0 correctly now).
        // Note: Idle% itself remains high because no Stone/Berries → no eligible recipes.
        // Fixes 2 (Stone distribution) and 3 (Opportunistic pickup) address material starvation.
        var world = new World(64, 64, 16001);
        var sim = new Simulation(world, 16001);
        sim.SpawnAgent();
        sim.SpawnAgent();

        for (int t = 0; t < 5000; t++)
            sim.Tick();

        // Joshua (Agent 1) should have zero DepositHome ticks in the stuck scenario
        var joshua = sim.Agents[0];
        Assert.True(joshua.IsAlive);

        int totalTicks = joshua.ActionTickCounts.Values.Sum();
        int depositTicks = joshua.ActionTickCounts.GetValueOrDefault(ActionType.DepositHome, 0);
        double depositPct = totalTicks > 0 ? 100.0 * depositTicks / totalTicks : 0;

        // Output action distribution for diagnostics
        var actionDist = joshua.ActionTickCounts
            .OrderByDescending(kv => kv.Value)
            .Select(kv => $"{kv.Key}: {100.0 * kv.Value / totalTicks:F1}%");
        System.Console.WriteLine($"Joshua actions: {string.Join(", ", actionDist)}");

        // DepositHome should be extremely rare (only happens when agent truly has 5+ depositable items)
        // Before fix, phantom DepositHome was scoring every tick at home tile
        Assert.True(depositPct < 5,
            $"Joshua DepositHome% should be < 5% (phantom eliminated). Got {depositPct:F1}%");
    }
}
