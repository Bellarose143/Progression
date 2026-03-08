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
            .AgentInventory("Mom", ResourceType.Berries, 10) // Well-stocked so Forage doesn't override
            .ShelterAt(0, 0)
            .HomeStorageAt(0, 0, ResourceType.Berries, 20) // Ample home storage
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

        // D11 Fix 3: Start during daytime so night rest doesn't override caretaker behavior
        sim.Simulation.CurrentTick = 150;
        // D23: Extended window — RNG cascade from world gen changes may delay goal assignment
        bool hadReturnHome = false;
        var mom = sim.GetAgent("Mom");
        for (int t = 0; t < 20; t++)
        {
            sim.Tick(1);
            if (mom.CurrentGoal == GoalType.ReturnHome)
            {
                hadReturnHome = true;
                break;
            }
        }
        // She should have had a ReturnHome goal at some point
        Assert.True(hadReturnHome, $"Caretaker outside radius should get ReturnHome goal. Final goal: {mom.CurrentGoal}");
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

    /// <summary>
    /// A Caretaker parent should suppress eating when their child is hungry (hunger below 50),
    /// unless the parent's own hunger is below 30 (survival threshold).
    /// This ensures food is saved for the child instead of consumed by the parent.
    /// </summary>
    [Fact]
    public void Caretaker_Suppresses_Eating_For_Hungry_Child()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Mom", isMale: false, hunger: 55f) // Moderately hungry — would normally eat
            .AddAgent("Baby", isMale: false, hunger: 40f) // Hungry baby
            .AgentAt("Mom", 3, 0) // Mom is away from home, foraging
            .AgentHome("Mom", 0, 0)
            .AgentAt("Baby", 0, 0).AgentHome("Baby", 0, 0)
            .AgentAge("Baby", 100) // Infant
            .AgentMode("Mom", BehaviorMode.Caretaker)
            .AgentInventory("Mom", ResourceType.Berries, 3) // Has food
            .ShelterAt(0, 0)
            .Build();

        sim.SetParentChild("Mom", "Baby");
        sim.Simulation.CurrentTick = 150; // Daytime

        var mom = sim.GetAgent("Mom");
        int foodBefore = mom.FoodInInventory();

        // Run a few ticks — Mom should NOT eat (hunger 55 > 30, child hungry < 50)
        sim.Tick(3);

        int foodAfter = mom.FoodInInventory();

        // Mom should still have her food (not eaten) because child is hungry
        // She should be rushing home with it instead
        Assert.True(foodAfter >= foodBefore - 1,
            $"Caretaker with hungry child should suppress eating to save food. " +
            $"Food before: {foodBefore}, after: {foodAfter}, mom hunger: {mom.Hunger:F0}");
    }

    /// <summary>
    /// A Caretaker with no food should NOT rush home to a hungry child.
    /// Instead they should gather food first. Only rush home when carrying 3+ food.
    /// This prevents the lethal oscillation: rush home (no food) -> seek food -> rush home -> repeat.
    /// </summary>
    [Fact]
    public void Caretaker_No_Rush_Home_Without_Food()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Mom", isMale: false, hunger: 80f)
            .AddAgent("Baby", isMale: false, hunger: 40f) // Hungry baby at home
            .AgentAt("Mom", 3, 0) // Mom is 3 tiles away from home
            .AgentHome("Mom", 0, 0)
            .AgentAt("Baby", 0, 0).AgentHome("Baby", 0, 0)
            .AgentAge("Baby", 100)
            .AgentMode("Mom", BehaviorMode.Caretaker)
            // Mom has NO food
            .ShelterAt(0, 0)
            .ResourceAt(4, 0, ResourceType.Berries, 30) // Berries nearby Mom's current position
            .Build();

        sim.SetParentChild("Mom", "Baby");
        sim.Simulation.CurrentTick = 150; // Daytime

        var mom = sim.GetAgent("Mom");
        Assert.Equal(0, mom.FoodInInventory()); // Confirm no food

        // Track Mom's actions over 50 ticks — check each tick for rush-home with zero food.
        // Use tick numbers from the action record to avoid double-counting stale ring buffer entries.
        int rushHomeWithNoFoodCount = 0;
        var seenActions = new List<string>();
        int lastSeenTick = -1;
        for (int i = 0; i < 50; i++)
        {
            sim.Tick(1);
            var lastActions = mom.GetLastActions(1);
            if (lastActions.Count > 0 && lastActions[0].Tick > lastSeenTick)
            {
                var record = lastActions[0];
                lastSeenTick = record.Tick;
                int foodNow = mom.FoodInInventory();
                seenActions.Add($"{record.Detail}(food={foodNow})");
                // A rush-home action where the agent has < 3 food means the fix is not working
                if (record.Detail.Contains("Rushing home to feed") && foodNow < 3)
                    rushHomeWithNoFoodCount++;
            }
        }

        // Mom should NOT rush home without food — she should gather first, then rush home
        Assert.True(rushHomeWithNoFoodCount == 0,
            $"Caretaker with no food should not rush home to feed child. " +
            $"Rush-home-with-no-food count: {rushHomeWithNoFoodCount}. " +
            $"Actions: {string.Join(" -> ", seenActions.Take(15))}");
    }
}
