namespace CivSim.Core;

/// <summary>
/// Shelter quality tiers, from lowest to highest.
/// Maps to structure strings: "lean_to" (Tier 1), "reinforced_shelter" (Tier 2), "improved_shelter" (Tier 3).
/// </summary>
public enum ShelterTier
{
    None = 0,
    LeanTo = 1,
    ReinforcedShelter = 2,
    ImprovedShelter = 3
}

/// <summary>
/// First-class settlement entity. Tracks members, structures, territory, shelter quality,
/// communal knowledge, and animal pens. Founded when the first shelter is built (US-007).
/// Also used by SettlementDetector for observation-based detection (legacy).
/// </summary>
public class Settlement
{
    private static int _nextId = 1;

    /// <summary>Reset the static ID counter. Used by integration tests to ensure deterministic behavior.</summary>
    public static void ResetIdCounter() => _nextId = 1;

    public int Id { get; }
    public string Name { get; set; } = string.Empty;
    public int FoundedTick { get; set; }

    /// <summary>Center position of the settlement.</summary>
    public (int X, int Y) CenterTile { get; set; }

    /// <summary>Backward-compatible accessor for CenterTile.X.</summary>
    public int CenterX
    {
        get => CenterTile.X;
        set => CenterTile = (value, CenterTile.Y);
    }

    /// <summary>Backward-compatible accessor for CenterTile.Y.</summary>
    public int CenterY
    {
        get => CenterTile.Y;
        set => CenterTile = (CenterTile.X, value);
    }

    /// <summary>Agent IDs of settlement members.</summary>
    public List<int> Members { get; set; } = new();

    /// <summary>Backward-compatible alias for Members.</summary>
    public List<int> ResidentAgentIds
    {
        get => Members;
        set => Members = value;
    }

    /// <summary>Structures belonging to this settlement: (X, Y, Type).</summary>
    public List<(int X, int Y, string Type)> Structures { get; } = new();

    /// <summary>Number of shelter structures (backward compat for SettlementDetector).</summary>
    public int ShelterCount { get; set; }

    /// <summary>Highest shelter quality among all settlement structures.</summary>
    public ShelterTier ShelterQuality { get; set; } = ShelterTier.None;

    /// <summary>Claimed territory tiles. Recalculated when structures change (US-009).</summary>
    public HashSet<(int X, int Y)> Territory { get; } = new();

    /// <summary>Communal knowledge shared by all settlement members.</summary>
    public HashSet<string> SharedKnowledge { get; } = new();

    /// <summary>Animal pens belonging to this settlement.</summary>
    public List<Pen> Pens { get; } = new();

    /// <summary>Default constructor. Auto-assigns Id from static counter.</summary>
    public Settlement()
    {
        Id = _nextId++;
    }

    /// <summary>
    /// Internal constructor for special-case settlements with explicit Id
    /// (e.g., founding group with Id = -1).
    /// Does not increment the static counter.
    /// </summary>
    internal Settlement(int explicitId)
    {
        Id = explicitId;
    }

    /// <summary>
    /// Recalculates ShelterQuality from the Structures list, taking the highest tier found.
    /// Called when structures are added or removed.
    /// </summary>
    public void RecalculateShelterQuality()
    {
        var best = ShelterTier.None;
        foreach (var (_, _, type) in Structures)
        {
            var tier = StructureToShelterTier(type);
            if (tier > best) best = tier;
        }
        ShelterQuality = best;
    }

    /// <summary>Maps a structure type string to its ShelterTier. Returns None for non-shelter structures.</summary>
    public static ShelterTier StructureToShelterTier(string structureType) => structureType switch
    {
        "lean_to" => ShelterTier.LeanTo,
        "shelter" => ShelterTier.LeanTo, // "shelter" is treated as equivalent to lean_to
        "reinforced_shelter" => ShelterTier.ReinforcedShelter,
        "improved_shelter" => ShelterTier.ImprovedShelter,
        _ => ShelterTier.None,
    };

    /// <summary>Returns the SimConfig float value for the current ShelterQuality.</summary>
    public float GetShelterQualityFloat() => ShelterQuality switch
    {
        ShelterTier.ImprovedShelter => SimConfig.ShelterQualityImproved,
        ShelterTier.ReinforcedShelter => SimConfig.ShelterQualityImproved, // reinforced uses same quality as improved
        >= ShelterTier.LeanTo => SimConfig.ShelterQualityLeanTo,
        _ => 0f,
    };

    public override string ToString()
    {
        return $"{Name} at ({CenterTile.X},{CenterTile.Y}) - {ShelterCount} shelters, {Members.Count} residents";
    }
}
