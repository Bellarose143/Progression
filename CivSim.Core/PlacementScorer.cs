namespace CivSim.Core;

/// <summary>
/// US-013: Evaluates candidate tiles for structure placement within a settlement.
/// Returns the best tile based on structure-specific scoring rules.
/// </summary>
public static class PlacementScorer
{
    /// <summary>
    /// Finds the best tile for placing a structure within the settlement's build range.
    /// Returns null if no valid tile is found.
    /// </summary>
    public static (int X, int Y)? FindBestTile(
        string structureType,
        Settlement settlement,
        World world,
        int agentX,
        int agentY)
    {
        var center = settlement.CenterTile;
        int range = SimConfig.StructureBuildRange; // 20 tiles from settlement center

        (int X, int Y)? bestTile = null;
        float bestScore = float.MinValue;
        int bestDist = int.MaxValue; // Tiebreaker: closest to agent

        for (int dx = -range; dx <= range; dx++)
        {
            for (int dy = -range; dy <= range; dy++)
            {
                int tx = center.X + dx;
                int ty = center.Y + dy;

                if (!world.IsInBounds(tx, ty)) continue;

                var tile = world.GetTile(tx, ty);

                // Must be passable (not water)
                if (tile.Biome == BiomeType.Water) continue;

                // Cannot place on tile that already has a structure of same type
                if (tile.Structures.Contains(structureType)) continue;

                float score = structureType switch
                {
                    "lean_to" or "shelter" or "reinforced_shelter" or "improved_shelter"
                        => ScoreShelter(tile, settlement),
                    "campfire" or "hearth"
                        => ScoreCampfire(tile, settlement),
                    _ => 0f,
                };

                int distToAgent = Math.Abs(tx - agentX) + Math.Abs(ty - agentY);

                if (score > bestScore || (score == bestScore && distToAgent < bestDist))
                {
                    bestScore = score;
                    bestTile = (tx, ty);
                    bestDist = distToAgent;
                }
            }
        }

        return bestTile;
    }

    /// <summary>
    /// Shelter scoring: +3.0 proximity to ResidentialCenter, +1.0 adjacent to existing shelter,
    /// -2.0 on farm tiles, -2.0 on pen tiles, +0.5 Plains, -0.5 Mountain.
    /// </summary>
    private static float ScoreShelter(Tile tile, Settlement settlement)
    {
        float score = 0f;
        var resCenter = settlement.Zones.ResidentialCenter;

        // +3.0 per tile closer to ResidentialCenter (linear falloff over build range)
        int distToRes = Math.Abs(tile.X - resCenter.X) + Math.Abs(tile.Y - resCenter.Y);
        score += 3.0f * Math.Max(0f, 1f - distToRes / (float)SimConfig.StructureBuildRange);

        // +1.0 adjacent to existing shelter
        if (HasAdjacentStructureOfType(tile, settlement, IsShelterType))
            score += 1.0f;

        // -2.0 on farm tiles
        if (tile.HasFarm)
            score -= 2.0f;

        // -2.0 on pen tiles
        if (tile.Structures.Contains("animal_pen"))
            score -= 2.0f;

        // Biome preference
        if (tile.Biome == BiomeType.Plains)
            score += 0.5f;
        else if (tile.Biome == BiomeType.Mountain)
            score -= 0.5f;

        return score;
    }

    /// <summary>
    /// Campfire scoring: +3.0 within 2 tiles of ResidentialCenter, -2.0 on farm tiles,
    /// +1.0 adjacent to shelter.
    /// </summary>
    private static float ScoreCampfire(Tile tile, Settlement settlement)
    {
        float score = 0f;
        var resCenter = settlement.Zones.ResidentialCenter;

        // +3.0 within 2 tiles of ResidentialCenter
        int distToRes = Math.Abs(tile.X - resCenter.X) + Math.Abs(tile.Y - resCenter.Y);
        if (distToRes <= 2)
            score += 3.0f;

        // -2.0 on farm tiles
        if (tile.HasFarm)
            score -= 2.0f;

        // +1.0 adjacent to shelter
        if (HasAdjacentStructureOfType(tile, settlement, IsShelterType))
            score += 1.0f;

        return score;
    }

    private static bool IsShelterType(string type) =>
        type == "lean_to" || type == "shelter" || type == "reinforced_shelter" || type == "improved_shelter";

    /// <summary>
    /// Checks if any settlement structure of the given type predicate is adjacent (within 1 tile) to the target tile.
    /// </summary>
    private static bool HasAdjacentStructureOfType(Tile tile, Settlement settlement, Func<string, bool> typePredicate)
    {
        foreach (var (sx, sy, sType) in settlement.Structures)
        {
            if (!typePredicate(sType)) continue;
            int dx = Math.Abs(tile.X - sx);
            int dy = Math.Abs(tile.Y - sy);
            if (dx <= 1 && dy <= 1 && !(dx == 0 && dy == 0))
                return true;
        }
        return false;
    }
}
