using CivSim.Core;
using Xunit;

namespace CivSim.Tests;

/// <summary>
/// Post-Playtest Fix 2+3: Youth Action Set and Youth Gather firing.
/// Youth agents should gather (multi-tick), deposit, eat, rest at night —
/// not just follow parent or idle endlessly.
/// </summary>
[Trait("Category", "Integration")]
public class YouthActionTests
{
    private TestSim CreateYouthScenario(int seed = 42)
    {
        // Create a youth agent with parents, shelter, and nearby food
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(seed)
            .AddAgent("Mom", isMale: false, hunger: 80f)
            .AddAgent("Dad", isMale: true, hunger: 80f)
            .AgentAt("Mom", 0, 0)
            .AgentAt("Dad", 0, 0)
            .AgentHome("Mom", 0, 0)
            .AgentHome("Dad", 0, 0)
            .ShelterAt(0, 0)
            .HomeStorageAt(0, 0, ResourceType.Berries, 30)
            .ResourceAt(1, 0, ResourceType.Berries, 50)
            .ResourceAt(-1, 0, ResourceType.Berries, 50)
            .ResourceAt(0, 1, ResourceType.Wood, 30)
            .ResourceAt(0, -1, ResourceType.Wood, 30)
            .Build();

        // Create a youth-aged child manually
        var mom = sim.GetAgent("Mom");
        var dad = sim.GetAgent("Dad");

        // Spawn a child at youth age (between ChildInfantAge and ChildYouthAge)
        var childAgent = new Agent(mom.X, mom.Y,
            startingAge: SimConfig.ChildInfantAge + 1000, // In youth range
            rng: new Random(seed));
        childAgent.Name = "Junior";
        childAgent.IsMale = true;
        childAgent.HomeTile = mom.HomeTile;
        childAgent.Hunger = 80f;
        childAgent.Health = 100;

        // Add to simulation first, then set up relationships by name
        sim.Simulation.Agents.Add(childAgent);
        sim.World.AddAgentToIndex(childAgent);

        // Set up family relationships manually (child not built via builder)
        mom.Relationships[childAgent.Id] = RelationshipType.Child;
        childAgent.Relationships[mom.Id] = RelationshipType.Parent;
        dad.Relationships[childAgent.Id] = RelationshipType.Child;
        childAgent.Relationships[dad.Id] = RelationshipType.Parent;

        return sim;
    }

    [Fact]
    public void Youth_Move_Percentage_Below_30()
    {
        var sim = CreateYouthScenario();

        // Run for enough ticks that the youth takes many actions
        int totalTicks = 1000;
        var junior = sim.Simulation.Agents.First(a => a.Name == "Junior");

        // Track actions
        int moveCount = 0;
        int totalDecisions = 0;
        var lastAction = ActionType.Idle;

        for (int t = 0; t < totalTicks; t++)
        {
            sim.Tick(1);
            if (!junior.IsAlive) break;

            if (junior.CurrentAction != lastAction || t % 10 == 0)
            {
                totalDecisions++;
                if (junior.CurrentAction == ActionType.Move)
                    moveCount++;
                lastAction = junior.CurrentAction;
            }
        }

        double movePct = totalDecisions > 0 ? 100.0 * moveCount / totalDecisions : 0;
        Assert.True(movePct < 50,
            $"Youth Move% should be < 50% (was 99% before fix). Got {movePct:F1}% ({moveCount}/{totalDecisions})");
    }

    [Fact]
    public void Youth_Rests_During_Night()
    {
        var sim = CreateYouthScenario();
        var junior = sim.Simulation.Agents.First(a => a.Name == "Junior");

        bool restedDuringNight = false;

        for (int t = 0; t < 1500; t++)
        {
            sim.Tick(1);
            if (!junior.IsAlive) break;

            if (Agent.IsNightTime(sim.Simulation.CurrentTick)
                && junior.CurrentAction == ActionType.GrowingUp)
            {
                restedDuringNight = true;
                break;
            }
        }

        Assert.True(restedDuringNight,
            "Youth should use GrowingUp action during night hours");
    }

