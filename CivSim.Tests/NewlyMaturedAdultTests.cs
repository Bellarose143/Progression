using CivSim.Core;
using Xunit;

namespace CivSim.Tests;

/// <summary>
/// Tests for newly matured adults — agents who transition from Youth to Adult stage.
/// These tests verify that adults with empty inventories (inherited from youth stage,
/// which doesn't gather into inventory) are not stuck in an idle loop.
/// Root cause: Youth never gathers into inventory, so upon maturation, ScoreExperiment
/// finds no available recipes (requires resources) and nothing else scores, leading to Idle.
/// </summary>
[Trait("Category", "Integration")]
public class NewlyMaturedAdultTests
{
    /// <summary>
    /// A newly matured adult with zero personal food (but full home storage) should
    /// trigger a Forage transition to gather personal food. Adults need personal food
    /// for travel and as buffer — they shouldn't sit idle at a well-stocked pantry.
    /// </summary>
    [Fact]
    public void Newly_Matured_Adult_With_Empty_Inventory_Forages_For_Food()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(42)
            .AddAgent("Eleanor", isMale: false, hunger: 80f)
            .AgentAt("Eleanor", 0, 0)
            .AgentHome("Eleanor", 0, 0)
            .AgentKnows("Eleanor", "clothing", "lean_to", "stone_knife", "fire")
            .ShelterAt(0, 0)
            .HomeStorageAt(0, 0, ResourceType.Berries, 20)
            .ResourceAt(2, 0, ResourceType.Berries, 15)
            .ResourceAt(0, 2, ResourceType.Wood, 10)
            .ResourceAt(1, 1, ResourceType.Stone, 10)
            .Build();

        var eleanor = sim.GetAgent("Eleanor");
        // Simulate newly matured: adult age, empty inventory, Home mode
        eleanor.Age = SimConfig.ChildYouthAge + 1; // Just became adult
        Assert.Equal(DevelopmentStage.Adult, eleanor.Stage);
        Assert.Equal(0, eleanor.InventoryCount());

        // Run a few ticks — she should NOT remain idle
        sim.Tick(20);

        // She should have transitioned out of pure Home idle:
        // Either she's foraging for food, or she's gathered something
        bool didSomething = eleanor.FoodInInventory() > 0
            || eleanor.InventoryCount() > 0
            || eleanor.CurrentMode == BehaviorMode.Forage
            || eleanor.CurrentAction != ActionType.Idle;

