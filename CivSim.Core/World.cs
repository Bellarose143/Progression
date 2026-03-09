namespace CivSim.Core;

/// <summary>
/// Owns the 2D tile grid, generates terrain with Perlin noise,
/// and provides spatial queries for agent interaction.
/// </summary>
public class World
{
    // ── Grid ───────────────────────────────────────────────────────────
    public int Width { get; }
    public int Height { get; }
    public Tile[,] Grid { get; }

    // ── Spatial Index ──────────────────────────────────────────────────
    /// <summary>
    /// Fast lookup: which agents currently occupy each tile?
    /// GDD v1.6.2: Maintained incrementally via Add/Remove/Update methods. No full rebuild needed after init.
    /// </summary>
    public Dictionary<(int X, int Y), List<Agent>> AgentsByPosition { get; }

    // ── Animal Spatial Index (D25a) ─────────────────────────────────
    public List<Animal> Animals { get; } = new();
    public List<Carcass> Carcasses { get; } = new();
    public List<Trap> Traps { get; } = new();
    public List<Pen> Pens { get; } = new();
    private Dictionary<(int X, int Y), List<Animal>> AnimalsByPosition { get; } = new();

    /// <summary>GDD v1.6.2: Counter of agent moves this tick. Reset at tick start. Used for pressure map cadence trigger.</summary>
    public int MovesThisTick { get; set; }

    // ── World Food Counter ─────────────────────────────────────────────
    /// <summary>Running total of edible food across all tiles (Berries + Grain + Animals + Fish).
    /// Maintained incrementally at mutation sites instead of scanning all tiles.</summary>
    public int TotalWorldFood { get; private set; }

    /// <summary>Adjusts the running world food counter by the given delta.</summary>
    internal void AdjustWorldFood(int delta) => TotalWorldFood += delta;

    // ── Seeding ────────────────────────────────────────────────────────
    public int Seed { get; }

    /// <summary>True when world size >= 200 (350×350 scale). Controls per-tile resource density.</summary>
    private readonly bool _largeWorld;

    private readonly Random random;

    // ── Constructors ───────────────────────────────────────────────────
    public World(int width, int height, int seed)
    {
        Width = width;
        Height = height;
        Seed = seed;
        _largeWorld = Math.Max(width, height) >= 200;
        Grid = new Tile[width, height];
        AgentsByPosition = new Dictionary<(int, int), List<Agent>>();
        random = new Random(seed);

        GenerateWorld(seed);
    }

    /// <summary>Convenience constructor using SimConfig defaults.</summary>
    public World(int seed)
        : this(SimConfig.DefaultGridWidth, SimConfig.DefaultGridHeight, seed)
    {
    }

    // ── World Generation ───────────────────────────────────────────────

    private void GenerateWorld(int seed)
    {
        var elevationNoise = new PerlinNoise(seed);
        var moistureNoise = new PerlinNoise(seed + 1000);

        // Scale noise frequency inversely with world size to keep biome patches proportional.
        // Reference: 64×64 used elevation=0.05, moisture=0.08.
        // For 350×350: 0.05 * 64/350 ≈ 0.009, 0.08 * 64/350 ≈ 0.015.
        double sizeRatio = 64.0 / Math.Max(Width, Height);
        double elevationScale = 0.05 * sizeRatio;
        double moistureScale = 0.08 * sizeRatio;

        for (int x = 0; x < Width; x++)
        {
            for (int y = 0; y < Height; y++)
            {
                double elevation = elevationNoise.GetValue(x * elevationScale, y * elevationScale);
                double moisture = moistureNoise.GetValue(x * moistureScale, y * moistureScale);

                BiomeType biome = DetermineBiome(elevation, moisture);
                Grid[x, y] = new Tile(x, y, biome);
                // Per-tile random regen offset prevents synchronized resource spikes
                Grid[x, y].RegenOffset = random.Next(0, SimConfig.RegenIntervalAnimals);
            }
        }

        PopulateResources();

        // Snapshot regen caps: tiles that spawned a resource use their CapacityOverride as the regen ceiling.
        // Tiles that never spawned a resource get no RegenCap entry (effective cap of 0),
        // preventing phantom regen on tiles that only had capacity set but no actual resources.
        for (int rx = 0; rx < Width; rx++)
            for (int ry = 0; ry < Height; ry++)
            {
                var tile = Grid[rx, ry];
                foreach (var kvp in tile.Resources)
                {
                    if (kvp.Value > 0)
                        tile.RegenCap[kvp.Key] = tile.GetCapacity(kvp.Key);
                }
            }

        // Initialize running food counter by summing all tile food once
        int initialFood = 0;
        for (int x = 0; x < Width; x++)
            for (int y = 0; y < Height; y++)
                initialFood += Grid[x, y].TotalFood();
        TotalWorldFood = initialFood;
        Console.WriteLine($"[World] TotalWorldFood initialized: {TotalWorldFood}");
    }

    private BiomeType DetermineBiome(double elevation, double moisture)
    {
        if (elevation < 0.35) return BiomeType.Water;
        if (elevation > 0.75) return BiomeType.Mountain;
        if (moisture < 0.3) return BiomeType.Desert;
        if (moisture > 0.65) return BiomeType.Forest;
        return BiomeType.Plains;
    }

