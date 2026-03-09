namespace CivSim.Core;

/// <summary>
/// One cell on the world grid. Supports multiple resource types per tile,
/// structures, build progress, and terrain modifiers from BiomeDefaults.
/// </summary>
public class Tile
{
    // ── Identity ───────────────────────────────────────────────────────
    public BiomeType Biome { get; }
    public int X { get; }
    public int Y { get; }

    // ── Resources ──────────────────────────────────────────────────────
    /// <summary>Current resource amounts on this tile. Multiple resource types can coexist.</summary>
    public Dictionary<ResourceType, int> Resources { get; }

    /// <summary>Per-resource-type capacity limits. Falls back to BiomeDefaults.ResourceCapacity if absent.</summary>
    public Dictionary<ResourceType, int> CapacityOverrides { get; }

    /// <summary>Maximum amount each resource can regen TO on this tile, based on initial spawn amounts.
    /// Resources not present at world-gen get no entry (effective regen cap of 0).
    /// This prevents tiles that never spawned a resource from slowly regenerating it to full capacity.</summary>
    public Dictionary<ResourceType, int> RegenCap { get; }

    // ── Structures ─────────────────────────────────────────────────────
    /// <summary>Completed structures on this tile (e.g., "shelter", "granary").</summary>
    public List<string> Structures { get; }

    /// <summary>Multi-tick construction in progress. Key = structure ID, Value = ticks invested.</summary>
    public Dictionary<string, int> BuildProgress { get; }

    // ── Terrain Modifiers ──────────────────────────────────────────────
    /// <summary>Movement cost multiplier. Initialized from BiomeDefaults.</summary>
    public float MovementCostMultiplier { get; set; }

    /// <summary>Gathering yield multiplier. Initialized from BiomeDefaults.</summary>
    public float GatheringEfficiencyMultiplier { get; set; }

    // ── Resource Distribution (GDD v1.8 Sections 5/6) ──────────────
    /// <summary>Section 6: Whether this tile is a notable point feature (cave, ore vein, spring, quarry).
    /// Point features drive permanent settlement geographic knowledge.</summary>
    public bool IsPointFeature { get; set; }

    /// <summary>Section 6: Type of point feature. e.g., "cave", "ore_vein", "natural_spring", "rich_quarry".
    /// Null if not a point feature.</summary>
    public string? PointFeatureType { get; set; }

    /// <summary>Section 6: Whether this tile is part of a secondary resource patch (berry patch, animal herd, etc.).
    /// Patches are clusters of 3-7 tiles placed during world generation.</summary>
    public bool IsResourcePatch { get; set; }

    /// <summary>Section 6: Identifier for the patch this tile belongs to. Tiles in the same patch share an ID.
    /// Null if not part of a patch. Format: "{ResourceType}_{sequenceNumber}" e.g., "Berries_3".</summary>
    public string? PatchId { get; set; }

    // ── Ecology & Farming ───────────────────────────────────────────
    /// <summary>Tick when this farm tile was last tended. -999 = never.</summary>
    public int LastTendedTick { get; set; } = -999;

    /// <summary>Tick when this tile was last gathered from. Used for overgrazing recovery (computed on demand).</summary>
    public int LastGatheredTick { get; set; }

    /// <summary>Per-tile random offset for resource regeneration. Prevents synchronized regen spikes.</summary>
    public int RegenOffset { get; set; }

    // ── Constructor ────────────────────────────────────────────────────
    public Tile(int x, int y, BiomeType biome)
    {
        X = x;
        Y = y;
        Biome = biome;

        Resources = new Dictionary<ResourceType, int>();
        CapacityOverrides = new Dictionary<ResourceType, int>();
        RegenCap = new Dictionary<ResourceType, int>();
        Structures = new List<string>();
        BuildProgress = new Dictionary<string, int>();

        MovementCostMultiplier = BiomeDefaults.MovementCost[biome];
        GatheringEfficiencyMultiplier = BiomeDefaults.GatheringEfficiency[biome];
    }

    /// <summary>Returns the capacity for a given resource type on this tile.</summary>
    public int GetCapacity(ResourceType resource)
    {
        if (CapacityOverrides.TryGetValue(resource, out int cap))
            return cap;
        return BiomeDefaults.ResourceCapacity[Biome];
    }

    /// <summary>Returns the total amount of food resources (Berries + Grain + Animals + Fish) on this tile.</summary>
    public int TotalFood()
    {
        int total = 0;
        if (Resources.TryGetValue(ResourceType.Berries, out int b)) total += b;
        if (Resources.TryGetValue(ResourceType.Grain, out int g)) total += g;
        if (Resources.TryGetValue(ResourceType.Meat, out int mt)) total += mt;
        if (Resources.TryGetValue(ResourceType.Fish, out int f)) total += f;
        return total;
    }

