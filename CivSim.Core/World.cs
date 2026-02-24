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

    /// <summary>GDD v1.6.2: Counter of agent moves this tick. Reset at tick start. Used for pressure map cadence trigger.</summary>
    public int MovesThisTick { get; set; }

    // ── Seeding ────────────────────────────────────────────────────────
    public int Seed { get; }

    private readonly Random random;

    // ── Constructors ───────────────────────────────────────────────────
    public World(int width, int height, int seed)
    {
        Width = width;
        Height = height;
        Seed = seed;
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

        double elevationScale = 0.05;
        double moistureScale = 0.08;

        for (int x = 0; x < Width; x++)
        {
            for (int y = 0; y < Height; y++)
            {
                double elevation = elevationNoise.GetValue(x * elevationScale, y * elevationScale);
                double moisture = moistureNoise.GetValue(x * moistureScale, y * moistureScale);

                BiomeType biome = DetermineBiome(elevation, moisture);
                Grid[x, y] = new Tile(x, y, biome);
            }
        }

        PopulateResources();
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
    /// </summary>
    private void PopulateResources()
    {
        PopulatePrimaryResources();
        PopulateSecondaryPatches();
        PopulatePointFeatures();
    }

    /// <summary>
    /// Section 6 Tier 1: Primary resources on biome-appropriate tiles with Perlin noise variance.
    /// Creates richer and poorer areas within the same biome type.
    /// Secondary resources (berries, animals) have REDUCED per-tile probability — patches provide the main source.
    /// </summary>
    private void PopulatePrimaryResources()
    {
        var densityNoise = new PerlinNoise(Seed + 2000);

        for (int x = 0; x < Width; x++)
        {
            for (int y = 0; y < Height; y++)
            {
                var tile = Grid[x, y];

                // Sample density noise → multiplier between ResourceDensityMin and 1.0
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
                        tile.CapacityOverrides[ResourceType.Animals] = SimConfig.CapacityAnimals;
                        // Wood: still common (80%) but quantity varies with density
                        if (random.NextDouble() < 0.80)
                        {
                            int maxWood = Math.Max(3, (int)(SimConfig.CapacityWood * densityMult));
                            int minWood = Math.Max(2, (int)(5 * densityMult));
                            tile.Resources[ResourceType.Wood] = random.Next(minWood, maxWood + 1);
                        }
                        // Berries: reduced to 15% per-tile (patches are the main source)
                        if (random.NextDouble() < 0.15)
                            tile.Resources[ResourceType.Berries] = random.Next(1, Math.Max(2, (int)(4 * densityMult)));
                        // Animals: reduced to 8% (herds are the main source)
                        if (random.NextDouble() < 0.08)
                            tile.Resources[ResourceType.Animals] = random.Next(1, Math.Max(2, (int)(3 * densityMult)));
                        break;

                    case BiomeType.Plains:
                        tile.CapacityOverrides[ResourceType.Grain] = SimConfig.CapacityGrain;
                        tile.CapacityOverrides[ResourceType.Berries] = SimConfig.CapacityBerries;
                        tile.CapacityOverrides[ResourceType.Animals] = SimConfig.CapacityAnimals;
                        // Wild grain: reduced to 20% (concentrations are the main source)
                        if (random.NextDouble() < 0.20)
                        {
                            int maxGrain = Math.Max(2, (int)(8 * densityMult));
                            tile.Resources[ResourceType.Grain] = random.Next(1, maxGrain + 1);
                        }
                        // Berries: reduced to 12%
                        if (random.NextDouble() < 0.12)
                            tile.Resources[ResourceType.Berries] = random.Next(1, Math.Max(2, (int)(4 * densityMult)));
                        // Animals: reduced to 10%
                        if (random.NextDouble() < 0.10)
                            tile.Resources[ResourceType.Animals] = random.Next(1, Math.Max(2, (int)(3 * densityMult)));
                        break;

                    case BiomeType.Mountain:
                        tile.CapacityOverrides[ResourceType.Stone] = SimConfig.CapacityStone;
                        tile.CapacityOverrides[ResourceType.Ore] = SimConfig.CapacityOre;
                        // Stone: reduced to 40% (quarries are the main source for large amounts)
                        if (random.NextDouble() < 0.40)
                        {
                            int maxStone = Math.Max(3, (int)(SimConfig.CapacityStone * densityMult * 0.6f));
                            tile.Resources[ResourceType.Stone] = random.Next(2, maxStone + 1);
                        }
                        // Ore: reduced to 10% (ore veins are the main source)
                        if (random.NextDouble() < 0.10)
                            tile.Resources[ResourceType.Ore] = random.Next(1, Math.Max(2, (int)(4 * densityMult)));
                        break;

                    case BiomeType.Water:
                        tile.CapacityOverrides[ResourceType.Fish] = SimConfig.CapacityFish;
                        // Fish: reduced to 20% (schools are the main source)
                        if (random.NextDouble() < 0.20)
                            tile.Resources[ResourceType.Fish] = random.Next(1, Math.Max(2, (int)(4 * densityMult)));
                        break;

                    case BiomeType.Desert:
                        tile.CapacityOverrides[ResourceType.Berries] = 3;
                        tile.CapacityOverrides[ResourceType.Stone] = 4;
                        if (random.NextDouble() < 0.15)
                            tile.Resources[ResourceType.Berries] = random.Next(1, 3);
                        if (random.NextDouble() < 0.1)
                            tile.Resources[ResourceType.Stone] = random.Next(1, 4);
                        break;
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
        PlacePatches(ResourceType.Berries, SimConfig.BerryPatchCount, SimConfig.PatchBerryAmount,
            tile => tile.Biome == BiomeType.Forest);

        PlacePatches(ResourceType.Animals, SimConfig.AnimalHerdCount, SimConfig.PatchAnimalAmount,
            tile => tile.Biome == BiomeType.Plains
                || (tile.Biome == BiomeType.Forest && HasAdjacentBiome(tile.X, tile.Y, BiomeType.Plains)));

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
                        ResourceType.Animals => SimConfig.CapacityAnimals,
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
    private bool HasAdjacentBiome(int x, int y, BiomeType biome)
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
                TryRegen(tile, ResourceType.Animals, SimConfig.RegenIntervalAnimals, 1, currentTick);
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
                }
                TryRegen(tile, ResourceType.Berries, SimConfig.RegenIntervalBerries, 1, currentTick);
                TryRegen(tile, ResourceType.Animals, SimConfig.RegenIntervalAnimals, 1, currentTick);
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
        if (currentTick % interval != 0) return;

        int current = tile.Resources.GetValueOrDefault(resource, 0);
        int cap = tile.GetCapacity(resource);
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
        if (ratio < SimConfig.OvergrazingThresholdLow && currentTick % (interval * 2) != 0)
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

        tile.Resources[resource] = Math.Min(cap, current + effectiveAmount);
    }

    /// <summary>
    /// Farm regen: immune to gathering pressure when tended.
    /// </summary>
    private void TryRegenFarm(Tile tile, ResourceType resource, int interval, int amount, int currentTick)
    {
        if (interval <= 0) return;
        if (currentTick % interval != 0) return;

        int current = tile.Resources.GetValueOrDefault(resource, 0);
        int cap = tile.GetCapacity(resource);
        if (current < cap)
        {
            tile.Resources[resource] = Math.Min(cap, current + amount);
        }
    }
}
