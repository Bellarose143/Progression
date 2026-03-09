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

                // No structure can be placed on a tile already occupied by another structure
                // (except shelter adjacency/upgrade and campfire which coexist with shelters)
                if (structureType is "animal_pen" or "granary" or "farm" && HasAnyStructure(tile)) continue;

                // Farm tiles must be farmable (Plains or cleared land)
                if (structureType == "farm" && !tile.IsFarmable) continue;

                float score = structureType switch
                {
                    "lean_to" or "shelter" or "reinforced_shelter" or "improved_shelter"
                        => ScoreShelter(tile, settlement),
                    "campfire" or "hearth"
                        => ScoreCampfire(tile, settlement),
                    "farm"
                        => ScoreFarm(tile, settlement),
                    "animal_pen"
                        => ScorePen(tile, settlement),
                    "granary"
                        => ScoreGranary(tile, settlement),
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

    /// <summary>
    /// US-014: Finds the best tile for the FIRST farm in a settlement.
    /// Scans 8 directions from ResidentialCenter, counts Plains tiles within 15 tiles,
    /// selects direction with most suitable farmland, places first farm 8-10 tiles from center.
    /// Returns null if no valid tile is found.
    /// </summary>
    public static (int X, int Y)? FindFirstFarmTile(
        Settlement settlement,
        World world,
        int agentX,
        int agentY)
    {
        var resCenter = settlement.Zones.ResidentialCenter;
        int scanRange = 15;

        // 8 directions: N, NE, E, SE, S, SW, W, NW
        int[][] directions = new[]
        {
            new[] { 0, -1 }, new[] { 1, -1 }, new[] { 1, 0 }, new[] { 1, 1 },
            new[] { 0, 1 }, new[] { -1, 1 }, new[] { -1, 0 }, new[] { -1, -1 }
        };

        int bestDirIndex = -1;
        int bestPlainCount = 0;

        // Scan each direction: count Plains tiles in a cone
        for (int d = 0; d < 8; d++)
        {
            int ddx = directions[d][0];
            int ddy = directions[d][1];
            int plainsCount = 0;

            for (int dist = 1; dist <= scanRange; dist++)
            {
                // Scan a widening cone: center line + 1 tile on each side perpendicular
                int cx = resCenter.X + ddx * dist;
                int cy = resCenter.Y + ddy * dist;

                // Perpendicular direction for cone width
                int px = -ddy, py = ddx;

                for (int w = -1; w <= 1; w++)
                {
                    int tx = cx + px * w;
                    int ty = cy + py * w;

                    if (!world.IsInBounds(tx, ty)) continue;
                    var tile = world.GetTile(tx, ty);
                    if (tile.Biome == BiomeType.Plains || tile.Structures.Contains("cleared"))
                        plainsCount++;
                }
            }

            if (plainsCount > bestPlainCount)
            {
                bestPlainCount = plainsCount;
                bestDirIndex = d;
            }
        }

        if (bestDirIndex < 0 || bestPlainCount == 0) return null;

        // Place first farm 8-10 tiles from center in the best direction
        int bdx = directions[bestDirIndex][0];
        int bdy = directions[bestDirIndex][1];

        // Try distances 8, 9, 10 — pick the first farmable tile
        for (int dist = 8; dist <= 10; dist++)
        {
            int fx = resCenter.X + bdx * dist;
            int fy = resCenter.Y + bdy * dist;

            if (!world.IsInBounds(fx, fy)) continue;
            var tile = world.GetTile(fx, fy);
            if (tile.Biome == BiomeType.Water) continue;
            if (!tile.IsFarmable) continue;
            if (tile.HasFarm) continue;
            if (HasAnyStructure(tile)) continue;

            return (fx, fy);
        }

        // Fallback: search nearby tiles around the 8-10 range in that direction
        for (int dist = 7; dist <= 12; dist++)
        {
            for (int w = -2; w <= 2; w++)
            {
                int px = -bdy, py = bdx; // perpendicular
                int fx = resCenter.X + bdx * dist + px * w;
                int fy = resCenter.Y + bdy * dist + py * w;

                if (!world.IsInBounds(fx, fy)) continue;
                var tile = world.GetTile(fx, fy);
                if (tile.Biome == BiomeType.Water) continue;
                if (!tile.IsFarmable) continue;
                if (tile.HasFarm) continue;
                if (HasAnyStructure(tile)) continue;

                return (fx, fy);
            }
        }

        return null;
    }

    /// <summary>
    /// US-014: Subsequent farm scoring.
    /// +3.0 adjacent to existing farm, +2.0 within 5 tiles of AgriculturalCenter,
    /// -3.0 within 5 tiles of ResidentialCenter, +1.0 Plains, -1.0 Forest,
    /// -5.0 on existing structure tiles.
    /// </summary>
    private static float ScoreFarm(Tile tile, Settlement settlement)
    {
        float score = 0f;

        // +3.0 adjacent to existing farm
        if (HasAdjacentStructureOfType(tile, settlement, t => t == "farm"))
            score += 3.0f;

        // +2.0 within 5 tiles of AgriculturalCenter
        var agCenter = settlement.Zones.AgriculturalCenter;
        int distToAg = Math.Abs(tile.X - agCenter.X) + Math.Abs(tile.Y - agCenter.Y);
        if (distToAg <= 5)
            score += 2.0f;

        // -3.0 within 5 tiles of ResidentialCenter
        var resCenter = settlement.Zones.ResidentialCenter;
        int distToRes = Math.Abs(tile.X - resCenter.X) + Math.Abs(tile.Y - resCenter.Y);
        if (distToRes <= 5)
            score -= 3.0f;

        // +1.0 Plains
        if (tile.Biome == BiomeType.Plains)
            score += 1.0f;

        // -1.0 Forest
        if (tile.Biome == BiomeType.Forest)
            score -= 1.0f;

        // -5.0 on existing structure tiles (any structure that isn't a farm)
        if (HasAnyStructure(tile))
            score -= 5.0f;

        return score;
    }

    /// <summary>
    /// US-015: Pen scoring.
    /// +2.0 adjacent to existing pens, +1.5 within 5 tiles of AnimalCenter,
    /// +1.0 between AgriculturalCenter and ResidentialCenter, -3.0 on farm tiles,
    /// -2.0 on shelter tiles.
    /// </summary>
    private static float ScorePen(Tile tile, Settlement settlement)
    {
        float score = 0f;

        // +2.0 adjacent to existing pens
        if (HasAdjacentStructureOfType(tile, settlement, t => t == "animal_pen"))
            score += 2.0f;

        // +1.5 within 5 tiles of AnimalCenter
        var animalCenter = settlement.Zones.AnimalCenter;
        int distToAnimal = Math.Abs(tile.X - animalCenter.X) + Math.Abs(tile.Y - animalCenter.Y);
        if (distToAnimal <= 5)
            score += 1.5f;

        // +1.0 between AgriculturalCenter and ResidentialCenter (within 5 tiles of midpoint)
        var agCenter = settlement.Zones.AgriculturalCenter;
        var resCenter = settlement.Zones.ResidentialCenter;
        int midX = (agCenter.X + resCenter.X) / 2;
        int midY = (agCenter.Y + resCenter.Y) / 2;
        int distToMid = Math.Abs(tile.X - midX) + Math.Abs(tile.Y - midY);
        if (distToMid <= 5)
            score += 1.0f;

        // -3.0 on farm tiles
        if (tile.HasFarm)
            score -= 3.0f;

        // -2.0 on shelter tiles
        if (tile.HasShelter)
            score -= 2.0f;

        return score;
    }

    /// <summary>
    /// US-015: Granary scoring.
    /// +2.0 between AgriculturalCenter and ResidentialCenter,
    /// +1.5 within 5 tiles of ResidentialCenter, -2.0 on farm tiles.
    /// </summary>
    private static float ScoreGranary(Tile tile, Settlement settlement)
    {
        float score = 0f;

        // +2.0 between AgriculturalCenter and ResidentialCenter (within 5 tiles of midpoint)
        var agCenter = settlement.Zones.AgriculturalCenter;
        var resCenter = settlement.Zones.ResidentialCenter;
        int midX = (agCenter.X + resCenter.X) / 2;
        int midY = (agCenter.Y + resCenter.Y) / 2;
        int distToMid = Math.Abs(tile.X - midX) + Math.Abs(tile.Y - midY);
        if (distToMid <= 5)
            score += 2.0f;

        // +1.5 within 5 tiles of ResidentialCenter
        int distToRes = Math.Abs(tile.X - resCenter.X) + Math.Abs(tile.Y - resCenter.Y);
        if (distToRes <= 5)
            score += 1.5f;

        // -2.0 on farm tiles
        if (tile.HasFarm)
            score -= 2.0f;

        return score;
    }

    /// <summary>
    /// Checks if a tile has any non-farm structure (shelter, campfire, pen, granary, etc.)
    /// </summary>
    private static bool HasAnyStructure(Tile tile)
    {
        foreach (var s in tile.Structures)
        {
            if (s != "farm" && s != "cleared")
                return true;
        }
        return false;
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