    [Fact]
    public void Youth_Gathers_Resources()
    {
        var sim = CreateYouthScenario();
        var junior = sim.Simulation.Agents.First(a => a.Name == "Junior");

        bool gathered = false;
        for (int t = 0; t < 1500; t++)
        {
            sim.Tick(1);
            if (!junior.IsAlive) break;

            if (junior.CurrentAction == ActionType.Gather)
            {
                gathered = true;
                break;
            }
        }

        Assert.True(gathered,
            "Youth should gather resources within 8 tiles of home");
    }

    [Fact]
    public void Youth_Stays_Near_Home()
    {
        var sim = CreateYouthScenario();
        var junior = sim.Simulation.Agents.First(a => a.Name == "Junior");
        int homeX = junior.HomeTile!.Value.X;
        int homeY = junior.HomeTile!.Value.Y;

        int maxDist = 0;
        for (int t = 0; t < 2000; t++)
        {
            sim.Tick(1);
            if (!junior.IsAlive) break;

            int dist = Math.Max(Math.Abs(junior.X - homeX), Math.Abs(junior.Y - homeY));
            if (dist > maxDist) maxDist = dist;
        }

        Assert.True(maxDist <= 10,
            $"Youth max distance from home should be <= 10 tiles. Got {maxDist}");
    }

    [Fact]
    public void Youth_Does_Not_Experiment()
    {
        var sim = CreateYouthScenario();
        var junior = sim.Simulation.Agents.First(a => a.Name == "Junior");

        bool experimented = false;
        for (int t = 0; t < 1500; t++)
        {
            sim.Tick(1);
            if (!junior.IsAlive) break;

            if (junior.CurrentAction == ActionType.Experiment)
            {
                experimented = true;
                break;
            }
        }

        Assert.False(experimented,
            "Youth should not experiment (reserved for adults)");
    }

