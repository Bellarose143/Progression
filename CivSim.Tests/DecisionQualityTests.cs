using CivSim.Core;
using Xunit;

namespace CivSim.Tests;

/// <summary>
/// Category 5: Decision Quality
/// All positions are OFFSETS from the simulation spawn center.
/// Rules: RULE-D1, RULE-D2, RULE-D3, RULE-D4
/// </summary>
public class DecisionQualityTests
{
    [Fact]
    public void Agent_Rests_During_Night()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Alice", isMale: false, hunger: 85f)
            .AgentAt("Alice", 0, 0)
            .AgentHome("Alice", 0, 0)
            .ShelterAt(0, 0)
            .ResourceAt(1, 0,  ResourceType.Berries, 30)
            .ResourceAt(-1, 0, ResourceType.Berries, 30)
            .Build();

        bool restedDuringNight = false;
        int  previousRestTick  = -1;

        for (int t = 0; t < 1500; t++)
        {
            sim.Tick(1);
            var alice = sim.GetAgent("Alice");
            if (!alice.IsAlive) break;

            if (alice.LastRestTick != previousRestTick && alice.LastRestTick > 0)
            {
                if (Agent.IsNightTime(alice.LastRestTick))
                    restedDuringNight = true;
                previousRestTick = alice.LastRestTick;
            }
        }

        Assert.True(restedDuringNight,
            "Agent should rest at least once during a night window across 3 sim-days (RULE-D2)");
    }

    [Fact]
    public void Content_Agent_Does_Not_Wander_Far_From_Home()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Bob", isMale: true, hunger: 90f)
            .AgentAt("Bob", 0, 0)
            .AgentHome("Bob", 0, 0)
            .ShelterAt(0, 0)
            .HomeStorageAt(0, 0, ResourceType.Berries, 60)
            .ResourceAt(1, 0,  ResourceType.Berries, 20)
            .ResourceAt(-1, 0, ResourceType.Berries, 20)
            .ResourceAt(0, 1,  ResourceType.Berries, 20)
            .ResourceAt(0, -1, ResourceType.Berries, 20)
            .Build();

        int homeX = sim.SpawnX, homeY = sim.SpawnY;

        var positions = sim.SamplePositions(300, sampleEveryNTicks: 5);
        var samples   = positions["Bob"];

        Assert.NotEmpty(samples);

        int maxDist = samples.Max(s => TestSim.ManhattanDistance(s.X, s.Y, homeX, homeY));

        Assert.True(maxDist <= 20,
            $"Content agent should not wander > 20 tiles from home. Max: {maxDist}");
    }

    [Fact]
    public void Agent_Movement_Does_Not_Oscillate()
    {
        // Test: agent at home with food nearby should gather and return
        // without bouncing back and forth excessively.
        // Food within 2 tiles stays in Home mode (no Home↔Forage oscillation).
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(7777)
            .AddAgent("Carol", isMale: false, hunger: 80f)
            .AgentAt("Carol", 5, 5)
            .AgentHome("Carol", 5, 5)
            .ShelterAt(5, 5)
            .ResourceAt(6, 5,  ResourceType.Berries, 30)
            .ResourceAt(7, 5,  ResourceType.Berries, 30)
            .Build();

        // Run a full day cycle for enough movement samples
        var positions = new List<(int X, int Y)>();
        for (int t = 0; t < 400; t++)
        {
            sim.Tick(1);
            var carol = sim.GetAgent("Carol");
            if (carol.IsAlive) positions.Add((carol.X, carol.Y));
        }

        int moves = 0, reversals = 0;
        for (int i = 1; i < positions.Count - 1; i++)
        {
            int dx1 = positions[i].X - positions[i-1].X, dy1 = positions[i].Y - positions[i-1].Y;
            int dx2 = positions[i+1].X - positions[i].X, dy2 = positions[i+1].Y - positions[i].Y;
            bool moved1 = (dx1 != 0 || dy1 != 0), moved2 = (dx2 != 0 || dy2 != 0);
            if (moved1) moves++;
            if (moved1 && moved2 && dx1 == -dx2 && dy1 == -dy2) reversals++;
        }

        // Only check oscillation when there are enough moves to be meaningful.
        // Small sample sizes are dominated by structural reversals (gather→return).
        if (moves >= 10)
        {
            double reversalPct = 100.0 * reversals / moves;
            // With mode-based forage→return cycles, reversals are structural:
            // home→resource→home is an inherent reversal on EVERY trip. With food
            // 1-2 tiles from home, nearly every move pair is out→back, producing
            // ~70-80% structural reversals. True pathological oscillation (agent
            // bouncing without gathering/depositing) would show as 90%+.
            // Threshold of 85% distinguishes structural from pathological.
            Assert.True(reversalPct < 85.0,
                $"Direction reversals should be < 85% of moves (RULE-D1). " +
                $"Actual: {reversalPct:F1}% ({reversals}/{moves})");
        }
    }

    [Fact]
    public void Content_Agent_Shows_Productive_Action_Variety()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Dana", isMale: false, hunger: 90f)
            .AgentAt("Dana", 0, 0)
            .AgentHome("Dana", 0, 0)
            .ShelterAt(0, 0)
            .HomeStorageAt(0, 0, ResourceType.Berries, 50)
            .ResourceAt(1, 0,  ResourceType.Berries, 30)
            .ResourceAt(-1, 0, ResourceType.Berries, 30)
            .ResourceAt(0, 1,  ResourceType.Wood, 20)
            .ResourceAt(0, -1, ResourceType.Wood, 20)
            .Build();

        // Run long enough for a full day cycle (explore budget expires, agent returns home)
        sim.Tick(600);

        var dana   = sim.GetAgent("Dana");
        var recent = dana.GetLastActions(10);

        Assert.NotEmpty(recent);

        var actionTypes = recent.Select(r => r.Action).Distinct().ToList();
        Assert.True(actionTypes.Count >= 2,
            $"Well-resourced agent should show action variety across a full day cycle. " +
            $"Got {actionTypes.Count} distinct action(s): {string.Join(", ", actionTypes)}");
    }
}
