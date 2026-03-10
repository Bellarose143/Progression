namespace CivSim.Core;

/// <summary>
/// US-012: Tracks weighted average centers for residential, agricultural, animal, and storage zones.
/// Recalculated when settlement structures change.
/// </summary>
public class SettlementZones
{
    /// <summary>Weighted average center of residential structures (shelter, campfire).</summary>
    public (int X, int Y) ResidentialCenter { get; private set; }

    /// <summary>Weighted average center of agricultural structures (farm).</summary>
    public (int X, int Y) AgriculturalCenter { get; private set; }

    /// <summary>Weighted average center of animal structures (animal_pen).</summary>
    public (int X, int Y) AnimalCenter { get; private set; }

    /// <summary>Weighted average center of storage structures (granary).</summary>
    public (int X, int Y) StorageCenter { get; private set; }

    private (int X, int Y) _defaultCenter;

    public SettlementZones((int X, int Y) defaultCenter)
    {
        _defaultCenter = defaultCenter;
        ResidentialCenter = defaultCenter;
        AgriculturalCenter = defaultCenter;
        AnimalCenter = defaultCenter;
        StorageCenter = defaultCenter;
    }

    /// <summary>
    /// Recalculates all zone centers from the settlement's structure list.
    /// Structures with no matching zone are ignored. Zones with no structures default to settlement center.
    /// </summary>
    public void Recalculate(List<(int X, int Y, string Type)> structures, (int X, int Y) settlementCenter)
    {
        _defaultCenter = settlementCenter;

        int resX = 0, resY = 0, resCount = 0;
        int agX = 0, agY = 0, agCount = 0;
        int anX = 0, anY = 0, anCount = 0;
        int stX = 0, stY = 0, stCount = 0;

        foreach (var (x, y, type) in structures)
        {
            switch (GetZone(type))
            {
                case Zone.Residential:
                    resX += x; resY += y; resCount++;
                    break;
                case Zone.Agricultural:
                    agX += x; agY += y; agCount++;
                    break;
                case Zone.Animal:
                    anX += x; anY += y; anCount++;
                    break;
                case Zone.Storage:
                    stX += x; stY += y; stCount++;
                    break;
            }
        }

        ResidentialCenter = resCount > 0 ? (resX / resCount, resY / resCount) : _defaultCenter;
        AgriculturalCenter = agCount > 0 ? (agX / agCount, agY / agCount) : _defaultCenter;
        AnimalCenter = anCount > 0 ? (anX / anCount, anY / anCount) : _defaultCenter;
        StorageCenter = stCount > 0 ? (stX / stCount, stY / stCount) : _defaultCenter;
    }

    private enum Zone { None, Residential, Agricultural, Animal, Storage }

    private static Zone GetZone(string structureType) => structureType switch
    {
        "lean_to" => Zone.Residential,
        "shelter" => Zone.Residential,
        "reinforced_shelter" => Zone.Residential,
        "improved_shelter" => Zone.Residential,
        "campfire" => Zone.Residential,
        "hearth" => Zone.Residential,
        "farm" => Zone.Agricultural,
        "animal_pen" => Zone.Animal,
        "granary" => Zone.Storage,
        _ => Zone.None,
    };
}
