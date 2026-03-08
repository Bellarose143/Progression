using CivSim.Core;
using Xunit;
using Xunit.Abstractions;

namespace CivSim.Tests;

/// <summary>
/// D21 Gate 2: Stone Distribution Tests
/// Validates that the D21 Fix 2 world-gen changes correctly distribute
/// Stone across Forest, Plains, and Desert biomes while leaving Mountain
/// Stone generation unchanged.
/// </summary>
public class StoneDistributionTests
{
    private readonly ITestOutputHelper _output;

    public StoneDistributionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // ── Test 1: WorldGen_StoneDistribution ──────────────────────────────

    [Fact]
    public void WorldGen_StoneDistribution()
    {
        var world = new World(64, 64, 16001);

        int forestWithStone = 0, totalForest = 0;
        int plainsWithStone = 0, totalPlains = 0;
        int waterWithStone = 0;
        bool anyExceedsCapacity = false;
        string capacityViolation = "";

        for (int x = 0; x < world.Width; x++)
        {
            for (int y = 0; y < world.Height; y++)
            {
                var tile = world.Grid[x, y];
                int stone = tile.Resources.GetValueOrDefault(ResourceType.Stone, 0);

                switch (tile.Biome)
                {
                    case BiomeType.Forest:
                        totalForest++;
                        if (stone > 0) forestWithStone++;
                        if (stone > 3)
                        {
                            anyExceedsCapacity = true;
                            capacityViolation = $"Forest tile ({x},{y}) has {stone} stone, max is 3";
                        }
                        break;

                    case BiomeType.Plains:
                        totalPlains++;
                        if (stone > 0) plainsWithStone++;
                        if (stone > 2)
                        {
                            anyExceedsCapacity = true;
                            capacityViolation = $"Plains tile ({x},{y}) has {stone} stone, max is 2";
                        }
                        break;

                    case BiomeType.Desert:
                        if (stone > 2)
                        {
                            anyExceedsCapacity = true;
                            capacityViolation = $"Desert tile ({x},{y}) has {stone} stone, max is 2";
                        }
                        break;

                    case BiomeType.Mountain:
                        // Mountain capacity is SimConfig.CapacityStone (30), but rich quarries can go to 50
                        int mountainCap = tile.IsPointFeature && tile.PointFeatureType == "rich_quarry"
                            ? SimConfig.RichQuarryStoneAmount
                            : SimConfig.CapacityStone;
                        if (stone > mountainCap)
                        {
                            anyExceedsCapacity = true;
                            capacityViolation = $"Mountain tile ({x},{y}) has {stone} stone, max is {mountainCap}";
                        }
                        break;

                    case BiomeType.Water:
                        if (stone > 0) waterWithStone++;
                        break;
                }
            }
        }

        _output.WriteLine($"Forest: {forestWithStone}/{totalForest} tiles have stone");
        _output.WriteLine($"Plains: {plainsWithStone}/{totalPlains} tiles have stone");
        _output.WriteLine($"Water tiles with stone: {waterWithStone}");

        // Stone exists on at least some Forest tiles
        Assert.True(forestWithStone > 0,
            $"Stone should exist on at least some Forest tiles. Found {forestWithStone}/{totalForest}");

        // Stone exists on at least some Plains tiles
        Assert.True(plainsWithStone > 0,
            $"Stone should exist on at least some Plains tiles. Found {plainsWithStone}/{totalPlains}");

        // Mountain tile Stone capacity check — verify at least one mountain tile
        // has capacity of SimConfig.CapacityStone (30)
        bool foundMountainCapacity = false;
        for (int x = 0; x < world.Width; x++)
            for (int y = 0; y < world.Height; y++)
            {
                var tile = world.Grid[x, y];
                if (tile.Biome == BiomeType.Mountain &&
                    tile.CapacityOverrides.GetValueOrDefault(ResourceType.Stone, 0) == SimConfig.CapacityStone)
                {
                    foundMountainCapacity = true;
                    break;
                }
            }
        Assert.True(foundMountainCapacity,
            $"At least one Mountain tile should have Stone capacity of {SimConfig.CapacityStone}");

        // Water tiles have 0 Stone
        Assert.Equal(0, waterWithStone);

        // No tile exceeds its biome's Stone capacity
        Assert.False(anyExceedsCapacity, capacityViolation);
    }

    // ── Test 2: WorldGen_StoneSpawnDensity ──────────────────────────────

