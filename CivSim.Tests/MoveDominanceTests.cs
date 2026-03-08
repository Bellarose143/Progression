using CivSim.Core;
using Xunit;

namespace CivSim.Tests;

/// <summary>
/// D20 Fix 2: Tests for Move dominance fix.
/// Verifies that forage search sets a goal (not goalless movement)
/// and that agents don't oscillate between 2 tiles indefinitely.
/// </summary>
public class MoveDominanceTests
{
    [Fact]
    public void ForageSearch_Sets_SeekFood_Goal()
    {
        // When an agent is in Forage mode with no remembered food,
        // StartForageSearch should set a SeekFood goal, not produce goalless movement.
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(42)
            .AddAgent("Alice", isMale: false, hunger: 60f)
            .AgentAt("Alice", 0, 0)
            .AgentHome("Alice", 0, 0)
            .ShelterAt(0, 0)
            // No food nearby — forces forage search
            .Build();

        var alice = sim.GetAgent("Alice");

        // Transition to Forage mode
        alice.TransitionMode(BehaviorMode.Forage, sim.Simulation.CurrentTick);

        // Run ticks — agent should eventually be in Move with a GatherFoodAt goal
        bool hadGoalDuringMove = false;
        for (int t = 0; t < 200; t++)
        {
            sim.Tick(1);
            if (!alice.IsAlive) break;

            if (alice.CurrentAction == ActionType.Move && alice.CurrentGoal == GoalType.GatherFoodAt)
            {
                hadGoalDuringMove = true;
                break;
            }
        }

        Assert.True(hadGoalDuringMove,
            "Forage search should set a GatherFoodAt goal during movement (not goalless Move)");
    }

    [Fact]
    public void Agent_DoesNot_Oscillate_Between_Two_Tiles()
    {
        // An agent near home with depleted food should not bounce between
        // the same two tiles for hundreds of ticks.
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1337)
            .AddAgent("Bob", isMale: true, hunger: 70f)
            .AgentAt("Bob", 0, 0)
            .AgentHome("Bob", 0, 0)
            .ShelterAt(0, 0)
            // Some wood but no food — creates forage pressure
            .ResourceAt(1, 0, ResourceType.Wood, 10)
            .Build();

        var bob = sim.GetAgent("Bob");

        // Track position history to detect 2-tile oscillation
        var posHistory = new List<(int X, int Y)>();

        for (int t = 0; t < 500; t++)
        {
            sim.Tick(1);
            if (!bob.IsAlive) break;

            posHistory.Add((bob.X, bob.Y));
        }

        // Check for 2-tile oscillation: if the same 2 positions account for > 80% of ticks,
        // the agent is stuck oscillating
        if (posHistory.Count > 100)
        {
            var posCounts = posHistory.GroupBy(p => p)
                .OrderByDescending(g => g.Count())
                .Take(2)
                .Select(g => g.Count())
                .ToList();

            if (posCounts.Count >= 2)
            {
                double top2Pct = 100.0 * (posCounts[0] + posCounts[1]) / posHistory.Count;
                Assert.True(top2Pct < 80,
                    $"Agent should not oscillate between 2 tiles. Top 2 tiles = {top2Pct:F1}% of {posHistory.Count} ticks");
            }
        }
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void FounderAgent_Move_Below_60_Percent_LateGame()
    {
        // Run seed 1337 for 5000 ticks from tick 50000 and verify Alexander (Agent 2)
        // doesn't spend > 60% of time in Move
        var world = new World(64, 64, 1337);
        var sim = new Simulation(world, 1337);
        sim.SpawnAgent();
        sim.SpawnAgent();

        // Fast-forward to tick 50000
        for (int t = 0; t < 50000; t++)
            sim.Tick();

        var agent2 = sim.Agents[1]; // Agent 2 = Alexander
        Assert.True(agent2.IsAlive, "Agent 2 should be alive at tick 50000");

        // Track 5000 ticks
        int moveTicks = 0;
        int totalTicks = 5000;
        for (int t = 0; t < totalTicks; t++)
        {
            sim.Tick();
            if (!agent2.IsAlive) break;
            if (agent2.CurrentAction == ActionType.Move)
                moveTicks++;
        }

        double movePct = 100.0 * moveTicks / totalTicks;
        Assert.True(movePct < 60,
            $"Founder agent Move% should be < 60% in late game. Got {movePct:F1}% ({moveTicks}/{totalTicks})");
    }
}
