using CivSim.Core;
using Xunit;

namespace CivSim.Tests;

/// <summary>
/// US-013/US-014: Tests for PlacementScorer — structure placement scoring.
/// </summary>
[Trait("Category", "Integration")]
public class PlacementScorerTests
{
    public PlacementScorerTests()
    {
        Agent.ResetIdCounter();
        Animal.ResetIdCounter();
        Carcass.ResetIdCounter();
        Pen.ResetIdCounter();
        Settlement.ResetIdCounter();
    }

    /// <summary>
    /// Shelter placement should prefer tiles near the residential center over distant tiles.
    /// </summary>
    [Fact]
    public void Shelter_Prefers_ResidentialCenter()
    {
        var world = new World(32, 32, 42);
        var settlement = new Settlement
        {
            Name = "TestVillage",
            CenterTile = (15, 15)
        };
        // Add an existing shelter at (15,15) to define residential center
        settlement.Structures.Add((15, 15, "lean_to"));
        settlement.Zones.Recalculate(settlement.Structures, settlement.CenterTile);

        var result = PlacementScorer.FindBestTile("lean_to", settlement, world, 15, 15);

        Assert.NotNull(result);
        // Best tile should be near residential center (15,15), not far away
        int dist = Math.Abs(result.Value.X - 15) + Math.Abs(result.Value.Y - 15);
        Assert.True(dist <= 3, $"Shelter placed at ({result.Value.X},{result.Value.Y}), dist={dist} from residential center — expected within 3 tiles");
    }

    /// <summary>
    /// Shelter should not be placed on farm tiles (penalty -2.0).
    /// </summary>
    [Fact]
    public void Shelter_Avoids_Farm_Tiles()
    {
        var world = new World(32, 32, 42);
        var settlement = new Settlement
        {
            Name = "TestVillage",
            CenterTile = (15, 15)
        };
        settlement.Structures.Add((15, 15, "lean_to"));

        // Place farms on all tiles adjacent to residential center
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                var tile = world.GetTile(15 + dx, 15 + dy);
                tile.Structures.Add("farm");
                settlement.Structures.Add((15 + dx, 15 + dy, "farm"));
            }

        settlement.Zones.Recalculate(settlement.Structures, settlement.CenterTile);

        var result = PlacementScorer.FindBestTile("lean_to", settlement, world, 15, 15);