        Assert.True(didSomething,
            $"Newly matured adult should not sit idle. Mode={eleanor.CurrentMode}, " +
            $"Action={eleanor.CurrentAction}, Inventory={eleanor.InventoryCount()}, " +
            $"Food={eleanor.FoodInInventory()}");
    }

    /// <summary>
    /// ShouldForageForFood returns true when personal food is 0, even if home storage
    /// has plenty. This prevents the idle trap for newly matured adults.
    /// </summary>
    [Fact]
    public void Zero_Personal_Food_Triggers_Forage_Despite_Full_Home_Storage()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(42)
            .AddAgent("Eleanor", isMale: false, hunger: 80f)
            .AgentAt("Eleanor", 0, 0)
            .AgentHome("Eleanor", 0, 0)
            .AgentKnows("Eleanor", "clothing", "lean_to")
            .ShelterAt(0, 0)
            .HomeStorageAt(0, 0, ResourceType.Berries, 20) // Well above ForageEntryHomeStorage (15)
            .ResourceAt(2, 0, ResourceType.Berries, 15)
            .Build();

        var eleanor = sim.GetAgent("Eleanor");
        eleanor.Age = SimConfig.ChildYouthAge + 1;
        Assert.Equal(0, eleanor.FoodInInventory());

        // Run until agent enters Forage or picks up food (generous tick budget
        // to account for nighttime rest and mode transition timing)
        bool leftHome = false;
        for (int t = 0; t < 500; t++)
        {
            sim.Tick(1);
            if (!eleanor.IsAlive) break;
            if (eleanor.CurrentMode == BehaviorMode.Forage
                || eleanor.FoodInInventory() > 0
                || eleanor.InventoryCount() > 0)
            {
                leftHome = true;
                break;
            }
        }

        Assert.True(leftHome,
            $"Agent with zero personal food should forage even with full home storage. " +
            $"Mode={eleanor.CurrentMode}, Food={eleanor.FoodInInventory()}, Inventory={eleanor.InventoryCount()}");
    }

    /// <summary>
    /// When an adult has low inventory and there are unknown recipes with resource
    /// requirements, the experiment material need check should trigger a forage trip
    /// to gather materials for experimentation.
    /// </summary>
    [Fact]
    public void Low_Inventory_Adult_Forages_For_Experiment_Materials()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(42)
            .AddAgent("Eleanor", isMale: false, hunger: 80f)
            .AgentAt("Eleanor", 0, 0)
            .AgentHome("Eleanor", 0, 0)
            // Knows basics plus foraging_knowledge, so Tier 1 food recipes are done.
            // Still needs stone_knife (Stone×1), fire (Wood×3), crude_axe (Wood×2+Stone×1).
            .AgentKnows("Eleanor", "clothing", "lean_to", "foraging_knowledge")
            .ShelterAt(0, 0)
            .HomeStorageAt(0, 0, ResourceType.Berries, 20)
            .ResourceAt(2, 0, ResourceType.Stone, 15)
            .ResourceAt(0, 2, ResourceType.Wood, 10)
            .Build();

        var eleanor = sim.GetAgent("Eleanor");
        eleanor.Age = SimConfig.ChildYouthAge + 1;
        Assert.Equal(DevelopmentStage.Adult, eleanor.Stage);
        Assert.Equal(0, eleanor.InventoryCount());

        // Verify she has unknown recipes that need resources she doesn't have
        var available = RecipeRegistry.GetAvailableRecipes(eleanor);
        Assert.Empty(available); // No resources = no available recipes

        // But there ARE recipes she COULD do if she had resources
        Assert.DoesNotContain("stone_knife", eleanor.Knowledge);
        Assert.DoesNotContain("fire", eleanor.Knowledge);

        // Skip past nighttime (ticks 0-119 are night) so agent isn't forced to rest
        sim.Tick(120);

        // Run ticks — she should go gather materials (D23: extended window for RNG cascade tolerance)
        sim.Tick(100);

        // Should have gathered some resources or be in Forage mode
        bool isGathering = eleanor.InventoryCount() > 0
            || eleanor.CurrentMode == BehaviorMode.Forage
            || eleanor.Inventory.GetValueOrDefault(ResourceType.Stone, 0) > 0
            || eleanor.Inventory.GetValueOrDefault(ResourceType.Wood, 0) > 0;

        Assert.True(isGathering,
            $"Adult with low inventory should forage for experiment materials. " +
            $"Mode={eleanor.CurrentMode}, Inventory={eleanor.InventoryCount()}, " +
            $"Stone={eleanor.Inventory.GetValueOrDefault(ResourceType.Stone, 0)}, " +
            $"Wood={eleanor.Inventory.GetValueOrDefault(ResourceType.Wood, 0)}");
    }

    /// <summary>
    /// A newly matured adult's first 50 decisions should include productive actions
    /// (Gather, Move, Experiment, Forage transitions) — not just Idle and Rest.
    /// This is the "Eleanor test" — verifies the idle trap is broken.
    /// </summary>
    [Fact]
    public void Newly_Matured_Adult_First_50_Decisions_Include_Productive_Actions()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(42)
            .AddAgent("Eleanor", isMale: false, hunger: 80f)
            .AgentAt("Eleanor", 0, 0)
            .AgentHome("Eleanor", 0, 0)
            .AgentKnows("Eleanor", "clothing", "lean_to")
            .ShelterAt(0, 0)
            .HomeStorageAt(0, 0, ResourceType.Berries, 20)
            .ResourceAt(2, 0, ResourceType.Berries, 15)
            .ResourceAt(0, 2, ResourceType.Wood, 10)
            .ResourceAt(1, 1, ResourceType.Stone, 10)
            .Build();

        var eleanor = sim.GetAgent("Eleanor");
        eleanor.Age = SimConfig.ChildYouthAge + 1;

        // Skip past nighttime (ticks 0-119 are night) so agent isn't forced to rest
        sim.Tick(120);

        // Track actions across 50 ticks
        var actionCounts = new Dictionary<ActionType, int>();
        for (int i = 0; i < 50; i++)
        {
            sim.Tick(1);
            if (!actionCounts.ContainsKey(eleanor.CurrentAction))
                actionCounts[eleanor.CurrentAction] = 0;
            actionCounts[eleanor.CurrentAction]++;
        }

        // Count productive actions (anything that isn't Idle or Rest)
        int productiveCount = 0;
        foreach (var kvp in actionCounts)
        {
            if (kvp.Key != ActionType.Idle && kvp.Key != ActionType.Rest)
                productiveCount += kvp.Value;
        }

        int idleCount = actionCounts.GetValueOrDefault(ActionType.Idle, 0);
        int restCount = actionCounts.GetValueOrDefault(ActionType.Rest, 0);

        // At least 20% of decisions should be productive (gather, move, experiment, etc.)
        Assert.True(productiveCount >= 10,
            $"Newly matured adult should have at least 10 productive actions in 50 ticks. " +
            $"Got {productiveCount} productive, {idleCount} idle, {restCount} rest. " +
            $"Actions: {string.Join(", ", actionCounts.Select(kv => $"{kv.Key}={kv.Value}"))}");
    }

    /// <summary>
    /// Verify that ScoreExperiment returns > 0 for an adult with sufficient resources
    /// and unknown recipes. This is a direct unit test of the scoring path.
    /// </summary>
    [Fact]
    public void ScoreExperiment_Returns_Positive_For_Adult_With_Resources()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(42)
            .AddAgent("Eleanor", isMale: false, hunger: 80f)
            .AgentAt("Eleanor", 0, 0)
            .AgentHome("Eleanor", 0, 0)
            .AgentKnows("Eleanor", "clothing", "lean_to")
            .AgentInventory("Eleanor", ResourceType.Stone, 3)
            .AgentInventory("Eleanor", ResourceType.Wood, 5)
            .AgentInventory("Eleanor", ResourceType.Berries, 3)
            .ShelterAt(0, 0)
            .Build();

        var eleanor = sim.GetAgent("Eleanor");
        eleanor.Age = SimConfig.ChildYouthAge + 1;
        eleanor.IsExposed = false; // Has shelter

        // Should have available recipes (stone_knife, fire, etc.)
        var available = RecipeRegistry.GetAvailableRecipes(eleanor);
        Assert.NotEmpty(available);

        // Score home actions — should include Experiment
        var random = new Random(42);
        var scores = UtilityScorer.ScoreHomeActions(eleanor, sim.World, 100, random);

        var experimentScore = scores.FirstOrDefault(s => s.Action == ActionType.Experiment);
        Assert.True(experimentScore.Score > 0,
            $"ScoreExperiment should return > 0 for agent with resources and unknown recipes. " +
            $"Available recipes: {string.Join(", ", available.Select(r => r.Id))}. " +
            $"Top scores: {string.Join(", ", scores.Take(5).Select(s => $"{s.Action}={s.Score:F3}"))}");
    }

    /// <summary>
    /// Verify that ScoreExperiment returns nothing when the agent has settlement
    /// knowledge but no resources — this confirms the root cause of the idle bug.
    /// </summary>
    [Fact]
    public void ScoreExperiment_Returns_Nothing_Without_Resources()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(42)
            .AddAgent("Eleanor", isMale: false, hunger: 80f)
            .AgentAt("Eleanor", 0, 0)
            .AgentHome("Eleanor", 0, 0)
            .AgentKnows("Eleanor", "clothing", "lean_to", "stone_knife", "fire", "foraging_knowledge")
            .ShelterAt(0, 0)
            .Build();

        var eleanor = sim.GetAgent("Eleanor");
        eleanor.Age = SimConfig.ChildYouthAge + 1;
        eleanor.IsExposed = false;
        // Empty inventory — no resources for Tier 2 recipes

        // All Tier 1 recipes are known; Tier 2 needs resources she doesn't have
        var available = RecipeRegistry.GetAvailableRecipes(eleanor);
        Assert.Empty(available);

        // Score home actions — Experiment should NOT appear
        var random = new Random(42);
        var scores = UtilityScorer.ScoreHomeActions(eleanor, sim.World, 100, random);

        var experimentScore = scores.FirstOrDefault(s => s.Action == ActionType.Experiment);
        Assert.Equal(0f, experimentScore.Score); // Default struct — 0 score means not present
    }
}
