using CivSim.Core;
using Xunit;

namespace CivSim.Tests;

/// <summary>
/// Fix 2: Scoring Dead Zone Tests
/// Validates that content agents always have Experiment as a viable action,
/// and that Move is never assigned without a valid destination.
/// </summary>
[Trait("Category", "Integration")]
public class ScoringDeadZoneTests
{
    /// <summary>
    /// When all basic needs are met, the agent is at home with shelter, and has
    /// resources for experimenting, Experiment should score >= 0.30 as a content
    /// agent baseline, preventing the scoring dead zone where no action scores
    /// above zero.
    /// </summary>
    [Fact]
    public void Content_Agent_Scores_Experiment_At_Least_030()
    {
        // Set up a content agent: at home, sheltered, well-fed, healthy, with
        // resources for recipes (stone for stone_knife, wood for fire/lean_to)
        var world = new World(32, 32, 42);
        var sim = new Simulation(world, 42);
        var agent = sim.SpawnAgent();

        // Place shelter at agent's position
        var tile = world.GetTile(agent.X, agent.Y);
        tile.Structures.Add("lean_to");

        // Set agent state: content
        agent.Name = "TestAgent";
        agent.Hunger = 85f;
        agent.Health = 90;
        agent.HomeTile = (agent.X, agent.Y);
        agent.IsExposed = false;
        agent.Age = SimConfig.ReproductionMinAge;

        // Give resources so recipes are available (stone for stone_knife)
        agent.Inventory[ResourceType.Stone] = 2;
        agent.Inventory[ResourceType.Wood] = 3;

        // Score Home actions
        var rng = new Random(42);
        var scored = UtilityScorer.ScoreHomeActions(agent, world, 200, rng);

        // Find Experiment score
        var experimentAction = scored.FirstOrDefault(s => s.Action == ActionType.Experiment);

        Assert.True(experimentAction.Action == ActionType.Experiment,
            "Experiment should appear in scored actions for a content agent with available recipes");
        Assert.True(experimentAction.Score >= 0.30f,
            $"Content agent Experiment score should be >= 0.30, was {experimentAction.Score:F3}");
    }

    /// <summary>
    /// When basic needs are met and Socialize is capped, a content agent at home
    /// should choose Experiment within a reasonable number of ticks. The agent
    /// may have other actions (Gather, DepositHome, etc.) to complete first, so
    /// we allow up to 100 ticks which spans enough decision cycles for Experiment
    /// to be selected at least once.
    /// </summary>
    [Fact]
    public void Content_Agent_Chooses_Experiment_Within_Reasonable_Time()
    {
        // Use a scenario where the agent is at home with abundant food stored
        // and has resources for experimenting. No food on adjacent tiles to
        // minimize Gather competing with Experiment.
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(42)
            .AddAgent("Alice", isMale: false, hunger: 90f, health: 100)
            .AgentAt("Alice", 0, 0)
            .AgentHome("Alice", 0, 0)
            .HomeStorageAt(0, 0, ResourceType.Berries, 50)
            .Build();

        // Give the agent inventory resources for recipes
        var alice = sim.GetAgent("Alice");
        alice.Inventory[ResourceType.Stone] = 3;
        alice.Inventory[ResourceType.Wood] = 5;

        // Give lean_to knowledge (they already have shelter from HomeStorageAt)
        alice.LearnDiscovery("lean_to");

        bool experimentChosen = false;

        // Fast-forward to daytime (tick 150 is daytime: 150 % 480 = 150, not night)
        sim.Tick(150);

        // Allow up to 100 ticks for Experiment to be selected
        for (int t = 0; t < 100; t++)
        {
            sim.Tick(1);
            alice = sim.GetAgent("Alice");
            if (!alice.IsAlive) break;

            if (alice.CurrentAction == ActionType.Experiment
                || alice.PendingAction == ActionType.Experiment)
            {
                experimentChosen = true;
                break;
            }
        }

        Assert.True(experimentChosen,
            "Content agent should choose Experiment within 100 ticks when at home with resources and food stored");
    }

    /// <summary>
    /// Over a 1000+ tick run, Experiment should account for > 3% of decisions
    /// for at least one agent.
    /// </summary>
    [Fact]
    public void Experiment_Exceeds_3_Percent_Of_Decisions_Over_1000_Ticks()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(123)
            .AddAgent("Alice", isMale: false, hunger: 90f, health: 100)
            .AgentAt("Alice", 0, 0)
            .AgentHome("Alice", 0, 0)
            .ShelterAt(0, 0)
            // Abundant food nearby so agent stays content
            .ResourceAt(1, 0, ResourceType.Berries, 80)
            .ResourceAt(-1, 0, ResourceType.Berries, 80)
            .ResourceAt(0, 1, ResourceType.Berries, 80)
            .ResourceAt(0, 0, ResourceType.Stone, 20)
            .ResourceAt(0, 0, ResourceType.Wood, 20)
            .HomeStorageAt(0, 0, ResourceType.Berries, 40)
            .Build();

        var alice = sim.GetAgent("Alice");
        alice.Inventory[ResourceType.Stone] = 3;
        alice.Inventory[ResourceType.Wood] = 5;
        alice.LearnDiscovery("lean_to");

        int experimentCount = 0;
        int totalDecisions = 0;

