using CivSim.Core;
using Xunit;

namespace CivSim.Tests;

/// <summary>
/// Category 3: Home-Pull Behavior
/// All positions are OFFSETS from the simulation spawn center.
/// Rules: RULE-H1, RULE-H2, RULE-H3
/// </summary>
[Trait("Category", "Integration")]
public class HomePullTests
{
    [Fact]
    public void Agent_Spends_Majority_Of_Time_Near_Home()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Alice", isMale: false, hunger: 80f)
            .AgentAt("Alice", 0, 0)
            .AgentHome("Alice", 0, 0)
            .ShelterAt(0, 0)
            .ResourceAt(-1, 0,  ResourceType.Berries, 30)
            .ResourceAt(1, 0,   ResourceType.Berries, 30)
            .ResourceAt(0, -1,  ResourceType.Berries, 30)
            .ResourceAt(0, 1,   ResourceType.Berries, 30)
            .ResourceAt(-1, -1, ResourceType.Berries, 20)
            .ResourceAt(1, 1,   ResourceType.Berries, 20)
            .Build();

        int homeX = sim.SpawnX, homeY = sim.SpawnY;

        var positions = sim.SamplePositions(500, sampleEveryNTicks: 5);
        var samples   = positions["Alice"];

        Assert.NotEmpty(samples);

        int withinRange = samples.Count(s =>
            TestSim.ManhattanDistance(s.X, s.Y, homeX, homeY) <= 15);
        double pct = 100.0 * withinRange / samples.Count;

        Assert.True(pct >= 80.0,
            $"Agent should spend >=80% of sampled ticks within 15 tiles of home. " +
            $"Actual: {pct:F1}% ({withinRange}/{samples.Count})");
    }

    [Fact]
    public void Agent_Returns_Home_Within_One_Sim_Day()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Bob", isMale: true, hunger: 80f)
            .AgentAt("Bob", 4, 0)           // start 4 tiles away from home
            .AgentHome("Bob", 0, 0)
            .ShelterAt(0, 0)
            .ResourceAt(0, 1, ResourceType.Berries, 20)
            .ResourceAt(0, -1, ResourceType.Berries, 20)
            .Build();

        int homeX = sim.SpawnX, homeY = sim.SpawnY;

        bool reached = sim.TickUntil(() =>
        {
            var bob = sim.GetAgent("Bob");
            return TestSim.ManhattanDistance(bob.X, bob.Y, homeX, homeY) <= 2;
        }, maxTicks: 480);

        Assert.True(reached,
            "Agent with HomeTile should return within 2 tiles of home within 480 ticks (1 sim-day)");
    }

    [Fact]
    public void Agent_Does_Not_Build_Shelter_Far_From_Home()
    {
        // Agent starts 8 tiles east, has existing home and wood+lean_to knowledge.
        // Home-pull should suppress building far from home.
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Carol", isMale: false, hunger: 80f)
            .AgentAt("Carol", 8, 0)         // 8 tiles east of spawn (far from home)
            .AgentHome("Carol", 0, 0)
            .ShelterAt(0, 0)                // existing shelter at home
            .AgentKnows("Carol", "lean_to")
            .AgentInventory("Carol", ResourceType.Wood, 8)
            .ResourceAt(8, 1, ResourceType.Berries, 20)
            .ResourceAt(0, 1, ResourceType.Berries, 20)
            .Build();

        int homeX = sim.SpawnX, homeY = sim.SpawnY;

        sim.Tick(500);

        // Count shelters that are > 8 tiles from home.
        int farShelters = 0;
        for (int x = 0; x < 32; x++)
        for (int y = 0; y < 32; y++)
        {
            if (sim.TileAt(x, y).HasShelter &&
                TestSim.ManhattanDistance(x, y, homeX, homeY) > 8)
                farShelters++;
        }

        Assert.True(farShelters == 0,
            $"No shelter should be built > 8 tiles from agent's home. " +
            $"Found {farShelters} far shelter(s).");
    }
}