    /// <summary>Returns whether this tile has a shelter structure.</summary>
    public bool HasShelter => Structures.Contains("lean_to") || Structures.Contains("shelter") || Structures.Contains("improved_shelter");

    /// <summary>Returns whether this tile has a farm.</summary>
    public bool HasFarm => Structures.Contains("farm");

    /// <summary>GDD v1.7: Returns whether this tile has a granary structure.</summary>
    public bool HasGranary => Structures.Contains("granary");

    /// <summary>Returns whether this tile can be farmed (Plains or cleared land only — never raw Forest/Water/Mountain).</summary>
    public bool IsFarmable => Biome == BiomeType.Plains || Structures.Contains("cleared");

    // ── Home Storage (GDD v1.8 Section 7) ───────────────────────────
    /// <summary>Section 7: Food stored in this tile's shelter. Only valid if HasShelter is true.
    /// Lean-to = 10 capacity, Improved shelter = 20 capacity. Decays over time (unlike granary).</summary>
    public Dictionary<ResourceType, int> HomeFoodStorage { get; } = new();

    /// <summary>Section 7: Tick when home storage last had a decay event applied.</summary>
    public int LastHomeDecayTick { get; set; }

    /// <summary>Section 7: Whether this tile has home storage (any shelter).</summary>
    public bool HasHomeStorage => HasShelter;

    /// <summary>Section 7: Maximum food units this tile's shelter can store.
    /// Improved shelter = 20, lean-to = 10, no shelter = 0.</summary>
    public int HomeStorageCapacity
    {
        get
        {
            if (Structures.Contains("improved_shelter"))
                return SimConfig.HomeStorageCapacityImproved;
            if (HasShelter)
                return SimConfig.HomeStorageCapacityLeanTo;
            return 0;
        }
    }

    /// <summary>Section 7: Total food items currently stored in home storage.</summary>
    public int HomeTotalFood
    {
        get
        {
            int total = 0;
            foreach (var kvp in HomeFoodStorage)
                total += kvp.Value;
            return total;
        }
    }

    /// <summary>Section 7: Decay interval for this tile's home storage (ticks between 1-food decay).
    /// Improved shelter decays slower than lean-to.</summary>
    public int HomeDecayInterval
    {
        get
        {
            if (Structures.Contains("improved_shelter"))
                return SimConfig.HomeStorageDecayIntervalImproved;
            return SimConfig.HomeStorageDecayIntervalLeanTo;
        }
    }

    /// <summary>Section 7: Deposits food into home storage. Returns amount actually deposited.</summary>
    public int DepositToHome(ResourceType type, int amount)
    {
        if (!HasHomeStorage) return 0;

        int spaceLeft = HomeStorageCapacity - HomeTotalFood;
        int toDeposit = Math.Min(amount, spaceLeft);
        if (toDeposit <= 0) return 0;

        if (!HomeFoodStorage.ContainsKey(type))
            HomeFoodStorage[type] = 0;
        HomeFoodStorage[type] += toDeposit;
        return toDeposit;
    }

    /// <summary>Section 7: Withdraws food from home storage. Returns amount actually withdrawn.</summary>
    public int WithdrawFromHome(ResourceType type, int amount)
    {
        if (!HasHomeStorage) return 0;

        if (!HomeFoodStorage.TryGetValue(type, out int available) || available <= 0)
            return 0;

        int toWithdraw = Math.Min(amount, available);
        HomeFoodStorage[type] -= toWithdraw;
        if (HomeFoodStorage[type] <= 0)
            HomeFoodStorage.Remove(type);
        return toWithdraw;
    }

    /// <summary>Section 7: Withdraws any available food from home storage (tries preserved first).
    /// Returns the type and amount withdrawn.</summary>
    public (ResourceType type, int amount) WithdrawAnyFoodFromHome(int amount = 1)
    {
        if (!HasHomeStorage) return (ResourceType.Berries, 0);

        ResourceType[] foodTypes = { ResourceType.PreservedFood, ResourceType.Berries, ResourceType.Grain, ResourceType.Meat, ResourceType.Fish };
        foreach (var food in foodTypes)
        {
            int withdrawn = WithdrawFromHome(food, amount);
            if (withdrawn > 0)
                return (food, withdrawn);
        }
        return (ResourceType.Berries, 0);
    }

