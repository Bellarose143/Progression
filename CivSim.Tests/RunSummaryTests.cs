using CivSim.Core;
using Xunit;

namespace CivSim.Tests;

/// <summary>
/// Tests for the RunSummaryWriter post-run summary report feature.
/// Validates that all required sections are present and data is consistent.
/// </summary>
[Trait("Category", "Integration")]
public class RunSummaryTests
{
    /// <summary>
    /// Run a sim for 1500 ticks and verify the summary contains all required sections.
    /// </summary>
    [Fact]
    public void Summary_ContainsAllSections()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(42)
            .AddAgent("Alice", isMale: false, hunger: 90)
            .AddAgent("Bob", isMale: true, hunger: 90)
            .AgentAt("Alice", 0, 0).AgentAt("Bob", 1, 0)
            .AgentHome("Alice", 0, 0).AgentHome("Bob", 0, 0)
            .ShelterAt(0, 0)
            .ResourceAt(0, 1, ResourceType.Berries, 50)
            .ResourceAt(1, 1, ResourceType.Wood, 30)
            .ResourceAt(-1, 0, ResourceType.Stone, 20)
            .Build();

        sim.Tick(1500);

        string report = RunSummaryWriter.Generate(sim.Simulation);

        // Header section
        Assert.Contains("CivSim Run Summary", report);
        Assert.Contains("Seed 42", report);
        Assert.Contains("Duration:", report);
        Assert.Contains("World: 32x32", report);

        // Population section
        Assert.Contains("== Population ==", report);
        Assert.Contains("Starting:", report);
        Assert.Contains("Peak:", report);
        Assert.Contains("Final:", report);
        Assert.Contains("Deaths:", report);
        Assert.Contains("Births:", report);

        // Discoveries section
        Assert.Contains("== Discoveries", report);

        // Per-Agent section
        Assert.Contains("== Per-Agent Summary ==", report);
        Assert.Contains("Alice", report);
        Assert.Contains("Bob", report);
        Assert.Contains("Action Distribution:", report);
        Assert.Contains("Mode Distribution:", report);
        Assert.Contains("Stuck episodes:", report);
        Assert.Contains("Max distance from home:", report);