    /// <summary>
    /// GDD v1.8 Section 6: Three-tier resource distribution.
    /// Tier 1: Primary resources with Perlin noise density variance.
    /// Tier 2: Secondary resource patches (clustered 3-7 tile groups).
    /// Tier 3: Point features (rare single-tile notable resources).
    /// D23: Cross-biome wood bleeding applied after primary resources.
    /// </summary>
    private void PopulateResources()
    {
        PopulatePrimaryResources();
        PopulateCrossBiomeWood();
        PopulateSecondaryPatches();
        PopulatePointFeatures();
    }

    /// <summary>
    /// Section 6 Tier 1: Primary resources on biome-appropriate tiles with Perlin noise variance.
    /// D23: Berry/animal scatter removed from Tier 1 — now placed via dedicated clustering passes.
    /// D23: Mountain stone/ore varied instead of flat values.
    /// </summary>
    private void PopulatePrimaryResources()
    {
        var densityNoise = new PerlinNoise(Seed + 2000);

        // ── Pass 1: Per-tile primary resources (no berries/animals — those use clustering) ──
        for (int x = 0; x < Width; x++)
        {
            for (int y = 0; y < Height; y++)
            {
                var tile = Grid[x, y];

                double noiseVal = densityNoise.GetValue(
                    x * SimConfig.ResourceDensityNoiseScale,
                    y * SimConfig.ResourceDensityNoiseScale);
                float densityMult = SimConfig.ResourceDensityMin
                    + (float)noiseVal * (1f - SimConfig.ResourceDensityMin);

                switch (tile.Biome)
                {
                    case BiomeType.Forest:
                        tile.CapacityOverrides[ResourceType.Wood] = SimConfig.CapacityWood;
                        tile.CapacityOverrides[ResourceType.Berries] = SimConfig.CapacityBerries;
                        // Wood: 80% of forest tiles have wood.
                        if (random.NextDouble() < 0.80)
                        {
                            if (_largeWorld)
                            {
                                // 350×350 scale: 3-4 per tile (down from up to 20 at 64×64)
                                tile.Resources[ResourceType.Wood] = random.Next(3, 5);
                            }
                            else
                            {
                                // Legacy 64×64: density-varied amount (preserves RNG sequence)
                                int maxWood = Math.Max(3, (int)(SimConfig.CapacityWood * densityMult));
                                int minWood = Math.Max(2, (int)(5 * densityMult));
                                tile.Resources[ResourceType.Wood] = random.Next(minWood, maxWood + 1);
                            }
                        }
                        // D23: Residual berry/animal scatter (2%) prevents food deserts between clusters
                        if (random.NextDouble() < 0.02)
                            tile.Resources[ResourceType.Berries] = 1;
                        // D25b: Was Animals scatter — replaced with Meat to preserve food economy + RNG
                        if (random.NextDouble() < 0.02)
                            tile.Resources[ResourceType.Meat] = 1;
                        // D21 Fix 2: Scattered loose stones on forest floor (finite, non-regenerating)
                        tile.CapacityOverrides[ResourceType.Stone] = _largeWorld ? 1 : 3;
                        if (random.NextDouble() < 0.40)
                        {
                            int stoneAmt = random.Next(1, 4); // always consume RNG call
                            tile.Resources[ResourceType.Stone] = _largeWorld ? 1 : stoneAmt;
                        }
                        break;

                    case BiomeType.Plains:
                        tile.CapacityOverrides[ResourceType.Grain] = SimConfig.CapacityGrain;
                        tile.CapacityOverrides[ResourceType.Berries] = SimConfig.CapacityBerries;
                        // Wild grain: 20% of plains tiles
                        if (random.NextDouble() < 0.20)
                        {
                            if (_largeWorld)
                                tile.Resources[ResourceType.Grain] = random.Next(2, 4); // 350×350: 2-3
                            else
                            {
                                int maxGrain = Math.Max(2, (int)(8 * densityMult));
                                tile.Resources[ResourceType.Grain] = random.Next(1, maxGrain + 1); // legacy
                            }
                        }
                        // D23: Residual berry/animal scatter (2%) prevents food deserts between clusters
                        if (random.NextDouble() < 0.02)
                            tile.Resources[ResourceType.Berries] = 1;
                        // D25b: Was Animals scatter — replaced with Meat to preserve food economy + RNG
                        if (random.NextDouble() < 0.02)
                            tile.Resources[ResourceType.Meat] = 1;
                        // D21 Fix 2: Scattered field stones (finite, non-regenerating)
                        tile.CapacityOverrides[ResourceType.Stone] = _largeWorld ? 1 : 2;
                        if (random.NextDouble() < 0.30)
                        {
                            int stoneAmt = random.Next(1, 3); // always consume RNG call
                            tile.Resources[ResourceType.Stone] = _largeWorld ? 1 : stoneAmt;
                        }
                        break;

                    case BiomeType.Mountain:
                        tile.CapacityOverrides[ResourceType.Stone] = SimConfig.CapacityStone;
                        tile.CapacityOverrides[ResourceType.Ore] = SimConfig.CapacityOre;
                        // Stone: 2-5 at 350×350 (was 1-5 at 64×64)
                        tile.Resources[ResourceType.Stone] = _largeWorld ? random.Next(2, 6) : random.Next(1, 6);
                        // Ore: 40% at 350×350 (was 5% at 64×64), 1-3 amount
                        if (random.NextDouble() < (_largeWorld ? 0.40 : 0.05))
                            tile.Resources[ResourceType.Ore] = random.Next(1, 4);
                        break;

                    case BiomeType.Water:
                        tile.CapacityOverrides[ResourceType.Fish] = SimConfig.CapacityFish;
                        // Fish: reduced to 20% (schools are the main source)
                        if (random.NextDouble() < 0.20)
                            tile.Resources[ResourceType.Fish] = random.Next(1, Math.Max(2, (int)(4 * densityMult)));
                        break;

                    case BiomeType.Desert:
                        tile.CapacityOverrides[ResourceType.Berries] = 3;
                        // D21 Fix 2: Exposed rocks in arid ground (capacity 2, 20% density)
                        tile.CapacityOverrides[ResourceType.Stone] = 2;
                        if (random.NextDouble() < 0.15)
                            tile.Resources[ResourceType.Berries] = random.Next(1, 3);
                        if (random.NextDouble() < 0.20)
                            tile.Resources[ResourceType.Stone] = random.Next(1, 3);
                        break;
                }
            }
        }

        // ── D23 Fix 1: Berry Patch Clustering ──
        PopulateBerryPatches();

        // ── D23 Fix 2: Animal Placement Cleanup ──
        PopulateAnimalClusters();
    }

