using CivSim.Core;
using Xunit;

namespace CivSim.Tests;

/// <summary>
/// Fix 5: Farm placement constraints — farms must never be on home tile,
/// never on forest/water/mountain tiles, and should prefer adjacency clustering
/// near the settlement.
/// </summary>
[Trait("Category", "Integration")]
public class FarmPlacementTests
{
    // ── Test 1: Farms are never placed on the home tile ────────────────

    [Fact]
    public void FarmPlacement_NeverOnHomeTile()
    {
        // Arrange: Agent at home with farming knowledge, home tile is plains
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(42)
            .AddAgent("Farmer", isMale: true, hunger: 90)
            .AgentAt("Farmer", 0, 0)
            .AgentHome("Farmer", 0, 0)
            .ShelterAt(0, 0)
            .AgentKnows("Farmer", "farming", "lean_to")
            .HomeStorageAt(0, 0, ResourceType.Berries, 1) // Low storage to motivate farming
            .Build();

        // Ensure home tile is Plains (farmable biome)
        var (hx, hy) = sim.WorldPos(0, 0);
        var homeTile = sim.TileAt(hx, hy);

        // Make sure the home tile area has plains tiles for farming
        // Replace home tile and surrounding tiles with plains
        for (int dx = -3; dx <= 3; dx++)
            for (int dy = -3; dy <= 3; dy++)
            {
                int tx = hx + dx, ty = hy + dy;
                if (tx >= 0 && tx < 32 && ty >= 0 && ty < 32)
                {
                    var oldTile = sim.World.GetTile(tx, ty);
                    if (oldTile.Biome != BiomeType.Plains)
                    {
                        var newTile = new Tile(tx, ty, BiomeType.Plains);
                        // Copy any existing structures/resources
                        foreach (var s in oldTile.Structures) newTile.Structures.Add(s);
                        foreach (var r in oldTile.Resources) newTile.Resources[r.Key] = r.Value;
                        foreach (var hs in oldTile.HomeFoodStorage) newTile.HomeFoodStorage[hs.Key] = hs.Value;
                        sim.World.Grid[tx, ty] = newTile;
                    }
                }
            }

        // Act: Run for enough ticks that farming decisions would be made
        sim.Tick(2000);

        // Assert: Home tile should never have a farm structure
        var homeAfter = sim.TileAt(hx, hy);
        Assert.False(homeAfter.HasFarm,
            $"Farm was placed on home tile ({hx},{hy}). Farms must never be on the home tile.");
    }

    // ── Test 2: Farms are never placed on forest tiles ─────────────────

    [Fact]
    public void FarmPlacement_NeverOnForestTile()
    {
        // Arrange: Agent with farming knowledge surrounded by forest tiles
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(42)
            .AddAgent("Farmer", isMale: true, hunger: 90)
            .AgentAt("Farmer", 0, 0)
            .AgentHome("Farmer", 0, 0)
            .ShelterAt(0, 0)
            .AgentKnows("Farmer", "farming", "lean_to")
            .HomeStorageAt(0, 0, ResourceType.Berries, 1)
            .Build();

        var (hx, hy) = sim.WorldPos(0, 0);

        // Make home tile plains, but surround with forest
        sim.World.Grid[hx, hy] = CreateReplacementTile(sim.World, hx, hy, BiomeType.Plains);
        // Restore shelter on home tile
        sim.World.Grid[hx, hy].Structures.Add("lean_to");
        sim.World.Grid[hx, hy].HomeFoodStorage[ResourceType.Berries] = 1;

        // Set all adjacent tiles to Forest
        for (int dx = -3; dx <= 3; dx++)
            for (int dy = -3; dy <= 3; dy++)
            {
                if (dx == 0 && dy == 0) continue; // skip home
                int tx = hx + dx, ty = hy + dy;
                if (tx >= 0 && tx < 32 && ty >= 0 && ty < 32)
                {
                    sim.World.Grid[tx, ty] = CreateReplacementTile(sim.World, tx, ty, BiomeType.Forest);
                    // Add some grain so agent might want to farm
                    sim.World.Grid[tx, ty].Resources[ResourceType.Grain] = 5;
                }
            }

        // Act: Run simulation
        sim.Tick(2000);

        // Assert: No farm should exist on any forest tile
        for (int dx = -3; dx <= 3; dx++)
            for (int dy = -3; dy <= 3; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                int tx = hx + dx, ty = hy + dy;
                if (tx >= 0 && tx < 32 && ty >= 0 && ty < 32)
                {
                    var tile = sim.TileAt(tx, ty);
                    if (tile.Biome == BiomeType.Forest && !tile.Structures.Contains("cleared"))
                    {
                        Assert.False(tile.HasFarm,
                            $"Farm was placed on forest tile ({tx},{ty}). Farms must never be on uncleared forest tiles.");
                    }
                }
            }
    }

    // ── Test 3: Farm placement prefers adjacent to existing farm ───────