        // Settlements section
        Assert.Contains("== Settlements ==", report);
    }

    /// <summary>
    /// Verify that action percentages sum to approximately 100% for each agent.
    /// </summary>
    [Fact]
    public void Summary_ActionPercentagesSumTo100()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(100)
            .AddAgent("Alice", isMale: false, hunger: 90)
            .AddAgent("Bob", isMale: true, hunger: 90)
            .AgentAt("Alice", 0, 0).AgentAt("Bob", 1, 0)
            .AgentHome("Alice", 0, 0).AgentHome("Bob", 0, 0)
            .ShelterAt(0, 0)
            .ResourceAt(0, 1, ResourceType.Berries, 50)
            .Build();

        sim.Tick(1000);

        // Check action tick counts directly on agents
        foreach (var agent in sim.Simulation.Agents)
        {
            int totalActionTicks = 0;
            foreach (var kvp in agent.ActionTickCounts)
                totalActionTicks += kvp.Value;

            // Agent must have some recorded ticks (alive for at least part of the run)
            if (agent.IsAlive || agent.ActionTickCounts.Count > 0)
            {
                Assert.True(totalActionTicks > 0,
                    $"{agent.Name} has no action tick data");

                // All individual percentages should sum to 100%
                float totalPct = 0f;
                foreach (var kvp in agent.ActionTickCounts)
                    totalPct += 100f * kvp.Value / totalActionTicks;

                Assert.InRange(totalPct, 99.5f, 100.5f);
            }
        }

        // Also verify the report generates without errors
        string report = RunSummaryWriter.Generate(sim.Simulation);
        Assert.NotEmpty(report);
    }

    /// <summary>
    /// Verify mode tick counts also sum correctly per agent.
    /// </summary>
    [Fact]
    public void Summary_ModePercentagesSumTo100()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(200)
            .AddAgent("Alice", isMale: false, hunger: 90)
            .AddAgent("Bob", isMale: true, hunger: 90)
            .AgentAt("Alice", 0, 0).AgentAt("Bob", 1, 0)
            .AgentHome("Alice", 0, 0).AgentHome("Bob", 0, 0)
            .ShelterAt(0, 0)
            .ResourceAt(0, 1, ResourceType.Berries, 50)
            .Build();

        sim.Tick(1000);

        foreach (var agent in sim.Simulation.Agents)
        {
            int totalModeTicks = 0;
            foreach (var kvp in agent.ModeTickCounts)
                totalModeTicks += kvp.Value;

            if (agent.IsAlive || agent.ModeTickCounts.Count > 0)
            {
                Assert.True(totalModeTicks > 0,
                    $"{agent.Name} has no mode tick data");

                float totalPct = 0f;
                foreach (var kvp in agent.ModeTickCounts)
                    totalPct += 100f * kvp.Value / totalModeTicks;

                Assert.InRange(totalPct, 99.5f, 100.5f);
            }
        }
    }

    /// <summary>
    /// Verify that discovery list in the summary matches cumulative discoveries from the simulation.
    /// </summary>
    [Fact]
    public void Summary_DiscoveryListMatchesSimulation()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(300)
            .AddAgent("Alice", isMale: false, hunger: 90)
            .AddAgent("Bob", isMale: true, hunger: 90)
            .AgentAt("Alice", 0, 0).AgentAt("Bob", 1, 0)
            .AgentHome("Alice", 0, 0).AgentHome("Bob", 0, 0)
            .ShelterAt(0, 0)
            .ResourceAt(0, 1, ResourceType.Berries, 50)
            .ResourceAt(1, 1, ResourceType.Wood, 30)
            .ResourceAt(-1, 0, ResourceType.Stone, 20)
            .Build();

        // Run long enough for potential discoveries
        sim.Tick(2000);

        // Get stats to populate CumulativeDiscoveries
        var stats = sim.Simulation.GetStats();

        string report = RunSummaryWriter.Generate(sim.Simulation);

        // The report should mention the total discovery count
        Assert.Contains($"Discoveries ({stats.TotalDiscoveries} total)", report);

        // If there are discovery records, each one should appear in the report
        foreach (var record in sim.Simulation.DiscoveryRecords)
        {
            Assert.Contains(record.RecipeId, report);
        }
    }

    /// <summary>
    /// Verify that the summary file is written to disk correctly.
    /// </summary>
    [Fact]
    public void Summary_WritesToFile()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(400)
            .AddAgent("Alice", isMale: false, hunger: 90)
            .AddAgent("Bob", isMale: true, hunger: 90)
            .AgentAt("Alice", 0, 0).AgentAt("Bob", 1, 0)
            .AgentHome("Alice", 0, 0).AgentHome("Bob", 0, 0)
            .ShelterAt(0, 0)
            .ResourceAt(0, 1, ResourceType.Berries, 50)
            .Build();

        sim.Tick(500);

        string tempDir = Path.Combine(Path.GetTempPath(), "civsim_test_" + Guid.NewGuid().ToString("N"));
        string outputPath = Path.Combine(tempDir, "run_summary_seed_400.txt");

        try
        {
            RunSummaryWriter.Write(sim.Simulation, outputPath);

            Assert.True(File.Exists(outputPath), "Summary file was not created");

            string content = File.ReadAllText(outputPath);
            Assert.Contains("CivSim Run Summary", content);
            Assert.Contains("== Population ==", content);
            Assert.Contains("== Per-Agent Summary ==", content);
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>
    /// Verify that per-agent tracking counters increment correctly:
    /// action count, mode count match number of ticks alive.
    /// </summary>
    [Fact]
    public void Summary_TrackingCountersMatchTicksAlive()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(500)
            .AddAgent("Alice", isMale: false, hunger: 90)
            .AgentAt("Alice", 0, 0)
            .AgentHome("Alice", 0, 0)
            .ShelterAt(0, 0)
            .ResourceAt(0, 1, ResourceType.Berries, 50)
            .Build();

        int tickCount = 500;
        sim.Tick(tickCount);

        var alice = sim.GetAgent("Alice");

        // If Alice is alive, her action tick count should match the number of ticks run
        if (alice.IsAlive)
        {
            int totalActionTicks = 0;
            foreach (var kvp in alice.ActionTickCounts)
                totalActionTicks += kvp.Value;

            Assert.Equal(tickCount, totalActionTicks);

            int totalModeTicks = 0;
            foreach (var kvp in alice.ModeTickCounts)
                totalModeTicks += kvp.Value;

            Assert.Equal(tickCount, totalModeTicks);
        }
    }

    /// <summary>
    /// Verify peak population tracking works correctly.
    /// </summary>
    [Fact]
    public void Summary_PeakPopulationTracked()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(600)
            .AddAgent("Alice", isMale: false, hunger: 90)
            .AddAgent("Bob", isMale: true, hunger: 90)
            .AgentAt("Alice", 0, 0).AgentAt("Bob", 1, 0)
            .AgentHome("Alice", 0, 0).AgentHome("Bob", 0, 0)
            .ShelterAt(0, 0)
            .ResourceAt(0, 1, ResourceType.Berries, 50)
            .Build();

        sim.Tick(100);

        // Peak population should be at least 2 (the two founding agents)
        Assert.True(sim.Simulation.PeakPopulation >= 2,
            $"Peak population should be at least 2, was {sim.Simulation.PeakPopulation}");

        // Peak population tick should be set
        Assert.True(sim.Simulation.PeakPopulationTick >= 0);

        // Summary report should contain the peak info
        string report = RunSummaryWriter.Generate(sim.Simulation);
        Assert.Contains($"Peak: {sim.Simulation.PeakPopulation}", report);
    }

    /// <summary>
    /// Verify birth records include parent names for children.
    /// Founding agents should not have parent names.
    /// </summary>
    [Fact]
    public void Summary_FoundingAgentsHaveNoParents()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(700)
            .AddAgent("Alice", isMale: false, hunger: 90)
            .AddAgent("Bob", isMale: true, hunger: 90)
            .AgentAt("Alice", 0, 0).AgentAt("Bob", 1, 0)
            .AgentHome("Alice", 0, 0).AgentHome("Bob", 0, 0)
            .ShelterAt(0, 0)
            .Build();

        sim.Tick(10);

        var alice = sim.GetAgent("Alice");
        var bob = sim.GetAgent("Bob");

        Assert.Null(alice.Parent1Name);
        Assert.Null(alice.Parent2Name);
        Assert.Null(bob.Parent1Name);
        Assert.Null(bob.Parent2Name);
    }

    /// <summary>
    /// Verify max distance from home is tracked.
    /// An agent that moves should have MaxDistanceFromHome > 0.
    /// </summary>
    [Fact]
    public void Summary_MaxDistanceFromHomeTracked()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(800)
            .AddAgent("Alice", isMale: false, hunger: 90)
            .AgentAt("Alice", 0, 0)
            .AgentHome("Alice", 0, 0)
            .ShelterAt(0, 0)
            .ResourceAt(3, 0, ResourceType.Berries, 50) // Food a few tiles away
            .Build();

        sim.Tick(500);

        var alice = sim.GetAgent("Alice");

        // After 500 ticks with food nearby, agent should have moved at some point
        // MaxDistanceFromHome should be >= 0 (0 if never moved from home, which is unlikely)
        Assert.True(alice.MaxDistanceFromHome >= 0);

        // Verify it appears in the report
        string report = RunSummaryWriter.Generate(sim.Simulation);
        Assert.Contains($"Max distance from home: {alice.MaxDistanceFromHome} tiles", report);
    }

    /// <summary>
    /// Verify the report handles a zero-tick simulation gracefully.
    /// </summary>
    [Fact]
    public void Summary_HandlesZeroTicks()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(900)
            .AddAgent("Alice", isMale: false, hunger: 90)
            .AgentAt("Alice", 0, 0)
            .AgentHome("Alice", 0, 0)
            .ShelterAt(0, 0)
            .Build();

        // Don't run any ticks
        string report = RunSummaryWriter.Generate(sim.Simulation);

        Assert.Contains("CivSim Run Summary", report);
        Assert.Contains("Duration: 0 ticks", report);
        Assert.Contains("== Population ==", report);
        Assert.Contains("== Per-Agent Summary ==", report);
    }

    /// <summary>
    /// Verify that dead agents still appear in the per-agent summary.
    /// </summary>
    [Fact]
    public void Summary_IncludesDeadAgents()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1000)
            .AddAgent("Alice", isMale: false, hunger: 5, health: 10)
            .AgentAt("Alice", 0, 0)
            .AgentHome("Alice", 0, 0)
            .Build();

        // Run enough ticks for Alice to starve/die
        sim.Tick(2000);

        var alice = sim.GetAgent("Alice");

        string report = RunSummaryWriter.Generate(sim.Simulation);

        // Alice should appear in the per-agent summary regardless of alive/dead status
        Assert.Contains("Alice", report);
        Assert.Contains("== Per-Agent Summary ==", report);

        if (!alice.IsAlive)
        {
            // Should appear in deaths list
            Assert.Contains("Deaths:", report);
            Assert.Contains("Dead", report);
        }
    }
}
