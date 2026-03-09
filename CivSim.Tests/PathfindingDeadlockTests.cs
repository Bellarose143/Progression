using CivSim.Core;
using Xunit;

namespace CivSim.Tests;

/// <summary>
/// D20 Fix 1: Tests for the safety-distance pathfinding deadlock fix.
/// Verifies that tile blacklists persist across safety-distance goal resets,
/// and that safety-distance backs off when MoveFailCount is high.
/// </summary>
[Trait("Category", "Integration")]
public class PathfindingDeadlockTests
{
    [Fact]
    public void Blacklist_PersistsAcross_SafetyDistanceGoalReset()
    {
        // Agent blacklists a tile, ClearGoal is called (as safety-distance does),
        // blacklisted tile should still be avoided in pathfinding.
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(42)
            .AddAgent("Alice", isMale: false, hunger: 80f)
            .AgentAt("Alice", 0, 0)
            .AgentHome("Alice", 0, 0)
            .ShelterAt(0, 0)
            .ResourceAt(1, 0, ResourceType.Berries, 50)
            .Build();

        var alice = sim.GetAgent("Alice");
        int currentTick = sim.Simulation.CurrentTick;

        // Blacklist a tile near home
        int blockedX = sim.SpawnX + 3;
        int blockedY = sim.SpawnY;
        alice.BlacklistTile(blockedX, blockedY, currentTick + 5000);

        // Verify tile is blacklisted
        Assert.True(alice.IsTileBlacklisted(blockedX, blockedY, currentTick));

        // ClearGoal — simulates what safety-distance does
        alice.CurrentGoal = GoalType.ReturnHome;
        alice.GoalTarget = alice.HomeTile;
        alice.GoalStartTick = currentTick;
        alice.ClearGoal();

        // After ClearGoal, blacklisted tile MUST still be blacklisted
        Assert.True(alice.IsTileBlacklisted(blockedX, blockedY, currentTick),
            "Blacklisted tile should persist after ClearGoal — blacklist is agent-level");

        // GetBlacklistedTileSet should contain it
        var avoidTiles = alice.GetBlacklistedTileSet(currentTick);
        Assert.Contains((blockedX, blockedY), avoidTiles);

        // PathFinder with avoidTiles should route around the blocked tile
        // Path from a point past the blocked tile back to home
        int startX = sim.SpawnX + 5;
        int startY = sim.SpawnY;
        var pathWithBlacklist = SimplePathfinder.FindPath(
            startX, startY, sim.SpawnX, sim.SpawnY, sim.World,
            avoidTiles: avoidTiles);

        // Path should exist (routes around) and NOT contain the blocked tile
        Assert.NotNull(pathWithBlacklist);
        Assert.DoesNotContain((blockedX, blockedY), pathWithBlacklist!);
    }

    [Fact]
    public void SafetyDistance_BacksOff_WhenMoveFailCountHigh()
    {
        // Agent placed beyond SafetyReturnDist from home with MoveFailCount >= 20.
        // Safety-distance should NOT re-issue ReturnHome every tick.
        var sim = new TestSimBuilder()
            .GridSize(128, 128).Seed(42)
            .AddAgent("Alice", isMale: false, hunger: 80f)
            .AgentAt("Alice", 45, 0)  // 45 tiles east of spawn (beyond SafetyReturnDist=40)
            .AgentHome("Alice", 0, 0) // Home at spawn
            .ShelterAt(0, 0)
            .ResourceAt(1, 0, ResourceType.Berries, 50)
            .Build();

        var alice = sim.GetAgent("Alice");

        // Verify agent is beyond SafetyReturnDist
        int dist = Math.Max(
            Math.Abs(alice.X - alice.HomeTile!.Value.X),
            Math.Abs(alice.Y - alice.HomeTile!.Value.Y));
        Assert.True(dist > SimConfig.SafetyReturnDist,
            $"Agent should be beyond SafetyReturnDist. Dist={dist}");

        // Set high MoveFailCount to trigger backoff
        alice.MoveFailCount = 25;

        // Record starting position
        int startX = alice.X;
        int startY = alice.Y;

        // Run 20 ticks — count how many times goal changes (each safety-distance fire = 1 reset)
        int goalResets = 0;
        GoalType? lastGoal = alice.CurrentGoal;
        for (int t = 0; t < 20; t++)
        {
            sim.Tick(1);
            if (!alice.IsAlive) break;
            if (alice.CurrentGoal != lastGoal)
            {
                goalResets++;
                lastGoal = alice.CurrentGoal;
            }
        }

        // Before fix: safety-distance fires EVERY tick → 20 goal resets
        // After fix: backs off, lets escalation work → very few resets
        Assert.True(goalResets <= 5,
            $"Safety-distance should back off when MoveFailCount >= 20. Got {goalResets} goal resets in 20 ticks");
    }

    [Fact]
    public void Agent_Eats_DuringSafetyDistanceStuck()
    {
        // Agent far from home with food should eat when hungry enough.
        // The P1 safety-eat check (Phase 1a) fires BEFORE safety-distance (Phase 1b),
        // so even a stuck agent beyond SafetyReturnDist will eat when hungry.
        var sim = new TestSimBuilder()
            .GridSize(128, 128).Seed(42)
            .AddAgent("Alice", isMale: false, hunger: 80f)  // Start well-fed
            .AgentAt("Alice", 45, 0)  // 45 tiles east (beyond SafetyReturnDist of 40)
            .AgentHome("Alice", 0, 0)
            .ShelterAt(0, 0)
            .AgentInventory("Alice", ResourceType.Berries, 10)
            .Build();

        var alice = sim.GetAgent("Alice");

        // Verify agent is beyond SafetyReturnDist
        int dist = Math.Max(
            Math.Abs(alice.X - alice.HomeTile!.Value.X),
            Math.Abs(alice.Y - alice.HomeTile!.Value.Y));
        Assert.True(dist > SimConfig.SafetyReturnDist,
            $"Agent should be beyond SafetyReturnDist. Dist={dist}");

        // Run until agent eats — they will walk home (safety-distance),
        // hunger depletes during travel, and they'll eat when hungry enough
        bool ate = false;
        for (int t = 0; t < 500; t++)
        {
            sim.Tick(1);
            if (!alice.IsAlive) break;

            if (alice.CurrentAction == ActionType.Eat)
            {
                ate = true;
                break;
            }
        }

        Assert.True(ate,
            "Agent with food in inventory should eat during safety-distance return when hungry");
    }
}
