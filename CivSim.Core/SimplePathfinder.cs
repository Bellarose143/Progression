namespace CivSim.Core;

/// <summary>
/// Directive #7 Fix 3: Simple A* pathfinder for ReturnHome goals.
/// Computes a waypoint path from source to destination, respecting terrain passability.
/// Used by the distance safety override to ensure agents can navigate around obstacles.
/// </summary>
public static class SimplePathfinder
{
    /// <summary>Maximum nodes to explore before giving up (prevents excessive computation).
    /// Increased from 2000 for 350×350 world scale — agents may need to path across larger distances.</summary>
    private const int MaxNodes = 40000;

    /// <summary>Higher budget for expensive pathfinding operations (e.g., exploration, long-range goals).</summary>
    public const int ExpensiveMaxNodes = 80000;

    /// <summary>
    /// Finds a path from (sx, sy) to (tx, ty) using A* with Chebyshev distance heuristic.
    /// Returns a list of waypoints (excluding start, including target), or null if no path found.
    /// D20 Fix 1: Optional avoidTiles parameter — tiles treated as impassable (agent blacklist).
    /// </summary>
    public static List<(int X, int Y)>? FindPath(int sx, int sy, int tx, int ty, World world,
        int maxNodes = MaxNodes, HashSet<(int X, int Y)>? avoidTiles = null)
    {
        if (sx == tx && sy == ty)
            return new List<(int X, int Y)>();

        var openSet = new PriorityQueue<(int X, int Y), float>();
        var cameFrom = new Dictionary<(int X, int Y), (int X, int Y)>();
        var gScore = new Dictionary<(int X, int Y), float>();

        var start = (sx, sy);
        var goal = (tx, ty);

        gScore[start] = 0;
        openSet.Enqueue(start, Heuristic(sx, sy, tx, ty));

        int explored = 0;

        while (openSet.Count > 0 && explored < maxNodes)
        {
            var current = openSet.Dequeue();
            explored++;

            if (current == goal)
                return ReconstructPath(cameFrom, current);

            // Explore 8 neighbors (Chebyshev adjacency)
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    if (dx == 0 && dy == 0) continue;

                    int nx = current.X + dx;
                    int ny = current.Y + dy;

                    if (!world.IsInBounds(nx, ny))
                        continue;

                    // Directive #10 Fix 5: 2-tile edge buffer — avoid world edges
                    if (nx < 2 || ny < 2 || nx >= world.Width - 2 || ny >= world.Height - 2)
                        continue;

                    var tile = world.GetTile(nx, ny);
                    if (float.IsPositiveInfinity(tile.MovementCostMultiplier))
                        continue;

                    // D20 Fix 1: Skip agent-blacklisted tiles
                    if (avoidTiles != null && avoidTiles.Contains((nx, ny)))
                        continue;

                    float moveCost = tile.MovementCostMultiplier;
                    // Diagonal moves cost ~1.41x more
                    if (dx != 0 && dy != 0)
                        moveCost *= 1.41f;

                    float tentativeG = gScore[current] + moveCost;
                    var neighbor = (nx, ny);

                    if (!gScore.TryGetValue(neighbor, out float existingG) || tentativeG < existingG)
                    {
                        cameFrom[neighbor] = current;
                        gScore[neighbor] = tentativeG;
                        float fScore = tentativeG + Heuristic(nx, ny, tx, ty);
                        openSet.Enqueue(neighbor, fScore);
                    }
                }
            }
        }

        // No path found within budget
        return null;
    }

    private static float Heuristic(int x1, int y1, int x2, int y2)
    {
        // Chebyshev distance (matches 8-directional movement)
        return Math.Max(Math.Abs(x2 - x1), Math.Abs(y2 - y1));
    }

    private static List<(int X, int Y)> ReconstructPath(
        Dictionary<(int X, int Y), (int X, int Y)> cameFrom, (int X, int Y) current)
    {
        var path = new List<(int X, int Y)> { current };
        while (cameFrom.ContainsKey(current))
        {
            current = cameFrom[current];
            path.Add(current);
        }
        path.Reverse();
        // Remove the start position (agent is already there)
        if (path.Count > 0)
            path.RemoveAt(0);
        return path;
    }
}
