using CivSim.Core;
using Xunit;

namespace CivSim.Tests;

/// <summary>
/// Fix 3: Experiment Stagnation Tests
/// Validates that experiment scoring maintains a competitive floor for content agents,
/// and that Idle/Rest never beats Experiment when undiscovered recipes are available.
/// </summary>
[Trait("Category", "Integration")]
public class ExperimentStagnationTests
{
    /// <summary>
    /// Fix 3A: When all content conditions are met (hunger > 60, health > 60,
    /// has shelter, at home) AND the agent has undiscovered recipes with available
    /// materials, Experiment must score at MINIMUM 0.30, even after dampening.
    /// This floor survives action dampening, trait multipliers, and other post-processing.
    /// </summary>
    [Fact]
    public void Experiment_ScoresMinimumFloor_WhenAgentComfortable_AndRecipesAvailable()
    {
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

        // Give resources so recipes are available (stone for stone_knife, wood for fire/lean_to)
        agent.Inventory[ResourceType.Stone] = 3;
        agent.Inventory[ResourceType.Wood] = 5;

        // Simulate heavy dampening: pretend agent has been experimenting consecutively
        // This is the scenario that causes stagnation — dampening pushes 0.30 below competitive range
        agent.LastChosenUtilityAction = ActionType.Experiment;
        agent.ConsecutiveSameActionTicks = 10; // Heavy dampening

        // Score Home actions (includes dampening + post-dampening floor enforcement)
        var rng = new Random(42);
        var scored = UtilityScorer.ScoreHomeActions(agent, world, 200, rng);

        // Find Experiment score
        var experimentAction = scored.FirstOrDefault(s => s.Action == ActionType.Experiment);

        Assert.True(experimentAction.Action == ActionType.Experiment,
            "Experiment should appear in scored actions for a content agent with available recipes");
        Assert.True(experimentAction.Score >= 0.30f,
            $"Content agent Experiment score should be >= 0.30 even after dampening, was {experimentAction.Score:F3}");
    }

    /// <summary>
    /// Fix 3B: When the agent has undiscovered recipes they could attempt AND the
    /// materials for at least one, Rest/Idle should not beat Experiment during daytime.
    /// An agent sitting idle while there are things to discover is broken behavior.
    /// </summary>
    [Fact]
    public void Idle_NeverBeatsExperiment_WhenRecipesAvailable()
    {
        var world = new World(32, 32, 42);
        var sim = new Simulation(world, 42);
        var agent = sim.SpawnAgent();

        // Place shelter at agent's position
        var tile = world.GetTile(agent.X, agent.Y);
        tile.Structures.Add("lean_to");

        // Set agent state: content but slightly damaged (so Rest would normally score)
        agent.Name = "TestAgent";
        agent.Hunger = 85f;
        agent.Health = 85;
        agent.HomeTile = (agent.X, agent.Y);
        agent.IsExposed = false;
        agent.Age = SimConfig.ReproductionMinAge;

        // Give resources so recipes are available
        agent.Inventory[ResourceType.Stone] = 3;
        agent.Inventory[ResourceType.Wood] = 5;

        // Score during daytime (tick 200 = 200 % 480 = 200, well within day)
        var rng = new Random(42);
        var scored = UtilityScorer.ScoreHomeActions(agent, world, 200, rng);

        var experimentAction = scored.FirstOrDefault(s => s.Action == ActionType.Experiment);
        var restAction = scored.FirstOrDefault(s => s.Action == ActionType.Rest);

        // Experiment must be present
        Assert.True(experimentAction.Action == ActionType.Experiment,
            "Experiment should appear in scored actions when recipes are available");

        // If Rest is present, it must score lower than Experiment
        if (restAction.Action == ActionType.Rest && restAction.Score > 0f)
        {
            Assert.True(restAction.Score < experimentAction.Score,
                $"Rest ({restAction.Score:F3}) should not beat Experiment ({experimentAction.Score:F3}) " +
                "when undiscovered recipes are available during daytime");
        }
    }

    /// <summary>
    /// Verify that the content floor does NOT apply when the agent is hungry
    /// (hunger <= 60). Hungry agents should prioritize food, not experimenting.
    /// </summary>
    [Fact]
    public void Experiment_Floor_DoesNotApply_WhenHungry()
    {
        var world = new World(32, 32, 42);
        var sim = new Simulation(world, 42);
        var agent = sim.SpawnAgent();

        var tile = world.GetTile(agent.X, agent.Y);
        tile.Structures.Add("lean_to");

        agent.Name = "TestAgent";
        agent.Hunger = 50f; // Below the 60 threshold
        agent.Health = 90;
        agent.HomeTile = (agent.X, agent.Y);
        agent.IsExposed = false;
        agent.Age = SimConfig.ReproductionMinAge;
        agent.Inventory[ResourceType.Stone] = 3;
        agent.Inventory[ResourceType.Wood] = 5;

        var rng = new Random(42);
        var scored = UtilityScorer.ScoreHomeActions(agent, world, 200, rng);

        var experimentAction = scored.FirstOrDefault(s => s.Action == ActionType.Experiment);

        // Hungry agent should NOT get the 0.30 floor
        // The hunger gate (65) should prevent Experiment from scoring at all
        Assert.True(experimentAction.Action != ActionType.Experiment || experimentAction.Score < 0.30f,
            $"Hungry agent (hunger={agent.Hunger}) should not get 0.30 floor, score was {experimentAction.Score:F3}");
    }

    /// <summary>
    /// Verify that the post-dampening floor keeps Experiment competitive over extended runs.
    /// Even after many consecutive Experiment actions, the floor prevents it from being
    /// permanently outcompeted.
    /// </summary>
    [Fact]
    public void Experiment_Survives_MaxDampening()
    {
        var world = new World(32, 32, 42);
        var sim = new Simulation(world, 42);
        var agent = sim.SpawnAgent();

        var tile = world.GetTile(agent.X, agent.Y);
        tile.Structures.Add("lean_to");

        agent.Name = "TestAgent";
        agent.Hunger = 90f;
        agent.Health = 95;
        agent.HomeTile = (agent.X, agent.Y);
        agent.IsExposed = false;
        agent.Age = SimConfig.ReproductionMinAge;
        agent.Inventory[ResourceType.Stone] = 3;
        agent.Inventory[ResourceType.Wood] = 5;

        // Maximum dampening: 20 consecutive ticks of Experiment
        agent.LastChosenUtilityAction = ActionType.Experiment;
        agent.ConsecutiveSameActionTicks = 20;

        var rng = new Random(42);
        var scored = UtilityScorer.ScoreHomeActions(agent, world, 200, rng);

        var experimentAction = scored.FirstOrDefault(s => s.Action == ActionType.Experiment);

        Assert.True(experimentAction.Action == ActionType.Experiment,
            "Experiment should survive even maximum dampening for content agents");
        Assert.True(experimentAction.Score >= 0.30f,
            $"Experiment score should be >= 0.30 even at max dampening, was {experimentAction.Score:F3}");
    }
}
