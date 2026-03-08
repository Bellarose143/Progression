using CivSim.Core;
using Xunit;

namespace CivSim.Tests;

/// <summary>
/// Explore Return-Path Validation: Agents in Explore mode should check their
/// return path every 5 tiles of Chebyshev distance from home. If no path exists,
/// they should immediately abort explore and head back.
/// </summary>
public class ExploreReturnPathTests
{
    [Fact]
    public void Agents_Do_Not_Get_Trapped_Far_From_Home()
    {
        // Run a full simulation with seed 16001 for 75000 ticks.
        // After the run, every alive agent should either:
        //   (a) be within 30 Chebyshev tiles of home, OR
        //   (b) have GoalType.ReturnHome (actively heading back)
        var world = new World(64, 64, 16001);
        var sim = new Simulation(world, 16001);

        for (int i = 0; i < 75000; i++)
            sim.Tick();

        foreach (var agent in sim.Agents.Where(a => a.IsAlive && a.HomeTile.HasValue))
        {
            var (hx, hy) = agent.HomeTile!.Value;
            int dist = Math.Max(Math.Abs(agent.X - hx), Math.Abs(agent.Y - hy));

            bool withinRange = dist <= 30;
            bool headingHome = agent.CurrentGoal == GoalType.ReturnHome;

            Assert.True(withinRange || headingHome,
                $"Agent '{agent.Name}' at ({agent.X},{agent.Y}) is {dist} tiles from home ({hx},{hy}) " +
                $"and is NOT heading home. Mode={agent.CurrentMode}, Goal={agent.CurrentGoal}");
        }
    }

    [Fact]
    public void No_Agent_Named_Joshua_Stuck_At_Water_Edge()
    {
        // Run a full simulation with seed 16001 for 75000 ticks.
        // Assert no agent named "Joshua" is stuck at a water edge tile
        // (a non-water tile adjacent to at least one water tile).
        var world = new World(64, 64, 16001);
        var sim = new Simulation(world, 16001);

        for (int i = 0; i < 75000; i++)
            sim.Tick();

        foreach (var agent in sim.Agents.Where(a => a.IsAlive && a.Name == "Joshua"))
        {
            bool atWaterEdge = IsWaterEdgeTile(agent.X, agent.Y, world);
            bool isStuck = agent.CurrentMode == BehaviorMode.Explore
                        && agent.ExploreStuckTicks > 10;

            Assert.False(atWaterEdge && isStuck,
                $"Joshua at ({agent.X},{agent.Y}) is stuck at a water edge tile. " +
                $"Mode={agent.CurrentMode}, StuckTicks={agent.ExploreStuckTicks}, Goal={agent.CurrentGoal}");
        }
    }

    /// <summary>
    /// Returns true if the tile at (x, y) is NOT water but has at least one adjacent water tile.
    /// </summary>
    private static bool IsWaterEdgeTile(int x, int y, World world)
    {
        var tile = world.GetTile(x, y);
        if (tile.Biome == BiomeType.Water)
            return false; // On water itself, not an "edge" tile

        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = x + dx;
                int ny = y + dy;
                if (!world.IsInBounds(nx, ny)) continue;
                if (world.GetTile(nx, ny).Biome == BiomeType.Water)
                    return true;
            }
        }
        return false;
    }
}
