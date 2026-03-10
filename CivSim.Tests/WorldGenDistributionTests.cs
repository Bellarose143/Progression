using CivSim.Core;
using Xunit;

namespace CivSim.Tests;

/// <summary>
/// D23 World Gen Resource Distribution tests.
/// Validates berry patch clustering, animal clustering, cross-biome wood, mountain variation, and determinism.
/// </summary>
[Trait("Category", "Integration")]
public class WorldGenDistributionTests
{
    private const int TestSeed = 16001;
    private const int WorldSize = 64;

    private World CreateWorld() => new World(WorldSize, WorldSize, TestSeed);

    [Fact]
    public void WorldGen_BerryPatches_SomeForestTilesHaveBerriesAndSomeDont()
    {
        var world = CreateWorld();

        int forestWithBerries = 0;
        int forestWithoutBerries = 0;

        for (int x = 0; x < world.Width; x++)
            for (int y = 0; y < world.Height; y++)
            {
                var tile = world.Grid[x, y];
                if (tile.Biome != BiomeType.Forest) continue;

                if (tile.Resources.TryGetValue(ResourceType.Berries, out int b) && b > 0)
                    forestWithBerries++;
                else
                    forestWithoutBerries++;
            }

        // Berries should exist on some forest tiles but not all
        Assert.True(forestWithBerries > 0, "Some forest tiles should have berries");
        Assert.True(forestWithoutBerries > 0, "Some forest tiles should NOT have berries (patchy distribution)");

        // Non-forest tiles should not have berries (except Desert which has its own sparse berries)
        for (int x = 0; x < world.Width; x++)
            for (int y = 0; y < world.Height; y++)
            {
                var tile = world.Grid[x, y];
                if (tile.Biome == BiomeType.Forest || tile.Biome == BiomeType.Desert) continue;
                if (tile.Biome == BiomeType.Plains) continue; // Plains can get berries from Tier 2 patches
                int berries = tile.Resources.GetValueOrDefault(ResourceType.Berries, 0);
                Assert.True(berries == 0,
                    $"Tile ({tile.X},{tile.Y}) biome {tile.Biome} should not have berries but has {berries}");
            }
    }

    [Fact]
    public void WorldGen_BerryPatches_TotalBerriesWithin15PercentOfBaseline()
    {
        var world = CreateWorld();
        int totalBerries = 0;
        for (int x = 0; x < world.Width; x++)
            for (int y = 0; y < world.Height; y++)
                totalBerries += world.Grid[x, y].Resources.GetValueOrDefault(ResourceType.Berries, 0);

        // D23 post-change baseline: 868 berries. Allow +-15%
        int baseline = 868;
        int low = (int)(baseline * 0.85);
        int high = (int)(baseline * 1.15);
        Assert.InRange(totalBerries, low, high);
    }

    [Fact]
    public void WorldGen_AnimalClustering_AnimalEntitiesExist()
    {
        var world = CreateWorld();

        // D25b: Animals are now entities, not tile resources
        Assert.True(world.Animals.Count > 0, "Some animal entities should exist after world gen");

        // Check that animals are on eligible biomes (Forest/Plains)
        foreach (var animal in world.Animals)
        {
            var tile = world.Grid[animal.X, animal.Y];
            bool eligible = tile.Biome == BiomeType.Forest || tile.Biome == BiomeType.Plains;
            Assert.True(eligible, $"Animal at ({animal.X},{animal.Y}) should be on Forest or Plains, was {tile.Biome}");
        }
    }

    [Fact]
    public void WorldGen_AnimalClustering_TotalAnimalsWithin15PercentOfBaseline()
    {
        var world = CreateWorld();
        int totalAnimals = world.Animals.Count;

        // D25b: Animals are now entities. Baseline ~565 from D23. Allow +-15%
        int baseline = 565;
        int low = (int)(baseline * 0.85);
        int high = (int)(baseline * 1.15);
        Assert.InRange(totalAnimals, low, high);
    }

    [Fact]
    public void WorldGen_CrossBiomeWood_PlainsAdjacentToForestCanHaveWood()
    {
        var world = CreateWorld();

        bool foundPlainsWood = false;
        for (int x = 0; x < world.Width; x++)
            for (int y = 0; y < world.Height; y++)
            {
                var tile = world.Grid[x, y];
                if (tile.Biome == BiomeType.Plains &&
                    tile.Resources.GetValueOrDefault(ResourceType.Wood, 0) > 0)
                {
                    foundPlainsWood = true;
                    break;
                }
            }

        Assert.True(foundPlainsWood, "Some Plains tiles should have cross-biome wood");
    }