    [Fact]
    public void WorldGen_StoneSpawnDensity()
    {
        int[] seeds = { 42, 1337, 16001, 55555, 99999 };

        foreach (int seed in seeds)
        {
            var world = new World(64, 64, seed);

            int totalForest = 0, forestWithStone = 0;
            int totalPlains = 0, plainsWithStone = 0;
            int totalDesert = 0, desertWithStone = 0;

            for (int x = 0; x < world.Width; x++)
            {
                for (int y = 0; y < world.Height; y++)
                {
                    var tile = world.Grid[x, y];
                    int stone = tile.Resources.GetValueOrDefault(ResourceType.Stone, 0);

                    switch (tile.Biome)
                    {
                        case BiomeType.Forest:
                            totalForest++;
                            if (stone > 0) forestWithStone++;
                            break;
                        case BiomeType.Plains:
                            totalPlains++;
                            if (stone > 0) plainsWithStone++;
                            break;
                        case BiomeType.Desert:
                            totalDesert++;
                            if (stone > 0) desertWithStone++;
                            break;
                    }
                }
            }

            double forestDensity = totalForest > 0 ? (double)forestWithStone / totalForest : 0;
            double plainsDensity = totalPlains > 0 ? (double)plainsWithStone / totalPlains : 0;
            double desertDensity = totalDesert > 0 ? (double)desertWithStone / totalDesert : 0;

            _output.WriteLine($"Seed {seed}:");
            _output.WriteLine($"  Forest: {forestWithStone}/{totalForest} = {forestDensity:P1}  (target: 40%)");
            _output.WriteLine($"  Plains: {plainsWithStone}/{totalPlains} = {plainsDensity:P1}  (target: 30%)");
            _output.WriteLine($"  Desert: {desertWithStone}/{totalDesert} = {desertDensity:P1}  (target: 20%)");

            // Forest: ~40% ± 15%
            if (totalForest > 0)
            {
                Assert.True(forestDensity >= 0.25 && forestDensity <= 0.55,
                    $"Seed {seed}: Forest stone density {forestDensity:P1} outside 25%-55% range " +
                    $"({forestWithStone}/{totalForest})");
            }

            // Plains: ~30% ± 15%
            if (totalPlains > 0)
            {
                Assert.True(plainsDensity >= 0.15 && plainsDensity <= 0.45,
                    $"Seed {seed}: Plains stone density {plainsDensity:P1} outside 15%-45% range " +
                    $"({plainsWithStone}/{totalPlains})");
            }

            // Desert: ~20% ± 15%
            if (totalDesert > 0)
            {
                Assert.True(desertDensity >= 0.05 && desertDensity <= 0.35,
                    $"Seed {seed}: Desert stone density {desertDensity:P1} outside 5%-35% range " +
                    $"({desertWithStone}/{totalDesert})");
            }
        }
    }

    // ── Test 3: WorldGen_Determinism ────────────────────────────────────

    [Fact]
    public void WorldGen_Determinism()
    {
        var world1 = new World(64, 64, 16001);
        var world2 = new World(64, 64, 16001);

        for (int x = 0; x < 64; x++)
        {
            for (int y = 0; y < 64; y++)
            {
                var tile1 = world1.Grid[x, y];
                var tile2 = world2.Grid[x, y];

                Assert.Equal(tile1.Biome, tile2.Biome);

                // Compare all resources
                var allResources = new HashSet<ResourceType>();
                foreach (var key in tile1.Resources.Keys) allResources.Add(key);
                foreach (var key in tile2.Resources.Keys) allResources.Add(key);

                foreach (var resource in allResources)
                {
                    int val1 = tile1.Resources.GetValueOrDefault(resource, 0);
                    int val2 = tile2.Resources.GetValueOrDefault(resource, 0);
                    Assert.True(val1 == val2,
                        $"Tile ({x},{y}) resource {resource}: world1={val1}, world2={val2}");
                }

                // Compare capacity overrides
                var allCapKeys = new HashSet<ResourceType>();
                foreach (var key in tile1.CapacityOverrides.Keys) allCapKeys.Add(key);
                foreach (var key in tile2.CapacityOverrides.Keys) allCapKeys.Add(key);

                foreach (var resource in allCapKeys)
                {
                    int cap1 = tile1.CapacityOverrides.GetValueOrDefault(resource, 0);
                    int cap2 = tile2.CapacityOverrides.GetValueOrDefault(resource, 0);
                    Assert.True(cap1 == cap2,
                        $"Tile ({x},{y}) capacity {resource}: world1={cap1}, world2={cap2}");
                }
            }
        }
    }

    // ── Test 4: WorldGen_MountainIntegrity ──────────────────────────────

    [Fact]
    public void WorldGen_MountainIntegrity()
    {
        var world = new World(64, 64, 16001);

        int totalMountain = 0;
        int mountainWithStone = 0;
        int maxMountainStone = 0;

        for (int x = 0; x < world.Width; x++)
        {
            for (int y = 0; y < world.Height; y++)
            {
                var tile = world.Grid[x, y];
                if (tile.Biome != BiomeType.Mountain) continue;

                totalMountain++;
                int stone = tile.Resources.GetValueOrDefault(ResourceType.Stone, 0);
                if (stone > 0) mountainWithStone++;
                if (stone > maxMountainStone) maxMountainStone = stone;

                // Mountain stone capacity should be SimConfig.CapacityStone (30)
                // UNLESS it's a rich_quarry point feature (50)
                int expectedCap = tile.IsPointFeature && tile.PointFeatureType == "rich_quarry"
                    ? SimConfig.RichQuarryStoneAmount
                    : SimConfig.CapacityStone;

                int actualCap = tile.CapacityOverrides.GetValueOrDefault(ResourceType.Stone, 0);
                Assert.True(actualCap == expectedCap,
                    $"Mountain tile ({x},{y}) Stone capacity: expected {expectedCap}, got {actualCap}" +
                    (tile.IsPointFeature ? $" (point feature: {tile.PointFeatureType})" : ""));
            }
        }

        double mountainDensity = totalMountain > 0 ? (double)mountainWithStone / totalMountain : 0;

        _output.WriteLine($"Mountain: {mountainWithStone}/{totalMountain} = {mountainDensity:P1}  (target: ~40% base + quarries)");
        _output.WriteLine($"Max mountain stone on a single tile: {maxMountainStone}");

        // Mountain should have stone on some tiles (40% base density from world gen)
        Assert.True(totalMountain > 0, "World should have Mountain tiles on seed 16001");
        Assert.True(mountainWithStone > 0,
            $"Mountain tiles should have stone. Found {mountainWithStone}/{totalMountain}");

        // Mountain density should be roughly 40% from primary resources
        // (point features add more, so expect at least 25%)
        Assert.True(mountainDensity >= 0.25,
            $"Mountain stone density {mountainDensity:P1} is below expected minimum of 25%");
    }
}