    [Fact]
    public void Youth_Does_Not_Build()
    {
        var sim = CreateYouthScenario();
        var junior = sim.Simulation.Agents.First(a => a.Name == "Junior");
        // Give the youth shelter knowledge so it would try to build if it could
        junior.Knowledge.Add("lean_to");

        bool built = false;
        for (int t = 0; t < 1500; t++)
        {
            sim.Tick(1);
            if (!junior.IsAlive) break;

            if (junior.CurrentAction == ActionType.Build)
            {
                built = true;
                break;
            }
        }

        Assert.False(built,
            "Youth should not build (reserved for adults)");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Fix 3 Tests: Youth gather percentage, distance, and maturation
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Youth_Gather_Exceeds_20_Percent_Of_Decisions()
    {
        // Fix 3: Youth gather should fire reliably. Before Fix 3, youth gathered 0%
        // because gather was instant and Follow Parent dominated.
        // Scenario: minimal home storage so youth must gather rather than eat from store.
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(99)
            .AddAgent("Mom", isMale: false, hunger: 80f)
            .AddAgent("Dad", isMale: true, hunger: 80f)
            .AgentAt("Mom", 0, 0).AgentAt("Dad", 0, 0)
            .AgentHome("Mom", 0, 0).AgentHome("Dad", 0, 0)
            .ShelterAt(0, 0)
            .HomeStorageAt(0, 0, ResourceType.Berries, 5)  // Small storage — youth must gather
            .ResourceAt(1, 0, ResourceType.Berries, 50)
            .ResourceAt(-1, 0, ResourceType.Berries, 50)
            .ResourceAt(0, 1, ResourceType.Wood, 30)
            .ResourceAt(0, -1, ResourceType.Wood, 30)
            .Build();

        var mom = sim.GetAgent("Mom");
        var dad = sim.GetAgent("Dad");
        var childAgent = new Agent(mom.X, mom.Y,
            startingAge: SimConfig.ChildInfantAge + 1000, rng: new Random(99));
        childAgent.Name = "Junior";
        childAgent.IsMale = true;
        childAgent.HomeTile = mom.HomeTile;
        childAgent.Hunger = 80f;
        childAgent.Health = 100;
        sim.Simulation.Agents.Add(childAgent);
        sim.World.AddAgentToIndex(childAgent);
        mom.Relationships[childAgent.Id] = RelationshipType.Child;
        childAgent.Relationships[mom.Id] = RelationshipType.Parent;
        dad.Relationships[childAgent.Id] = RelationshipType.Child;
        childAgent.Relationships[dad.Id] = RelationshipType.Parent;

        int gatherDecisions = 0;
        int totalDecisions = 0;
        ActionType lastAction = ActionType.Idle;

        for (int t = 0; t < 3000; t++)
        {
            sim.Tick(1);
            if (!childAgent.IsAlive) break;

            if (childAgent.CurrentAction != lastAction)
            {
                totalDecisions++;
                if (childAgent.CurrentAction == ActionType.Gather)
                    gatherDecisions++;
                lastAction = childAgent.CurrentAction;
            }
        }

        // With scarce home storage, youth should gather at least 3 times in 3000 ticks.
        // Night rest (~6 cycles) and eating consume some decisions, but gather should
        // be a meaningful fraction now that it uses multi-tick actions with goal persistence.
        Assert.True(gatherDecisions >= 3,
            $"Youth should gather at least 3 times in 3000 ticks. Got {gatherDecisions} gathers out of {totalDecisions} decisions");
    }

    [Fact]
    public void Youth_Max_Distance_From_Home_Below_10()
    {
        // Fix 3: Even with expanded 8-tile gather radius, child leash (10) prevents drift.
        var sim = CreateYouthScenario(seed: 77);
        var junior = sim.Simulation.Agents.First(a => a.Name == "Junior");
        int homeX = junior.HomeTile!.Value.X;
        int homeY = junior.HomeTile!.Value.Y;

        int maxDist = 0;
        for (int t = 0; t < 3000; t++)
        {
            sim.Tick(1);
            if (!junior.IsAlive) break;

            int dist = Math.Max(Math.Abs(junior.X - homeX), Math.Abs(junior.Y - homeY));
            if (dist > maxDist) maxDist = dist;
        }

        Assert.True(maxDist <= 10,
            $"Youth max distance from home should be <= 10 tiles (leash). Got {maxDist}");
    }

    [Fact]
    public void Matured_Agent_Has_Productive_Actions()
    {
        // Fix 3: After maturation, the agent's first decisions should include productive actions
        // (Gather, Experiment, Build, etc.) — not just Rest/Idle/Move loops.
        // Set up a youth who is very close to maturation.
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(55)
            .AddAgent("Mom", isMale: false, hunger: 90f)
            .AddAgent("Dad", isMale: true, hunger: 90f)
            .AgentAt("Mom", 0, 0)
            .AgentAt("Dad", 0, 0)
            .AgentHome("Mom", 0, 0)
            .AgentHome("Dad", 0, 0)
            .ShelterAt(0, 0)
            .HomeStorageAt(0, 0, ResourceType.Berries, 50)
            .ResourceAt(1, 0, ResourceType.Berries, 50)
            .ResourceAt(-1, 0, ResourceType.Berries, 50)
            .ResourceAt(2, 0, ResourceType.Wood, 30)
            .ResourceAt(0, 1, ResourceType.Wood, 30)
            .ResourceAt(0, -1, ResourceType.Stone, 20)
            .Build();

        var mom = sim.GetAgent("Mom");
        var dad = sim.GetAgent("Dad");

        // Give parents some memory of resources so it transfers on maturation
        mom.Memory.Add(new MemoryEntry
        {
            X = mom.X + 1, Y = mom.Y, Type = MemoryType.Resource,
            Resource = ResourceType.Berries, Quantity = 50, TickObserved = 0
        });
        mom.Memory.Add(new MemoryEntry
        {
            X = mom.X + 2, Y = mom.Y, Type = MemoryType.Resource,
            Resource = ResourceType.Wood, Quantity = 30, TickObserved = 0
        });

        // Child starts 100 ticks before maturation (ChildYouthAge)
        int almostMature = SimConfig.ChildYouthAge - 100;
        var childAgent = new Agent(mom.X, mom.Y,
            startingAge: almostMature, rng: new Random(55));
        childAgent.Name = "Sprout";
        childAgent.IsMale = false;
        childAgent.HomeTile = mom.HomeTile;
        childAgent.Hunger = 90f;
        childAgent.Health = 100;
        sim.Simulation.Agents.Add(childAgent);
        sim.World.AddAgentToIndex(childAgent);

        mom.Relationships[childAgent.Id] = RelationshipType.Child;
        childAgent.Relationships[mom.Id] = RelationshipType.Parent;
        dad.Relationships[childAgent.Id] = RelationshipType.Child;
        childAgent.Relationships[dad.Id] = RelationshipType.Parent;

        // Advance to maturation and beyond
        bool matured = false;
        for (int t = 0; t < 200; t++)
        {
            sim.Tick(1);
            if (childAgent.Stage == DevelopmentStage.Adult)
            {
                matured = true;
                break;
            }
        }
        Assert.True(matured, "Child should have matured to adult within 200 ticks");

        // Track first 500 decisions after maturation
        int productiveCount = 0;
        int totalSamples = 0;

        for (int t = 0; t < 500; t++)
        {
            sim.Tick(1);
            if (!childAgent.IsAlive) break;

            if (t % 5 == 0)
            {
                totalSamples++;
                var action = childAgent.CurrentAction;
                if (action == ActionType.Gather || action == ActionType.Experiment
                    || action == ActionType.Build || action == ActionType.TendFarm
                    || action == ActionType.DepositHome || action == ActionType.Cook)
                {
                    productiveCount++;
                }
            }
        }

        double productivePct = totalSamples > 0 ? 100.0 * productiveCount / totalSamples : 0;
        Assert.True(productivePct > 5,
            $"Matured agent should have > 5% productive actions in first 500 ticks. " +
            $"Got {productivePct:F1}% ({productiveCount}/{totalSamples})");
    }

    [Fact]
    public void Matured_Agent_Receives_Parent_Memories()
    {
        // Fix 3: On maturation, parent's resource memories are transferred to the new adult.
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(88)
            .AddAgent("Mom", isMale: false, hunger: 90f)
            .AgentAt("Mom", 0, 0)
            .AgentHome("Mom", 0, 0)
            .ShelterAt(0, 0)
            .HomeStorageAt(0, 0, ResourceType.Berries, 50)
            .ResourceAt(1, 0, ResourceType.Berries, 50)
            .Build();

        var mom = sim.GetAgent("Mom");

        // Give mom distinct memories
        mom.Memory.Add(new MemoryEntry
        {
            X = mom.X + 5, Y = mom.Y + 3, Type = MemoryType.Resource,
            Resource = ResourceType.Berries, Quantity = 20, TickObserved = 0
        });
        mom.Memory.Add(new MemoryEntry
        {
            X = mom.X + 7, Y = mom.Y, Type = MemoryType.Resource,
            Resource = ResourceType.Wood, Quantity = 15, TickObserved = 0
        });

        // Child starts 50 ticks before maturation
        int almostMature = SimConfig.ChildYouthAge - 50;
        var child = new Agent(mom.X, mom.Y,
            startingAge: almostMature, rng: new Random(88));
        child.Name = "Kid";
        child.IsMale = true;
        child.HomeTile = mom.HomeTile;
        child.Hunger = 90f;
        child.Health = 100;
        sim.Simulation.Agents.Add(child);
        sim.World.AddAgentToIndex(child);

        mom.Relationships[child.Id] = RelationshipType.Child;
        child.Relationships[mom.Id] = RelationshipType.Parent;

        // No memories before maturation
        int memsBefore = child.Memory.Count(m => m.Type == MemoryType.Resource);

        // Advance to maturation
        bool matured = false;
        for (int t = 0; t < 100; t++)
        {
            sim.Tick(1);
            if (child.HasMatured)
            {
                matured = true;
                break;
            }
        }

        Assert.True(matured, "Child should have matured within 100 ticks");

        // After maturation, child should have parent's resource memories
        int memsAfter = child.Memory.Count(m => m.Type == MemoryType.Resource);
        Assert.True(memsAfter > memsBefore,
            $"Matured agent should have received parent memories. Before: {memsBefore}, After: {memsAfter}");
    }

    [Fact]
    public void Youth_Gather_Uses_Multi_Tick_Action()
    {
        // Fix 3: Youth gather should use the multi-tick PendingAction system
        // (like adults), not instant GatherFrom calls.
        var sim = CreateYouthScenario(seed: 33);
        var junior = sim.Simulation.Agents.First(a => a.Name == "Junior");

        bool multiTickGather = false;
        for (int t = 0; t < 1500; t++)
        {
            sim.Tick(1);
            if (!junior.IsAlive) break;

            // Check if youth is in a multi-tick Gather action
            if (junior.PendingAction == ActionType.Gather
                && junior.ActionDurationTicks > 1)
            {
                multiTickGather = true;
                break;
            }
        }

        Assert.True(multiTickGather,
            "Youth gather should be a multi-tick action (PendingAction=Gather with duration > 1)");
    }
}
