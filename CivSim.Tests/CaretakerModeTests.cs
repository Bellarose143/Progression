using CivSim.Core;
using Xunit;

namespace CivSim.Tests;

/// <summary>
/// Tests for Caretaker mode: parent-child coordination, radius constraints,
/// child feeding, and spouse coordination.
/// This mode was completely untested before — critical for family dynamics.
/// </summary>
public class CaretakerModeTests
{
    /// <summary>
    /// A parent with a young child should enter Caretaker mode.
    /// </summary>
    [Fact]
    public void Caretaker_Entered_When_Has_Young_Child()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Mom", isMale: false, hunger: 80f)
            .AddAgent("Baby", isMale: false, hunger: 80f)
            .AgentAt("Mom", 0, 0).AgentHome("Mom", 0, 0)
            .AgentAt("Baby", 0, 0).AgentHome("Baby", 0, 0)
            .AgentAge("Baby", 100) // Infant
            .AgentInventory("Mom", ResourceType.Berries, 8) // Well-stocked so Forage doesn't override
            .ShelterAt(0, 0)
            .ResourceAt(1, 0, ResourceType.Berries, 30)
            .Build();

        // Set up parent-child relationship
        sim.SetParentChild("Mom", "Baby");

        sim.Tick(10);

        var mom = sim.GetAgent("Mom");
        Assert.Equal(BehaviorMode.Caretaker, mom.CurrentMode);
    }

    /// <summary>
    /// A parent should exit Caretaker mode when their child ages past the threshold.
    /// CaretakerExitChildAge = 4 sim-years = 215040 ticks.
    /// </summary>
    [Fact]
    public void Caretaker_Exited_When_Child_Ages_Out()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Mom", isMale: false, hunger: 80f)
            .AddAgent("Kid", isMale: true, hunger: 80f)
            .AgentAt("Mom", 0, 0).AgentHome("Mom", 0, 0)
            .AgentAt("Kid", 0, 0).AgentHome("Kid", 0, 0)
            .AgentAge("Kid", SimConfig.CaretakerExitChildAge - 5) // Almost aged out
            .AgentMode("Mom", BehaviorMode.Caretaker)
            .ShelterAt(0, 0)
            .ResourceAt(1, 0, ResourceType.Berries, 30)
            .Build();

        sim.SetParentChild("Mom", "Kid");

        // Run a few ticks — child will age past threshold
        sim.Tick(20);

        var mom = sim.GetAgent("Mom");
        Assert.NotEqual(BehaviorMode.Caretaker, mom.CurrentMode);
    }

    /// <summary>
    /// Only one parent should be in Caretaker mode. If Mom is already caretaking,
    /// Dad should stay in Home/Forage as the provider.
    /// </summary>
    [Fact]
    public void Only_One_Parent_Caretakes_At_A_Time()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Mom", isMale: false, hunger: 80f)
            .AddAgent("Dad", isMale: true, hunger: 80f)
            .AddAgent("Baby", isMale: true, hunger: 80f)
            .AgentAt("Mom", 0, 0).AgentHome("Mom", 0, 0)
            .AgentAt("Dad", 0, 0).AgentHome("Dad", 0, 0)
            .AgentAt("Baby", 0, 0).AgentHome("Baby", 0, 0)
            .AgentAge("Baby", 100) // Infant
            .ShelterAt(0, 0)
            .ResourceAt(1, 0, ResourceType.Berries, 30)
            .Build();

        // Set up family relationships
        sim.SetParentChild("Mom", "Baby");
        sim.SetParentChild("Dad", "Baby");
        sim.SetSpouses("Mom", "Dad");

        sim.Tick(15);

        var mom = sim.GetAgent("Mom");
        var dad = sim.GetAgent("Dad");

        // Exactly one should be Caretaker
        int caretakerCount = 0;
        if (mom.CurrentMode == BehaviorMode.Caretaker) caretakerCount++;
        if (dad.CurrentMode == BehaviorMode.Caretaker) caretakerCount++;

        Assert.Equal(1, caretakerCount);
    }

    /// <summary>
    /// A Caretaker who drifts beyond CaretakerForageRange (8 tiles) from home
    /// should return home via ReturnHome goal.
    /// </summary>
    [Fact]
    public void Caretaker_Returns_Home_When_Outside_Radius()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Mom", isMale: false, hunger: 80f)
            .AddAgent("Baby", isMale: false, hunger: 80f)
            .AgentAt("Mom", 12, 0) // 12 tiles east — outside 8-tile radius
            .AgentHome("Mom", 0, 0)
            .AgentAt("Baby", 0, 0).AgentHome("Baby", 0, 0)
            .AgentAge("Baby", 100)
            .AgentMode("Mom", BehaviorMode.Caretaker)
            .ShelterAt(0, 0)
            .ResourceAt(1, 0, ResourceType.Berries, 30)
            .Build();

        sim.SetParentChild("Mom", "Baby");

        sim.Tick(5);

        var mom = sim.GetAgent("Mom");
        // She should have a ReturnHome goal set
        Assert.Equal(GoalType.ReturnHome, mom.CurrentGoal);
    }

    /// <summary>
    /// Caretaker-mode scoring should filter out actions targeting tiles beyond
    /// CaretakerForageRange. The agent should NOT attempt to gather food 15 tiles away.
    /// </summary>
    [Fact]
    public void Caretaker_Does_Not_Target_Beyond_Radius()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Mom", isMale: false, hunger: 70f)
            .AddAgent("Baby", isMale: false, hunger: 80f)
            .AgentAt("Mom", 0, 0).AgentHome("Mom", 0, 0)
            .AgentAt("Baby", 0, 0).AgentHome("Baby", 0, 0)
            .AgentAge("Baby", 100)
            .AgentMode("Mom", BehaviorMode.Caretaker)
            .ShelterAt(0, 0)
            // Only food is far away — beyond Caretaker radius
            .ResourceAt(12, 0, ResourceType.Berries, 30)
            .Build();

        sim.SetParentChild("Mom", "Baby");

        // Give Mom memory of distant food
        var pos = sim.WorldPos(12, 0);
        var mom = sim.GetAgent("Mom");
        mom.Memory.Add(new MemoryEntry
        {
            X = pos.X, Y = pos.Y,
            Type = MemoryType.Resource,
            Resource = ResourceType.Berries,
            Quantity = 30,
            TickObserved = 0
        });

        // Sample Mom's position over 100 ticks
        var positions = sim.SamplePositions(100, sampleEveryNTicks: 5);
        var samples = positions["Mom"];

        // Mom should stay close to home, not walk 12 tiles away
        int nearHome = samples.Count(s =>
            TestSim.ManhattanDistance(s.X, s.Y, sim.SpawnX, sim.SpawnY) <= SimConfig.CaretakerForageRange + 2);
        double pct = 100.0 * nearHome / samples.Count;

        Assert.True(pct >= 80.0,
            $"Caretaker should stay within radius of home. " +
            $"Near home: {pct:F1}% ({nearHome}/{samples.Count})");
    }

    /// <summary>
    /// A Caretaker at home should feed a hungry child from inventory.
    /// </summary>
    [Fact]
    public void Caretaker_Feeds_Hungry_Child_At_Home()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Mom", isMale: false, hunger: 80f)
            .AddAgent("Baby", isMale: false, hunger: 50f) // Hungry baby
            .AgentAt("Mom", 0, 0).AgentHome("Mom", 0, 0)
            .AgentAt("Baby", 0, 0).AgentHome("Baby", 0, 0)
            .AgentAge("Baby", 100)
            .AgentMode("Mom", BehaviorMode.Caretaker)
            .AgentInventory("Mom", ResourceType.Berries, 5)
            .ShelterAt(0, 0)
            .Build();

        sim.SetParentChild("Mom", "Baby");

        float babyHungerBefore = sim.GetAgent("Baby").Hunger;
        sim.Tick(30);
        float babyHungerAfter = sim.GetAgent("Baby").Hunger;

        // Baby should have been fed (hunger should increase or at least not drop drastically)
        // Even with natural drain, feeding should keep it higher than pure starvation
        Assert.True(babyHungerAfter > babyHungerBefore - 5f,
            $"Caretaker should feed hungry baby. Before: {babyHungerBefore:F1}, After: {babyHungerAfter:F1}");
    }

    /// <summary>
    /// A Caretaker deposits surplus food to home storage so infants can self-feed.
    /// Agent keeps 2 food, deposits the rest.
    /// </summary>
    [Fact]
    public void Caretaker_Deposits_Surplus_Food_At_Home()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Mom", isMale: false, hunger: 90f) // Well-fed
            .AddAgent("Baby", isMale: false, hunger: 90f) // Well-fed baby
            .AgentAt("Mom", 0, 0).AgentHome("Mom", 0, 0)
            .AgentAt("Baby", 0, 0).AgentHome("Baby", 0, 0)
            .AgentAge("Baby", 100)
            .AgentMode("Mom", BehaviorMode.Caretaker)
            .AgentInventory("Mom", ResourceType.Berries, 8)
            .ShelterAt(0, 0)
            .Build();

        sim.SetParentChild("Mom", "Baby");

        sim.Tick(20);

        var homeTile = sim.TileAt(sim.SpawnX, sim.SpawnY);
        var mom = sim.GetAgent("Mom");

        // Mom should have deposited surplus — keeping ~2, depositing rest
        // Home storage should have some food
        Assert.True(homeTile.HomeTotalFood > 0 || mom.FoodInInventory() <= 3,
            $"Caretaker should deposit surplus food. " +
            $"Home food: {homeTile.HomeTotalFood}, Mom inventory: {mom.FoodInInventory()}");
    }
}
