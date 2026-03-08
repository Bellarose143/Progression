namespace CivSim.Core;

public class Carcass
{
    private static int _nextId = 1;
    public static void ResetIdCounter() => _nextId = 1;

    public int Id { get; }
    public int X { get; }
    public int Y { get; }
    public AnimalSpecies Species { get; }
    public int MeatYield { get; set; }
    public int HideYield { get; set; }
    public int BoneYield { get; set; }
    public int DecayTicksRemaining { get; set; }
    public bool IsActive => DecayTicksRemaining > 0;

    public Carcass(int x, int y, AnimalSpecies species, int meatYield, int hideYield = 0, int boneYield = 0)
    {
        Id = _nextId++;
        X = x;
        Y = y;
        Species = species;
        MeatYield = meatYield;
        HideYield = hideYield;
        BoneYield = boneYield;
        // D25b: Carcass lasts 2 × CarcassDecayTicks total. Meat halves at CarcassDecayTicks.
        DecayTicksRemaining = 2 * SimConfig.CarcassDecayTicks;
    }
}