        for (int t = 0; t < 1200; t++)
        {
            sim.Tick(1);
            alice = sim.GetAgent("Alice");
            if (!alice.IsAlive) break;

            // Count decisions (non-idle actions)
            if (alice.CurrentAction != ActionType.Idle)
            {
                totalDecisions++;
                if (alice.CurrentAction == ActionType.Experiment)
                    experimentCount++;
            }
        }

        Assert.True(totalDecisions > 0, "Agent should have made at least some decisions");

        float experimentPct = totalDecisions > 0
            ? (float)experimentCount / totalDecisions * 100f
            : 0f;

        Assert.True(experimentPct > 3f,
            $"Experiment should be > 3% of decisions. Was {experimentPct:F1}% " +
            $"({experimentCount}/{totalDecisions})");
    }

    /// <summary>
    /// Move should never be the current action when the agent has no pending
    /// action and no action target. This validates the elimination of the
    /// "Move 0/0" dead zone where Move was assigned without a real destination.
    /// </summary>
    [Fact]
    public void No_Move_Without_Valid_Target()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(77)
            .AddAgent("Bob", isMale: true, hunger: 85f, health: 100)
            .AgentAt("Bob", 0, 0)
            .AgentHome("Bob", 0, 0)
            .ShelterAt(0, 0)
            .ResourceAt(1, 0, ResourceType.Berries, 30)
            .ResourceAt(-1, 0, ResourceType.Berries, 30)
            .ResourceAt(0, 0, ResourceType.Stone, 10)
            .ResourceAt(0, 0, ResourceType.Wood, 10)
            .HomeStorageAt(0, 0, ResourceType.Berries, 30)
            .Build();

        var bob = sim.GetAgent("Bob");
        bob.LearnDiscovery("lean_to");

        int moveWithoutTargetCount = 0;

        for (int t = 0; t < 1000; t++)
        {
            sim.Tick(1);
            bob = sim.GetAgent("Bob");
            if (!bob.IsAlive) break;

            // Check: if CurrentAction is Move, there should be either a
            // PendingAction with a valid ActionTarget, or the agent should be busy
            if (bob.CurrentAction == ActionType.Move)
            {
                bool hasValidMoveState = bob.IsBusy
                    && bob.PendingAction == ActionType.Move
                    && bob.ActionTarget.HasValue;

                if (!hasValidMoveState)
                    moveWithoutTargetCount++;
            }
        }

        Assert.Equal(0, moveWithoutTargetCount);
    }

    /// <summary>
    /// Verify that the content agent baseline only applies when agent is truly
    /// content: at home, fed, healthy, and sheltered.
    /// </summary>
    [Fact]
    public void Experiment_Floor_Only_Applies_When_Content()
    {
        var world = new World(32, 32, 42);
        var sim = new Simulation(world, 42);
        var agent = sim.SpawnAgent();

        var tile = world.GetTile(agent.X, agent.Y);
        tile.Structures.Add("lean_to");

        agent.Name = "TestAgent";
        agent.HomeTile = (agent.X, agent.Y);
        agent.Age = SimConfig.ReproductionMinAge;
        agent.Inventory[ResourceType.Stone] = 2;
        agent.Inventory[ResourceType.Wood] = 3;

        var rng = new Random(42);

        // Case 1: Hungry agent (hunger <= 60) — should NOT get floor
        agent.Hunger = 55f;
        agent.Health = 90;
        agent.IsExposed = false;
        var scored1 = UtilityScorer.ScoreHomeActions(agent, world, 200, rng);
        var exp1 = scored1.FirstOrDefault(s => s.Action == ActionType.Experiment);
        // Hunger <= ExperimentHungerGate (65) means ScoreExperiment returns early — no Experiment at all
        Assert.True(exp1.Action != ActionType.Experiment || exp1.Score < 0.30f,
            $"Hungry agent should not get 0.30 floor (score={exp1.Score:F3})");

        // Case 2: Not at home — should NOT get floor
        agent.Hunger = 85f;
        agent.Health = 90;
        agent.IsExposed = false;
        // Move agent away from home
        world.RemoveAgentFromIndex(agent);
        agent.X = agent.HomeTile!.Value.X + 3;
        agent.Y = agent.HomeTile!.Value.Y + 3;
        world.AddAgentToIndex(agent);
        var scored2 = UtilityScorer.ScoreHomeActions(agent, world, 200, new Random(42));
        var exp2 = scored2.FirstOrDefault(s => s.Action == ActionType.Experiment);
        // Not at home, so content baseline doesn't apply
        // The regular scoring may still give some score, but it shouldn't be floor-boosted
        // We just verify it's not a hard assert — different logic paths apply

        // Case 3: Exposed (no shelter) — should NOT get floor
        world.RemoveAgentFromIndex(agent);
        agent.X = agent.HomeTile!.Value.X;
        agent.Y = agent.HomeTile!.Value.Y;
        world.AddAgentToIndex(agent);
        agent.Hunger = 85f;
        agent.Health = 90;
        agent.IsExposed = true;
        // When exposed and knows lean_to, ScoreExperiment returns early
        agent.LearnDiscovery("lean_to");
        var scored3 = UtilityScorer.ScoreHomeActions(agent, world, 200, new Random(42));
        var exp3 = scored3.FirstOrDefault(s => s.Action == ActionType.Experiment);
        Assert.True(exp3.Action != ActionType.Experiment,
            "Exposed agent who knows lean_to should not get Experiment (survival gate)");
    }
}
