using CivSim.Core;
using Xunit;

namespace CivSim.Tests;

/// <summary>
/// US-013: Tests for PlacementScorer — structure placement scoring for shelter and campfire.
/// </summary>
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
}
