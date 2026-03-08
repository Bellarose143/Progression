using CivSim.Core;
using Xunit;

namespace CivSim.Tests;

/// <summary>
/// Fix 2: DepositHome Spam Guards — threshold, cooldown, and committed-goal checks.
/// All positions are OFFSETS from the simulation spawn center.
/// </summary>
public class DepositHomeTests
{
    /// <summary>
    /// Fix 2A: DepositHome should score 0 when total depositable inventory is below DEPOSIT_HOME_MIN_THRESHOLD.
    /// Agent at home with 2 items (below threshold of 3) — DepositHome must not appear in scored actions.
    /// </summary>
    [Fact]
    public void DepositHome_DoesNotFire_WhenInventoryBelowThreshold()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Alice", isMale: false, hunger: 90f)
            .AgentAt("Alice", 0, 0)
            .AgentHome("Alice", 0, 0)
            .ShelterAt(0, 0)
            .AgentInventory("Alice", ResourceType.Grain, 2)
            .Build();

        var alice = sim.GetAgent("Alice");
        var world = sim.World;
        var currentTick = 100;

        // Score actions — with only 2 items, DepositHome should not score
        var scored = UtilityScorer.ScoreHomeActions(alice, world, currentTick, new Random(1));

        bool hasDeposit = scored.Any(s => s.Action == ActionType.DepositHome);
        Assert.False(hasDeposit,
            "DepositHome should score 0 when agent carries fewer items than DEPOSIT_HOME_MIN_THRESHOLD (2 < 3)");
    }

    /// <summary>
    /// Fix 2C: DepositHome should score 0 when agent has a committed goal (e.g. GatherResourceAt)
    /// and is NOT at home tile. This prevents mid-trip deposit pulls.
    /// </summary>
    [Fact]
    public void DepositHome_DoesNotFire_WhenAgentHasCommittedGoal_AndNotAtHome()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Bob", isMale: true, hunger: 90f)
            .AgentAt("Bob", 3, 0)      // NOT at home (home is 0,0)
            .AgentHome("Bob", 0, 0)
            .ShelterAt(0, 0)
            .AgentInventory("Bob", ResourceType.Grain, 6)
            .AgentInventory("Bob", ResourceType.Wood, 4)
            .Build();

        var bob = sim.GetAgent("Bob");
        // Set a committed goal — agent is actively gathering stone
        bob.CurrentGoal = GoalType.GatherResourceAt;
        bob.GoalTarget = (sim.SpawnX + 5, sim.SpawnY);
        bob.GoalResource = ResourceType.Stone;
        bob.GoalStartTick = 50;

        var world = sim.World;
        var currentTick = 100;

        var scored = UtilityScorer.ScoreHomeActions(bob, world, currentTick, new Random(1));

        bool hasDeposit = scored.Any(s => s.Action == ActionType.DepositHome);
        Assert.False(hasDeposit,
            "DepositHome should score 0 when agent has an active committed goal and is not at home tile");
    }

    /// <summary>
    /// Fix 2B: After executing DepositHome, the cooldown prevents it from scoring > 0
    /// within DEPOSIT_HOME_COOLDOWN ticks. After the cooldown expires, it can score again.
    /// </summary>
    [Fact]
    public void DepositHome_Cooldown_PreventsRepeatWithinWindow()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Carol", isMale: false, hunger: 90f)
            .AgentAt("Carol", 0, 0)
            .AgentHome("Carol", 0, 0)
            .ShelterAt(0, 0)
            .AgentInventory("Carol", ResourceType.Grain, 6)
            .AgentInventory("Carol", ResourceType.Wood, 4)
            .Build();

        var carol = sim.GetAgent("Carol");
        var world = sim.World;

        // Simulate that Carol just completed a deposit at tick 100
        carol.LastDepositTick = 100;

        // At tick 101 (1 tick later, within cooldown of 5), DepositHome should not score
        var scoredDuringCooldown = UtilityScorer.ScoreHomeActions(carol, world, 101, new Random(1));
        bool hasDuringCooldown = scoredDuringCooldown.Any(s => s.Action == ActionType.DepositHome);
        Assert.False(hasDuringCooldown,
            "DepositHome should score 0 within the cooldown window (1 tick after last deposit, cooldown=5)");

        // At tick 105 (5 ticks later, cooldown expired), DepositHome should be able to score
        var scoredAfterCooldown = UtilityScorer.ScoreHomeActions(carol, world, 105, new Random(1));
        bool hasAfterCooldown = scoredAfterCooldown.Any(s => s.Action == ActionType.DepositHome);
        Assert.True(hasAfterCooldown,
            "DepositHome should be able to score after cooldown expires (5 ticks after last deposit)");
    }
}
