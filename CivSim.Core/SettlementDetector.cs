namespace CivSim.Core;

/// <summary>
/// GDD v1.7.1: Detects settlement clusters (3+ shelters within SettlementRadius).
/// Uses flood-fill from shelter tiles to identify clusters. Assigns residents based on HomeTile proximity.
/// Called periodically from Simulation.Tick() — observation only, no AI behavior change.
/// </summary>
public static class SettlementDetector
{
    /// <summary>
    /// Scans the world for shelter clusters and returns detected settlements.
    /// Algorithm: iterate all tiles, find shelters not yet assigned to a cluster,
    /// flood-fill within SettlementRadius to find cluster size.
    /// </summary>
    public static List<Settlement> Detect(World world, List<Agent> agents, int currentTick, Random random)
    {
        int radius = SimConfig.SettlementRadius;
        int threshold = SimConfig.SettlementShelterThreshold;
        var settlements = new List<Settlement>();
        var visited = new HashSet<(int, int)>();

        for (int x = 0; x < world.Width; x++)
        {
            for (int y = 0; y < world.Height; y++)
            {
                if (visited.Contains((x, y))) continue;

                var tile = world.GetTile(x, y);
                if (!tile.HasShelter) continue;

                // Found an unvisited shelter — flood-fill to find the cluster
                var cluster = new List<(int X, int Y)>();
                var queue = new Queue<(int X, int Y)>();
                queue.Enqueue((x, y));
                visited.Add((x, y));

                while (queue.Count > 0)
                {
                    var (cx, cy) = queue.Dequeue();
                    cluster.Add((cx, cy));

                    // Check neighbors within radius
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            int nx = cx + dx, ny = cy + dy;

                            if (!world.IsInBounds(nx, ny)) continue;
                            if (visited.Contains((nx, ny))) continue;

                            // Only include if within SettlementRadius of the seed shelter
                            int distFromSeed = Math.Max(Math.Abs(nx - x), Math.Abs(ny - y));
                            if (distFromSeed > radius) continue;

                            var neighborTile = world.GetTile(nx, ny);
                            if (neighborTile.HasShelter)
                            {
                                visited.Add((nx, ny));
                                queue.Enqueue((nx, ny));
                            }
                        }
                    }
                }

                // Only create settlement if cluster meets threshold
                if (cluster.Count >= threshold)
                {
                    // Calculate center (average position)
                    int sumX = 0, sumY = 0;
                    foreach (var (sx, sy) in cluster)
                    {
                        sumX += sx;
                        sumY += sy;
                    }
                    int centerX = sumX / cluster.Count;
                    int centerY = sumY / cluster.Count;

                    // Find resident agents (HomeTile within radius of center)
                    var residents = new List<int>();
                    foreach (var agent in agents)
                    {
                        if (!agent.IsAlive || !agent.HomeTile.HasValue) continue;
                        int dist = Math.Max(
                            Math.Abs(agent.HomeTile.Value.X - centerX),
                            Math.Abs(agent.HomeTile.Value.Y - centerY));
                        if (dist <= radius)
                            residents.Add(agent.Id);
                    }

                    settlements.Add(new Settlement
                    {
                        Name = SettlementNameGenerator.Generate(random),
                        CenterX = centerX,
                        CenterY = centerY,
                        ShelterCount = cluster.Count,
                        ResidentAgentIds = residents,
                        FoundedTick = currentTick
                    });
                }
            }
        }

        return settlements;
    }
}