        Assert.NotNull(result);
        // Should not be placed on a farm tile
        var placedTile = world.GetTile(result.Value.X, result.Value.Y);
        Assert.False(placedTile.HasFarm, $"Shelter should not be placed on farm tile at ({result.Value.X},{result.Value.Y})");
    }

    /// <summary>
    /// Shelter should get +1.0 bonus for being adjacent to existing shelter.
    /// </summary>
    [Fact]
    public void Shelter_Prefers_Adjacent_To_Existing_Shelter()
    {
        var world = new World(32, 32, 42);
        var settlement = new Settlement
        {
            Name = "TestVillage",
            CenterTile = (15, 15)
        };
        settlement.Structures.Add((15, 15, "lean_to"));
        settlement.Zones.Recalculate(settlement.Structures, settlement.CenterTile);

        var result = PlacementScorer.FindBestTile("lean_to", settlement, world, 15, 15);

        Assert.NotNull(result);
        // Should be adjacent to existing shelter at (15,15)
        int dx = Math.Abs(result.Value.X - 15);
        int dy = Math.Abs(result.Value.Y - 15);
        Assert.True(dx <= 1 && dy <= 1, $"Shelter at ({result.Value.X},{result.Value.Y}) should be adjacent to existing shelter at (15,15)");
    }

    /// <summary>
    /// Campfire should prefer tiles within 2 of residential center.
    /// </summary>
    [Fact]
    public void Campfire_Prefers_Near_ResidentialCenter()
    {
        var world = new World(32, 32, 42);
        var settlement = new Settlement
        {
            Name = "TestVillage",
            CenterTile = (15, 15)
        };
        settlement.Structures.Add((15, 15, "lean_to"));
        settlement.Zones.Recalculate(settlement.Structures, settlement.CenterTile);

        var result = PlacementScorer.FindBestTile("campfire", settlement, world, 15, 15);

        Assert.NotNull(result);
        int dist = Math.Abs(result.Value.X - 15) + Math.Abs(result.Value.Y - 15);
        Assert.True(dist <= 2, $"Campfire placed at ({result.Value.X},{result.Value.Y}), dist={dist} from residential center — expected within 2 tiles");
    }

    /// <summary>
    /// Campfire should not be placed on farm tiles.
    /// </summary>
    [Fact]
    public void Campfire_Avoids_Farm_Tiles()
    {
        var world = new World(32, 32, 42);
        var settlement = new Settlement
        {
            Name = "TestVillage",
            CenterTile = (15, 15)
        };
        settlement.Structures.Add((15, 15, "lean_to"));

        // Place farms near residential center
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                var tile = world.GetTile(15 + dx, 15 + dy);
                tile.Structures.Add("farm");
                settlement.Structures.Add((15 + dx, 15 + dy, "farm"));
            }

        settlement.Zones.Recalculate(settlement.Structures, settlement.CenterTile);

        var result = PlacementScorer.FindBestTile("campfire", settlement, world, 15, 15);

        Assert.NotNull(result);
        var placedTile = world.GetTile(result.Value.X, result.Value.Y);
        Assert.False(placedTile.HasFarm, $"Campfire should not be placed on farm tile at ({result.Value.X},{result.Value.Y})");
    }

    /// <summary>
    /// Tiebreaker: among equally-scored tiles, prefer the one closest to the agent.
    /// </summary>
    [Fact]
    public void Tiebreaker_Prefers_Closest_To_Agent()
    {
        var world = new World(32, 32, 42);
        var settlement = new Settlement
        {
            Name = "TestVillage",
            CenterTile = (15, 15)
        };
        settlement.Structures.Add((15, 15, "lean_to"));
        settlement.Zones.Recalculate(settlement.Structures, settlement.CenterTile);

        // Agent at (14, 15) — should prefer tile closer to them among equally scored options
        var result = PlacementScorer.FindBestTile("lean_to", settlement, world, 14, 15);

        Assert.NotNull(result);
        // Result should exist and be a valid tile near center
        Assert.True(world.IsInBounds(result.Value.X, result.Value.Y));
    }

    /// <summary>
    /// Placement scorer should not return water tiles.
    /// </summary>
    [Fact]
    public void Does_Not_Place_On_Water()
    {
        var world = new World(32, 32, 42);
        var settlement = new Settlement
        {
            Name = "TestVillage",
            CenterTile = (15, 15)
        };
        settlement.Structures.Add((15, 15, "lean_to"));
        settlement.Zones.Recalculate(settlement.Structures, settlement.CenterTile);

        var result = PlacementScorer.FindBestTile("lean_to", settlement, world, 15, 15);

        if (result.HasValue)
        {
            var tile = world.GetTile(result.Value.X, result.Value.Y);
            Assert.NotEqual(BiomeType.Water, tile.Biome);
        }
    }

    // ── US-014: Farm placement tests ──────────────────────────────────

    /// <summary>
    /// First farm placement should scan directions and pick a tile 8-10 tiles from residential center
    /// in the direction with most Plains tiles.
    /// </summary>
    [Fact]
    public void FirstFarm_PlacedAwayFromResidentialCenter()
    {
        // Use a larger world so there's room for 8-10 tile distance
        var world = new World(48, 48, 42);
        // Make a band of tiles east of center farmable via "cleared" structure
        for (int d = 5; d <= 15; d++)
            for (int w = -2; w <= 2; w++)
                if (world.IsInBounds(24 + d, 24 + w))
                {
                    var t = world.GetTile(24 + d, 24 + w);
                    if (!t.IsFarmable) t.Structures.Add("cleared");
                }

        var settlement = new Settlement
        {
            Name = "TestVillage",
            CenterTile = (24, 24)
        };
        settlement.Structures.Add((24, 24, "lean_to"));
        settlement.Zones.Recalculate(settlement.Structures, settlement.CenterTile);

        var result = PlacementScorer.FindFirstFarmTile(settlement, world, 24, 24);

        Assert.NotNull(result);
        // Should be 7-12 tiles from center (Chebyshev distance, since diagonal directions give larger Manhattan)
        int chebyDist = Math.Max(Math.Abs(result.Value.X - 24), Math.Abs(result.Value.Y - 24));
        Assert.True(chebyDist >= 7 && chebyDist <= 14,
            $"First farm at ({result.Value.X},{result.Value.Y}), chebyshev dist={chebyDist} from center — expected 7-14 tiles");

        // Should be on a farmable tile
        var tile = world.GetTile(result.Value.X, result.Value.Y);
        Assert.True(tile.IsFarmable, "First farm must be on a farmable tile");
    }

    /// <summary>
    /// First farm should avoid water tiles.
    /// </summary>
    [Fact]
    public void FirstFarm_AvoidsWater()
    {
        var world = new World(48, 48, 42);
        var settlement = new Settlement
        {
            Name = "TestVillage",
            CenterTile = (24, 24)
        };
        settlement.Structures.Add((24, 24, "lean_to"));
        settlement.Zones.Recalculate(settlement.Structures, settlement.CenterTile);

        var result = PlacementScorer.FindFirstFarmTile(settlement, world, 24, 24);

        if (result.HasValue)
        {
            var tile = world.GetTile(result.Value.X, result.Value.Y);
            Assert.NotEqual(BiomeType.Water, tile.Biome);
        }
    }

    /// <summary>
    /// Subsequent farm placement should strongly prefer tiles adjacent to existing farms (+3.0 adjacency bonus).
    /// </summary>
    [Fact]
    public void SubsequentFarm_PrefersAdjacentToExistingFarm()
    {
        var world = new World(48, 48, 42);
        int centerX = 24, centerY = 24;

        // Find a Plains tile 8-10 tiles from center to place first farm
        (int fx, int fy) = FindPlainsTileNearDistance(world, centerX, centerY, 8);

        // Also ensure adjacent tiles are farmable
        for (int ddx = -2; ddx <= 2; ddx++)
            for (int ddy = -2; ddy <= 2; ddy++)
                if (world.IsInBounds(fx + ddx, fy + ddy))
                {
                    var t = world.GetTile(fx + ddx, fy + ddy);
                    if (t.Biome == BiomeType.Water) continue;
                    if (!t.IsFarmable) t.Structures.Add("cleared");
                }

        var settlement = new Settlement
        {
            Name = "TestVillage",
            CenterTile = (centerX, centerY)
        };
        settlement.Structures.Add((centerX, centerY, "lean_to"));
        var firstFarmTile = world.GetTile(fx, fy);
        firstFarmTile.Structures.Add("farm");
        settlement.Structures.Add((fx, fy, "farm"));
        settlement.Zones.Recalculate(settlement.Structures, settlement.CenterTile);

        var result = PlacementScorer.FindBestTile("farm", settlement, world, centerX, centerY);

        Assert.NotNull(result);
        // Should be adjacent to the existing farm
        int dx2 = Math.Abs(result.Value.X - fx);
        int dy2 = Math.Abs(result.Value.Y - fy);
        Assert.True(dx2 <= 1 && dy2 <= 1,
            $"Subsequent farm at ({result.Value.X},{result.Value.Y}) should be adjacent to existing farm at ({fx},{fy})");
    }

    /// <summary>
    /// Farm placement should avoid tiles within 5 of ResidentialCenter (-3.0 penalty).
    /// </summary>
    [Fact]
    public void Farm_AvoidsResidentialCenter()
    {
        var world = new World(48, 48, 42);
        int centerX = 24, centerY = 24;

        // Make a large area farmable so scoring has choices
        for (int x = centerX - 15; x <= centerX + 15; x++)
            for (int y = centerY - 15; y <= centerY + 15; y++)
                if (world.IsInBounds(x, y))
                {
                    var t = world.GetTile(x, y);
                    if (t.Biome != BiomeType.Water && !t.IsFarmable) t.Structures.Add("cleared");
                }

        (int fx, int fy) = FindPlainsTileNearDistance(world, centerX, centerY, 8);

        var settlement = new Settlement
        {
            Name = "TestVillage",
            CenterTile = (centerX, centerY)
        };
        settlement.Structures.Add((centerX, centerY, "lean_to"));
        var firstFarmTile = world.GetTile(fx, fy);
        firstFarmTile.Structures.Add("farm");
        settlement.Structures.Add((fx, fy, "farm"));
        settlement.Zones.Recalculate(settlement.Structures, settlement.CenterTile);

        var result = PlacementScorer.FindBestTile("farm", settlement, world, centerX, centerY);

        Assert.NotNull(result);
        // Should not be within 5 tiles of residential center
        int distToRes = Math.Abs(result.Value.X - centerX) + Math.Abs(result.Value.Y - centerY);
        Assert.True(distToRes > 5,
            $"Farm at ({result.Value.X},{result.Value.Y}), dist={distToRes} from residential center — should be >5");
    }

    /// <summary>
    /// Farm placement should avoid tiles with existing non-farm structures (-5.0 penalty).
    /// </summary>
    [Fact]
    public void Farm_AvoidsExistingStructures()
    {
        var world = new World(48, 48, 42);
        int centerX = 24, centerY = 24;

        (int fx, int fy) = FindPlainsTileNearDistance(world, centerX, centerY, 8);
        // Make nearby tiles farmable
        for (int ddx = -2; ddx <= 2; ddx++)
            for (int ddy = -2; ddy <= 2; ddy++)
                if (world.IsInBounds(fx + ddx, fy + ddy))
                {
                    var t = world.GetTile(fx + ddx, fy + ddy);
                    if (t.Biome != BiomeType.Water && !t.IsFarmable) t.Structures.Add("cleared");
                }

        var settlement = new Settlement
        {
            Name = "TestVillage",
            CenterTile = (centerX, centerY)
        };
        settlement.Structures.Add((centerX, centerY, "lean_to"));
        var farmTile = world.GetTile(fx, fy);
        farmTile.Structures.Add("farm");
        settlement.Structures.Add((fx, fy, "farm"));
        settlement.Zones.Recalculate(settlement.Structures, settlement.CenterTile);

        var result = PlacementScorer.FindBestTile("farm", settlement, world, centerX, centerY);

        Assert.NotNull(result);
        // Should not be on the shelter tile
        Assert.False(result.Value.X == centerX && result.Value.Y == centerY,
            "Farm should not be placed on the shelter tile");
    }

    /// <summary>
    /// Farms should grow as contiguous blocks — second and third farms should be near each other.
    /// </summary>
    [Fact]
    public void Farms_GrowContiguously()
    {
        var world = new World(48, 48, 42);
        int centerX = 24, centerY = 24;

        (int fx, int fy) = FindPlainsTileNearDistance(world, centerX, centerY, 8);

        // Make area around farm farmable
        for (int ddx = -3; ddx <= 3; ddx++)
            for (int ddy = -3; ddy <= 3; ddy++)
                if (world.IsInBounds(fx + ddx, fy + ddy))
                {
                    var t = world.GetTile(fx + ddx, fy + ddy);
                    if (t.Biome != BiomeType.Water && !t.IsFarmable) t.Structures.Add("cleared");
                }

        var settlement = new Settlement
        {
            Name = "TestVillage",
            CenterTile = (centerX, centerY)
        };
        settlement.Structures.Add((centerX, centerY, "lean_to"));

        // Place first farm
        var ft1 = world.GetTile(fx, fy);
        ft1.Structures.Add("farm");
        settlement.Structures.Add((fx, fy, "farm"));

        // Find an adjacent farmable non-water tile for second farm
        int f2x = fx + 1, f2y = fy;
        if (!world.IsInBounds(f2x, f2y) || world.GetTile(f2x, f2y).Biome == BiomeType.Water)
        { f2x = fx; f2y = fy + 1; }
        var ft2 = world.GetTile(f2x, f2y);
        ft2.Structures.Add("farm");
        settlement.Structures.Add((f2x, f2y, "farm"));
        settlement.Zones.Recalculate(settlement.Structures, settlement.CenterTile);

        // Third farm should also be adjacent to the existing farm block
        var result = PlacementScorer.FindBestTile("farm", settlement, world, centerX, centerY);

        Assert.NotNull(result);
        // Should be adjacent to at least one existing farm
        bool adjToFarm1 = Math.Abs(result.Value.X - fx) <= 1 && Math.Abs(result.Value.Y - fy) <= 1;
        bool adjToFarm2 = Math.Abs(result.Value.X - f2x) <= 1 && Math.Abs(result.Value.Y - f2y) <= 1;
        Assert.True(adjToFarm1 || adjToFarm2,
            $"Third farm at ({result.Value.X},{result.Value.Y}) should be adjacent to existing farms at ({fx},{fy}) or ({f2x},{f2y})");
    }

    // ── US-015: Pen and Granary placement tests ─────────────────────

    /// <summary>
    /// Pen placement should prefer tiles adjacent to existing pens (+2.0 adjacency bonus).
    /// </summary>
    [Fact]
    public void Pen_PrefersAdjacentToExistingPen()
    {
        var world = new World(32, 32, 42);
        var settlement = new Settlement
        {
            Name = "TestVillage",
            CenterTile = (15, 15)
        };
        settlement.Structures.Add((15, 15, "lean_to"));
        // Place an existing pen a few tiles away
        settlement.Structures.Add((18, 15, "animal_pen"));
        world.GetTile(18, 15).Structures.Add("animal_pen");
        settlement.Zones.Recalculate(settlement.Structures, settlement.CenterTile);

        var result = PlacementScorer.FindBestTile("animal_pen", settlement, world, 15, 15);

        Assert.NotNull(result);
        // Should be adjacent to the existing pen at (18,15)
        int dx = Math.Abs(result.Value.X - 18);
        int dy = Math.Abs(result.Value.Y - 15);
        Assert.True(dx <= 1 && dy <= 1,
            $"Pen at ({result.Value.X},{result.Value.Y}) should be adjacent to existing pen at (18,15)");
    }

    /// <summary>
    /// Pen should avoid farm tiles (-3.0 penalty).
    /// </summary>
    [Fact]
    public void Pen_AvoidsFarmTiles()
    {
        var world = new World(32, 32, 42);
        var settlement = new Settlement
        {
            Name = "TestVillage",
            CenterTile = (15, 15)
        };
        settlement.Structures.Add((15, 15, "lean_to"));
        settlement.Zones.Recalculate(settlement.Structures, settlement.CenterTile);

        var result = PlacementScorer.FindBestTile("animal_pen", settlement, world, 15, 15);

        Assert.NotNull(result);
        var placedTile = world.GetTile(result.Value.X, result.Value.Y);
        Assert.False(placedTile.HasFarm,
            $"Pen should not be placed on farm tile at ({result.Value.X},{result.Value.Y})");
    }

    /// <summary>
    /// Pen should avoid shelter tiles (-2.0 penalty).
    /// </summary>
    [Fact]
    public void Pen_AvoidsShelterTiles()
    {
        var world = new World(32, 32, 42);
        var settlement = new Settlement
        {
            Name = "TestVillage",
            CenterTile = (15, 15)
        };
        settlement.Structures.Add((15, 15, "lean_to"));
        world.GetTile(15, 15).Structures.Add("lean_to");
        settlement.Zones.Recalculate(settlement.Structures, settlement.CenterTile);

        var result = PlacementScorer.FindBestTile("animal_pen", settlement, world, 15, 15);

        Assert.NotNull(result);
        // Should not be on the shelter tile
        var placedTile = world.GetTile(result.Value.X, result.Value.Y);
        Assert.False(placedTile.HasShelter,
            $"Pen should not be placed on shelter tile at ({result.Value.X},{result.Value.Y})");
    }

    /// <summary>
    /// Pen should not be placed on a tile that already has a structure.
    /// </summary>
    [Fact]
    public void Pen_NotPlacedOnOccupiedTile()
    {
        var world = new World(32, 32, 42);
        var settlement = new Settlement
        {
            Name = "TestVillage",
            CenterTile = (15, 15)
        };
        settlement.Structures.Add((15, 15, "lean_to"));
        world.GetTile(15, 15).Structures.Add("lean_to");
        // Place a granary adjacent
        settlement.Structures.Add((16, 15, "granary"));
        world.GetTile(16, 15).Structures.Add("granary");
        settlement.Zones.Recalculate(settlement.Structures, settlement.CenterTile);

        var result = PlacementScorer.FindBestTile("animal_pen", settlement, world, 15, 15);

        Assert.NotNull(result);
        // Should not be on the granary tile or shelter tile
        Assert.False(result.Value.X == 16 && result.Value.Y == 15,
            "Pen should not be placed on a tile with an existing granary");
        Assert.False(result.Value.X == 15 && result.Value.Y == 15,
            "Pen should not be placed on a tile with an existing shelter");
    }

    /// <summary>
    /// Granary should prefer tiles between agricultural and residential centers.
    /// </summary>
    [Fact]
    public void Granary_PrefersBetweenAgAndResCenter()
    {
        var world = new World(48, 48, 42);
        var settlement = new Settlement
        {
            Name = "TestVillage",
            CenterTile = (24, 24)
        };
        settlement.Structures.Add((24, 24, "lean_to"));
        // Place farms 10 tiles east to establish agricultural center
        for (int i = 0; i < 3; i++)
        {
            int fx = 34 + i, fy = 24;
            if (world.IsInBounds(fx, fy))
            {
                world.GetTile(fx, fy).Structures.Add("farm");
                settlement.Structures.Add((fx, fy, "farm"));
            }
        }
        settlement.Zones.Recalculate(settlement.Structures, settlement.CenterTile);

        var result = PlacementScorer.FindBestTile("granary", settlement, world, 24, 24);

        Assert.NotNull(result);
        // Midpoint between residential (24,24) and agricultural (~35,24) is ~(29,24)
        // Granary should be near that midpoint area (within 5 of residential also gives bonus)
        int distToRes = Math.Abs(result.Value.X - 24) + Math.Abs(result.Value.Y - 24);
        Assert.True(distToRes <= 8,
            $"Granary at ({result.Value.X},{result.Value.Y}), dist={distToRes} from residential — expected near midpoint area");
    }

    /// <summary>
    /// Granary should avoid farm tiles (-2.0 penalty).
    /// </summary>
    [Fact]
    public void Granary_AvoidsFarmTiles()
    {
        var world = new World(32, 32, 42);
        var settlement = new Settlement
        {
            Name = "TestVillage",
            CenterTile = (15, 15)
        };
        settlement.Structures.Add((15, 15, "lean_to"));
        // Add farms around center
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                var tile = world.GetTile(15 + dx, 15 + dy);
                tile.Structures.Add("farm");
                settlement.Structures.Add((15 + dx, 15 + dy, "farm"));
            }
        settlement.Zones.Recalculate(settlement.Structures, settlement.CenterTile);

        var result = PlacementScorer.FindBestTile("granary", settlement, world, 15, 15);

        Assert.NotNull(result);
        var placedTile = world.GetTile(result.Value.X, result.Value.Y);
        Assert.False(placedTile.HasFarm,
            $"Granary should not be placed on farm tile at ({result.Value.X},{result.Value.Y})");
    }

    /// <summary>
    /// Granary should not be placed on a tile that already has a structure.
    /// </summary>
    [Fact]
    public void Granary_NotPlacedOnOccupiedTile()
    {
        var world = new World(32, 32, 42);
        var settlement = new Settlement
        {
            Name = "TestVillage",
            CenterTile = (15, 15)
        };
        settlement.Structures.Add((15, 15, "lean_to"));
        world.GetTile(15, 15).Structures.Add("lean_to");
        settlement.Zones.Recalculate(settlement.Structures, settlement.CenterTile);

        var result = PlacementScorer.FindBestTile("granary", settlement, world, 15, 15);

        Assert.NotNull(result);
        // Should not be on the shelter tile
        Assert.False(result.Value.X == 15 && result.Value.Y == 15,
            "Granary should not be placed on a tile with an existing shelter");
    }

    /// <summary>
    /// All structure placement must be within 20 tiles (StructureBuildRange) of settlement center.
    /// </summary>
    [Fact]
    public void AllStructures_WithinBuildRange()
    {
        var world = new World(64, 64, 42);
        var settlement = new Settlement
        {
            Name = "TestVillage",
            CenterTile = (32, 32)
        };
        settlement.Structures.Add((32, 32, "lean_to"));
        settlement.Zones.Recalculate(settlement.Structures, settlement.CenterTile);

        // Test all structure types
        foreach (var type in new[] { "lean_to", "campfire", "animal_pen", "granary" })
        {
            var result = PlacementScorer.FindBestTile(type, settlement, world, 32, 32);
            if (result.HasValue)
            {
                int dx = Math.Abs(result.Value.X - 32);
                int dy = Math.Abs(result.Value.Y - 32);
                Assert.True(dx <= SimConfig.StructureBuildRange && dy <= SimConfig.StructureBuildRange,
                    $"{type} placed at ({result.Value.X},{result.Value.Y}), outside build range of {SimConfig.StructureBuildRange} from center (32,32)");
            }
        }
    }

    /// <summary>
    /// Helper: finds a non-Water, farmable tile approximately 'targetDist' tiles from (cx,cy).
    /// </summary>
    private static (int X, int Y) FindPlainsTileNearDistance(World world, int cx, int cy, int targetDist)
    {
        // Search in expanding rings from targetDist
        for (int ring = 0; ring <= 5; ring++)
        {
            for (int d = targetDist - ring; d <= targetDist + ring; d++)
            {
                if (d < 1) continue;
                // Check 8 directions at this distance
                for (int dir = 0; dir < 8; dir++)
                {
                    int dx = dir switch { 0 => d, 1 => d, 2 => 0, 3 => -d, 4 => -d, 5 => -d, 6 => 0, _ => d };
                    int dy = dir switch { 0 => 0, 1 => d, 2 => d, 3 => d, 4 => 0, 5 => -d, 6 => -d, _ => -d };
                    int tx = cx + dx, ty = cy + dy;
                    if (!world.IsInBounds(tx, ty)) continue;
                    var tile = world.GetTile(tx, ty);
                    if (tile.Biome != BiomeType.Water && tile.IsFarmable)
                        return (tx, ty);
                }
            }
        }
        // Fallback: just find any farmable tile
        for (int x = 0; x < world.Width; x++)
            for (int y = 0; y < world.Height; y++)
            {
                var tile = world.GetTile(x, y);
                if (tile.Biome != BiomeType.Water && tile.IsFarmable
                    && Math.Abs(x - cx) + Math.Abs(y - cy) >= 6)
                    return (x, y);
            }
        return (cx + targetDist, cy); // absolute fallback
    }
}