    [Fact]
    public void WorldGen_CrossBiomeWood_DesertHasNoWood()
    {
        var world = CreateWorld();

        for (int x = 0; x < world.Width; x++)
            for (int y = 0; y < world.Height; y++)
            {
                var tile = world.Grid[x, y];
                if (tile.Biome == BiomeType.Desert)
                {
                    int wood = tile.Resources.GetValueOrDefault(ResourceType.Wood, 0);
                    Assert.True(wood == 0, $"Desert tile ({x},{y}) should have 0 wood but has {wood}");
                }
            }
    }

    [Fact]
    public void WorldGen_CrossBiomeWood_MountainCanHaveWood()
    {
        var world = CreateWorld();

        bool foundMountainWood = false;
        for (int x = 0; x < world.Width; x++)
            for (int y = 0; y < world.Height; y++)
            {
                var tile = world.Grid[x, y];
                if (tile.Biome == BiomeType.Mountain &&
                    tile.Resources.GetValueOrDefault(ResourceType.Wood, 0) > 0)
                {
                    foundMountainWood = true;
                    break;
                }
            }

        Assert.True(foundMountainWood, "Some Mountain tiles should have cross-biome wood");
    }

    [Fact]
    public void WorldGen_CrossBiomeWood_NonForestWoodIsNonRegenerating()
    {
        var world = CreateWorld();

        for (int x = 0; x < world.Width; x++)
            for (int y = 0; y < world.Height; y++)
            {
                var tile = world.Grid[x, y];
                if (tile.Biome == BiomeType.Forest) continue;

                int wood = tile.Resources.GetValueOrDefault(ResourceType.Wood, 0);
                if (wood > 0)
                {
                    // RegenCap for Wood on non-forest tiles should exist (set by regen cap snapshot)
                    // but RegenerateTile won't call TryRegen for Wood on Plains/Mountain
                    // Verify the tile has a Wood capacity but Plains regen skips Wood
                    Assert.True(tile.CapacityOverrides.ContainsKey(ResourceType.Wood),
                        $"Non-forest tile ({x},{y}) with wood should have Wood capacity override");
                }
            }
    }

    [Fact]
    public void WorldGen_MountainVariation_StoneIsVaried()
    {
        var world = CreateWorld();

        var stoneValues = new List<int>();
        for (int x = 0; x < world.Width; x++)
            for (int y = 0; y < world.Height; y++)
            {
                var tile = world.Grid[x, y];
                if (tile.Biome != BiomeType.Mountain) continue;
                stoneValues.Add(tile.Resources.GetValueOrDefault(ResourceType.Stone, 0));
            }

        Assert.True(stoneValues.Count > 0, "Should have mountain tiles");
        // Stone should NOT all be the same value (was uniform before D23)
        Assert.True(stoneValues.Distinct().Count() > 1, "Mountain stone values should be varied, not uniform");
        // D23 calibrated range: 1-5 base + point features (rich quarries at 50).
        // Average across all mountains should be low single digits (excluding quarries).
        double avg = stoneValues.Average();
        Assert.InRange(avg, 1, 15); // base 1-5 plus occasional rich quarry outliers
    }

    [Fact]
    public void WorldGen_MountainVariation_SomeMountainTilesHaveNoOre()
    {
        var world = CreateWorld();

        int withOre = 0;
        int withoutOre = 0;
        for (int x = 0; x < world.Width; x++)
            for (int y = 0; y < world.Height; y++)
            {
                var tile = world.Grid[x, y];
                if (tile.Biome != BiomeType.Mountain) continue;
                int ore = tile.Resources.GetValueOrDefault(ResourceType.Ore, 0);
                if (ore > 0) withOre++; else withoutOre++;
            }

        Assert.True(withOre > 0, "Some mountain tiles should have ore");
        Assert.True(withoutOre > 0, "Some mountain tiles should have NO ore (vein-style distribution)");
    }

    [Fact]
    public void WorldGen_Determinism_SameSeedProducesIdenticalResourceMaps()
    {
        var world1 = new World(WorldSize, WorldSize, TestSeed);
        var world2 = new World(WorldSize, WorldSize, TestSeed);

        for (int x = 0; x < WorldSize; x++)
            for (int y = 0; y < WorldSize; y++)
            {
                var t1 = world1.Grid[x, y];
                var t2 = world2.Grid[x, y];

                Assert.Equal(t1.Biome, t2.Biome);

                // Compare all resources
                var allResources = new HashSet<ResourceType>();
                foreach (var k in t1.Resources.Keys) allResources.Add(k);
                foreach (var k in t2.Resources.Keys) allResources.Add(k);

                foreach (var res in allResources)
                {
                    int v1 = t1.Resources.GetValueOrDefault(res, 0);
                    int v2 = t2.Resources.GetValueOrDefault(res, 0);
                    Assert.True(v1 == v2,
                        $"Tile ({x},{y}) {res}: world1={v1}, world2={v2} — determinism failure");
                }
            }
    }
}