    /// <summary>
    /// D23 Fix 1: Berry patches.
    /// 350×350: clusters of 15-25 forest tiles, 2-3 center, 1 edge.
    /// 64×64 (legacy): clusters of 2-5 tiles, 8-10 center, 3-5 edge.
    /// </summary>
    private void PopulateBerryPatches()
    {
        var forestTiles = new List<(int X, int Y)>();
        for (int x = 0; x < Width; x++)
            for (int y = 0; y < Height; y++)
                if (Grid[x, y].Biome == BiomeType.Forest)
                    forestTiles.Add((x, y));

        if (forestTiles.Count == 0) return;

        // Patch center selection rate: 1% for large worlds (fewer, bigger), 10% for legacy
        double centerChance = _largeWorld ? 0.01 : 0.10;
        var patchCenters = new List<(int X, int Y)>();
        foreach (var pos in forestTiles)
        {
            if (random.NextDouble() < centerChance)
                patchCenters.Add(pos);
        }

        var berryTiles = new HashSet<(int, int)>();

        foreach (var center in patchCenters)
        {
            int patchSize = _largeWorld ? random.Next(15, 26) : random.Next(2, 6);
            var patch = FloodFillPatch(center.X, center.Y, patchSize,
                t => t.Biome == BiomeType.Forest);

            // Center tile berries
            var centerTile = Grid[center.X, center.Y];
            centerTile.Resources[ResourceType.Berries] = _largeWorld
                ? random.Next(2, 4)    // 350×350: 2-3
                : random.Next(8, 11);  // 64×64: 8-10
            berryTiles.Add((center.X, center.Y));

            // Edge tile berries
            for (int i = 1; i < patch.Count; i++)
            {
                var (px, py) = patch[i];
                var edgeTile = Grid[px, py];
                edgeTile.Resources[ResourceType.Berries] = _largeWorld
                    ? 1                    // 350×350: 1
                    : random.Next(3, 6);   // 64×64: 3-5
                berryTiles.Add((px, py));
            }
        }
    }

    /// <summary>
    /// D25a: Animal placement — creates Animal entities AND sets tile Resources[Animals]
    /// for backward compatibility (agents still eat from tiles until D25b).
    /// RNG cascade: identical random.Next/NextDouble call sequence to the D23 version.
    /// Species selection uses a secondary RNG seeded per-tile to avoid cascade.
    /// </summary>
    private void PopulateAnimalClusters()
    {
        var eligibleTiles = new List<(int X, int Y)>();
        for (int x = 0; x < Width; x++)
            for (int y = 0; y < Height; y++)
            {
                var biome = Grid[x, y].Biome;
                if (biome == BiomeType.Forest || biome == BiomeType.Plains)
                    eligibleTiles.Add((x, y));
            }

        if (eligibleTiles.Count == 0) return;

        // 350×350: ~0.005 density to keep total animals similar to 64×64 (~400-600)
        // 64×64 legacy: 0.04 density (preserves RNG-dependent test baselines)
        double animalDensity = _largeWorld ? 0.005 : 0.04;
        int targetCount = (int)(eligibleTiles.Count * animalDensity);
        var animalTiles = new HashSet<(int, int)>();
        (int X, int Y) lastPlaced = (-1, -1);

        // Shuffle eligible tiles for random selection — SAME random calls as before
        for (int i = eligibleTiles.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (eligibleTiles[i], eligibleTiles[j]) = (eligibleTiles[j], eligibleTiles[i]);
        }

        int placed = 0;
        int eligIndex = 0;
        int nextHerdId = 1;

        while (placed < targetCount && eligIndex < eligibleTiles.Count)
        {
            (int X, int Y) chosen;

            // 50% chance to try adjacent to last placed tile — SAME random call
            if (lastPlaced.X >= 0 && random.NextDouble() < 0.50)
            {
                var adj = GetAdjacentEligible(lastPlaced.X, lastPlaced.Y, animalTiles);
                if (adj.HasValue)
                {
                    chosen = adj.Value;
                }
                else
                {
                    chosen = eligibleTiles[eligIndex++];
                }
            }
            else
            {
                chosen = eligibleTiles[eligIndex++];
            }

            if (animalTiles.Contains(chosen)) continue;

            animalTiles.Add(chosen);
            var tile = Grid[chosen.X, chosen.Y];
            int animalCount = random.Next(3, 6); // 3-5 animals — SAME random call
            // D25a: Spawn Animal entities using SECONDARY RNG (no cascade risk)
            var tileRng = new Random(Seed + chosen.X * 1000 + chosen.Y);
            var biome = tile.Biome;
            int herdId = nextHerdId++;
            // D25a fix: Clamp territory center away from map edges to prevent edge trapping
            // Use wolf territory radius (largest) but cap to half world size for small test worlds
            int maxRadius = Math.Min(25, Math.Min(Width, Height) / 2 - 1);
            var territoryCenter = (
                Math.Clamp(chosen.X, maxRadius, Width - 1 - maxRadius),
                Math.Clamp(chosen.Y, maxRadius, Height - 1 - maxRadius)
            );

            for (int a = 0; a < animalCount; a++)
            {
                var species = PickSpeciesForBiome(biome, tileRng);
                var animal = new Animal(species, chosen.X, chosen.Y, herdId, territoryCenter);
                Animals.Add(animal);
                AddAnimalToIndex(animal);
            }
            // D25b: Place Meat on tiles with animal clusters to preserve food economy
            // (was tile.Resources[Animals] = animalCount in D25a)
            tile.Resources[ResourceType.Meat] = animalCount;

            lastPlaced = chosen;
            placed++;
        }
    }