    [Fact]
    public void FarmPlacement_PrefersAdjacentToExistingFarm()
    {
        // Arrange: Use the scoring function directly to verify adjacency preference
        var world = new World(32, 32, 42);

        // Create a plains area
        int cx = 16, cy = 16;
        for (int dx = -5; dx <= 5; dx++)
            for (int dy = -5; dy <= 5; dy++)
            {
                int tx = cx + dx, ty = cy + dy;
                if (world.IsInBounds(tx, ty))
                    world.Grid[tx, ty] = new Tile(tx, ty, BiomeType.Plains);
            }

        // Place an existing farm at (cx+1, cy)
        world.Grid[cx + 1, cy].Structures.Add("farm");

        // Create an agent with home at (cx, cy)
        var sim = new Simulation(world, 42);
        var agent = sim.SpawnAgent();
        agent.HomeTile = (cx, cy);
        agent.X = cx;
        agent.Y = cy;
        agent.LearnDiscovery("farming");

        // Act: Score farm placement candidates
        var bestTile = UtilityScorer.ScoreFarmPlacementCandidates(agent, world, cx, cy);

        // Assert: Best tile should be adjacent to the existing farm (within 1 Chebyshev)
        Assert.True(bestTile.HasValue, "Should find a candidate tile for farm placement");

        int distToExistingFarm = Math.Max(
            Math.Abs(bestTile.Value.X - (cx + 1)),
            Math.Abs(bestTile.Value.Y - cy));
        Assert.True(distToExistingFarm <= 1,
            $"Best farm candidate ({bestTile.Value.X},{bestTile.Value.Y}) should be adjacent to " +
            $"existing farm at ({cx + 1},{cy}), but Chebyshev distance was {distToExistingFarm}");

        // Also verify it's not the home tile
        Assert.False(bestTile.Value.X == cx && bestTile.Value.Y == cy,
            "Best farm candidate must not be the home tile");
    }

    // ── Test 4: Farm cap is enforced (unit test on scoring) ───────────

    [Fact]
    public void FarmPlacement_RespectsMaxFarmTilesPreGranary()
    {
        // Arrange: Fill up to the cap with farms near home
        var world = new World(32, 32, 42);
        int cx = 16, cy = 16;

        // Create a large plains area
        for (int dx = -8; dx <= 8; dx++)
            for (int dy = -8; dy <= 8; dy++)
            {
                int tx = cx + dx, ty = cy + dy;
                if (world.IsInBounds(tx, ty))
                    world.Grid[tx, ty] = new Tile(tx, ty, BiomeType.Plains);
            }

        // Place farms up to the cap
        int farmsPlaced = 0;
        for (int dx = -3; dx <= 3 && farmsPlaced < SimConfig.MaxFarmTilesPreGranary; dx++)
            for (int dy = -3; dy <= 3 && farmsPlaced < SimConfig.MaxFarmTilesPreGranary; dy++)
            {
                if (dx == 0 && dy == 0) continue; // skip home
                int tx = cx + dx, ty = cy + dy;
                if (world.IsInBounds(tx, ty))
                {
                    world.Grid[tx, ty].Structures.Add("farm");
                    farmsPlaced++;
                }
            }

        Assert.Equal(SimConfig.MaxFarmTilesPreGranary, farmsPlaced);

        // Create agent at home
        var sim = new Simulation(world, 42);
        var agent = sim.SpawnAgent();
        agent.HomeTile = (cx, cy);
        agent.X = cx;
        agent.Y = cy;
        agent.LearnDiscovery("farming");
        agent.Hunger = 90;

        // Act: Score TendFarm actions — should only score existing farms, not new placements
        var scores = UtilityScorer.ScoreAll(agent, world, 0, new Random(42));

        // Find all TendFarm scored actions
        var farmScores = scores.Where(s => s.Action == ActionType.TendFarm).ToList();

        // Any scored farm tiles must be existing farms (already have farm structure)
        foreach (var scored in farmScores)
        {
            if (scored.TargetTile.HasValue)
            {
                var tile = world.GetTile(scored.TargetTile.Value.X, scored.TargetTile.Value.Y);
                Assert.True(tile.HasFarm,
                    $"TendFarm scored for tile ({scored.TargetTile.Value.X},{scored.TargetTile.Value.Y}) " +
                    $"which doesn't have an existing farm — cap should prevent new placements.");
            }
        }
    }

    // ── Test 5: IsFarmable excludes forest and water ──────────────────

    [Fact]
    public void IsFarmable_ExcludesForestWaterMountain()
    {
        var plains = new Tile(0, 0, BiomeType.Plains);
        var forest = new Tile(1, 0, BiomeType.Forest);
        var water = new Tile(2, 0, BiomeType.Water);
        var mountain = new Tile(3, 0, BiomeType.Mountain);
        var desert = new Tile(4, 0, BiomeType.Desert);
        var clearedForest = new Tile(5, 0, BiomeType.Forest);
        clearedForest.Structures.Add("cleared");

        Assert.True(plains.IsFarmable, "Plains should be farmable");
        Assert.False(forest.IsFarmable, "Uncleared forest should NOT be farmable");
        Assert.False(water.IsFarmable, "Water should NOT be farmable");
        Assert.False(mountain.IsFarmable, "Mountain should NOT be farmable");
        Assert.False(desert.IsFarmable, "Desert should NOT be farmable");
        Assert.True(clearedForest.IsFarmable, "Cleared forest should be farmable");
    }

    // ── Helper ─────────────────────────────────────────────────────────

    private static Tile CreateReplacementTile(World world, int x, int y, BiomeType biome)
    {
        var oldTile = world.GetTile(x, y);
        var newTile = new Tile(x, y, biome);
        foreach (var s in oldTile.Structures) newTile.Structures.Add(s);
        foreach (var r in oldTile.Resources) newTile.Resources[r.Key] = r.Value;
        foreach (var hs in oldTile.HomeFoodStorage) newTile.HomeFoodStorage[hs.Key] = hs.Value;
        return newTile;
    }
}
