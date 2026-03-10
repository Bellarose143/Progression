namespace CivSim.Core;

/// <summary>
/// D25d: An animal pen structure that houses domesticated animals.
/// Built with animal_domestication knowledge. Houses up to Capacity animals.
/// Penned animals consume grain from the pen's FoodStore.
/// </summary>
public class Pen
{
    private static int _nextId = 1;
    public static void ResetIdCounter() => _nextId = 1;

    public int Id { get; }
    public int TileX { get; }
    public int TileY { get; }

    /// <summary>Max animals: 5 small (Rabbit, Boar/Pig, Sheep) or 3 large (Deer, Cow).</summary>
    public int Capacity { get; set; }

    /// <summary>Animal IDs currently housed in this pen.</summary>
    public List<int> AnimalIds { get; } = new();

    /// <summary>Grain stored for feeding penned animals.</summary>
    public int FoodStore { get; set; }

    /// <summary>Maximum grain the pen can hold.</summary>
    public int MaxFoodStore { get; set; } = SimConfig.PenMaxFoodStore;

    /// <summary>Tick when food was last consumed by penned animals.</summary>
    public int LastFeedTick { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>Agent ID of who built this pen (for ownership tracking).</summary>
    public int BuilderAgentId { get; set; }

    public int AnimalCount => AnimalIds.Count;
    public bool IsFull => AnimalIds.Count >= Capacity;

    public Pen(int tileX, int tileY, int capacity, int builderAgentId)
    {
        Id = _nextId++;
        TileX = tileX;
        TileY = tileY;
        Capacity = capacity;
        BuilderAgentId = builderAgentId;
    }

    /// <summary>Returns true if the pen is a small-animal pen (Rabbit, Boar/Pig, Sheep).</summary>
    public static bool IsSmallAnimal(AnimalSpecies species) =>
        species == AnimalSpecies.Rabbit || species == AnimalSpecies.Boar || species == AnimalSpecies.Sheep;

    /// <summary>Returns the capacity for a pen based on whether it houses small or large animals.</summary>
    public static int CapacityFor(AnimalSpecies species) =>
        IsSmallAnimal(species) ? SimConfig.PenCapacitySmall : SimConfig.PenCapacityLarge;

    /// <summary>Count of animals of a given species in this pen.</summary>
    public int CountSpecies(AnimalSpecies species, IReadOnlyList<Animal> allAnimals)
    {
        int count = 0;
        foreach (var id in AnimalIds)
        {
            var animal = allAnimals.FirstOrDefault(a => a.Id == id && a.IsAlive);
            if (animal != null && animal.Species == species) count++;
        }
        return count;
    }
}
