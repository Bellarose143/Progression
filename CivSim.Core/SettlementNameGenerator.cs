namespace CivSim.Core;

/// <summary>
/// GDD v1.7.1: Generates settlement names from prefix + suffix combinations.
/// Names are deterministic per seed for reproducibility.
/// </summary>
public static class SettlementNameGenerator
{
    private static readonly string[] Prefixes =
    {
        "Ash", "Stone", "River", "Oak", "Elder", "Dawn",
        "Moss", "Iron", "Willow", "Ember", "Frost", "Cedar",
        "Thorn", "Clay", "Birch", "Storm", "Haven", "Flint"
    };

    private static readonly string[] Suffixes =
    {
        "haven", "ford", "gate", "hold", "rest", "stead",
        "vale", "moor", "wick", "dell", "mere", "glen",
        "field", "crest", "hollow", "brook", "ridge", "wood"
    };

    public static string Generate(Random random)
    {
        var prefix = Prefixes[random.Next(Prefixes.Length)];
        var suffix = Suffixes[random.Next(Suffixes.Length)];
        return prefix + suffix;
    }
}