    /// <summary>Section 7: Decays one food item from home storage (called by Simulation on cadence).
    /// Removes one unit of the least valuable food type first.</summary>
    public bool DecayOneHomeFood()
    {
        if (HomeTotalFood <= 0) return false;

        // Decay non-preserved food first (berries spoil before preserved food)
        ResourceType[] decayOrder = { ResourceType.Berries, ResourceType.Fish, ResourceType.Meat, ResourceType.Grain, ResourceType.PreservedFood };
        foreach (var food in decayOrder)
        {
            if (HomeFoodStorage.TryGetValue(food, out int amt) && amt > 0)
            {
                HomeFoodStorage[food]--;
                if (HomeFoodStorage[food] <= 0)
                    HomeFoodStorage.Remove(food);
                return true;
            }
        }
        return false;
    }

    // ── Material Storage (Directive: Surplus Drives) ──────────────────
    /// <summary>Materials stored at this home tile (wood, stone). No structure needed —
    /// just a pile on the ground next to the shelter. Flat 30-unit capacity, no decay.</summary>
    public Dictionary<ResourceType, int> HomeMaterialStorage { get; } = new();

    /// <summary>Total materials stored (wood + stone combined).</summary>
    public int HomeTotalMaterials
    {
        get
        {
            int total = 0;
            foreach (var kvp in HomeMaterialStorage)
                total += kvp.Value;
            return total;
        }
    }

    /// <summary>Maximum material storage capacity (flat 30, no structure needed).</summary>
    public const int MaterialStorageCapacity = 30;

    /// <summary>Deposits materials into home material storage. Returns amount actually deposited.</summary>
    public int DepositMaterialToHome(ResourceType type, int amount)
    {
        int spaceLeft = MaterialStorageCapacity - HomeTotalMaterials;
        int toDeposit = Math.Min(amount, spaceLeft);
        if (toDeposit <= 0) return 0;

        if (!HomeMaterialStorage.ContainsKey(type))
            HomeMaterialStorage[type] = 0;
        HomeMaterialStorage[type] += toDeposit;
        return toDeposit;
    }

    /// <summary>Withdraws materials from home material storage. Returns amount withdrawn.</summary>
    public int WithdrawMaterialFromHome(ResourceType type, int amount)
    {
        if (!HomeMaterialStorage.TryGetValue(type, out int available) || available <= 0)
            return 0;

        int toWithdraw = Math.Min(amount, available);
        HomeMaterialStorage[type] -= toWithdraw;
        if (HomeMaterialStorage[type] <= 0)
            HomeMaterialStorage.Remove(type);
        return toWithdraw;
    }

    // ── Granary Storage (GDD v1.7) ───────────────────────────────────
    /// <summary>GDD v1.7: Food stored in this tile's granary. Only valid if HasGranary is true.</summary>
    public Dictionary<ResourceType, int> GranaryFoodStorage { get; } = new();

    /// <summary>GDD v1.7: Total food items currently stored in the granary.</summary>
    public int GranaryTotalFood
    {
        get
        {
            int total = 0;
            foreach (var kvp in GranaryFoodStorage)
                total += kvp.Value;
            return total;
        }
    }

    /// <summary>
    /// GDD v1.7: Deposits food into the granary. Returns the amount actually deposited (may be less if full).
    /// </summary>
    public int DepositToGranary(ResourceType type, int amount)
    {
        if (!HasGranary) return 0;

        int spaceLeft = SimConfig.GranaryCapacity - GranaryTotalFood;
        int toDeposit = Math.Min(amount, spaceLeft);
        if (toDeposit <= 0) return 0;

        if (!GranaryFoodStorage.ContainsKey(type))
            GranaryFoodStorage[type] = 0;
        GranaryFoodStorage[type] += toDeposit;
        return toDeposit;
    }

    /// <summary>
    /// GDD v1.7: Withdraws food from the granary. Returns the amount actually withdrawn.
    /// </summary>
    public int WithdrawFromGranary(ResourceType type, int amount)
    {
        if (!HasGranary) return 0;

        if (!GranaryFoodStorage.TryGetValue(type, out int available) || available <= 0)
            return 0;

        int toWithdraw = Math.Min(amount, available);
        GranaryFoodStorage[type] -= toWithdraw;
        if (GranaryFoodStorage[type] <= 0)
            GranaryFoodStorage.Remove(type);
        return toWithdraw;
    }

    /// <summary>
    /// GDD v1.7: Withdraws any available food from the granary (tries each food type). Returns the type and amount withdrawn.
    /// </summary>
    public (ResourceType type, int amount) WithdrawAnyFood(int amount = 1)
    {
        if (!HasGranary) return (ResourceType.Berries, 0);

        ResourceType[] foodTypes = { ResourceType.PreservedFood, ResourceType.Berries, ResourceType.Grain, ResourceType.Meat, ResourceType.Fish };
        foreach (var food in foodTypes)
        {
            int withdrawn = WithdrawFromGranary(food, amount);
            if (withdrawn > 0)
                return (food, withdrawn);
        }
        return (ResourceType.Berries, 0);
    }
}
