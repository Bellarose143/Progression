using CivSim.Core;
using System.Text.Json;

namespace CivSim.Diagnostics;

public static class ResourceCensus
{
    public static void Run(int seed = 16001)
    {
        var world = new World(64, 64, seed);

        // Count biomes
        var biomeCounts = new Dictionary<string, int>();
        // Count resources by type (total amount)
        var resourceTotals = new Dictionary<string, int>();
        // Count tiles that have each resource
        var tilesWithResource = new Dictionary<string, int>();
        // Patch and point feature counts
        int patchTiles = 0;
        int pointFeatureTiles = 0;
        var pointFeatureTypes = new Dictionary<string, int>();

        // Berry-specific: forest tiles with/without berries
        int forestTilesWithBerries = 0;
        int forestTilesWithoutBerries = 0;

        for (int x = 0; x < world.Width; x++)
        {
            for (int y = 0; y < world.Height; y++)
            {
                var tile = world.Grid[x, y];

                // Biome counts
                string biomeName = tile.Biome.ToString();
                biomeCounts.TryGetValue(biomeName, out int bc);
                biomeCounts[biomeName] = bc + 1;

                // Resource counts
                foreach (var kvp in tile.Resources)
                {
                    if (kvp.Value <= 0) continue;
                    string resName = kvp.Key.ToString();

                    resourceTotals.TryGetValue(resName, out int rt);
                    resourceTotals[resName] = rt + kvp.Value;

                    tilesWithResource.TryGetValue(resName, out int tw);
                    tilesWithResource[resName] = tw + 1;
                }

                // Patch/feature tracking
                if (tile.IsResourcePatch) patchTiles++;
                if (tile.IsPointFeature)
                {
                    pointFeatureTiles++;
                    string ft = tile.PointFeatureType ?? "unknown";
                    pointFeatureTypes.TryGetValue(ft, out int fc);
                    pointFeatureTypes[ft] = fc + 1;
                }

                // Berry-specific for forest
                if (tile.Biome == BiomeType.Forest)
                {
                    if (tile.Resources.TryGetValue(ResourceType.Berries, out int berryAmt) && berryAmt > 0)
                        forestTilesWithBerries++;
                    else
                        forestTilesWithoutBerries++;
                }
            }
        }

        // Console output
        Console.WriteLine($"=== Resource Census for Seed {seed} ===");
        Console.WriteLine($"World size: {world.Width}x{world.Height} = {world.Width * world.Height} tiles");

        Console.WriteLine("\n--- Biome Counts ---");
        foreach (var b in biomeCounts.OrderBy(b => b.Key))
            Console.WriteLine($"  {b.Key,-12}: {b.Value,5} tiles ({100.0 * b.Value / (world.Width * world.Height):F1}%)");

        Console.WriteLine("\n--- Resource Totals ---");
        foreach (var r in resourceTotals.OrderBy(r => r.Key))
        {
            int tileCount = tilesWithResource.GetValueOrDefault(r.Key, 0);
            Console.WriteLine($"  {r.Key,-16}: {r.Value,6} total across {tileCount,5} tiles (avg {(tileCount > 0 ? (double)r.Value / tileCount : 0):F1}/tile)");
        }
        int totalResources = resourceTotals.Values.Sum();
        Console.WriteLine($"\n  TOTAL RESOURCES: {totalResources}");

        Console.WriteLine("\n--- Forest Berry Analysis ---");
        int totalForest = forestTilesWithBerries + forestTilesWithoutBerries;
        Console.WriteLine($"  Forest tiles with berries:    {forestTilesWithBerries,5} ({(totalForest > 0 ? 100.0 * forestTilesWithBerries / totalForest : 0):F1}%)");
        Console.WriteLine($"  Forest tiles without berries: {forestTilesWithoutBerries,5} ({(totalForest > 0 ? 100.0 * forestTilesWithoutBerries / totalForest : 0):F1}%)");

        Console.WriteLine("\n--- Patches & Point Features ---");
        Console.WriteLine($"  Tiles in resource patches: {patchTiles}");
        Console.WriteLine($"  Point feature tiles:       {pointFeatureTiles}");
        foreach (var pf in pointFeatureTypes.OrderBy(p => p.Key))
            Console.WriteLine($"    {pf.Key}: {pf.Value}");

        // JSON output
        var jsonObj = new
        {
            seed,
            timestamp = DateTime.UtcNow.ToString("o"),
            worldSize = $"{world.Width}x{world.Height}",
            totalTiles = world.Width * world.Height,
            biomes = biomeCounts.OrderBy(b => b.Key).ToDictionary(b => b.Key, b => b.Value),
            resourceTotals = resourceTotals.OrderBy(r => r.Key).ToDictionary(r => r.Key, r => r.Value),
            tilesWithResource = tilesWithResource.OrderBy(r => r.Key).ToDictionary(r => r.Key, r => r.Value),
            totalResources,
            forestBerryAnalysis = new
            {
                forestTilesWithBerries,
                forestTilesWithoutBerries,
                totalForestTiles = totalForest,
                percentWithBerries = totalForest > 0 ? Math.Round(100.0 * forestTilesWithBerries / totalForest, 1) : 0
            },
            patchTileCount = patchTiles,
            pointFeatures = pointFeatureTypes.OrderBy(p => p.Key).ToDictionary(p => p.Key, p => p.Value)
        };

        string jsonPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "diagnostics", "d23-resource-census-seed16001.json");
        string fullPath = Path.GetFullPath(jsonPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string json = JsonSerializer.Serialize(jsonObj, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(fullPath, json);
        Console.WriteLine($"\nJSON saved to: {fullPath}");
    }
}
