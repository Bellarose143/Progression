using System.Text.Json;

namespace CivSim.Core;

/// <summary>
/// GDD v1.8 Section 4: Curated name generator using real human names from JSON data files.
/// Names are gender-aware and drawn without replacement from the living population.
/// Duplicates receive generation numerals (Mary II, James III).
/// </summary>
public static class NameGenerator
{
    private static List<string> _maleNames = new();
    private static List<string> _femaleNames = new();
    private static bool _loaded;

    /// <summary>
    /// Roman numeral suffixes for generation numerals.
    /// Index 0 = "II" (first duplicate), 1 = "III", etc.
    /// </summary>
    private static readonly string[] RomanNumerals =
    {
        "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X",
        "XI", "XII", "XIII", "XIV", "XV", "XVI", "XVII", "XVIII", "XIX", "XX"
    };

    /// <summary>
    /// Loads curated name lists from JSON files in the specified data folder.
    /// Expected format: { "names": ["Name1", "Name2", ...] }
    /// </summary>
    public static void LoadNames(string dataFolder)
    {
        string malePath = Path.Combine(dataFolder, "maleNames.json");
        string femalePath = Path.Combine(dataFolder, "femaleNames.json");

        if (File.Exists(malePath))
        {
            string json = File.ReadAllText(malePath);
            var doc = JsonDocument.Parse(json);
            _maleNames = doc.RootElement.GetProperty("names")
                .EnumerateArray()
                .Select(e => e.GetString()!)
                .ToList();
        }

        if (File.Exists(femalePath))
        {
            string json = File.ReadAllText(femalePath);
            var doc = JsonDocument.Parse(json);
            _femaleNames = doc.RootElement.GetProperty("names")
                .EnumerateArray()
                .Select(e => e.GetString()!)
                .ToList();
        }

        _loaded = _maleNames.Count > 0 && _femaleNames.Count > 0;
    }

    /// <summary>
    /// Generates a name for an agent.
    /// If names are loaded, draws from the gender-appropriate curated list.
    /// If a name is already used by a living agent, appends a generation numeral.
    /// Dead agents' names are available for reuse (no numeral needed).
    /// </summary>
    /// <param name="random">Random source for shuffling.</param>
    /// <param name="isMale">True for male names, false for female.</param>
    /// <param name="livingNames">Set of names currently used by living agents.</param>
    /// <returns>A unique name (possibly with numeral suffix).</returns>
    public static string Generate(Random random, bool isMale, IEnumerable<string>? livingNames = null)
    {
        if (!_loaded)
        {
            // Fallback: use the curated lists directly if they have entries,
            // otherwise return a simple generated name
            return GenerateFallback(random);
        }

        var namePool = isMale ? _maleNames : _femaleNames;
        var livingSet = livingNames != null ? new HashSet<string>(livingNames) : new HashSet<string>();

        // Try to find an unused base name first (shuffle a copy of the pool)
        var shuffled = namePool.OrderBy(_ => random.Next()).ToList();
        foreach (var name in shuffled)
        {
            if (!livingSet.Contains(name))
                return name;
        }

        // All base names are taken — pick a random name and add a generation numeral
        string baseName = namePool[random.Next(namePool.Count)];
        for (int i = 0; i < RomanNumerals.Length; i++)
        {
            string candidate = $"{baseName} {RomanNumerals[i]}";
            if (!livingSet.Contains(candidate))
                return candidate;
        }

        // Extreme fallback: very large population, all names + numerals exhausted
        return $"{baseName} {random.Next(100, 999)}";
    }

    /// <summary>
    /// Backward-compatible overload for cases where gender is not specified.
    /// Randomly selects male or female.
    /// </summary>
    public static string Generate(Random random)
    {
        bool isMale = random.Next(2) == 0;
        return Generate(random, isMale);
    }

    /// <summary>Fallback name generation when JSON files are not loaded.</summary>
    private static string GenerateFallback(Random random)
    {
        // Simple syllable approach as last resort
        string[] firsts = { "Al", "Be", "Ca", "Da", "El", "Fa", "Ga", "Ha", "Jo", "Ka", "La", "Ma", "Na", "Ol", "Pa", "Ra", "Sa", "Ta" };
        string[] seconds = { "ra", "na", "la", "va", "sa", "ma", "da", "lia", "ren", "ton", "vin", "ley" };
        return firsts[random.Next(firsts.Length)] + seconds[random.Next(seconds.Length)];
    }
}