    /// <summary>D25a: Species selection by biome using secondary RNG.</summary>
    private static AnimalSpecies PickSpeciesForBiome(BiomeType biome, Random rng)
    {
        double roll = rng.NextDouble();
        if (biome == BiomeType.Forest)
        {
            if (roll < 0.35) return AnimalSpecies.Deer;
            if (roll < 0.65) return AnimalSpecies.Rabbit;
            if (roll < 0.90) return AnimalSpecies.Boar;
            return AnimalSpecies.Wolf;
        }
        else // Plains
        {
            if (roll < 0.20) return AnimalSpecies.Cow;
            if (roll < 0.40) return AnimalSpecies.Sheep;
            if (roll < 0.70) return AnimalSpecies.Rabbit;
            if (roll < 0.90) return AnimalSpecies.Deer;
            return AnimalSpecies.Wolf;
        }
    }

    /// <summary>Returns a random adjacent tile that is Forest/Plains and not yet an animal tile.</summary>
    private (int X, int Y)? GetAdjacentEligible(int x, int y, HashSet<(int, int)> used)
    {
        var candidates = new List<(int X, int Y)>();
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = x + dx, ny = y + dy;
                if (!IsInBounds(nx, ny)) continue;
                if (used.Contains((nx, ny))) continue;
                var biome = Grid[nx, ny].Biome;
                if (biome == BiomeType.Forest || biome == BiomeType.Plains)
                    candidates.Add((nx, ny));
            }
        if (candidates.Count == 0) return null;
        return candidates[random.Next(candidates.Count)];
    }

    /// <summary>
    /// D23 Fix 3: Cross-biome wood bleeding. Non-forest biomes get sparse wood
    /// based on proximity to forest. All cross-biome wood is finite, non-regenerating.
    /// </summary>
    private void PopulateCrossBiomeWood()
    {
        for (int x = 0; x < Width; x++)
        {
            for (int y = 0; y < Height; y++)
            {
                var tile = Grid[x, y];

                switch (tile.Biome)
                {
                    case BiomeType.Plains:
                        bool adjForest = HasAdjacentBiome(x, y, BiomeType.Forest);
                        if (adjForest && random.NextDouble() < 0.30)
                        {
                            // Plains adjacent to forest: 30% chance, 1-3 wood
                            tile.CapacityOverrides[ResourceType.Wood] = 3;
                            tile.Resources[ResourceType.Wood] = random.Next(1, 4);
                        }
                        else if (!adjForest && random.NextDouble() < 0.05)
                        {
                            // Plains not adjacent to forest: 5% chance, 1 wood (lone tree)
                            tile.CapacityOverrides[ResourceType.Wood] = 1;
                            tile.Resources[ResourceType.Wood] = 1;
                        }
                        break;

                    case BiomeType.Mountain:
                        if (random.NextDouble() < 0.15)
                        {
                            // Mountain: 15% chance, 1-2 wood
                            tile.CapacityOverrides[ResourceType.Wood] = 2;
                            tile.Resources[ResourceType.Wood] = random.Next(1, 3);
                        }
                        break;

                    // Desert: 0 wood. Water: 0 wood. No change.
                }
            }
        }
    }

    /// <summary>
    /// Section 6 Tier 2: Secondary resource patches — clustered groups of 3-7 tiles.
    /// Berry patches in forests, animal herds on plains/forest-edge, fish schools along coast, grain on plains.
    /// Each patch is spaced apart from others of the same type.
    /// </summary>
    private void PopulateSecondaryPatches()
    {
        // D23: Berries and Animals removed from Tier 2 — now handled by D23 clustering in PopulatePrimaryResources.
        // Only Fish and Grain patches remain in Tier 2.

        PlacePatches(ResourceType.Fish, SimConfig.FishSchoolCount, SimConfig.PatchFishAmount,
            tile => tile.Biome == BiomeType.Water && HasAdjacentLand(tile.X, tile.Y));

        PlacePatches(ResourceType.Grain, SimConfig.GrainPatchCount, SimConfig.PatchGrainAmount,
            tile => tile.Biome == BiomeType.Plains);
    }

    /// <summary>Places 'count' patches of the given resource type on tiles matching the filter.</summary>
    private void PlacePatches(ResourceType resource, int count, int amountPerTile, Func<Tile, bool> tileFilter)
    {
        // Collect all valid tiles
        var validTiles = new List<(int X, int Y)>();
        for (int x = 0; x < Width; x++)
            for (int y = 0; y < Height; y++)
                if (tileFilter(Grid[x, y]))
                    validTiles.Add((x, y));

        if (validTiles.Count == 0) return;

        var patchCenters = new List<(int X, int Y)>();
        int patchIndex = 0;

        for (int i = 0; i < count && validTiles.Count > 0; i++)
        {
            // Pick a random valid tile as center
            int attempts = 0;
            (int X, int Y) center;
            bool tooClose;
            do
            {
                center = validTiles[random.Next(validTiles.Count)];
                tooClose = patchCenters.Any(c =>
                    Math.Max(Math.Abs(c.X - center.X), Math.Abs(c.Y - center.Y)) < SimConfig.PatchMinSpacing);
                attempts++;
            } while (tooClose && attempts < 50);

            if (tooClose) continue; // Couldn't find a valid spot

            patchCenters.Add(center);
            string patchId = $"{resource}_{patchIndex++}";

            // BFS flood-fill to create patch of 3-7 tiles
            int patchSize = random.Next(SimConfig.PatchSizeMin, SimConfig.PatchSizeMax + 1);
            var patchTiles = FloodFillPatch(center.X, center.Y, patchSize, tileFilter);

            foreach (var (px, py) in patchTiles)
            {
                var tile = Grid[px, py];
                tile.IsResourcePatch = true;
                tile.PatchId = patchId;

                // Set resource amount (add to any existing from Tier 1)
                if (!tile.Resources.ContainsKey(resource))
                    tile.Resources[resource] = 0;
                tile.Resources[resource] += amountPerTile;

                // Ensure capacity is set
                if (!tile.CapacityOverrides.ContainsKey(resource))
                {
                    int cap = resource switch
                    {
                        ResourceType.Berries => SimConfig.CapacityBerries,
                        ResourceType.Fish => SimConfig.CapacityFish,
                        ResourceType.Grain => SimConfig.CapacityGrain,
                        _ => 15
                    };
                    tile.CapacityOverrides[resource] = cap;
                }

                // Clamp to capacity
                tile.Resources[resource] = Math.Min(tile.Resources[resource], tile.CapacityOverrides[resource]);
            }
        }
    }

    /// <summary>BFS flood-fill from a center tile, expanding to adjacent tiles matching the filter.</summary>
    private List<(int X, int Y)> FloodFillPatch(int cx, int cy, int maxSize, Func<Tile, bool> tileFilter)
    {
        var result = new List<(int X, int Y)> { (cx, cy) };
        var visited = new HashSet<(int, int)> { (cx, cy) };
        var frontier = new Queue<(int X, int Y)>();
        frontier.Enqueue((cx, cy));

        while (frontier.Count > 0 && result.Count < maxSize)
        {
            var (fx, fy) = frontier.Dequeue();

            // Check 4-connected neighbors (more natural patch shapes than 8-connected)
            int[] dx = { 0, 0, 1, -1 };
            int[] dy = { 1, -1, 0, 0 };

            // Shuffle neighbor order for variety
            for (int i = 3; i > 0; i--)
            {
                int j = random.Next(i + 1);
                (dx[i], dx[j]) = (dx[j], dx[i]);
                (dy[i], dy[j]) = (dy[j], dy[i]);
            }

            for (int d = 0; d < 4 && result.Count < maxSize; d++)
            {
                int nx = fx + dx[d], ny = fy + dy[d];
                if (!IsInBounds(nx, ny)) continue;
                if (visited.Contains((nx, ny))) continue;
                visited.Add((nx, ny));

                if (tileFilter(Grid[nx, ny]))
                {
                    result.Add((nx, ny));
                    frontier.Enqueue((nx, ny));
                }
            }
        }

        return result;
    }

    /// <summary>Returns true if any of the 8 neighbors is the specified biome.</summary>
    public bool HasAdjacentBiome(int x, int y, BiomeType biome)
    {
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = x + dx, ny = y + dy;
                if (IsInBounds(nx, ny) && Grid[nx, ny].Biome == biome)
                    return true;
            }
        return false;
    }

    /// <summary>Returns true if any of the 8 neighbors is a non-Water biome (land).</summary>
    private bool HasAdjacentLand(int x, int y)
    {
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = x + dx, ny = y + dy;
                if (IsInBounds(nx, ny) && Grid[nx, ny].Biome != BiomeType.Water)
                    return true;
            }
        return false;
    }

    /// <summary>
    /// Section 6 Tier 3: Point features — rare single-tile notable resources.
    /// Caves, ore veins, natural springs, rich stone quarries.
    /// These drive permanent settlement geographic knowledge (Section 5).
    /// </summary>
    private void PopulatePointFeatures()
    {
        // Caves: mountain tiles, landmark only (no resources)
        PlacePointFeatures("cave", SimConfig.CaveCount,
            tile => tile.Biome == BiomeType.Mountain,
            (tile) => { /* Cave: geographic landmark, no direct resources */ });

        // Ore veins: mountain tiles with rich ore deposits
        PlacePointFeatures("ore_vein", SimConfig.OreVeinCount,
            tile => tile.Biome == BiomeType.Mountain,
            (tile) =>
            {
                tile.CapacityOverrides[ResourceType.Ore] = SimConfig.OreVeinAmount;
                tile.Resources[ResourceType.Ore] = SimConfig.OreVeinAmount;
            });

        // Natural springs: forest/plains tiles adjacent to water
        PlacePointFeatures("natural_spring", SimConfig.NaturalSpringCount,
            tile => (tile.Biome == BiomeType.Forest || tile.Biome == BiomeType.Plains)
                    && HasAdjacentBiome(tile.X, tile.Y, BiomeType.Water),
            (tile) => { /* Spring: geographic landmark, future mechanic potential */ });

        // Rich quarries: mountain tiles with concentrated stone
        PlacePointFeatures("rich_quarry", SimConfig.RichQuarryCount,
            tile => tile.Biome == BiomeType.Mountain,
            (tile) =>
            {
                tile.CapacityOverrides[ResourceType.Stone] = SimConfig.RichQuarryStoneAmount;
                tile.Resources[ResourceType.Stone] = SimConfig.RichQuarryStoneAmount;
            });
    }

    /// <summary>Places 'count' point features of the given type on tiles matching the filter.</summary>
    private void PlacePointFeatures(string featureType, int count,
        Func<Tile, bool> tileFilter, Action<Tile> applyResources)
    {
        var validTiles = new List<(int X, int Y)>();
        for (int x = 0; x < Width; x++)
            for (int y = 0; y < Height; y++)
                if (tileFilter(Grid[x, y]) && !Grid[x, y].IsPointFeature)
                    validTiles.Add((x, y));

        if (validTiles.Count == 0) return;

        var placedCenters = new List<(int X, int Y)>();

        for (int i = 0; i < count && validTiles.Count > 0; i++)
        {
            int attempts = 0;
            (int X, int Y) pos;
            bool tooClose;
            do
            {
                pos = validTiles[random.Next(validTiles.Count)];
                tooClose = placedCenters.Any(c =>
                    Math.Max(Math.Abs(c.X - pos.X), Math.Abs(c.Y - pos.Y)) < SimConfig.PointFeatureMinSpacing);
                attempts++;
            } while (tooClose && attempts < 50);

            if (tooClose) continue;

            placedCenters.Add(pos);
            var tile = Grid[pos.X, pos.Y];
            tile.IsPointFeature = true;
            tile.PointFeatureType = featureType;
            applyResources(tile);
        }
    }

    // ── Tile Access ────────────────────────────────────────────────────

    public Tile GetTile(int x, int y)
    {
        if (!IsInBounds(x, y))
            throw new ArgumentOutOfRangeException($"Coordinates ({x},{y}) out of bounds");

        return Grid[x, y];
    }

    public bool IsInBounds(int x, int y)
    {
        return x >= 0 && x < Width && y >= 0 && y < Height;
    }

    /// <summary>
    /// Returns all tiles within the given Chebyshev radius of (centerX, centerY).
    /// </summary>
    public List<Tile> GetNeighbors(int centerX, int centerY, int radius = 1)
    {
        var neighbors = new List<Tile>();
        for (int dx = -radius; dx <= radius; dx++)
        {
            for (int dy = -radius; dy <= radius; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = centerX + dx;
                int ny = centerY + dy;
                if (IsInBounds(nx, ny))
                    neighbors.Add(Grid[nx, ny]);
            }
        }
        return neighbors;
    }

    // ── Spatial Index ──────────────────────────────────────────────────

    /// <summary>Full rebuild of the agent spatial index from the agent list. Used once at initialization.</summary>
    public void RebuildSpatialIndex(List<Agent> agents)
    {
        AgentsByPosition.Clear();
        foreach (var agent in agents)
        {
            if (!agent.IsAlive) continue;
            var key = (agent.X, agent.Y);
            if (!AgentsByPosition.ContainsKey(key))
                AgentsByPosition[key] = new List<Agent>();
            AgentsByPosition[key].Add(agent);
        }
    }

    /// <summary>GDD v1.6.2: O(1) incremental add — for spawns and births.</summary>
    public void AddAgentToIndex(Agent agent)
    {
        var key = (agent.X, agent.Y);
        if (!AgentsByPosition.ContainsKey(key))
            AgentsByPosition[key] = new List<Agent>();
        AgentsByPosition[key].Add(agent);
    }

    /// <summary>GDD v1.6.2: O(1) incremental remove — for deaths.</summary>
    public void RemoveAgentFromIndex(Agent agent)
    {
        var key = (agent.X, agent.Y);
        if (AgentsByPosition.TryGetValue(key, out var list))
        {
            list.Remove(agent);
            if (list.Count == 0)
                AgentsByPosition.Remove(key);
        }
    }

    /// <summary>GDD v1.6.2: O(1) incremental position update — called by Agent.MoveTo().</summary>
    public void UpdateAgentPosition(Agent agent, int oldX, int oldY, int newX, int newY)
    {
        // Remove from old position
        var oldKey = (oldX, oldY);
        if (AgentsByPosition.TryGetValue(oldKey, out var oldList))
        {
            oldList.Remove(agent);
            if (oldList.Count == 0)
                AgentsByPosition.Remove(oldKey);
        }

        // Add to new position
        var newKey = (newX, newY);
        if (!AgentsByPosition.ContainsKey(newKey))
            AgentsByPosition[newKey] = new List<Agent>();
        AgentsByPosition[newKey].Add(agent);

        MovesThisTick++;
    }

    /// <summary>Gets agents at a specific position. Returns empty list if none.</summary>
    public List<Agent> GetAgentsAt(int x, int y)
    {
        return AgentsByPosition.TryGetValue((x, y), out var agents) ? agents : new List<Agent>();
    }

    /// <summary>Finds an agent by ID in the spatial index. Returns null if not found.</summary>
    public Agent? GetAgentById(int id)
    {
        foreach (var agents in AgentsByPosition.Values)
        {
            foreach (var agent in agents)
            {
                if (agent.Id == id)
                    return agent;
            }
        }
        return null;
    }

    /// <summary>Gets all agents adjacent to the given position (Chebyshev radius 1).</summary>
    public List<Agent> GetAdjacentAgents(int x, int y)
    {
        var result = new List<Agent>();
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                if (AgentsByPosition.TryGetValue((x + dx, y + dy), out var agents))
                    result.AddRange(agents);
            }
        }
        return result;
    }

    // ── Animal Spatial Index Methods (D25a) ──────────────────────────

    public void AddAnimalToIndex(Animal animal)
    {
        var key = (animal.X, animal.Y);
        if (!AnimalsByPosition.ContainsKey(key))
            AnimalsByPosition[key] = new List<Animal>();
        AnimalsByPosition[key].Add(animal);
    }

    public void RemoveAnimalFromIndex(Animal animal)
    {
        var key = (animal.X, animal.Y);
        if (AnimalsByPosition.TryGetValue(key, out var list))
        {
            list.Remove(animal);
            if (list.Count == 0)
                AnimalsByPosition.Remove(key);
        }
    }

    public void UpdateAnimalPosition(Animal animal, int oldX, int oldY)
    {
        var oldKey = (oldX, oldY);
        if (AnimalsByPosition.TryGetValue(oldKey, out var oldList))
        {
            oldList.Remove(animal);
            if (oldList.Count == 0)
                AnimalsByPosition.Remove(oldKey);
        }
        var newKey = (animal.X, animal.Y);
        if (!AnimalsByPosition.ContainsKey(newKey))
            AnimalsByPosition[newKey] = new List<Animal>();
        AnimalsByPosition[newKey].Add(animal);
    }

    public List<Animal> GetAnimalsAt(int x, int y)
    {
        return AnimalsByPosition.TryGetValue((x, y), out var animals) ? animals : new List<Animal>();
    }

    public List<Animal> GetAnimalsInRadius(int x, int y, int radius)
    {
        var result = new List<Animal>();
        for (int dx = -radius; dx <= radius; dx++)
        {
            for (int dy = -radius; dy <= radius; dy++)
            {
                if (AnimalsByPosition.TryGetValue((x + dx, y + dy), out var animals))
                    result.AddRange(animals.Where(a => a.IsAlive));
            }
        }
        return result;
    }

    // ── Gathering Pressure ─────────────────────────────────────────────

    /// <summary>
    /// Pressure map: how many agents are within GatheringPressureRadius of each tile.
    /// Computed once per tick by Simulation, then used during regeneration.
    /// </summary>
    public int[,]? PressureMap { get; set; }

    /// <summary>
    /// Builds the gathering pressure map from the current spatial index.
    /// For each living agent, increments all tiles within GatheringPressureRadius.
    /// </summary>
    public void BuildPressureMap()
    {
        PressureMap = new int[Width, Height];
        int radius = SimConfig.GatheringPressureRadius;

        foreach (var agents in AgentsByPosition.Values)
        {
            foreach (var agent in agents)
            {
                if (!agent.IsAlive) continue;

                int minX = Math.Max(0, agent.X - radius);
                int maxX = Math.Min(Width - 1, agent.X + radius);
                int minY = Math.Max(0, agent.Y - radius);
                int maxY = Math.Min(Height - 1, agent.Y + radius);

                for (int px = minX; px <= maxX; px++)
                    for (int py = minY; py <= maxY; py++)
                        PressureMap[px, py]++;
            }
        }
    }

    // ── Resource Regeneration ──────────────────────────────────────────

    /// <summary>
    /// Per-resource regeneration with different tick intervals.
    /// GDD v1.6.1: includes overgrazing penalties and gathering pressure.
    /// Stone and Ore are non-renewable and never regenerate.
    /// </summary>
    public void RegenerateResources(int currentTick)
    {
        for (int x = 0; x < Width; x++)
        {
            for (int y = 0; y < Height; y++)
            {
                var tile = Grid[x, y];

                // Increment TicksSinceLastGathered for overgrazing recovery
                tile.TicksSinceLastGathered++;

                RegenerateTile(tile, currentTick);
            }
        }
    }

    private void RegenerateTile(Tile tile, int currentTick)
    {
        // Per-resource regeneration based on biome and tick intervals
        switch (tile.Biome)
        {
            case BiomeType.Forest:
                TryRegen(tile, ResourceType.Wood, SimConfig.RegenIntervalWood, 1, currentTick);
                TryRegen(tile, ResourceType.Berries, SimConfig.RegenIntervalBerries, 1, currentTick);
                break;

            case BiomeType.Plains:
                // Farmed grain regens based on tending state
                if (tile.HasFarm)
                {
                    bool recentlyTended = (currentTick - tile.LastTendedTick) <= SimConfig.FarmTendedGracePeriod;
                    if (recentlyTended)
                    {
                        // Tended farm: boosted regen, immune to gathering pressure
                        TryRegenFarm(tile, ResourceType.Grain, SimConfig.RegenIntervalGrainFarmed, SimConfig.RegenAmountGrainFarmed, currentTick);
                    }
                    else
                    {
                        // GDD v1.7.2: Untended farm reverts to wild grain regen rate
                        TryRegen(tile, ResourceType.Grain, SimConfig.RegenIntervalGrain, 1, currentTick);
                    }
                    // Farm tiles: only grain regenerates, no berries/other resources
                    break;
                }
                TryRegen(tile, ResourceType.Berries, SimConfig.RegenIntervalBerries, 1, currentTick);
                break;

            case BiomeType.Mountain:
                // Stone and Ore are NON-RENEWABLE — no regeneration
                break;

            case BiomeType.Water:
                TryRegen(tile, ResourceType.Fish, SimConfig.RegenIntervalFish, 1, currentTick);
                break;

            case BiomeType.Desert:
                TryRegen(tile, ResourceType.Berries, SimConfig.RegenIntervalBerries, 1, currentTick);
                break;
        }
    }

    /// <summary>
    /// Attempts to regenerate a resource on a tile, applying overgrazing and gathering pressure penalties.
    /// </summary>
    private void TryRegen(Tile tile, ResourceType resource, int interval, int amount, int currentTick)
    {
        if (interval <= 0) return; // 0 means non-renewable
        // Per-tile offset desynchronizes regen across the map (no global spikes)
        if ((currentTick + tile.RegenOffset) % interval != 0) return;

        // Use RegenCap (snapshot of initial spawn) instead of GetCapacity (CapacityOverrides).
        // This prevents tiles that never had a resource from slowly regenerating it to full capacity.
        int cap = tile.RegenCap.GetValueOrDefault(resource, 0);
        if (cap <= 0) return; // Resource was never present on this tile

        int current = tile.Resources.GetValueOrDefault(resource, 0);
        if (current >= cap) return;

        // Overgrazing check: ratio of current / capacity
        float ratio = cap > 0 ? (float)current / cap : 1f;

        // Critical overgrazing: regen paused until tile ungathered for recovery period
        if (ratio < SimConfig.OvergrazingThresholdCritical)
        {
            if (tile.TicksSinceLastGathered < SimConfig.OvergrazingRecoveryTicks)
                return; // Still recovering — no regen
        }

        // Low overgrazing: halve regen rate (skip every other interval)
        if (ratio < SimConfig.OvergrazingThresholdLow && (currentTick + tile.RegenOffset) % (interval * 2) != 0)
            return;

        // Gathering pressure penalty
        float pressureMultiplier = 1.0f;
        if (PressureMap != null)
        {
            int pressure = PressureMap[tile.X, tile.Y];
            if (pressure >= SimConfig.GatheringPressureThreshold3)
                pressureMultiplier = 0.25f;
            else if (pressure >= SimConfig.GatheringPressureThreshold2)
                pressureMultiplier = 0.50f;
            else if (pressure >= SimConfig.GatheringPressureThreshold1)
                pressureMultiplier = 0.75f;
        }

        // Apply pressure: reduce amount or skip based on multiplier
        int effectiveAmount = Math.Max(1, (int)(amount * pressureMultiplier));
        if (pressureMultiplier < 1.0f && random.NextDouble() > pressureMultiplier)
            return; // Probabilistic skip for fractional pressure

        int newValue = Math.Min(cap, current + effectiveAmount);
        tile.Resources[resource] = newValue;
        if (ModeTransitionManager.IsFoodResource(resource))
            AdjustWorldFood(newValue - current);
    }

    /// <summary>
    /// Farm regen: immune to gathering pressure when tended.
    /// </summary>
    private void TryRegenFarm(Tile tile, ResourceType resource, int interval, int amount, int currentTick)
    {
        if (interval <= 0) return;
        if ((currentTick + tile.RegenOffset) % interval != 0) return;

        int current = tile.Resources.GetValueOrDefault(resource, 0);
        int cap = tile.GetCapacity(resource);
        if (current < cap)
        {
            int newValue = Math.Min(cap, current + amount);
            tile.Resources[resource] = newValue;
            if (ModeTransitionManager.IsFoodResource(resource))
                AdjustWorldFood(newValue - current);
        }
    }

    // ── Diagnostics ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns the sum of all resource units across all tiles on the map.
    /// Used for before/after measurement of resource regen fixes.
    /// </summary>
    public int TotalResourceCount()
    {
        int total = 0;
        for (int x = 0; x < Width; x++)
            for (int y = 0; y < Height; y++)
                foreach (var kvp in Grid[x, y].Resources)
                    total += kvp.Value;
        return total;
    }
}
